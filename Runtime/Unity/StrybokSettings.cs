using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strybok
{
    /// <summary>
    /// Project-wide Strybok configuration. Create it with
    /// <c>Tools &gt; Strybok &gt; Settings</c>; it is stored as
    /// <c>Assets/Resources/StrybokSettings.asset</c> so that it is available both at
    /// runtime (<see cref="Load"/>) and to the build post-processors that add the
    /// iOS Associated Domains entitlement and the Android intent filter.
    /// </summary>
    public sealed class StrybokSettings : ScriptableObject
    {
        /// <summary>Name of the asset inside a <c>Resources</c> folder.</summary>
        public const string ResourceName = "StrybokSettings";

        [Tooltip("Base URL of the Strybok API.")]
        [SerializeField] private string apiBaseUrl = "https://api.strybok.com";

        [Tooltip("Link hostnames of this app, e.g. my-app.strybok.link. No scheme, no path.")]
        [SerializeField] private string[] linkHosts = new string[0];

        [Tooltip("Send a best-effort app_open event after a link was resolved.")]
        [SerializeField] private bool reportAppOpens = true;

        [Tooltip("Seconds allowed for one resolve attempt.")]
        [Min(0.1f)]
        [SerializeField] private float timeoutSeconds = 5f;

        [Tooltip("Extra attempts after a timeout, network error or 5xx.")]
        [Range(0, 10)]
        [SerializeField] private int maxRetries = 2;

        [Header("Link creation")]
        [Tooltip("Publishable client key (pk_...) from the dashboard, \"Links from the app\". Only needed for StrybokSdk.CreateLinkAsync. Never put a secret API key (lb_...) here.")]
        [SerializeField] private string clientKey = "";

        [Header("Links without an API call")]
        [Tooltip("Deliver path links (https://<host>/product/42?id=7) and Firebase long links (https://<host>/?link=...). Off by default: then only links with a single slug path are ours. Switch it on when the integration guide (dashboard setup screen) shows parameterLinks.")]
        [SerializeField] private bool parameterLinks = false;

        [Tooltip("parameterLinks.payloadKeys of the integration guide, in its order: the query keys sent with a path link. Up to 20 keys of A-Z a-z 0-9 _ . -; never a key that carries personal data. Only used with Parameter Links on.")]
        [SerializeField] private string[] payloadKeys = new string[0];

        [Header("Deferred links")]
        [Tooltip("Restore a link that was tapped before the app was installed. Off by default. Android: your code must also supply an install referrer source in the options; iOS: your code must pass the pasted link to StrybokSdk.RedeemPastedLinkAsync. Leave it off and call StrybokSdk.EnableDeferredLinks() later if you ask for consent first.")]
        [SerializeField] private bool deferredLinks = false;

        [Tooltip("iOS only, together with Deferred Links: let the server guess the link from the network address. FORBIDDEN BY APPLE'S RULES (fingerprinting); the App Review and legal risk is yours, and a guess can be another person's link. Off by default.")]
        [SerializeField] private bool allowProbabilisticMatching = false;

        [Header("Build")]
        [Tooltip("iOS: add 'applinks:<host>' for every link host to the Associated Domains entitlement of the generated Xcode project.")]
        [SerializeField] private bool addIosAssociatedDomains = true;

        [Tooltip("Android: add a verified (autoVerify) https intent filter for every link host to the launcher activity of the generated Gradle project.")]
        [SerializeField] private bool addAndroidIntentFilter = true;

        public string ApiBaseUrl
        {
            get { return apiBaseUrl; }
            set { apiBaseUrl = value; }
        }

        /// <summary>Link hosts as entered. See <see cref="GetNormalizedHosts"/>.</summary>
        public string[] LinkHosts
        {
            get { return linkHosts; }
            set { linkHosts = value ?? new string[0]; }
        }

        public bool ReportAppOpens
        {
            get { return reportAppOpens; }
            set { reportAppOpens = value; }
        }

        public float TimeoutSeconds
        {
            get { return timeoutSeconds; }
            set { timeoutSeconds = value; }
        }

        public int MaxRetries
        {
            get { return maxRetries; }
            set { maxRetries = value; }
        }

        /// <summary>Publishable client key (<c>pk_…</c>); only needed for creating links. Not a secret.</summary>
        public string ClientKey
        {
            get { return clientKey; }
            set { clientKey = value ?? string.Empty; }
        }

        /// <summary>
        /// Whether <see cref="StrybokOptions.ParameterLinks"/> is set, with <see cref="PayloadKeys"/>.
        /// Off by default.
        /// </summary>
        public bool ParameterLinks
        {
            get { return parameterLinks; }
            set { parameterLinks = value; }
        }

        /// <summary>The payload keys of <see cref="StrybokOptions.ParameterLinks"/> as entered.</summary>
        public string[] PayloadKeys
        {
            get { return payloadKeys; }
            set { payloadKeys = value ?? new string[0]; }
        }

        /// <summary>See <see cref="StrybokOptions.DeferredLinks"/>. Off by default.</summary>
        public bool DeferredLinks
        {
            get { return deferredLinks; }
            set { deferredLinks = value; }
        }

        /// <summary>See <see cref="StrybokOptions.AllowProbabilisticMatching"/>: forbidden by Apple's rules. Off by default.</summary>
        public bool AllowProbabilisticMatching
        {
            get { return allowProbabilisticMatching; }
            set { allowProbabilisticMatching = value; }
        }

        public bool AddIosAssociatedDomains
        {
            get { return addIosAssociatedDomains; }
            set { addIosAssociatedDomains = value; }
        }

        public bool AddAndroidIntentFilter
        {
            get { return addAndroidIntentFilter; }
            set { addAndroidIntentFilter = value; }
        }

        /// <summary>Link hosts trimmed, lower-cased, without blanks and duplicates.</summary>
        public List<string> GetNormalizedHosts()
        {
            var hosts = new List<string>();
            if (linkHosts == null) return hosts;
            foreach (string entry in linkHosts)
            {
                string host = entry == null ? string.Empty : entry.Trim().ToLowerInvariant();
                if (host.Length > 0 && !hosts.Contains(host)) hosts.Add(host);
            }
            return hosts;
        }

        /// <summary>Options for <see cref="StrybokSdk.Initialize(StrybokOptions)"/> built from this asset.</summary>
        public StrybokOptions ToOptions()
        {
            return new StrybokOptions
            {
                ApiBaseUrl = apiBaseUrl,
                AllowedHosts = GetNormalizedHosts(),
                ReportAppOpens = reportAppOpens,
                Timeout = TimeSpan.FromSeconds(Mathf.Max(0.1f, timeoutSeconds)),
                MaxRetries = Mathf.Clamp(maxRetries, 0, 10),
                ClientKey = string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim(),
                ParameterLinks = parameterLinks ? new ParameterLinksOptions(GetTrimmedPayloadKeys().ToArray()) : null,
                DeferredLinks = deferredLinks,
                AllowProbabilisticMatching = allowProbabilisticMatching,
            };
        }

        /// <summary>
        /// The payload keys trimmed, without blank entries, in their order. Anything else (a
        /// duplicate, a space inside a key) is left for <see cref="StrybokClient"/> to reject.
        /// </summary>
        public List<string> GetTrimmedPayloadKeys()
        {
            var keys = new List<string>();
            if (payloadKeys == null) return keys;
            foreach (string entry in payloadKeys)
            {
                string key = entry == null ? string.Empty : entry.Trim();
                if (key.Length > 0) keys.Add(key);
            }
            return keys;
        }

        /// <summary>Loads <c>Resources/StrybokSettings</c>; null when the asset does not exist.</summary>
        public static StrybokSettings Load()
        {
            return Resources.Load<StrybokSettings>(ResourceName);
        }
    }
}
