using System;
using System.Collections.Generic;
using System.Threading;

namespace Strybok
{
    /// <summary>Platform reported with <c>app_open</c> events.</summary>
    public enum StrybokPlatform
    {
        Unknown = 0,
        IOS = 1,
        Android = 2,
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

        /// <summary>Time allowed for a single resolve attempt. Default 5 s.</summary>
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

        /// <summary>Clock used for duplicate detection; replaced in tests.</summary>
        internal Func<DateTime> Clock { get; set; }

        /// <summary>A shallow copy, so later changes to the original do not affect a running client.</summary>
        public StrybokOptions Clone()
        {
            var copy = (StrybokOptions)MemberwiseClone();
            copy.AllowedHosts = AllowedHosts == null ? new List<string>() : new List<string>(AllowedHosts);
            return copy;
        }
    }
}
