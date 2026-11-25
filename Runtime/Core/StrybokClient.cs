using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok
{
    /// <summary>
    /// Core of the SDK, free of any Unity dependency: decides which URLs are ours,
    /// resolves them through the Strybok API and delivers every link exactly once.
    /// It can also create links to share (<see cref="CreateLinkAsync"/>).
    ///
    /// In a Unity project you normally use <c>StrybokSdk</c>, which creates a client
    /// with the Unity link source and transport. Use this class directly for custom
    /// set-ups and in tests.
    ///
    /// Deferred links (restoring a link that was tapped before the app was installed) are
    /// optional and off by default: see <see cref="StrybokOptions.DeferredLinks"/>. The SDK
    /// ships no store integration and never reads the pasteboard; your game supplies the
    /// install referrer (Android) or the pasted text (iOS).
    /// </summary>
    public sealed partial class StrybokClient : IDisposable
    {
        /// <summary>Version reported with analytics events.</summary>
        public const string SdkVersion = "0.3.0";

        // scheme "https", host without user info, optional port, exactly one slug
        // segment, then an optional query and/or fragment (the last group). They do not
        // decide whether a URL is ours; of the query only the campaign parameters are used.
        private static readonly Regex LinkPattern = new Regex(
            @"\A[Hh][Tt][Tt][Pp][Ss]://([^/?#@:\s\\]+)(?::([0-9]{0,5}))?(/[A-Za-z0-9_-]{1,64})((?:[?#][^\r\n]*)?)\z",
            RegexOptions.CultureInvariant);

        private static readonly IReadOnlyDictionary<string, string> ResolveHeaders =
            new Dictionary<string, string> { { "Accept", "application/json" } };

        private static readonly IReadOnlyDictionary<string, string> EventHeaders =
            new Dictionary<string, string> { { "Content-Type", "application/json" } };

        private const int MaxRoutePathLength = 512;

        private readonly object _gate = new object();
        private readonly string _apiBase;
        private readonly string _clientKey;
        private readonly IReadOnlyDictionary<string, string> _createLinkHeaders;
        private readonly HashSet<string> _allowedHosts;
        private readonly TimeSpan _timeout;
        private readonly int _maxRetries;
        private readonly TimeSpan _retryDelay;
        private readonly TimeSpan _dedupeWindow;
        private readonly bool _reportAppOpens;
        private readonly Action<StrybokException> _onError;
        private readonly Action<Exception> _onHandlerException;
        private readonly ILinkSource _source;
        private readonly IHttpTransport _transport;
        private readonly string _platform;
        private readonly SynchronizationContext _callbackContext;
        private readonly Func<DateTime> _clock;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        /// <summary>Links resolved while nobody was listening (typically the cold-start link).</summary>
        private readonly List<ResolvedDeepLink> _pending = new List<ResolvedDeepLink>();

        private Action<ResolvedDeepLink> _handlers;
        private Action<string> _urlHandler;
        private TaskCompletionSource<bool> _initialization;
        private ResolvedDeepLink _initialLink;
        private string _lastUrl;
        private DateTime _lastUrlAt;
        private volatile bool _disposed;

        public StrybokClient(StrybokOptions options)
            : this(options, false)
        {
        }

        /// <summary>
        /// <paramref name="createLinksOnly"/>: a client that is only used for
        /// <see cref="CreateLinkAsync"/> does not need <c>AllowedHosts</c>.
        /// </summary>
        internal StrybokClient(StrybokOptions options, bool createLinksOnly)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _apiBase = NormalizeApiBaseUrl(options.ApiBaseUrl);
            _clientKey = NormalizeClientKey(options.ClientKey);
            if (_clientKey != null)
            {
                // Not "Authorization": the key is publishable and must never be taken for a bearer secret.
                _createLinkHeaders = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Accept", "application/json" },
                    { "X-Strybok-Key", _clientKey },
                };
            }

            _allowedHosts = new HashSet<string>(StringComparer.Ordinal);
            if (options.AllowedHosts != null)
            {
                foreach (string host in options.AllowedHosts)
                {
                    string normalized = host == null ? string.Empty : host.Trim().ToLowerInvariant();
                    if (normalized.Length > 0) _allowedHosts.Add(normalized);
                }
            }
            if (_allowedHosts.Count == 0 && !createLinksOnly)
            {
                throw new ArgumentException("AllowedHosts must contain at least one hostname", nameof(options));
            }
            if (options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive");
            }
            if (options.MaxRetries < 0 || options.MaxRetries > 10)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must be between 0 and 10");
            }
            if (options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromMinutes(1))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RetryDelay must be between 0 and 1 minute");
            }
            if (options.LinkSource == null)
            {
                throw new ArgumentException("LinkSource is required (StrybokSdk supplies UnityLinkSource)", nameof(options));
            }
            if (options.HttpTransport == null)
            {
                throw new ArgumentException("HttpTransport is required (StrybokSdk supplies UnityWebRequestTransport)", nameof(options));
            }

            _timeout = options.Timeout;
            _maxRetries = options.MaxRetries;
            _retryDelay = options.RetryDelay;
            _dedupeWindow = options.DedupeWindow;
            _reportAppOpens = options.ReportAppOpens;
            _onError = options.OnError;
            _onHandlerException = options.OnHandlerException;
            _source = options.LinkSource;
            _transport = options.HttpTransport;
            _callbackContext = options.CallbackContext;
            _clock = options.Clock ?? (() => DateTime.UtcNow);
            switch (options.Platform)
            {
                case StrybokPlatform.IOS: _platform = "ios"; break;
                case StrybokPlatform.Android: _platform = "android"; break;
                default: _platform = "unknown"; break;
            }
            ConfigureDeferredLinks(options);
        }

        /// <summary>
        /// Resolved links, each delivered exactly once.
        ///
        /// Links that were resolved while nobody was subscribed (for example the one
        /// that cold-started the app) are kept and handed to the first subscriber:
        /// immediately, inside <c>+=</c>, when it subscribes on the callback context
        /// (Unity's main thread), otherwise posted to that context.
        /// </summary>
        public event Action<ResolvedDeepLink> LinkReceived
        {
            add
            {
                if (value == null) return;
                List<ResolvedDeepLink> queued = null;
                lock (_gate)
                {
                    if (_disposed) return;
                    _handlers += value;
                    if (_pending.Count > 0)
                    {
                        queued = new List<ResolvedDeepLink>(_pending);
                        _pending.Clear();
                    }
                }
                if (queued == null) return;
                Dispatch(() =>
                {
                    foreach (ResolvedDeepLink link in queued) Deliver(value, link);
                });
            }
            remove
            {
                lock (_gate) _handlers -= value;
            }
        }

        /// <summary>
        /// Starts listening for links and handles the one that launched the app.
        ///
        /// Completes once the launch link, if any, has been resolved (or has failed),
        /// so it is safe to await before deciding which scene to show. Calling it again
        /// returns the same task.
        /// </summary>
        public Task InitializeAsync()
        {
            TaskCompletionSource<bool> initialization;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_initialization != null) return _initialization.Task;
                initialization = _initialization = new TaskCompletionSource<bool>();
            }
            Observe(RunInitializationAsync(initialization));
            return initialization.Task;
        }

        /// <summary>
        /// The link that launched the app, if it has not been delivered through
        /// <see cref="LinkReceived"/> yet. Returns it at most once and removes it from
        /// the queue, so using both APIs never navigates twice. Null otherwise.
        /// </summary>
        public async Task<ResolvedDeepLink> GetInitialLinkAsync()
        {
            await InitializeAsync();
            lock (_gate)
            {
                ResolvedDeepLink link = _initialLink;
                _initialLink = null;
                return link != null && _pending.Remove(link) ? link : null;
            }
        }

        /// <summary>
        /// Whether <paramref name="url"/> is a Strybok link of this app: <c>https</c>, a
        /// host from <c>AllowedHosts</c>, default port, no credentials and a single slug path.
        /// </summary>
        public bool IsStrybokUrl(string url)
        {
            string lookup;
            return TryParseLink(url, out lookup);
        }

        /// <summary>
        /// Resolves <paramref name="url"/> through the API without touching
        /// <see cref="LinkReceived"/> and without reporting an app open (QR scanners, tests).
        ///
        /// Fails with a <see cref="StrybokException"/>: <see cref="UnsupportedLinkException"/>
        /// before any network call when the URL is not ours, <see cref="LinkNotFoundException"/>
        /// for unknown, disabled or expired links, and network/timeout/server errors
        /// otherwise. Fails with <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.
        /// </summary>
        public async Task<ResolvedDeepLink> ResolveAsync(string url)
        {
            ThrowIfDisposed();
            string lookup;
            if (!TryParseLink(url, out lookup))
            {
                throw new UnsupportedLinkException("Not a link for this app", url);
            }

            var request = new StrybokHttpRequest(
                "GET",
                _apiBase + "/v1/public/resolve?url=" + Uri.EscapeDataString(lookup),
                ResolveHeaders,
                null,
                _timeout);

            StrybokException lastError = null;
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                if (attempt > 0) await DelayAsync(TimeSpan.FromTicks(_retryDelay.Ticks << (attempt - 1)));
                ThrowIfDisposed();

                StrybokHttpResponse response;
                try
                {
                    response = await SendAsync(request);
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    lastError = new StrybokTimeoutException(url);
                    continue;
                }
                catch (Exception error)
                {
                    ThrowIfDisposed();
                    lastError = new StrybokNetworkException("Network error", url, error);
                    continue;
                }

                int status = response.StatusCode;
                if (status == 200) return ParseResolveResponse(url, response.Body);
                if (status == 404) throw new LinkNotFoundException(url);
                var serverError = new StrybokServerException("Unexpected status " + status, url, status);
                if (status < 500) throw serverError; // Client errors will not improve.
                lastError = serverError;
            }
            throw lastError;
        }

        /// <summary>
        /// Asks the service for a short link to a screen of this app, for a "Share" button.
        /// Hand <see cref="CreatedLink.Url"/> to the share sheet.
        ///
        /// Needs <see cref="StrybokOptions.ClientKey"/>, the publishable key (<c>pk_…</c>) from
        /// the dashboard, where creating links from the app must be switched on and the allowed
        /// route patterns listed. It only runs when you call it: it works before and without
        /// <see cref="InitializeAsync"/>, never raises <see cref="LinkReceived"/> and never
        /// reports to <c>OnError</c>.
        ///
        /// Fails with a <see cref="StrybokException"/>:
        /// <see cref="StrybokUnauthorizedException"/> (no key configured: no network call; or the
        /// key was refused), <see cref="StrybokValidationException"/> (bad route: no network
        /// call; or rejected by the service), <see cref="RouteNotAllowedException"/>,
        /// <see cref="StrybokRateLimitedException"/>, and
        /// <see cref="StrybokNetworkException"/> / <see cref="StrybokTimeoutException"/> /
        /// <see cref="StrybokServerException"/> after the configured retries. The last four are
        /// temporary: let the user try again later. Errors never contain the client key or the
        /// payload. Fails with <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.
        ///
        /// Asking again for the same content returns the same link, so repeated taps on "Share"
        /// do not pile up links. Do not put personal data in the route or the payload: whoever
        /// receives the link can read them.
        /// </summary>
        /// <param name="routePath">In-app route the link opens: starts with one <c>/</c>, at most 512 characters.</param>
        /// <param name="payload">
        /// Optional JSON object delivered with the link. Values may be <c>string</c>, <c>bool</c>,
        /// numbers, <c>null</c>, <c>Dictionary&lt;string, object&gt;</c> and lists of these, as
        /// written by <see cref="StrybokJson.Serialize"/>.
        /// </param>
        /// <param name="title">Optional title of the link preview.</param>
        /// <param name="description">Optional description of the link preview.</param>
        /// <param name="imageUrl">Optional image of the link preview; a public <c>https</c> URL.</param>
        public Task<CreatedLink> CreateLinkAsync(
            string routePath,
            IReadOnlyDictionary<string, object> payload = null,
            string title = null,
            string description = null,
            string imageUrl = null)
        {
            Func<string> payloadJson = null;
            if (payload != null) payloadJson = () => SerializePayload(payload);
            return CreateLinkCoreAsync(routePath, payloadJson, title, description, imageUrl);
        }

        /// <summary>
        /// Same as <see cref="CreateLinkAsync"/> with the payload given as JSON text, e.g. from
        /// <c>JsonUtility.ToJson(myPayload)</c>. The text must be a JSON object; an array, a
        /// plain value or broken JSON fails with <see cref="StrybokValidationException"/> before
        /// any network call. Null means no payload.
        /// </summary>
        public Task<CreatedLink> CreateLinkWithPayloadJsonAsync(
            string routePath,
            string payloadJson,
            string title = null,
            string description = null,
            string imageUrl = null)
        {
            Func<string> payload = null;
            if (payloadJson != null) payload = () => CheckPayloadJson(payloadJson);
            return CreateLinkCoreAsync(routePath, payload, title, description, imageUrl);
        }

        /// <summary>
        /// Stops listening, aborts requests in flight and drops queued links. Nothing is
        /// delivered afterwards. The instance cannot be reused. Safe to call twice.
        /// </summary>
        public void Dispose()
        {
            Action<string> urlHandler;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pending.Clear();
                _initialLink = null;
                _handlers = null;
                _statusHandlers = null;
                urlHandler = _urlHandler;
                _urlHandler = null;
            }
            if (urlHandler != null) Unsubscribe(urlHandler);
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception)
            {
                // A transport reacted badly to cancellation; nothing left to do.
            }
        }

        // ---------------------------------------------------------------------

        private async Task RunInitializationAsync(TaskCompletionSource<bool> initialization)
        {
            try
            {
                // Listen first so a link arriving during start-up is not missed.
                Action<string> urlHandler = url => Observe(HandleAsync(url, false));
                bool disposed;
                lock (_gate)
                {
                    disposed = _disposed;
                    if (!disposed) _urlHandler = urlHandler;
                }
                if (disposed) return;
                _source.UrlReceived += urlHandler;
                if (_disposed) Unsubscribe(urlHandler); // Disposed while subscribing.

                string initial = null;
                try
                {
                    Task<string> pending = _source.GetInitialLinkAsync();
                    if (pending != null) initial = await pending;
                }
                catch (Exception)
                {
                    initial = null; // The platform could not tell us; treat as "no launch link".
                }
                if (!string.IsNullOrEmpty(initial)) await HandleAsync(initial, true);
            }
            catch (Exception)
            {
                // Starting must never throw into the app.
            }
            finally
            {
                initialization.TrySetResult(true);
                try
                {
                    // After everything else, and never awaited: the deferred-link check.
                    OnStartCompleted();
                }
                catch (Exception)
                {
                    // Starting must never throw into the app.
                }
            }
        }

        private async Task HandleAsync(string url, bool isInitial)
        {
            try
            {
                if (_disposed || !IsStrybokUrl(url)) return;
                // From here on a deferred link would be a second navigation: a normal link wins.
                lock (_gate) _normalLinkAccepted = true;
                if (IsDuplicate(url)) return;

                ResolvedDeepLink link;
                try
                {
                    link = await ResolveAsync(url);
                }
                catch (StrybokException error)
                {
                    ReportError(error);
                    return;
                }

                Action<ResolvedDeepLink> handlers;
                lock (_gate)
                {
                    if (_disposed) return; // Disposed while the request was in flight.
                    if (isInitial) _initialLink = link;
                    handlers = _handlers;
                    if (handlers == null) _pending.Add(link);
                }
                if (handlers != null) Dispatch(() => Deliver(handlers, link));
                if (_reportAppOpens) Observe(ReportAppOpenAsync(link));
            }
            catch (Exception)
            {
                // Disposed mid-flight, or something unexpected: a link must never crash the app.
            }
        }

        /// <summary>
        /// Whether <paramref name="url"/> is ours. <paramref name="lookup"/> is then what is
        /// sent to the API: <c>https://{lower-cased host}/{slug}</c> plus the campaign query.
        /// The rest of the query and the fragment are never part of it, they may hold
        /// private data.
        /// </summary>
        internal bool TryParseLink(string url, out string lookup)
        {
            lookup = null;
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            Match match = LinkPattern.Match(url);
            if (!match.Success) return false;

            string port = match.Groups[2].Value;
            if (port.Length > 0 && int.Parse(port) != 443) return false;

            string candidate = match.Groups[1].Value.ToLowerInvariant();
            if (!_allowedHosts.Contains(candidate)) return false;

            string rawQuery = string.Empty;
            string tail = match.Groups[4].Value;
            if (tail.Length > 0 && tail[0] == '?')
            {
                int fragment = tail.IndexOf('#');
                rawQuery = fragment < 0 ? tail.Substring(1) : tail.Substring(1, fragment - 1);
            }
            lookup = "https://" + candidate + match.Groups[3].Value + CampaignQuery.Build(rawQuery);
            return true;
        }

        /// <summary>
        /// The OS can hand over the same tap twice, e.g. as the launch URL and again
        /// as a regular link.
        /// </summary>
        private bool IsDuplicate(string url)
        {
            DateTime now = _clock();
            lock (_gate)
            {
                bool duplicate = false;
                if (_lastUrl != null && string.Equals(_lastUrl, url, StringComparison.Ordinal))
                {
                    TimeSpan elapsed = now - _lastUrlAt;
                    duplicate = elapsed >= TimeSpan.Zero && elapsed < _dedupeWindow;
                }
                _lastUrl = url;
                _lastUrlAt = now;
                return duplicate;
            }
        }

        private static ResolvedDeepLink ParseResolveResponse(string url, string body)
        {
            object json;
            var map = StrybokJson.TryParse(body, out json) ? json as Dictionary<string, object> : null;
            if (map != null)
            {
                string linkId = GetString(map, "linkId");
                string appId = GetString(map, "appId");
                string routePath = GetString(map, "routePath");
                if (linkId != null && appId != null && routePath != null &&
                    routePath.StartsWith("/", StringComparison.Ordinal) &&
                    !routePath.StartsWith("//", StringComparison.Ordinal))
                {
                    object payload;
                    map.TryGetValue("payload", out payload);
                    object campaign;
                    map.TryGetValue("campaign", out campaign);
                    return new ResolvedDeepLink(
                        url, linkId, appId, routePath, payload as Dictionary<string, object>, ParseCampaign(campaign));
                }
            }
            throw new StrybokServerException("Malformed resolve response", url, 200);
        }

        /// <summary>Keeps the five string fields; anything else, or no object at all, is not an error.</summary>
        private static LinkCampaign ParseCampaign(object json)
        {
            var map = json as Dictionary<string, object>;
            if (map == null) return LinkCampaign.Empty;
            var campaign = new LinkCampaign(
                GetString(map, "source"),
                GetString(map, "medium"),
                GetString(map, "campaign"),
                GetString(map, "term"),
                GetString(map, "content"));
            return campaign.IsEmpty ? LinkCampaign.Empty : campaign;
        }

        /// <summary>
        /// Appends <c>,"campaign":{…}</c> with what resolve returned, unchanged; nothing when
        /// there are no values.
        /// </summary>
        private static void AppendCampaign(StringBuilder body, LinkCampaign campaign)
        {
            if (campaign == null || campaign.IsEmpty) return;
            body.Append(",\"campaign\":{");
            bool first = true;
            AppendCampaignField(body, "source", campaign.Source, ref first);
            AppendCampaignField(body, "medium", campaign.Medium, ref first);
            AppendCampaignField(body, "campaign", campaign.Campaign, ref first);
            AppendCampaignField(body, "term", campaign.Term, ref first);
            AppendCampaignField(body, "content", campaign.Content, ref first);
            body.Append('}');
        }

        private static void AppendCampaignField(StringBuilder body, string name, string value, ref bool first)
        {
            if (value == null) return;
            if (!first) body.Append(',');
            first = false;
            body.Append('"').Append(name).Append("\":");
            StrybokJson.WriteString(body, value);
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) ? value as string : null;
        }

        // ---------------------------------------------------------------------
        // Creating links
        // ---------------------------------------------------------------------

        /// <param name="payloadJson">
        /// Null for no payload; otherwise returns the payload as JSON object text or throws
        /// <see cref="StrybokValidationException"/>. Called after the key and the route were checked.
        /// </param>
        private async Task<CreatedLink> CreateLinkCoreAsync(
            string routePath,
            Func<string> payloadJson,
            string title,
            string description,
            string imageUrl)
        {
            ThrowIfDisposed();

            // The checks of the contract, in its order, before any network call.
            if (_clientKey == null)
            {
                throw new StrybokUnauthorizedException("No client key is configured (StrybokOptions.ClientKey)");
            }
            if (routePath == null || !routePath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new StrybokValidationException("routePath must start with \"/\"");
            }
            if (routePath.StartsWith("//", StringComparison.Ordinal))
            {
                throw new StrybokValidationException("routePath must not start with \"//\"");
            }
            if (routePath.Length > MaxRoutePathLength)
            {
                throw new StrybokValidationException("routePath must be at most 512 characters");
            }

            // Built once: every retry sends exactly the same body. Arguments that were not
            // given are left out (the service treats null like "omitted", but we never send it).
            var body = new StringBuilder(256);
            body.Append("{\"routePath\":");
            StrybokJson.WriteString(body, routePath);
            if (payloadJson != null)
            {
                body.Append(",\"payload\":").Append(payloadJson());
            }
            AppendOptional(body, "title", title);
            AppendOptional(body, "description", description);
            AppendOptional(body, "imageUrl", imageUrl);
            body.Append('}');

            var request = new StrybokHttpRequest(
                "POST", _apiBase + "/v1/public/links", _createLinkHeaders, body.ToString(), _timeout);

            // Creation is idempotent on the service (same content, same link), so a retry after
            // a lost answer cannot make a second link.
            StrybokException lastError = null;
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                if (attempt > 0) await DelayAsync(TimeSpan.FromTicks(_retryDelay.Ticks << (attempt - 1)));
                ThrowIfDisposed();

                StrybokHttpResponse response;
                try
                {
                    response = await SendAsync(request);
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    lastError = new StrybokTimeoutException("The create-link request timed out", null);
                    continue;
                }
                catch (Exception error)
                {
                    ThrowIfDisposed();
                    lastError = new StrybokNetworkException("Network error", null, error);
                    continue;
                }

                int status = response.StatusCode;
                // 200: an identical link already existed. 201: a new one was made.
                if (status == 200 || status == 201) return ParseCreateLinkResponse(status, response.Body);
                if (status == 401)
                {
                    throw new StrybokUnauthorizedException(
                        "The client key was not accepted, or creating links from the app is switched off");
                }
                if (status == 403) throw new RouteNotAllowedException(routePath);
                if (status == 429) throw new StrybokRateLimitedException(ParseRetryAfter(response.GetHeader("Retry-After")));
                if (status == 400 || status == 413 || status == 422)
                {
                    throw ParseValidationResponse(status, response.Body);
                }
                var serverError = new StrybokServerException("Unexpected status " + status, null, status);
                if (status < 500) throw serverError; // Client errors will not improve.
                lastError = serverError;
            }
            throw lastError;
        }

        private static void AppendOptional(StringBuilder body, string name, string value)
        {
            if (value == null) return;
            body.Append(",\"").Append(name).Append("\":");
            StrybokJson.WriteString(body, value);
        }

        private static string SerializePayload(IReadOnlyDictionary<string, object> payload)
        {
            try
            {
                return StrybokJson.Serialize(payload);
            }
            catch (Exception)
            {
                // Nested too deeply, or a collection that failed while being read. The
                // cause is not attached: it could quote parts of the payload.
                throw new StrybokValidationException("payload cannot be written as JSON");
            }
        }

        private static string CheckPayloadJson(string payloadJson)
        {
            object parsed;
            if (!StrybokJson.TryParse(payloadJson, out parsed) || !(parsed is Dictionary<string, object>))
            {
                throw new StrybokValidationException("payload must be a JSON object");
            }
            // Sent as written (numbers keep their exact text), without the byte order mark
            // the parser tolerates.
            return payloadJson.Trim('\uFEFF', ' ', '\t', '\n', '\r');
        }

        private static CreatedLink ParseCreateLinkResponse(int status, string body)
        {
            object json;
            var map = StrybokJson.TryParse(body, out json) ? json as Dictionary<string, object> : null;
            if (map != null)
            {
                string linkId = GetString(map, "linkId");
                string slug = GetString(map, "slug");
                string routePath = GetString(map, "routePath");
                string url = GetString(map, "url");
                if (linkId != null && slug != null && routePath != null && url != null &&
                    url.StartsWith("https://", StringComparison.Ordinal))
                {
                    return new CreatedLink(url, slug, routePath, linkId);
                }
            }
            throw new StrybokServerException("Malformed create-link response", null, status);
        }

        /// <summary>The service's <c>error.message</c> and <c>error.details.issues</c>, when the body has them.</summary>
        private static StrybokValidationException ParseValidationResponse(int status, string body)
        {
            string message = null;
            List<StrybokValidationIssue> issues = null;

            object json;
            var root = StrybokJson.TryParse(body, out json) ? json as Dictionary<string, object> : null;
            object value;
            var error = root != null && root.TryGetValue("error", out value) ? value as Dictionary<string, object> : null;
            if (error != null)
            {
                message = GetString(error, "message");
                var details = error.TryGetValue("details", out value) ? value as Dictionary<string, object> : null;
                var list = details != null && details.TryGetValue("issues", out value) ? value as List<object> : null;
                if (list != null)
                {
                    issues = new List<StrybokValidationIssue>();
                    foreach (object item in list)
                    {
                        var issue = item as Dictionary<string, object>;
                        if (issue == null) continue;
                        string path = GetString(issue, "path");
                        string text = GetString(issue, "message");
                        if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(text)) continue;
                        issues.Add(new StrybokValidationIssue(path, text));
                    }
                }
            }

            if (string.IsNullOrEmpty(message)) message = "The server rejected the link (status " + status + ")";
            return new StrybokValidationException(message, status, issues);
        }

        /// <summary>Whole seconds from a <c>Retry-After</c> header; null for anything else (an HTTP date, for example).</summary>
        internal static TimeSpan? ParseRetryAfter(string header)
        {
            if (header == null) return null;
            string text = header.Trim();
            if (text.Length == 0) return null;
            long seconds = 0;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return null;
                if (seconds <= int.MaxValue) seconds = seconds * 10 + (c - '0');
            }
            return TimeSpan.FromSeconds(Math.Min(seconds, int.MaxValue));
        }

        /// <summary>
        /// The key without surrounding whitespace; null when it is missing or cannot be sent as a
        /// header value (spaces, control or non-ASCII characters): such a key is never valid.
        /// </summary>
        private static string NormalizeClientKey(string clientKey)
        {
            if (clientKey == null) return null;
            string key = clientKey.Trim();
            if (key.Length == 0) return null;
            foreach (char c in key)
            {
                if (c <= 0x20 || c >= 0x7F) return null;
            }
            return key;
        }

        /// <summary>
        /// Best effort and never awaited by navigation. This is a self-reported metric
        /// (an app can be modified), separate from server-side fallback visits.
        /// </summary>
        private async Task ReportAppOpenAsync(ResolvedDeepLink link)
        {
            try
            {
                var body = new StringBuilder(160);
                body.Append("{\"eventId\":");
                StrybokJson.WriteString(body, NewUuidV4());
                body.Append(",\"linkId\":");
                StrybokJson.WriteString(body, link.LinkId);
                body.Append(",\"type\":\"app_open\",\"platform\":");
                StrybokJson.WriteString(body, _platform);
                body.Append(",\"sdkVersion\":");
                StrybokJson.WriteString(body, SdkVersion);
                AppendCampaign(body, link.Campaign);
                body.Append('}');

                await SendAsync(new StrybokHttpRequest(
                    "POST", _apiBase + "/v1/public/events", EventHeaders, body.ToString(), _timeout));
            }
            catch (Exception)
            {
                // Analytics must never affect the app.
            }
        }

        /// <summary>
        /// One HTTP call limited to the configured timeout. Throws
        /// <see cref="TimeoutException"/> when there was no answer in time and
        /// <see cref="ObjectDisposedException"/> when the client was disposed meanwhile.
        /// </summary>
        private async Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                Task<StrybokHttpResponse> send = _transport.SendAsync(request, cancellation.Token);
                if (send == null) throw new InvalidOperationException("The transport returned no task");

                Task timer = Task.Delay(_timeout, cancellation.Token);
                Task first = await Task.WhenAny(send, timer);
                if (first == send)
                {
                    Cancel(cancellation); // Stops the timer.
                    StrybokHttpResponse response = await send;
                    if (response == null) throw new InvalidOperationException("The transport returned no response");
                    return response;
                }

                Observe(send);
                bool disposed = _disposed;
                Cancel(cancellation); // Tells the transport to abort.
                if (disposed) throw new ObjectDisposedException(nameof(StrybokClient));
                throw new TimeoutException();
            }
        }

        private async Task DelayAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ObjectDisposedException(nameof(StrybokClient));
            }
        }

        private void ReportError(StrybokException error)
        {
            if (_disposed || _onError == null) return;
            Dispatch(() =>
            {
                if (_disposed) return;
                try
                {
                    _onError(error);
                }
                catch (Exception handlerError)
                {
                    ReportHandlerException(handlerError);
                }
            });
        }

        private void Deliver(Action<ResolvedDeepLink> handlers, ResolvedDeepLink link)
        {
            if (_disposed) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ResolvedDeepLink>)handler)(link);
                }
                catch (Exception handlerError)
                {
                    ReportHandlerException(handlerError);
                }
            }
        }

        private void ReportHandlerException(Exception error)
        {
            try
            {
                if (_onHandlerException != null) _onHandlerException(error);
            }
            catch (Exception)
            {
                // Nothing sensible left to do.
            }
        }

        /// <summary>Runs <paramref name="action"/> on the callback context, directly when already there.</summary>
        private void Dispatch(Action action)
        {
            SynchronizationContext context = _callbackContext;
            if (context == null || SynchronizationContext.Current == context)
            {
                action();
            }
            else
            {
                context.Post(_ => action(), null);
            }
        }

        private void Unsubscribe(Action<string> urlHandler)
        {
            try
            {
                _source.UrlReceived -= urlHandler;
            }
            catch (Exception)
            {
                // A custom source failed to unsubscribe; we ignore its links from now on anyway.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StrybokClient));
        }

        private static void Cancel(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception)
            {
                // A cancellation callback of the transport threw; the call is over either way.
            }
        }

        /// <summary>Marks a task we do not await as observed, so its failure is never "unobserved".</summary>
        private static void Observe(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                t => { Exception ignored = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static string NormalizeApiBaseUrl(string apiBaseUrl)
        {
            Uri parsed;
            if (string.IsNullOrWhiteSpace(apiBaseUrl) ||
                !Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out parsed) ||
                (parsed.Scheme != "https" && parsed.Scheme != "http") ||
                parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            {
                throw new ArgumentException(
                    "ApiBaseUrl must be an absolute http(s) URL without query, e.g. https://api.strybok.com",
                    "options");
            }
            return apiBaseUrl.Trim().TrimEnd('/');
        }

        /// <summary>Random RFC 4122 version 4 UUID; identifies one event for de-duplication only.</summary>
        internal static string NewUuidV4()
        {
            var bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);

            const string Hex = "0123456789abcdef";
            var text = new StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10) text.Append('-');
                text.Append(Hex[bytes[i] >> 4]).Append(Hex[bytes[i] & 0x0f]);
            }
            return text.ToString();
        }
    }
}
