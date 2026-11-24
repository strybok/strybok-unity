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
            };
        }

        /// <summary>Loads <c>Resources/StrybokSettings</c>; null when the asset does not exist.</summary>
        public static StrybokSettings Load()
        {
            return Resources.Load<StrybokSettings>(ResourceName);
        }
    }
}
