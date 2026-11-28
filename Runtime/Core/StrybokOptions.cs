using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok
{
    /// <summary>Platform reported with <c>app_open</c> events.</summary>
    public enum StrybokPlatform
    {
        Unknown = 0,
        IOS = 1,
        Android = 2,
    }

    /// <summary>
    /// The <see cref="StrybokOptions.ParameterLinks"/> option: links without an API call (path
    /// links and Firebase long links). Copy it from <c>parameterLinks</c> of the integration guide
    /// (<c>GET /v1/apps/:appId/integration</c>, shown on the dashboard's setup screen).
    /// </summary>
    public sealed class ParameterLinksOptions
    {
        /// <summary>No payload keys yet; add them to <see cref="PayloadKeys"/>.</summary>
        public ParameterLinksOptions()
        {
        }

        /// <summary>With these payload keys, in this order.</summary>
        public ParameterLinksOptions(params string[] payloadKeys)
        {
            PayloadKeys = payloadKeys == null ? null : new List<string>(payloadKeys);
        }

        /// <summary>
        /// The query keys whose values are sent with a path link and become its payload, in the
        /// owner's order (<c>parameterLinks.payloadKeys</c> of the integration guide). At most 20
        /// distinct keys of 1–40 characters of <c>A–Z a–z 0–9 _ . -</c>; not <c>utm_…</c> (in any
        /// case), <c>st</c>, <c>sd</c>, <c>si</c> or <c>strybok_click</c>. An empty list is valid:
        /// path links then carry no payload. Anything else is rejected when the client is created.
        ///
        /// The keys are compiled into the game: a key the owner adds in the dashboard reaches
        /// players with the next release. Never configure a key that carries personal data.
        /// </summary>
        public IList<string> PayloadKeys { get; set; } = new List<string>();
    }

    /// <summary>Configuration of a <see cref="StrybokClient"/>.</summary>
    public sealed class StrybokOptions
    {
        /// <summary>
        /// Required. Base URL of the Strybok API, e.g. <c>https://api.strybok.com</c>.
        /// A path prefix is preserved.
        /// </summary>
        public string ApiBaseUrl { get; set; }

        /// <summary>
        /// Required. Link hostnames of this app, e.g. <c>my-app.strybok.link</c>; compared
        /// lower-case. Links on any other host are ignored.
        /// </summary>
        public ICollection<string> AllowedHosts { get; set; } = new List<string>();

        /// <summary>Time allowed for a single resolve or create-link attempt. Default 5 s.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Extra attempts after a timeout, network error or 5xx. Resolving is an idempotent
        /// GET, so retrying is safe. 404 and other 4xx are never retried. Default 2.
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Pause before the first retry; doubles for each further retry. Default 400 ms.</summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// An identical URL received again within this window is treated as a duplicate
        /// delivery of the same tap and dropped. Default 2 s.
        /// </summary>
        public TimeSpan DedupeWindow { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Whether to send a best-effort <c>app_open</c> event after a link was resolved. Default true.</summary>
        public bool ReportAppOpens { get; set; } = true;

        /// <summary>
        /// Receives errors for links that arrived from the OS but could not be resolved.
        /// Links that are simply not ours are ignored without an error.
        /// </summary>
        public Action<StrybokException> OnError { get; set; }

        /// <summary>
        /// Publishable client key of the application (<c>pk_…</c>) from the dashboard
        /// ("Links from the app"). Only needed for <see cref="StrybokClient.CreateLinkAsync"/>.
        /// It is safe to ship inside the app: it can only create links for the route patterns
        /// listed in the dashboard. Never put a secret API key (<c>lb_…</c>) here.
        /// </summary>
        public string ClientKey { get; set; }

        /// <summary>
        /// Links without an API call: deliver path links (<c>https://my-app.strybok.link/product/42?id=7</c>)
        /// and Firebase long links (<c>https://my-app.strybok.link/?link=…</c>) and send the values of
        /// these payload keys with them. Default: null, the option is not set, and only links with
        /// a single slug path are ours, exactly as in 0.3.0.
        ///
        /// Set it to <c>parameterLinks</c> of the integration guide when the owner switched path
        /// links or Firebase long links on. The server decides which routes, keys and web domains are
        /// allowed, and the delivered link carries the route and payload of its answer, never the
        /// tapped path. With the option any address of the link hosts that passes the route rules
        /// is resolved, so <see cref="OnError"/> gets <see cref="LinkNotFoundException"/> for one
        /// that is no link. Deferred links keep the slug rule.
        /// </summary>
        public ParameterLinksOptions ParameterLinks { get; set; }

        /// <summary>
        /// Receives exceptions thrown by your own <c>LinkReceived</c> / <c>OnError</c>
        /// handlers, so one faulty handler cannot stop delivery to the others.
        /// <c>StrybokSdk</c> logs them with <c>Debug.LogException</c> by default.
        /// </summary>
        public Action<Exception> OnHandlerException { get; set; }

        /// <summary>Source of incoming URLs. Required by <see cref="StrybokClient"/>; <c>StrybokSdk</c> defaults to <c>UnityLinkSource</c>.</summary>
        public ILinkSource LinkSource { get; set; }

        /// <summary>HTTP transport. Required by <see cref="StrybokClient"/>; <c>StrybokSdk</c> defaults to <c>UnityWebRequestTransport</c>.</summary>
        public IHttpTransport HttpTransport { get; set; }

        /// <summary>Platform reported with <c>app_open</c> events. <c>StrybokSdk</c> detects it from <c>Application.platform</c>.</summary>
        public StrybokPlatform Platform { get; set; } = StrybokPlatform.Unknown;

        /// <summary>
        /// When set, <c>LinkReceived</c> and <c>OnError</c> handlers are posted to this
        /// context (called directly when already on it). <c>StrybokSdk</c> uses Unity's
        /// main-thread context. When null, handlers run wherever the work completed.
        /// </summary>
        public SynchronizationContext CallbackContext { get; set; }

        /// <summary>
        /// Restore a link that was tapped before the app was installed. Default: off. Can also
        /// be switched on later with <c>EnableDeferredLinks()</c>, for example after a consent
        /// screen.
        ///
        /// Needs <see cref="DeferredLinkStore"/> and, on Android,
        /// <see cref="AndroidPackageName"/> and <see cref="InstallReferrerSources"/>; on iOS,
        /// <see cref="IosBundleId"/> (<c>StrybokSdk</c> fills the store and the identifiers).
        /// Without the store or the identifier nothing happens and the status stays
        /// <see cref="DeferredLinkStatus.Disabled"/>. The owner must also have switched the
        /// feature on for the application in the dashboard.
        ///
        /// Android: on the first launch the SDK asks the sources for the store's install
        /// referrer and exchanges the click id found in it for the link. iOS: only
        /// <c>RedeemPastedLinkAsync</c> and, with <see cref="AllowProbabilisticMatching"/>,
        /// matching. The link arrives through <c>LinkReceived</c> with
        /// <see cref="ResolvedDeepLink.IsDeferred"/> set.
        /// </summary>
        public bool DeferredLinks { get; set; }

        /// <summary>
        /// iOS only, and only together with <see cref="DeferredLinks"/>: let the server guess
        /// the link from the device's network address, device family and language. Default: off.
        ///
        /// <b>This is fingerprinting in Apple's terms and is forbidden by the App Store Review
        /// Guidelines and the Apple Developer Program License Agreement, with or without the
        /// user's consent.</b> The App Review and legal risk is yours. A match is a guess: on a
        /// shared network it can be another person's link, so check
        /// <see cref="ResolvedDeepLink.IsProbabilisticMatch"/>. Needs <see cref="DeviceFamily"/>
        /// and <see cref="Locale"/>, the owner's setting in the dashboard, and an opt-in on each link.
        /// </summary>
        public bool AllowProbabilisticMatching { get; set; }

        /// <summary>
        /// Android: the referrer sources, asked in order until one returns a referrer with a
        /// click id of ours. The SDK ships none: each is a function of your game around the
        /// store's install referrer API. Empty means the check ends at once with
        /// <see cref="DeferredLinkStatus.Unavailable"/>.
        /// </summary>
        public IList<InstallReferrerSource> InstallReferrerSources { get; set; } = new List<InstallReferrerSource>();

        /// <summary>
        /// Android: the app's own application ID. Required for deferred links on Android;
        /// <c>StrybokSdk</c> defaults to <c>Application.identifier</c>.
        /// </summary>
        public string AndroidPackageName { get; set; }

        /// <summary>
        /// iOS: the app's own bundle identifier. Required for deferred links on iOS;
        /// <c>StrybokSdk</c> defaults to <c>Application.identifier</c>.
        /// </summary>
        public string IosBundleId { get; set; }

        /// <summary>
        /// iOS probabilistic matching: returns <c>"iphone"</c>, <c>"ipad"</c> or <c>"ipod"</c>.
        /// Anything else (or no callback) ends matching with
        /// <see cref="DeferredLinkStatus.Unavailable"/>. <c>StrybokSdk</c> derives it from
        /// <c>SystemInfo.deviceModel</c>.
        /// </summary>
        public Func<string> DeviceFamily { get; set; }

        /// <summary>
        /// iOS probabilistic matching: returns the device's first preferred language as a
        /// BCP 47 tag, e.g. <c>en-GB</c>. Null (or no callback) ends matching with
        /// <see cref="DeferredLinkStatus.Unavailable"/>. <c>StrybokSdk</c> derives a language
        /// code from <c>Application.systemLanguage</c>.
        /// </summary>
        public Func<string> Locale { get; set; }

        /// <summary>
        /// iOS probabilistic matching, optional: answers whether the pasteboard probably holds
        /// a web URL, <b>without reading it</b> (<c>UIPasteboard.general.detectPatterns</c> in a
        /// native plugin of your own). When it answers true, matching waits for
        /// <c>RedeemPastedLinkAsync</c> or <c>SkipPastedLink</c>. The SDK never touches the
        /// pasteboard.
        /// </summary>
        public Func<CancellationToken, Task<bool>> PasteboardMayContainLink { get; set; }

        /// <summary>
        /// Where the check keeps its attempt counter and its "done" flag. Required for
        /// deferred links; <c>StrybokSdk</c> defaults to <c>PlayerPrefs</c>.
        /// </summary>
        public IDeferredLinkStore DeferredLinkStore { get; set; }

        /// <summary>How long one referrer source may take; shortened in tests.</summary>
        internal TimeSpan InstallReferrerSourceTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Clock used for duplicate detection; replaced in tests.</summary>
        internal Func<DateTime> Clock { get; set; }

        /// <summary>A shallow copy, so later changes to the original do not affect a running client.</summary>
        public StrybokOptions Clone()
        {
            var copy = (StrybokOptions)MemberwiseClone();
            copy.AllowedHosts = AllowedHosts == null ? new List<string>() : new List<string>(AllowedHosts);
            copy.InstallReferrerSources = InstallReferrerSources == null
                ? new List<InstallReferrerSource>()
                : new List<InstallReferrerSource>(InstallReferrerSources);
            if (ParameterLinks != null)
            {
                copy.ParameterLinks = new ParameterLinksOptions
                {
                    PayloadKeys = ParameterLinks.PayloadKeys == null ? null : new List<string>(ParameterLinks.PayloadKeys),
                };
            }
            return copy;
        }
    }
}
