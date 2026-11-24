using System;
using System.Threading.Tasks;

namespace Strybok
{
    /// <summary>
    /// Where incoming link URLs come from. The Unity implementation is
    /// <c>UnityLinkSource</c>; tests and custom integrations can provide their own.
    /// </summary>
    public interface ILinkSource
    {
        /// <summary>The link that launched the app (cold start), or null.</summary>
        Task<string> GetInitialLinkAsync();

        /// <summary>
        /// Links received while the app is running. Implementations may also replay
        /// the launch link here; the SDK de-duplicates it.
        /// </summary>
        event Action<string> UrlReceived;
    }
}
