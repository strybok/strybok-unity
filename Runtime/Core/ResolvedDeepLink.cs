using System;
using System.Collections.Generic;

namespace Strybok
{
    /// <summary>
    /// A link resolved by the Strybok API.
    ///
    /// <see cref="RoutePath"/> and <see cref="Payload"/> are plain data chosen by whoever
    /// created the link. Treat them like any other untrusted input: map them onto
    /// scenes and screens your game already knows, never execute them.
    /// </summary>
    public sealed class ResolvedDeepLink
    {
        private static readonly IReadOnlyDictionary<string, object> EmptyPayload =
            new Dictionary<string, object>();

        private string _payloadJson;

        public ResolvedDeepLink(
            string url,
            string linkId,
            string appId,
            string routePath,
            IReadOnlyDictionary<string, object> payload)
        {
            Url = url;
            LinkId = linkId;
            AppId = appId;
            RoutePath = routePath;
            Payload = payload ?? EmptyPayload;
        }

        /// <summary>The full URL that opened the app, exactly as received (with query and fragment).</summary>
        public string Url { get; }

        /// <summary><see cref="Url"/> as a <see cref="System.Uri"/>, for reading query parameters.</summary>
        public Uri Uri
        {
            get { return new Uri(Url, UriKind.Absolute); }
        }

        public string LinkId { get; }

        public string AppId { get; }

        /// <summary>In-app route, always starting with a single <c>/</c> (for example <c>/invite/abc123</c>).</summary>
        public string RoutePath { get; }

        /// <summary>
        /// Decoded JSON object attached to the link; empty when none was set.
        /// Values are <c>string</c>, <c>bool</c>, <c>long</c> (integers), <c>double</c>,
        /// <c>null</c>, <c>Dictionary&lt;string, object&gt;</c> (objects) or
        /// <c>List&lt;object&gt;</c> (arrays).
        /// </summary>
        public IReadOnlyDictionary<string, object> Payload { get; }

        /// <summary>
        /// <see cref="Payload"/> as a JSON object string, e.g. for
        /// <c>JsonUtility.FromJson&lt;MyPayload&gt;(link.PayloadJson)</c>.
        /// </summary>
        public string PayloadJson
        {
            get { return _payloadJson ?? (_payloadJson = StrybokJson.Serialize(Payload)); }
        }

        /// <summary>The payload value for <paramref name="key"/> when it is a string, otherwise null.</summary>
        public string GetString(string key)
        {
            object value;
            return key != null && Payload.TryGetValue(key, out value) ? value as string : null;
        }

        public override string ToString()
        {
            return "ResolvedDeepLink(" + RoutePath + ", linkId: " + LinkId + ")";
        }
    }
}
