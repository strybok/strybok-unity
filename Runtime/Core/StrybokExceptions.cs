using System;

namespace Strybok
{
    /// <summary>Base class of every error reported by the SDK.</summary>
    public abstract class StrybokException : Exception
    {
        protected StrybokException(string message, string url, Exception innerException = null)
            : base(message, innerException)
        {
            Url = url;
        }

        /// <summary>The link that was being handled, exactly as received; null when unknown.</summary>
        public string Url { get; }

        // Deliberately without the URL: its query may hold private data and this
        // text usually ends up in logs.
        public override string ToString()
        {
            return GetType().Name + ": " + Message;
        }
    }

    /// <summary>
    /// The URL is not a Strybok link of this app: wrong scheme, a host outside
    /// <c>AllowedHosts</c>, or a path that is not a single slug. Thrown by
    /// <see cref="StrybokClient.ResolveAsync"/> before any network call.
    /// </summary>
    public sealed class UnsupportedLinkException : StrybokException
    {
        public UnsupportedLinkException(string message, string url) : base(message, url) { }
    }

    /// <summary>The service does not know this link, or it is disabled or expired (HTTP 404).</summary>
    public sealed class LinkNotFoundException : StrybokException
    {
        public LinkNotFoundException(string url) : base("No active link matches this URL", url) { }
    }

    /// <summary>
    /// The request could not be completed (offline, DNS, connection reset) after all retries.
    /// The transport's error is available as <see cref="Exception.InnerException"/>.
    /// </summary>
    public sealed class StrybokNetworkException : StrybokException
    {
        public StrybokNetworkException(string message, string url, Exception cause = null)
            : base(message, url, cause) { }
    }

    /// <summary>The service did not answer in time, after all retries.</summary>
    public sealed class StrybokTimeoutException : StrybokException
    {
        public StrybokTimeoutException(string url) : base("The resolve request timed out", url) { }
    }

    /// <summary>The service answered with an unexpected status code or body.</summary>
    public sealed class StrybokServerException : StrybokException
    {
        public StrybokServerException(string message, string url, int statusCode) : base(message, url)
        {
            StatusCode = statusCode;
        }

        /// <summary>HTTP status of the answer; 200 when the body was malformed.</summary>
        public int StatusCode { get; }
    }
}
