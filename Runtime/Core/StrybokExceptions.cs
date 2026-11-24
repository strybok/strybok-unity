using System;
using System.Collections.Generic;

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

        /// <summary>
        /// The link that was being handled, exactly as received; null when unknown and for
        /// errors of <see cref="StrybokClient.CreateLinkAsync"/>.
        /// </summary>
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

        public StrybokTimeoutException(string message, string url) : base(message, url) { }
    }

    /// <summary>The service answered with an unexpected status code or body.</summary>
    public sealed class StrybokServerException : StrybokException
    {
        public StrybokServerException(string message, string url, int statusCode) : base(message, url)
        {
            StatusCode = statusCode;
        }

        /// <summary>HTTP status of the answer; 200 (or 201) when the body was malformed.</summary>
        public int StatusCode { get; }
    }

    /// <summary>
    /// <see cref="StrybokClient.CreateLinkAsync"/> only. No <c>ClientKey</c> is configured (no
    /// network call was made), or the service refused it (HTTP 401): the key is wrong or was
    /// replaced, or creating links from the app is switched off in the dashboard. The service
    /// does not say which. Retrying does not help.
    /// </summary>
    public sealed class StrybokUnauthorizedException : StrybokException
    {
        public StrybokUnauthorizedException(string message) : base(message, null) { }
    }

    /// <summary>
    /// <see cref="StrybokClient.CreateLinkAsync"/> only. The route is not covered by the route
    /// patterns listed for the application in the dashboard (HTTP 403). Retrying does not help.
    /// </summary>
    public sealed class RouteNotAllowedException : StrybokException
    {
        public RouteNotAllowedException(string routePath)
            : base("The route is not covered by the application's route patterns", null)
        {
            RoutePath = routePath;
        }

        /// <summary>The route that was refused.</summary>
        public string RoutePath { get; }
    }

    /// <summary>
    /// <see cref="StrybokClient.CreateLinkAsync"/> only. Too many requests from this device's
    /// address or for this app, or the app's daily limit of new links is reached (HTTP 429).
    /// Temporary: let the user try again later. The SDK never retries this by itself.
    /// </summary>
    public sealed class StrybokRateLimitedException : StrybokException
    {
        public StrybokRateLimitedException(TimeSpan? retryAfter)
            : base("Too many requests; try again later", null)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>How long to wait, from the <c>Retry-After</c> header; null when the service did not say.</summary>
        public TimeSpan? RetryAfter { get; }
    }

    /// <summary>One problem the service found in a create-link request.</summary>
    public sealed class StrybokValidationIssue
    {
        public StrybokValidationIssue(string path, string message)
        {
            Path = path ?? string.Empty;
            Message = message ?? string.Empty;
        }

        /// <summary>Which field, e.g. <c>title</c> or <c>payload.a</c>; may be empty.</summary>
        public string Path { get; }

        public string Message { get; }

        public override string ToString()
        {
            return Path.Length == 0 ? Message : Path + ": " + Message;
        }
    }

    /// <summary>
    /// <see cref="StrybokClient.CreateLinkAsync"/> only. The request was rejected before it was
    /// sent (bad route path, or a payload that is not a JSON object), or by the service
    /// (HTTP 400, 413 or 422). Retrying does not help.
    /// </summary>
    public sealed class StrybokValidationException : StrybokException
    {
        private static readonly IReadOnlyList<StrybokValidationIssue> NoIssues = new StrybokValidationIssue[0];

        public StrybokValidationException(
            string message,
            int statusCode = 0,
            IReadOnlyList<StrybokValidationIssue> issues = null)
            : base(message, null)
        {
            StatusCode = statusCode;
            Issues = issues ?? NoIssues;
        }

        /// <summary>HTTP status of the answer; 0 when the request was rejected before sending.</summary>
        public int StatusCode { get; }

        /// <summary>The service's <c>error.details.issues</c>; empty when it sent none.</summary>
        public IReadOnlyList<StrybokValidationIssue> Issues { get; }
    }
}
