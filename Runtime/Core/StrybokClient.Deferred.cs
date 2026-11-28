using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok
{
    // Deferred links (SDK contract, "Deferred links (optional, Android)" and "Deferred links on
    // iOS (optional)"). Nothing in this file reports to OnError, throws into the game, or logs
    // or stores a referrer, a pasted text or a click id.
    public sealed partial class StrybokClient
    {
        private const string RedeemPath = "/v1/public/deferred-links/redeem";
        private const string MatchPath = "/v1/public/deferred-links/match";
        private const string AttemptsKey = "strybok.deferred.attempts";
        private const string DoneKey = "strybok.deferred.done";
        private const int MaxDeferredAttempts = 3;

        private static readonly IReadOnlyDictionary<string, string> DeferredHeaders =
            new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Accept", "application/json" },
            };

        /// <summary>What the server accepts as <c>locale</c>.</summary>
        private static readonly Regex LocalePattern = new Regex(
            @"\A[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{1,8}){0,4}\z",
            RegexOptions.CultureInvariant);

        // Set once in the constructor.
        private IDeferredLinkStore _deferredStore;
        private InstallReferrerSource[] _referrerSources;

        /// <summary>The package name on Android, the bundle id on iOS; null when not given.</summary>
        private string _deferredIdentifier;

        /// <summary>A store, an identifier and a platform with a flow: what the option needs to do anything.</summary>
        private bool _deferredPossible;
        private bool _allowMatching;
        private Func<string> _deviceFamily;
        private Func<string> _locale;
        private Func<CancellationToken, Task<bool>> _pasteboardMayContainLink;
        private TimeSpan _referrerSourceTimeout;

        // Guarded by _gate.
        private Action<DeferredLinkStatus> _statusHandlers;
        private DeferredLinkStatus _deferredStatus;
        private Task _pasteChain = Task.FromResult(true);

        /// <summary>The <c>DeferredLinks</c> option, or <see cref="EnableDeferredLinks"/> was called.</summary>
        private bool _deferredRequested;
        private bool _startCompleted;
        private bool _deferredCheckStarted;

        /// <summary>In-memory mirror of the stored "done": set the moment a flow decides to store it.</summary>
        private bool _deferredDone;

        /// <summary>A link of this app arrived from the link source since the process started.</summary>
        private bool _normalLinkAccepted;
        private bool _awaitingPaste;

        private enum DeferredAnswer
        {
            Link,
            Unusable,
            NotFound,
            Transient,
            Disposed,
        }

        private enum DeferredDelivery
        {
            Delivered,
            Superseded,
            Disposed,
        }

        /// <summary>
        /// Where the deferred-link check stands. Readable at any time; see
        /// <see cref="DeferredLinkStatusChanged"/>.
        /// </summary>
        public DeferredLinkStatus DeferredLinkStatus
        {
            get { lock (_gate) return _deferredStatus; }
        }

        /// <summary>
        /// Raised when <see cref="DeferredLinkStatus"/> changes, on the callback context (Unity's
        /// main thread with <c>StrybokSdk</c>). For diagnostics; a deferred link itself arrives
        /// through <see cref="LinkReceived"/>.
        /// </summary>
        public event Action<DeferredLinkStatus> DeferredLinkStatusChanged
        {
            add
            {
                lock (_gate)
                {
                    if (!_disposed) _statusHandlers += value;
                }
            }
            remove
            {
                lock (_gate) _statusHandlers -= value;
            }
        }

        /// <summary>
        /// Switches the <c>DeferredLinks</c> option on after the client was created, for example
        /// after a consent screen. When <see cref="InitializeAsync"/> has already completed the
        /// check runs now, once; otherwise it runs after it as usual. Calling it again does
        /// nothing. Without a store and the platform's identifier the status stays
        /// <see cref="Strybok.DeferredLinkStatus.Disabled"/>.
        /// </summary>
        public void EnableDeferredLinks()
        {
            bool run = false;
            lock (_gate)
            {
                if (_disposed || _deferredRequested) return;
                _deferredRequested = true;
                if (!_deferredPossible) return;
                if (_startCompleted && !_deferredCheckStarted)
                {
                    _deferredCheckStarted = true;
                    run = true;
                }
            }

            SetDeferredStatus(DeferredLinkStatus.Pending, null);
            if (run) Observe(RunDeferredCheckAsync());
        }

        /// <summary>
        /// iOS: redeems the link a person copied on the page before the App Store and pasted in
        /// the game. Pass the pasted text as it is; the SDK never reads the pasteboard itself.
        ///
        /// Text that is not a copied link of this app is dropped without a request
        /// (<see cref="PastedLinkResult.NotOurs"/>) — the person may have had anything on their
        /// clipboard. Otherwise only the single-use id found in the link is sent, with the
        /// bundle id and the SDK version. On success the link is raised through
        /// <see cref="LinkReceived"/> with <see cref="ResolvedDeepLink.IsDeferred"/> set, even
        /// when a normal link was delivered earlier in this launch.
        ///
        /// The task never faults and nothing is reported to <c>OnError</c>. Calls are handled
        /// one after another.
        /// </summary>
        public Task<PastedLinkResult> RedeemPastedLinkAsync(string pastedText)
        {
            lock (_gate)
            {
                Task<PastedLinkResult> run = RedeemPastedAfterAsync(_pasteChain, pastedText);
                _pasteChain = run;
                return run;
            }
        }

        /// <summary>
        /// iOS with probabilistic matching: tells the SDK that the person moved on without
        /// pasting, so matching may start. Does nothing unless the status is
        /// <see cref="Strybok.DeferredLinkStatus.AwaitingPaste"/>.
        /// </summary>
        public void SkipPastedLink()
        {
            ContinueMatchingIfAwaiting();
        }

        // ---------------------------------------------------------------------

        private void ConfigureDeferredLinks(StrybokOptions options)
        {
            _deferredStore = options.DeferredLinkStore;
            _referrerSources = options.InstallReferrerSources == null
                ? new InstallReferrerSource[0]
                : new List<InstallReferrerSource>(options.InstallReferrerSources).ToArray();
            _deferredIdentifier = _platform == "android"
                ? NormalizeIdentifier(options.AndroidPackageName)
                : _platform == "ios" ? NormalizeIdentifier(options.IosBundleId) : null;
            _deferredPossible = _deferredStore != null && _deferredIdentifier != null;
            _allowMatching = options.AllowProbabilisticMatching;
            _deviceFamily = options.DeviceFamily;
            _locale = options.Locale;
            _pasteboardMayContainLink = options.PasteboardMayContainLink;
            _referrerSourceTimeout = options.InstallReferrerSourceTimeout;
            _deferredRequested = options.DeferredLinks;
            _deferredStatus = _deferredRequested && _deferredPossible
                ? DeferredLinkStatus.Pending
                : DeferredLinkStatus.Disabled;
        }

        /// <summary>Called once <see cref="InitializeAsync"/> has completed everything else.</summary>
        private void OnStartCompleted()
        {
            lock (_gate)
            {
                _startCompleted = true;
                if (_disposed || !_deferredRequested || !_deferredPossible || _deferredCheckStarted) return;
                _deferredCheckStarted = true;
            }

            Observe(RunDeferredCheckAsync());
        }

        /// <summary>Never awaited by anything: a slow source delays nothing but the deferred link.</summary>
        private async Task RunDeferredCheckAsync()
        {
            try
            {
                if (_platform == "android") await CheckAndroidAsync();
                else await CheckIosAsync();
            }
            catch (Exception)
            {
                // Never into the game. The attempt was counted; a later launch tries again.
                SetDeferredStatus(DeferredLinkStatus.Pending, DeferredLinkStatus.Checking);
            }
        }

        private async Task CheckAndroidAsync()
        {
            SetDeferredStatus(DeferredLinkStatus.Checking, null);
            bool done;
            int attempts;
            if (!TryReadDeferredState(out done, out attempts))
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, null);
                return;
            }
            if (done)
            {
                SetDeferredStatus(DeferredLinkStatus.NotFirstLaunch, null);
                return;
            }
            if (attempts >= MaxDeferredAttempts)
            {
                FinishDeferred(DeferredLinkStatus.Failed);
                return;
            }
            // Before any source is asked: a launch that dies from here on has used up an attempt.
            if (!TryWriteDeferred(AttemptsKey, (attempts + 1).ToString(CultureInfo.InvariantCulture)))
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, null);
                return;
            }

            string clickId = null;
            bool unavailableForNow = false;
            bool anyReferrer = false;
            foreach (InstallReferrerSource source in _referrerSources)
            {
                InstallReferrerAnswer answer = await AskSourceAsync(source);
                if (_disposed) return;
                if (answer.Kind == InstallReferrerAnswerKind.Referrer)
                {
                    anyReferrer = true;
                    clickId = DeferredLinkParsing.FindClickIdInReferrer(answer.ReferrerValue);
                    if (clickId != null) break;
                }
                else if (answer.Kind == InstallReferrerAnswerKind.UnavailableForNow)
                {
                    unavailableForNow = true;
                }
            }

            if (clickId == null)
            {
                if (unavailableForNow)
                {
                    SetDeferredStatus(DeferredLinkStatus.Pending, null);
                    return;
                }
                FinishDeferred(anyReferrer ? DeferredLinkStatus.NoReferrer : DeferredLinkStatus.Unavailable);
                return;
            }

            if (NormalLinkAccepted())
            {
                FinishDeferred(DeferredLinkStatus.Superseded);
                return;
            }

            var body = new StringBuilder(160);
            body.Append("{\"platform\":\"android\",\"clickId\":");
            StrybokJson.WriteString(body, clickId);
            body.Append(",\"packageName\":");
            StrybokJson.WriteString(body, _deferredIdentifier);
            body.Append(",\"sdkVersion\":");
            StrybokJson.WriteString(body, SdkVersion);
            body.Append('}');

            FinishAutomatic(await PostDeferredAsync(RedeemPath, body.ToString(), false));
        }

        private async Task CheckIosAsync()
        {
            bool done;
            int attempts;
            if (!TryReadDeferredState(out done, out attempts))
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, null);
                return;
            }
            if (done)
            {
                SetDeferredStatus(DeferredLinkStatus.NotFirstLaunch, null);
                return;
            }
            if (!_allowMatching)
            {
                // Only the pasted-link flow exists; nothing is stored, so pasting keeps working.
                SetDeferredStatus(DeferredLinkStatus.Unavailable, null);
                return;
            }

            Func<CancellationToken, Task<bool>> mayContainLink = _pasteboardMayContainLink;
            if (mayContainLink != null)
            {
                bool waitForPaste = false;
                try
                {
                    Task<bool> asked = mayContainLink(_lifetime.Token);
                    if (asked != null) waitForPaste = await asked;
                }
                catch (Exception)
                {
                    waitForPaste = false;
                }

                if (waitForPaste)
                {
                    lock (_gate)
                    {
                        if (_disposed || _deferredDone) return; // A paste was handled meanwhile.
                        _awaitingPaste = true;
                    }
                    SetDeferredStatus(DeferredLinkStatus.AwaitingPaste, null);
                    return;
                }
            }

            await RunMatchingAsync();
        }

        /// <summary>Flow B from the attempt counter on.</summary>
        private async Task RunMatchingAsync()
        {
            bool done;
            int attempts;
            if (!TryReadDeferredState(out done, out attempts))
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, null);
                return;
            }
            if (done) return; // The pasted link was handled meanwhile; its status stands.

            SetDeferredStatus(DeferredLinkStatus.Checking, null);
            if (attempts >= MaxDeferredAttempts)
            {
                FinishDeferred(DeferredLinkStatus.Failed);
                return;
            }
            if (!TryWriteDeferred(AttemptsKey, (attempts + 1).ToString(CultureInfo.InvariantCulture)))
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, null);
                return;
            }
            if (NormalLinkAccepted())
            {
                FinishDeferred(DeferredLinkStatus.Superseded);
                return;
            }

            string family = Ask(_deviceFamily);
            string locale = Ask(_locale);
            if ((family != "iphone" && family != "ipad" && family != "ipod") ||
                locale == null || !LocalePattern.IsMatch(locale))
            {
                FinishDeferred(DeferredLinkStatus.Unavailable);
                return;
            }

            var body = new StringBuilder(160);
            body.Append("{\"platform\":\"ios\",\"bundleId\":");
            StrybokJson.WriteString(body, _deferredIdentifier);
            body.Append(",\"deviceFamily\":");
            StrybokJson.WriteString(body, family);
            body.Append(",\"locale\":");
            StrybokJson.WriteString(body, locale);
            body.Append(",\"sdkVersion\":");
            StrybokJson.WriteString(body, SdkVersion);
            body.Append('}');

            FinishAutomatic(await PostDeferredAsync(MatchPath, body.ToString(), true));
        }

        private async Task RunMatchingSafelyAsync()
        {
            try
            {
                await RunMatchingAsync();
            }
            catch (Exception)
            {
                SetDeferredStatus(DeferredLinkStatus.Pending, DeferredLinkStatus.Checking);
            }
        }

        private void ContinueMatchingIfAwaiting()
        {
            lock (_gate)
            {
                if (_disposed || !_awaitingPaste) return;
                _awaitingPaste = false;
            }

            Observe(RunMatchingSafelyAsync());
        }

        /// <summary>The end of both automatic flows, where a normal link wins.</summary>
        private void FinishAutomatic(DeferredResponse response)
        {
            switch (response.Answer)
            {
                case DeferredAnswer.Disposed:
                    return;
                case DeferredAnswer.Transient:
                    SetDeferredStatus(DeferredLinkStatus.Pending, null);
                    return;
                case DeferredAnswer.NotFound:
                    FinishDeferred(DeferredLinkStatus.NotFound);
                    return;
                case DeferredAnswer.Unusable:
                    FinishDeferred(DeferredLinkStatus.Failed);
                    return;
            }

            if (!TryMarkDeferredDone()) return; // The other flow delivered meanwhile; its status stands.
            // "Done" is stored before the link is handed over: a crash after delivery cannot
            // deliver again. If it cannot be stored, the link is dropped rather than risk a
            // second delivery.
            if (!TryWriteDeferred(DoneKey, "1"))
            {
                SetDeferredStatus(DeferredLinkStatus.Failed, null);
                return;
            }

            switch (DeliverDeferred(response.Link, true))
            {
                case DeferredDelivery.Delivered:
                    SetDeferredStatus(DeferredLinkStatus.Delivered, null);
                    break;
                case DeferredDelivery.Superseded:
                    SetDeferredStatus(DeferredLinkStatus.Superseded, null);
                    break;
            }
        }

        private async Task<PastedLinkResult> RedeemPastedAfterAsync(Task previous, string pastedText)
        {
            try
            {
                await previous;
            }
            catch (Exception)
            {
                // The earlier call never faults; nothing to do if it somehow did.
            }

            try
            {
                return await RedeemPastedCoreAsync(pastedText);
            }
            catch (Exception)
            {
                return PastedLinkResult.Failed;
            }
        }

        private async Task<PastedLinkResult> RedeemPastedCoreAsync(string pastedText)
        {
            lock (_gate)
            {
                if (_disposed || !_deferredRequested || !_deferredPossible || _platform != "ios")
                {
                    return PastedLinkResult.Disabled;
                }
            }

            bool done;
            int attempts;
            if (!TryReadDeferredState(out done, out attempts)) return PastedLinkResult.Failed;
            if (done) return PastedLinkResult.AlreadyHandled;

            string clickId = null;
            if (pastedText != null)
            {
                string url = pastedText.Trim();
                string lookup;
                // The copied link is a stored link's: the slug rule, with or without ParameterLinks.
                if (TryParseSlugLink(url, out lookup)) clickId = DeferredLinkParsing.FindClickIdInLinkQuery(url);
            }
            if (clickId == null)
            {
                // Nothing to paste after all: probabilistic matching, if it was waiting, may start.
                ContinueMatchingIfAwaiting();
                return PastedLinkResult.NotOurs;
            }

            var body = new StringBuilder(160);
            body.Append("{\"platform\":\"ios\",\"clickId\":");
            StrybokJson.WriteString(body, clickId);
            body.Append(",\"bundleId\":");
            StrybokJson.WriteString(body, _deferredIdentifier);
            body.Append(",\"sdkVersion\":");
            StrybokJson.WriteString(body, SdkVersion);
            body.Append('}');

            DeferredResponse response = await PostDeferredAsync(RedeemPath, body.ToString(), false);
            switch (response.Answer)
            {
                case DeferredAnswer.Disposed:
                case DeferredAnswer.Transient:
                    return PastedLinkResult.Failed; // "Done" is not stored: the person can paste again.

                case DeferredAnswer.NotFound:
                    if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
                    TryWriteDeferred(DoneKey, "1");
                    SetDeferredStatus(DeferredLinkStatus.NotFound, null);
                    return PastedLinkResult.NotFound;

                case DeferredAnswer.Unusable:
                    if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
                    TryWriteDeferred(DoneKey, "1");
                    SetDeferredStatus(DeferredLinkStatus.Failed, null);
                    return PastedLinkResult.Failed;
            }

            if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
            if (!TryWriteDeferred(DoneKey, "1"))
            {
                SetDeferredStatus(DeferredLinkStatus.Failed, null);
                return PastedLinkResult.Failed;
            }
            // Pasting was the person's own action: delivered even after a normal link.
            if (DeliverDeferred(response.Link, false) != DeferredDelivery.Delivered)
            {
                return PastedLinkResult.Failed;
            }
            SetDeferredStatus(DeferredLinkStatus.Delivered, null);
            return PastedLinkResult.Delivered;
        }

        /// <summary>
        /// Asks one source, for at most 10 seconds. A source that throws, returns no task or
        /// takes too long is "unavailable for now".
        /// </summary>
        private async Task<InstallReferrerAnswer> AskSourceAsync(InstallReferrerSource source)
        {
            if (source == null) return InstallReferrerAnswer.Unavailable;

            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                try
                {
                    Task<InstallReferrerAnswer> ask = source(attempt.Token);
                    if (ask == null) return InstallReferrerAnswer.UnavailableForNow;
                    if (!ask.IsCompleted)
                    {
                        Task timer = Task.Delay(_referrerSourceTimeout, attempt.Token);
                        Task first = await Task.WhenAny(ask, timer);
                        if (first != ask)
                        {
                            Observe(ask);
                            return InstallReferrerAnswer.UnavailableForNow;
                        }
                    }
                    return await ask;
                }
                catch (Exception)
                {
                    return InstallReferrerAnswer.UnavailableForNow;
                }
                finally
                {
                    Cancel(attempt); // Stops the timer, or tells the abandoned source to give up.
                }
            }
        }

        /// <summary>
        /// One redeem or match call with the retries of resolving: timeouts, network errors and
        /// 5xx are retried with the identical body, 429 and other answers are not.
        /// </summary>
        private async Task<DeferredResponse> PostDeferredAsync(string path, string body, bool probabilistic)
        {
            var request = new StrybokHttpRequest("POST", _apiBase + path, DeferredHeaders, body, _timeout);
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                StrybokHttpResponse response;
                try
                {
                    if (attempt > 0) await DelayAsync(TimeSpan.FromTicks(_retryDelay.Ticks << (attempt - 1)));
                    if (_disposed) return new DeferredResponse(DeferredAnswer.Disposed);
                    response = await SendAsync(request);
                }
                catch (Exception)
                {
                    if (_disposed) return new DeferredResponse(DeferredAnswer.Disposed);
                    continue; // Timeout or network failure.
                }

                int status = response.StatusCode;
                if (status == 200)
                {
                    ResolvedDeepLink link = ParseDeferredResponse(response.Body, probabilistic);
                    return link == null ? new DeferredResponse(DeferredAnswer.Unusable) : new DeferredResponse(link);
                }
                if (status == 429) return new DeferredResponse(DeferredAnswer.Transient);
                if (status >= 500) continue;
                // 404 and every other 4xx are the server's refusal; anything else is not an
                // answer of this endpoint at all.
                return new DeferredResponse(status >= 400 ? DeferredAnswer.NotFound : DeferredAnswer.Unusable);
            }

            return new DeferredResponse(DeferredAnswer.Transient);
        }

        /// <summary>
        /// A usable answer: the fields of resolving, <c>deferred</c> = true, a <c>url</c> that is
        /// a link of this app and, for a match, <c>matched</c> = "probabilistic". Otherwise null.
        /// </summary>
        private ResolvedDeepLink ParseDeferredResponse(string body, bool probabilistic)
        {
            object json;
            var map = StrybokJson.TryParse(body, out json) ? json as Dictionary<string, object> : null;
            if (map == null) return null;

            string linkId = GetString(map, "linkId");
            string appId = GetString(map, "appId");
            string routePath = GetString(map, "routePath");
            string url = GetString(map, "url");
            if (linkId == null || appId == null || routePath == null || url == null) return null;
            if (!routePath.StartsWith("/", StringComparison.Ordinal) ||
                routePath.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            object deferred;
            if (!map.TryGetValue("deferred", out deferred) || !(deferred is bool) || !(bool)deferred) return null;

            // The server offers deferred links only for stored links: one slug segment, with or
            // without ParameterLinks.
            string lookup;
            if (!TryParseSlugLink(url, out lookup)) return null;
            if (probabilistic && !string.Equals(GetString(map, "matched"), "probabilistic", StringComparison.Ordinal))
            {
                return null;
            }

            object payload;
            map.TryGetValue("payload", out payload);
            object campaign;
            map.TryGetValue("campaign", out campaign);
            return new ResolvedDeepLink(
                url,
                linkId,
                appId,
                routePath,
                payload as Dictionary<string, object>,
                ParseCampaign(campaign),
                true,
                probabilistic);
        }

        /// <summary>
        /// Hands a deferred link to the normal pipeline: raised, or queued until a handler
        /// attaches. It is never the launch link and no <c>app_open</c> is reported for it.
        /// </summary>
        private DeferredDelivery DeliverDeferred(ResolvedDeepLink link, bool normalLinkWins)
        {
            Action<ResolvedDeepLink> handlers;
            lock (_gate)
            {
                if (_disposed) return DeferredDelivery.Disposed;
                if (normalLinkWins && _normalLinkAccepted) return DeferredDelivery.Superseded;
                handlers = _handlers;
                if (handlers == null)
                {
                    _pending.Add(link);
                    return DeferredDelivery.Delivered;
                }
            }

            Dispatch(() => Deliver(handlers, link));
            return DeferredDelivery.Delivered;
        }

        /// <summary>
        /// Reads "done" and the attempt counter. False when the store failed: the caller then
        /// stops, because without the state nothing may be asked or delivered.
        /// </summary>
        private bool TryReadDeferredState(out bool done, out int attempts)
        {
            done = false;
            attempts = 0;
            lock (_gate)
            {
                if (_deferredDone)
                {
                    done = true;
                    return true;
                }
            }

            try
            {
                if (!string.IsNullOrEmpty(_deferredStore.Read(DoneKey)))
                {
                    lock (_gate) _deferredDone = true;
                    done = true;
                    return true;
                }

                string text = _deferredStore.Read(AttemptsKey);
                if (!string.IsNullOrEmpty(text) &&
                    !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out attempts))
                {
                    attempts = MaxDeferredAttempts; // Not a counter of ours: stop rather than loop.
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool TryWriteDeferred(string key, string value)
        {
            try
            {
                _deferredStore.Write(key, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>False when another flow already decided to store "done".</summary>
        private bool TryMarkDeferredDone()
        {
            lock (_gate)
            {
                if (_deferredDone) return false;
                _deferredDone = true;
                _awaitingPaste = false;
                return true;
            }
        }

        /// <summary>Stores "done" and sets a final status, unless another flow finished first.</summary>
        private void FinishDeferred(DeferredLinkStatus status)
        {
            if (!TryMarkDeferredDone()) return;
            TryWriteDeferred(DoneKey, "1");
            SetDeferredStatus(status, null);
        }

        /// <param name="status">The new status.</param>
        /// <param name="onlyFrom">When set, the status only changes if it is this one now.</param>
        private void SetDeferredStatus(DeferredLinkStatus status, DeferredLinkStatus? onlyFrom)
        {
            Action<DeferredLinkStatus> handlers;
            lock (_gate)
            {
                if (_deferredStatus == status) return;
                if (onlyFrom.HasValue && _deferredStatus != onlyFrom.Value) return;
                _deferredStatus = status;
                if (_disposed) return;
                handlers = _statusHandlers;
            }

            if (handlers == null) return;
            Dispatch(() =>
            {
                if (_disposed) return;
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((Action<DeferredLinkStatus>)handler)(status);
                    }
                    catch (Exception handlerError)
                    {
                        ReportHandlerException(handlerError);
                    }
                }
            });
        }

        private bool NormalLinkAccepted()
        {
            lock (_gate) return _normalLinkAccepted;
        }

        private static string Ask(Func<string> callback)
        {
            if (callback == null) return null;
            try
            {
                return callback();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NormalizeIdentifier(string value)
        {
            string trimmed = value == null ? null : value.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        private readonly struct DeferredResponse
        {
            public DeferredResponse(DeferredAnswer answer)
            {
                Answer = answer;
                Link = null;
            }

            public DeferredResponse(ResolvedDeepLink link)
            {
                Answer = DeferredAnswer.Link;
                Link = link;
            }

            public DeferredAnswer Answer { get; }

            public ResolvedDeepLink Link { get; }
        }
    }
}
