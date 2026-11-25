using System.Threading;
using System.Threading.Tasks;

namespace Strybok
{
    /// <summary>
    /// Where the deferred-link check stands (<see cref="StrybokClient.DeferredLinkStatus"/>).
    /// For developers and QA only: a check that finds nothing is the normal case, and nothing
    /// here is ever reported to <c>OnError</c>.
    /// </summary>
    public enum DeferredLinkStatus
    {
        /// <summary>
        /// <c>DeferredLinks</c> is off, or the feature cannot run: no
        /// <see cref="IDeferredLinkStore"/>, no package name / bundle id, or a platform that is
        /// neither Android nor iOS (the Editor, for example).
        /// </summary>
        Disabled = 0,

        /// <summary>The check is running in this launch.</summary>
        Checking,

        /// <summary>
        /// The check has not started yet, or this launch could not finish it (store or network
        /// not reachable); a later launch tries again.
        /// </summary>
        Pending,

        /// <summary>The check was finished in an earlier launch.</summary>
        NotFirstLaunch,

        /// <summary>
        /// Android: no referrer source is configured, or none can ever answer on this device.
        /// iOS: nothing automatic exists (probabilistic matching is off, or the device family
        /// or locale is not available); a pasted link can still be redeemed while matching is off.
        /// </summary>
        Unavailable,

        /// <summary>The store returned a referrer without a click id of ours (an ordinary install).</summary>
        NoReferrer,

        /// <summary>The server refused: unknown, too old, already used, or not this application's.</summary>
        NotFound,

        /// <summary>A normal link of this app arrived first.</summary>
        Superseded,

        /// <summary>The link was handed to <c>LinkReceived</c>.</summary>
        Delivered,

        /// <summary>Three launches could not finish the check, or the server's answer was unusable.</summary>
        Failed,

        /// <summary>
        /// iOS with probabilistic matching: the pasteboard may hold a link, so matching waits
        /// for <c>RedeemPastedLinkAsync</c> or <c>SkipPastedLink</c>.
        /// </summary>
        AwaitingPaste,
    }

    /// <summary>Result of <see cref="StrybokClient.RedeemPastedLinkAsync"/>.</summary>
    public enum PastedLinkResult
    {
        /// <summary>The link was handed to <c>LinkReceived</c>.</summary>
        Delivered = 0,

        /// <summary>The text is not a copied link of this app. Nothing was sent or kept.</summary>
        NotOurs,

        /// <summary>The server refused the link: unknown, older than 24 hours, already used, or another app's.</summary>
        NotFound,

        /// <summary>A deferred link was already handled on this installation. Nothing was sent.</summary>
        AlreadyHandled,

        /// <summary>Deferred links are off or cannot run here (see <see cref="DeferredLinkStatus.Disabled"/>), or this is not iOS.</summary>
        Disabled,

        /// <summary>
        /// The request failed, or the answer was unusable. After a network failure the person
        /// can paste again.
        /// </summary>
        Failed,
    }

    /// <summary>Which of its three answers an <see cref="InstallReferrerSource"/> gave.</summary>
    public enum InstallReferrerAnswerKind
    {
        /// <summary>The store could not be asked; a later launch may succeed.</summary>
        UnavailableForNow = 0,

        /// <summary>This store is not on the device, is too old, or its library is not in the app.</summary>
        Unavailable,

        /// <summary>The store returned its install referrer string (it may be empty).</summary>
        Referrer,
    }

    /// <summary>The answer of an <see cref="InstallReferrerSource"/>: exactly one of three.</summary>
    public readonly struct InstallReferrerAnswer
    {
        private readonly string _referrer;

        private InstallReferrerAnswer(InstallReferrerAnswerKind kind, string referrer)
        {
            Kind = kind;
            _referrer = referrer;
        }

        /// <summary>The store could not be asked; a later launch may succeed.</summary>
        public static InstallReferrerAnswer UnavailableForNow
        {
            get { return new InstallReferrerAnswer(InstallReferrerAnswerKind.UnavailableForNow, null); }
        }

        /// <summary>
        /// This store is not on the device, is too old, or its library is not in the app.
        /// Asking again is pointless.
        /// </summary>
        public static InstallReferrerAnswer Unavailable
        {
            get { return new InstallReferrerAnswer(InstallReferrerAnswerKind.Unavailable, null); }
        }

        /// <summary>
        /// The raw install referrer string, exactly as the store returned it. It may be empty;
        /// null is treated as empty.
        /// </summary>
        public static InstallReferrerAnswer Referrer(string referrer)
        {
            return new InstallReferrerAnswer(InstallReferrerAnswerKind.Referrer, referrer ?? string.Empty);
        }

        /// <summary>Which answer this is.</summary>
        public InstallReferrerAnswerKind Kind { get; }

        /// <summary>
        /// The referrer string. Only read by the SDK to find its own click id; never stored,
        /// logged or sent.
        /// </summary>
        internal string ReferrerValue
        {
            get { return _referrer ?? string.Empty; }
        }

        /// <summary>Never contains the referrer.</summary>
        public override string ToString()
        {
            return "InstallReferrerAnswer(" + Kind + ")";
        }
    }

    /// <summary>
    /// Asks one store for the install referrer of this app (Android). Supplied by your game:
    /// the SDK ships no store integration.
    ///
    /// Called at most once per launch and at most three times in the life of an installation,
    /// from wherever <c>InitializeAsync</c> continued (Unity's main thread with
    /// <c>StrybokSdk</c>). It must return its task quickly and do the waiting asynchronously,
    /// must not throw (a throw counts as "unavailable for now"), must answer within 10 seconds
    /// (afterwards the SDK cancels the token, abandons the call and counts it as "unavailable
    /// for now") and must release whatever connection it opened.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the SDK gave up waiting or was disposed.</param>
    public delegate Task<InstallReferrerAnswer> InstallReferrerSource(CancellationToken cancellationToken);

    /// <summary>
    /// A small persistent key-value store for the two values the deferred-link check keeps per
    /// installation: an attempt counter and a "done" flag. It must survive restarts of the app
    /// and be removed with the app's data. <c>StrybokSdk</c> uses <c>PlayerPrefs</c>.
    ///
    /// The SDK writes only the keys <c>strybok.deferred.attempts</c> and
    /// <c>strybok.deferred.done</c>, with short ASCII values — never the referrer, a pasted
    /// text or a click id. Without a store deferred links stay
    /// <see cref="DeferredLinkStatus.Disabled"/>: a store that only lives in memory would
    /// redeem again on the next launch, so do not pass one. A method that throws is treated as
    /// a failed read or write; the check then stops for this launch rather than risk
    /// delivering twice.
    /// </summary>
    public interface IDeferredLinkStore
    {
        /// <summary>The value stored for <paramref name="key"/>, or null when there is none.</summary>
        string Read(string key);

        /// <summary>Stores <paramref name="value"/> for <paramref name="key"/> durably before returning.</summary>
        void Write(string key, string value);
    }
}
