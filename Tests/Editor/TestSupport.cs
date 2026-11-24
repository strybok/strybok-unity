using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;

namespace Strybok.Tests
{
    /// <summary>Records requests and answers them like the Strybok API would.</summary>
    internal sealed class FakeApi : IHttpTransport
    {
        private readonly object _gate = new object();
        private readonly List<StrybokHttpRequest> _resolves = new List<StrybokHttpRequest>();
        private readonly List<StrybokHttpRequest> _eventRequests = new List<StrybokHttpRequest>();
        private readonly List<CancellationToken> _tokens = new List<CancellationToken>();
        private readonly List<StrybokHttpRequest> _creates = new List<StrybokHttpRequest>();

        public FakeApi(Func<StrybokHttpRequest, Task<StrybokHttpResponse>> onResolve = null)
        {
            OnResolve = onResolve;
        }

        public Func<StrybokHttpRequest, Task<StrybokHttpResponse>> OnResolve { get; set; }

        public Func<Task<StrybokHttpResponse>> OnEvent { get; set; }

        /// <summary>Answers create-link calls; 201 with <see cref="T.CreatedBody"/> when null.</summary>
        public Func<StrybokHttpRequest, Task<StrybokHttpResponse>> OnCreate { get; set; }

        /// <summary>The create-link calls, in order.</summary>
        public List<StrybokHttpRequest> Creates
        {
            get { lock (_gate) return new List<StrybokHttpRequest>(_creates); }
        }

        public List<StrybokHttpRequest> Resolves
        {
            get { lock (_gate) return new List<StrybokHttpRequest>(_resolves); }
        }

        public List<StrybokHttpRequest> EventRequests
        {
            get { lock (_gate) return new List<StrybokHttpRequest>(_eventRequests); }
        }

        /// <summary>Cancellation tokens of the resolve calls, in order.</summary>
        public List<CancellationToken> ResolveTokens
        {
            get { lock (_gate) return new List<CancellationToken>(_tokens); }
        }

        /// <summary>Bodies of the event calls, parsed.</summary>
        public List<Dictionary<string, object>> Events
        {
            get
            {
                var events = new List<Dictionary<string, object>>();
                foreach (StrybokHttpRequest request in EventRequests)
                {
                    object parsed;
                    Assert.That(StrybokJson.TryParse(request.Body, out parsed), Is.True, "event body is JSON");
                    events.Add((Dictionary<string, object>)parsed);
                }
                return events;
            }
        }

        public Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request, CancellationToken cancellationToken)
        {
            if (new Uri(request.Url).AbsolutePath.EndsWith("/v1/public/events", StringComparison.Ordinal))
            {
                lock (_gate) _eventRequests.Add(request);
                return OnEvent != null ? OnEvent() : T.Respond(202, string.Empty);
            }
            if (new Uri(request.Url).AbsolutePath.EndsWith("/v1/public/links", StringComparison.Ordinal))
            {
                lock (_gate) _creates.Add(request);
                return OnCreate != null ? OnCreate(request) : T.Respond(201, T.CreatedBody());
            }
            lock (_gate)
            {
                _resolves.Add(request);
                _tokens.Add(cancellationToken);
            }
            return OnResolve != null ? OnResolve(request) : T.Ok();
        }
    }

    /// <summary>Shared fixtures and helpers.</summary>
    internal static class T
    {
        public const string Host = "my-app.strybok.link";
        public const string Api = "https://api.strybok.com";
        public const string Invite = "https://" + Host + "/invite777";

        public static Task<StrybokHttpResponse> Respond(int status, string body)
        {
            return Task.FromResult(new StrybokHttpResponse(status, body));
        }

        public static Task<StrybokHttpResponse> Ok(string route = "/invite/abc123", string linkId = "link_1")
        {
            return Respond(
                200,
                "{\"linkId\":\"" + linkId + "\",\"appId\":\"app_1\",\"routePath\":\"" + route +
                "\",\"payload\":{\"invitationId\":\"abc123\"}}");
        }

        public const string ClientKey = "pk_0123456789abcdefghijABCDEFGHIJ01";

        public static string CreatedBody(string route = "/product/42")
        {
            return "{\"linkId\":\"link_9\",\"url\":\"https://" + Host + "/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"" +
                   route + "\"}";
        }

        public static Task<StrybokHttpResponse> Respond(int status, string body, string retryAfter)
        {
            return Task.FromResult(new StrybokHttpResponse(
                status, body, new Dictionary<string, string> { { "retry-after", retryAfter } }));
        }

        /// <summary>A call that never gets an answer.</summary>
        public static Task<StrybokHttpResponse> Never()
        {
            return new TaskCompletionSource<StrybokHttpResponse>().Task;
        }

        public static StrybokOptions Options(FakeApi api, FakeLinkSource source = null)
        {
            return new StrybokOptions
            {
                ApiBaseUrl = Api,
                AllowedHosts = { Host },
                LinkSource = source ?? new FakeLinkSource(),
                HttpTransport = api,
                Timeout = TimeSpan.FromMilliseconds(150),
                MaxRetries = 2,
                RetryDelay = TimeSpan.FromMilliseconds(1),
            };
        }

        /// <summary>
        /// Runs an async test body on the thread pool and blocks until it is done. This
        /// keeps the tests independent of the Editor's update loop and of the Test
        /// Framework version (async tests need 1.3+).
        /// </summary>
        public static void Run(Func<Task> body)
        {
            Task task = Task.Run(body);
            if (!task.Wait(TimeSpan.FromSeconds(30)) && !task.IsCompleted)
            {
                Assert.Fail("The test did not finish within 30 seconds");
            }
            task.GetAwaiter().GetResult();
        }

        /// <summary>Time for anything unexpected to (not) happen.</summary>
        public static Task Settle()
        {
            return Task.Delay(60);
        }

        public static async Task Until(Func<bool> condition, string what)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
                await Task.Delay(5);
            }
        }

        public static async Task<TException> Throws<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                Assert.Fail("Expected " + typeof(TException).Name + " but got " + other);
            }
            Assert.Fail("Expected " + typeof(TException).Name + " but nothing was thrown");
            return null;
        }
    }

    /// <summary>Thread-safe list for values collected from callbacks.</summary>
    internal sealed class Bag<TItem>
    {
        private readonly List<TItem> _items = new List<TItem>();

        public void Add(TItem item)
        {
            lock (_items) _items.Add(item);
        }

        public int Count
        {
            get { lock (_items) return _items.Count; }
        }

        public List<TItem> Items
        {
            get { lock (_items) return new List<TItem>(_items); }
        }
    }
}
