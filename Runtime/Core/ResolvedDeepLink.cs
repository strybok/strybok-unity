using System;
using System.Collections.Generic;

namespace Strybok
{
    /// <summary>
    /// A link resolved by the Strybok API.
    ///
    /// <see cref="RoutePath"/>, <see cref="Payload"/> and <see cref="Campaign"/> are plain
    /// data chosen by whoever created or shared the link. Treat them like any other
    /// untrusted input: map them onto scenes and screens your game already knows, never
    /// execute them, and use campaign values as labels only.
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
            : this(url, linkId, appId, routePath, payload, null)
        {
        }

        public ResolvedDeepLink(
            string url,
            string linkId,
            string appId,
            string routePath,
            IReadOnlyDictionary<string, object> payload,
            LinkCampaign campaign)
            : this(url, linkId, appId, routePath, payload, campaign, false, false)
        {
        }

        public ResolvedDeepLink(
            string url,
            string linkId,
            string appId,
            string routePath,
            IReadOnlyDictionary<string, object> payload,
            LinkCampaign campaign,
            bool isDeferred,
            bool isProbabilisticMatch)
        {
            Url = url;
            LinkId = linkId;
            AppId = appId;
            RoutePath = routePath;
            Payload = payload ?? EmptyPayload;
            Campaign = campaign ?? LinkCampaign.Empty;
            IsDeferred = isDeferred;
            IsProbabilisticMatch = isProbabilisticMatch;
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
        /// Campaign values of this open: the link's defaults, overridden by the <c>utm_*</c>
        /// parameters of the tapped URL. Never null; <see cref="LinkCampaign.IsEmpty"/> when
        /// there are none.
        /// </summary>
        public LinkCampaign Campaign { get; }

        /// <summary>
        /// True when the link was restored after an installation (a deferred link: the store's
        /// install referrer on Android, a pasted link or a probabilistic match on iOS) instead
        /// of opening the app directly. False for every link that arrived the normal way.
        /// </summary>
        public bool IsDeferred { get; }

        /// <summary>
        /// True only for a link the server guessed from the network address (iOS probabilistic
        /// matching, <c>AllowProbabilisticMatching</c>). <b>Check it</b>: such a link may belong
        /// to another person on the same network, so it must never by itself sign anyone in,
        /// accept an invitation, apply a personal discount or show personal content.
        /// </summary>
        public bool IsProbabilisticMatch { get; }

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
