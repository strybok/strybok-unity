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

        /// <summary>Used by CreateLinkAsync before Initialize; never initialized, so it does not listen for links.</summary>
        private static StrybokClient s_linkCreator;

        /// <summary>Options from the settings asset, or null when there is none. Replaced in tests.</summary>
        internal static Func<StrybokOptions> SettingsOptions = LoadSettingsOptions;
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

            StrybokOptions effective = WithUnityDefaults(options);

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

        /// <summary>
        /// Asks the service for a short link to a screen of this game, for a "Share" button:
        /// <code>
        /// CreatedLink link = await StrybokSdk.CreateLinkAsync("/product/42", title: "Red shoes");
        /// Share(link.Url);
        /// </code>
        /// Needs the publishable client key (<c>pk_…</c>): <c>ClientKey</c> in the options or
        /// in the <see cref="StrybokSettings"/> asset. Creating links from the app must be
        /// switched on in the dashboard, with the allowed route patterns.
        ///
        /// It only runs when you call it and has nothing to do with receiving links. After
        /// <see cref="Initialize(StrybokOptions)"/> it uses that configuration; before (or
        /// without) it, the <see cref="StrybokSettings"/> asset, which then does not need a
        /// link host. Failures are thrown from the awaited task as
        /// <see cref="StrybokException"/>s and never raise <see cref="LinkFailed"/>; see
        /// <see cref="StrybokClient.CreateLinkAsync"/> for the list.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="Initialize(StrybokOptions)"/> was not called and there is no settings asset.
        /// </exception>
        public static Task<CreatedLink> CreateLinkAsync(
            string routePath,
            System.Collections.Generic.IReadOnlyDictionary<string, object> payload = null,
            string title = null,
            string description = null,
            string imageUrl = null)
        {
            return LinkCreator().CreateLinkAsync(routePath, payload, title, description, imageUrl);
        }

        /// <summary>
        /// <see cref="CreateLinkAsync"/> with the payload as JSON object text, e.g.
        /// <c>JsonUtility.ToJson(myPayload)</c>. Null means no payload.
        /// </summary>
        public static Task<CreatedLink> CreateLinkWithPayloadJsonAsync(
            string routePath,
            string payloadJson,
            string title = null,
            string description = null,
            string imageUrl = null)
        {
            return LinkCreator().CreateLinkWithPayloadJsonAsync(routePath, payloadJson, title, description, imageUrl);
        }

        /// <summary>Whether <paramref name="url"/> is one of this app's Strybok links.</summary>
        public static bool IsStrybokUrl(string url)
        {
            return RequireClient().IsStrybokUrl(url);
        }

        /// <summary>
        /// Stops listening and drops queued links; nothing is delivered afterwards, and
        /// requests in flight (including <see cref="CreateLinkAsync"/>) are aborted.
        /// Subscriptions to <see cref="LinkReceived"/> and <see cref="LinkFailed"/> are
        /// kept for a later <see cref="Initialize(StrybokOptions)"/>.
        /// </summary>
        public static void Shutdown()
        {
            StrybokClient client = s_client;
            StrybokClient linkCreator = s_linkCreator;
            s_client = null;
            s_linkCreator = null;
            s_initialization = null;
            s_forwarding = false;
            if (client != null) client.Dispose();
            if (linkCreator != null) linkCreator.Dispose();
        }

        /// <summary>The Unity link source, transport, platform and main-thread context where the options have none.</summary>
        private static StrybokOptions WithUnityDefaults(StrybokOptions options)
        {
            StrybokOptions effective = options.Clone();
            if (effective.LinkSource == null) effective.LinkSource = new UnityLinkSource();
            if (effective.HttpTransport == null) effective.HttpTransport = new UnityWebRequestTransport();
            if (effective.Platform == StrybokPlatform.Unknown) effective.Platform = UnityLinkSource.CurrentPlatform;
            if (effective.CallbackContext == null) effective.CallbackContext = SynchronizationContext.Current;
            if (effective.OnHandlerException == null) effective.OnHandlerException = Debug.LogException;
            return effective;
        }

        /// <summary>
        /// The initialized client, or one made from the settings asset that never starts
        /// listening: creating a link must not depend on, or trigger, link handling.
        /// </summary>
        private static StrybokClient LinkCreator()
        {
            if (s_client != null) return s_client;
            if (s_linkCreator != null) return s_linkCreator;

            Func<StrybokOptions> load = SettingsOptions;
            StrybokOptions options = load != null ? load() : null;
            if (options == null)
            {
                throw new InvalidOperationException(
                    "Call StrybokSdk.Initialize(...) first, or create the StrybokSettings asset with " +
                    "Tools > Strybok > Settings.");
            }
            s_linkCreator = new StrybokClient(WithUnityDefaults(options), true);
            return s_linkCreator;
        }

        private static StrybokOptions LoadSettingsOptions()
        {
            StrybokSettings settings = StrybokSettings.Load();
            return settings == null ? null : settings.ToOptions();
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
            SettingsOptions = LoadSettingsOptions;
        }
    }
}
