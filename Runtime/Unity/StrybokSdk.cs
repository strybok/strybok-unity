using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Strybok
{
    /// <summary>
    /// Entry point for Unity projects: one shared <see cref="StrybokClient"/> wired to
    /// <see cref="UnityLinkSource"/> and <see cref="UnityWebRequestTransport"/>, with
    /// handlers called on the main thread.
    ///
    /// <code>
    /// StrybokSdk.LinkReceived += link => Router.Open(link.RoutePath, link.Payload);
    /// StrybokSdk.Initialize(new StrybokOptions
    /// {
    ///     ApiBaseUrl = "https://api.strybok.com",
    ///     AllowedHosts = { "my-app.strybok.link" },
    /// });
    /// </code>
    ///
    /// Call everything here from the main thread.
    /// </summary>
    public static class StrybokSdk
    {
        private static StrybokClient s_client;
        private static Task s_initialization;
        private static Action<ResolvedDeepLink> s_linkHandlers;
        private static Action<StrybokException> s_errorHandlers;
        private static bool s_forwarding;

        /// <summary>Whether <see cref="Initialize(StrybokOptions)"/> has been called (and not <see cref="Shutdown"/>).</summary>
        public static bool IsInitialized
        {
            get { return s_client != null; }
        }

        /// <summary>The shared client, or null before <see cref="Initialize(StrybokOptions)"/>.</summary>
        public static StrybokClient Client
        {
            get { return s_client; }
        }

        /// <summary>
        /// Resolved links, each delivered exactly once, on the main thread.
        ///
        /// You may subscribe before or after <see cref="Initialize(StrybokOptions)"/>.
        /// Links resolved while nobody was subscribed (typically the one that
        /// cold-started the app) are queued and handed to the first subscriber as soon
        /// as it subscribes, inside the <c>+=</c> call.
        /// </summary>
        public static event Action<ResolvedDeepLink> LinkReceived
        {
            add
            {
                if (value == null) return;
                s_linkHandlers += value;
                if (s_client != null && !s_forwarding)
                {
                    s_forwarding = true;
                    s_client.LinkReceived += ForwardLink; // Replays queued links.
                }
            }
            remove
            {
                s_linkHandlers -= value;
                if (s_linkHandlers == null && s_forwarding)
                {
                    s_forwarding = false;
                    if (s_client != null) s_client.LinkReceived -= ForwardLink; // Queue again.
                }
            }
        }

        /// <summary>
        /// Errors for links of this app that could not be resolved (see
        /// <see cref="StrybokException"/>), on the main thread. When nobody listens here
        /// and no <c>OnError</c> option is set, failures are logged as warnings.
        /// </summary>
        public static event Action<StrybokException> LinkFailed
        {
            add { s_errorHandlers += value; }
            remove { s_errorHandlers -= value; }
        }

        /// <summary>
        /// Initializes from the <see cref="StrybokSettings"/> asset in a <c>Resources</c>
        /// folder (<c>Tools &gt; Strybok &gt; Settings</c>).
        /// </summary>
        public static Task Initialize()
        {
            StrybokSettings settings = StrybokSettings.Load();
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "No StrybokSettings asset found in a Resources folder. Create it with " +
                    "Tools > Strybok > Settings, or call StrybokSdk.Initialize(options).");
            }
            return Initialize(settings.ToOptions());
        }

        /// <summary>
        /// Starts listening for links and resolves the one that launched the app.
        /// Call it once, as early as possible (first scene). The returned task
        /// completes when the launch link has been resolved or has failed; it never
        /// faults because of a link. Calling it again returns the same task and keeps
        /// the first configuration.
        /// </summary>
        /// <exception cref="ArgumentException">The options are invalid.</exception>
        public static Task Initialize(StrybokOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (s_client != null)
            {
                Debug.LogWarning("[Strybok] Initialize was called again; keeping the existing configuration.");
                return s_initialization;
            }

            StrybokOptions effective = options.Clone();
            if (effective.LinkSource == null) effective.LinkSource = new UnityLinkSource();
            if (effective.HttpTransport == null) effective.HttpTransport = new UnityWebRequestTransport();
            if (effective.Platform == StrybokPlatform.Unknown) effective.Platform = UnityLinkSource.CurrentPlatform;
            if (effective.CallbackContext == null) effective.CallbackContext = SynchronizationContext.Current;
            if (effective.OnHandlerException == null) effective.OnHandlerException = Debug.LogException;

            Action<StrybokException> userOnError = effective.OnError;
            effective.OnError = error =>
            {
                if (userOnError != null) userOnError(error);
                Action<StrybokException> handlers = s_errorHandlers;
                if (handlers != null) handlers(error);
                else if (userOnError == null) Debug.LogWarning("[Strybok] Link failed: " + error);
            };

            var client = new StrybokClient(effective);
            s_client = client;
            if (s_linkHandlers != null)
            {
                s_forwarding = true;
                client.LinkReceived += ForwardLink;
            }
            s_initialization = client.InitializeAsync();
            return s_initialization;
        }

        /// <summary>
        /// The link that launched the app, if it has not been delivered through
        /// <see cref="LinkReceived"/> yet; null otherwise. Returns it at most once, so
        /// using both APIs never navigates twice.
        /// </summary>
        public static Task<ResolvedDeepLink> GetInitialLinkAsync()
        {
            return RequireClient().GetInitialLinkAsync();
        }

        /// <summary>
        /// Resolves a URL explicitly (QR scanner, tests). Does not raise
        /// <see cref="LinkReceived"/> and does not report an app open. Fails with a
        /// <see cref="StrybokException"/>.
        /// </summary>
        public static Task<ResolvedDeepLink> ResolveAsync(string url)
        {
            return RequireClient().ResolveAsync(url);
        }

        /// <summary>Whether <paramref name="url"/> is one of this app's Strybok links.</summary>
        public static bool IsStrybokUrl(string url)
        {
            return RequireClient().IsStrybokUrl(url);
        }

        /// <summary>
        /// Stops listening and drops queued links; nothing is delivered afterwards.
        /// Subscriptions to <see cref="LinkReceived"/> and <see cref="LinkFailed"/> are
        /// kept for a later <see cref="Initialize(StrybokOptions)"/>.
        /// </summary>
        public static void Shutdown()
        {
            StrybokClient client = s_client;
            s_client = null;
            s_initialization = null;
            s_forwarding = false;
            if (client != null) client.Dispose();
        }

        private static void ForwardLink(ResolvedDeepLink link)
        {
            Action<ResolvedDeepLink> handlers = s_linkHandlers;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ResolvedDeepLink>)handler)(link);
                }
                catch (Exception error)
                {
                    Debug.LogException(error); // One faulty handler must not block the others.
                }
            }
        }

        private static StrybokClient RequireClient()
        {
            if (s_client == null)
            {
                throw new InvalidOperationException("Call StrybokSdk.Initialize(...) first.");
            }
            return s_client;
        }

        // Covers "Enter Play Mode" without a domain reload in the Editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shutdown();
            s_linkHandlers = null;
            s_errorHandlers = null;
        }
    }
}
