using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok
{
    /// <summary>One HTTP call the SDK wants to make.</summary>
    public sealed class StrybokHttpRequest
    {
        public StrybokHttpRequest(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string body,
            TimeSpan timeout)
        {
            Method = method;
            Url = url;
            Headers = headers;
            Body = body;
            Timeout = timeout;
        }

        /// <summary><c>GET</c> or <c>POST</c>.</summary>
        public string Method { get; }

        /// <summary>Absolute URL, already escaped.</summary>
        public string Url { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>UTF-8 text body, or null when there is none.</summary>
        public string Body { get; }

        /// <summary>
        /// Time the SDK waits for this call. The SDK enforces it itself and then cancels
        /// the token; a transport may use it as a hint for its own timeout.
        /// </summary>
        public TimeSpan Timeout { get; }
    }

    /// <summary>The answer to a <see cref="StrybokHttpRequest"/>.</summary>
    public sealed class StrybokHttpResponse
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

        public StrybokHttpResponse(int statusCode, string body)
            : this(statusCode, body, null)
        {
        }

        /// <param name="headers">Response headers; null when the transport does not report them.</param>
        public StrybokHttpResponse(int statusCode, string body, IReadOnlyDictionary<string, string> headers)
        {
            StatusCode = statusCode;
            Body = body ?? string.Empty;
            Headers = headers ?? NoHeaders;
        }

        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>
        /// Response headers, never null. The SDK only reads <c>Retry-After</c> (for
        /// <see cref="StrybokRateLimitedException"/>), so a custom transport may leave the rest out.
        /// </summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>The value of a response header, whatever the case of its name; null when absent.</summary>
        public string GetHeader(string name)
        {
            if (name == null) return null;
            foreach (KeyValuePair<string, string> header in Headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) return header.Value;
            }
            return null;
        }
    }

    /// <summary>
    /// Performs HTTP calls for the SDK. The Unity implementation is
    /// <c>UnityWebRequestTransport</c>.
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>
        /// Sends the request and completes with the response for <b>any</b> HTTP status.
        /// Throw (or fault the task) only when no HTTP answer was received; the SDK
        /// reports that as a network error and retries. Stop the call when
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request, CancellationToken cancellationToken);
    }
}
