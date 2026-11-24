using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Strybok
{
    /// <summary>
    /// <see cref="ILinkSource"/> backed by Unity: <c>Application.absoluteURL</c> is the
    /// launch URL, <c>Application.deepLinkActivated</c> reports links while the app runs.
    /// Unity raises both on the main thread.
    /// </summary>
    public sealed class UnityLinkSource : ILinkSource
    {
        // Application.absoluteURL keeps the last link for the whole process lifetime.
        // Remembering what was already handed out stops a second client (created after
        // a Shutdown) from navigating to an old link again.
        private static string s_consumedUrl;

        private Action<string> _handlers;
        private bool _hooked;

        public event Action<string> UrlReceived
        {
            add
            {
                _handlers += value;
                if (_hooked || _handlers == null) return;
                Application.deepLinkActivated += OnDeepLinkActivated;
                _hooked = true;
            }
            remove
            {
                _handlers -= value;
                if (!_hooked || _handlers != null) return;
                Application.deepLinkActivated -= OnDeepLinkActivated;
                _hooked = false;
            }
        }

        public Task<string> GetInitialLinkAsync()
        {
            string url = Application.absoluteURL;
            if (string.IsNullOrEmpty(url) || url == s_consumedUrl) return Task.FromResult<string>(null);
            s_consumedUrl = url;
            return Task.FromResult(url);
        }

        private void OnDeepLinkActivated(string url)
        {
            s_consumedUrl = url;
            Action<string> handlers = _handlers;
            if (handlers != null) handlers(url);
        }

        /// <summary>Platform name for <c>app_open</c> events, from <c>Application.platform</c>.</summary>
        public static StrybokPlatform CurrentPlatform
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.IPhonePlayer: return StrybokPlatform.IOS;
                    case RuntimePlatform.Android: return StrybokPlatform.Android;
                    default: return StrybokPlatform.Unknown;
                }
            }
        }

        // Covers "Enter Play Mode" without a domain reload in the Editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_consumedUrl = null;
        }
    }
}
