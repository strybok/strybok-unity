using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Strybok.Tests
{
    /// <summary>
    /// Exercises <see cref="UnityWebRequestTransport"/> against a real HTTP server on
    /// the loopback interface. Plain HTTP is allowed for the duration of the fixture
    /// and the project's setting is restored afterwards.
    /// </summary>
    public sealed class UnityWebRequestTransportTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

        private HttpListener _listener;
        private Thread _serverThread;
        private string _baseUrl;
#if UNITY_2022_1_OR_NEWER
        private InsecureHttpOption _previousHttpOption;
#endif
        private readonly List<string> _requests = new List<string>();

        [OneTimeSetUp]
        public void StartServer()
        {
#if UNITY_2022_1_OR_NEWER
            // Unity 2022.1+ refuses plain HTTP unless the project allows it.
            _previousHttpOption = PlayerSettings.insecureHttpOption;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
#endif

            int port = FreePort();
            _baseUrl = "http://127.0.0.1:" + port;
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl + "/");
            _listener.Start();
            _serverThread = new Thread(Serve) { IsBackground = true, Name = "Strybok test server" };
            _serverThread.Start();
        }

        [OneTimeTearDown]
        public void StopServer()
        {
#if UNITY_2022_1_OR_NEWER
            PlayerSettings.insecureHttpOption = _previousHttpOption;
#endif
            if (_listener != null)
            {
                _listener.Stop();
                _listener.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private void Serve()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    return; // Listener stopped.
                }
                ThreadPool.QueueUserWorkItem(_ => Answer(context));
            }
        }

        private void Answer(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                lock (_requests)
                {
                    _requests.Add(
                        request.HttpMethod + " " + request.RawUrl +
                        " accept=" + request.Headers["Accept"] +
                        " type=" + request.ContentType +
                        " body=" + body);
                }

                int status = 200;
                string answer = "{\"ok\":true}";
                string path = request.Url.AbsolutePath;
                if (path == "/missing")
                {
                    status = 404;
                    answer = "{\"error\":{\"code\":\"LINK_NOT_FOUND\"}}";
                }
                else if (path == "/broken")
                {
                    status = 503;
                    answer = "unavailable";
                }
                else if (path == "/api/v1/public/resolve")
                {
                    answer = "{\"linkId\":\"link_1\",\"appId\":\"app_1\",\"routePath\":\"/invite/abc123\"," +
                             "\"payload\":{\"invitationId\":\"abc123\"}}";
                }
                else if (path == "/events" || path == "/api/v1/public/events")
                {
                    status = 202;
                    answer = string.Empty;
                }
                else if (path == "/slow")
                {
                    Thread.Sleep(3000);
                }
                else if (path == "/limited")
                {
                    status = 429;
                    answer = "{\"error\":{\"code\":\"RATE_LIMITED\"}}";
                    context.Response.AddHeader("Retry-After", "60");
                }
                else if (path == "/api/v1/public/links")
                {
                    lock (_requests)
                    {
                        _requests.Add(
                            "LINKS key=" + request.Headers["X-Strybok-Key"] +
                            " authorization=" + request.Headers["Authorization"]);
                    }
                    status = 201;
                    answer = "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\"," +
                             "\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}";
                }

                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client went away (aborted request); nothing to answer.
            }
        }

        private List<string> Requests
        {
            get { lock (_requests) return new List<string>(_requests); }
        }

        /// <summary>Lets the Editor run until the task is done.</summary>
        private static IEnumerator Await(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The request did not finish within 15 seconds");
                yield return null;
            }
        }

        private static StrybokHttpRequest Get(string url)
        {
            return new StrybokHttpRequest(
                "GET", url, new Dictionary<string, string> { { "Accept", "application/json" } }, null, TimeSpan.FromSeconds(5));
        }

        [UnityTest]
        public IEnumerator Get_ReturnsStatusAndBodyAndSendsHeaders()
        {
            var transport = new UnityWebRequestTransport();
            Task<StrybokHttpResponse> task = transport.SendAsync(
                Get(_baseUrl + "/v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777"), CancellationToken.None);
            yield return Await(task);

            Assert.That(task.IsFaulted, Is.False, task.Exception == null ? null : task.Exception.ToString());
            Assert.That(task.Result.StatusCode, Is.EqualTo(200));
            Assert.That(task.Result.Body, Is.EqualTo("{\"ok\":true}"));
            Assert.That(
                Requests,
                Has.Member("GET /v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777 accept=application/json type= body="));
        }

        [UnityTest]
        public IEnumerator Post_SendsTheJsonBody()
        {
            var transport = new UnityWebRequestTransport();
            var request = new StrybokHttpRequest(
                "POST",
                _baseUrl + "/events",
                new Dictionary<string, string> { { "Content-Type", "application/json" } },
                "{\"type\":\"app_open\",\"name\":\"é\"}",
                TimeSpan.FromSeconds(5));
            Task<StrybokHttpResponse> task = transport.SendAsync(request, CancellationToken.None);
            yield return Await(task);

            Assert.That(task.IsFaulted, Is.False, task.Exception == null ? null : task.Exception.ToString());
            Assert.That(task.Result.StatusCode, Is.EqualTo(202));
            Assert.That(task.Result.Body, Is.EqualTo(string.Empty));
            Assert.That(
                Requests,
                Has.Member("POST /events accept=*/* type=application/json body={\"type\":\"app_open\",\"name\":\"é\"}"));
        }

        [UnityTest]
        public IEnumerator ErrorStatuses_AreResponsesNotExceptions()
        {
            var transport = new UnityWebRequestTransport();
            Task<StrybokHttpResponse> missing = transport.SendAsync(Get(_baseUrl + "/missing"), CancellationToken.None);
            Task<StrybokHttpResponse> broken = transport.SendAsync(Get(_baseUrl + "/broken"), CancellationToken.None);
            yield return Await(Task.WhenAll(missing, broken));

            Assert.That(missing.Result.StatusCode, Is.EqualTo(404));
            Assert.That(missing.Result.Body, Does.Contain("LINK_NOT_FOUND"));
            Assert.That(broken.Result.StatusCode, Is.EqualTo(503));
            Assert.That(broken.Result.Body, Is.EqualTo("unavailable"));
        }

        [UnityTest]
        public IEnumerator ResponseHeaders_AreReported()
        {
            var transport = new UnityWebRequestTransport();
            Task<StrybokHttpResponse> task = transport.SendAsync(Get(_baseUrl + "/limited"), CancellationToken.None);
            yield return Await(task);

            Assert.That(task.IsFaulted, Is.False, task.Exception == null ? null : task.Exception.ToString());
            Assert.That(task.Result.StatusCode, Is.EqualTo(429));
            Assert.That(task.Result.GetHeader("Retry-After"), Is.EqualTo("60"));
            Assert.That(task.Result.GetHeader("X-Not-There"), Is.Null);
        }

        [UnityTest]
        public IEnumerator Client_CreatesALinkThroughTheRealTransport()
        {
            const string key = "pk_0123456789abcdefghijABCDEFGHIJ01";
            var client = new StrybokClient(new StrybokOptions
            {
                ApiBaseUrl = _baseUrl + "/api",
                AllowedHosts = { "my-app.strybok.link" },
                LinkSource = new Strybok.Testing.FakeLinkSource(),
                HttpTransport = new UnityWebRequestTransport(),
                CallbackContext = SynchronizationContext.Current,
                ClientKey = key,
            });
            try
            {
                Task<CreatedLink> task = client.CreateLinkAsync(
                    "/product/42", new Dictionary<string, object> { { "name", "é" } }, "Red shoes");
                yield return Await(task.ContinueWith(_ => { }));

                Assert.That(task.IsFaulted, Is.False, task.Exception == null ? null : task.Exception.ToString());
                Assert.That(task.Result.Url, Is.EqualTo("https://my-app.strybok.link/Ab3xK9"));
                Assert.That(task.Result.LinkId, Is.EqualTo("link_9"));
                Assert.That(
                    Requests,
                    Has.Member(
                        "POST /api/v1/public/links accept=application/json type=application/json " +
                        "body={\"routePath\":\"/product/42\",\"payload\":{\"name\":\"é\"},\"title\":\"Red shoes\"}"));
                Assert.That(Requests, Has.Member("LINKS key=" + key + " authorization="));

                Task<CreatedLink> limited = new StrybokClient(new StrybokOptions
                {
                    ApiBaseUrl = _baseUrl + "/limited-api",
                    AllowedHosts = { "my-app.strybok.link" },
                    LinkSource = new Strybok.Testing.FakeLinkSource(),
                    HttpTransport = new LimitedTransport(new UnityWebRequestTransport(), _baseUrl + "/limited"),
                    ClientKey = key,
                }).CreateLinkAsync("/product/42");
                yield return Await(limited.ContinueWith(_ => { }));
                Assert.That(limited.IsFaulted, Is.True);
                var rateLimited = limited.Exception.InnerException as StrybokRateLimitedException;
                Assert.That(rateLimited, Is.Not.Null, limited.Exception.InnerException.ToString());
                Assert.That(rateLimited.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(60)));
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>Sends every request to one URL of the test server.</summary>
        private sealed class LimitedTransport : IHttpTransport
        {
            private readonly IHttpTransport _inner;
            private readonly string _url;

            public LimitedTransport(IHttpTransport inner, string url)
            {
                _inner = inner;
                _url = url;
            }

            public Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request, CancellationToken cancellationToken)
            {
                return _inner.SendAsync(
                    new StrybokHttpRequest(request.Method, _url, request.Headers, request.Body, request.Timeout),
                    cancellationToken);
            }
        }

        [UnityTest]
        public IEnumerator ConnectionFailure_FaultsTheTask()
        {
            var transport = new UnityWebRequestTransport();
            Task<StrybokHttpResponse> task = transport.SendAsync(
                Get("http://127.0.0.1:" + FreePort() + "/nobody-listens"), CancellationToken.None);
            yield return Await(task.ContinueWith(_ => { }));

            Assert.That(task.IsFaulted, Is.True);
            Assert.That(task.Exception.InnerException, Is.InstanceOf<IOException>());
        }

        [UnityTest]
        public IEnumerator Cancellation_AbortsTheRequest()
        {
            var transport = new UnityWebRequestTransport();
            using (var cancellation = new CancellationTokenSource())
            {
                DateTime started = DateTime.UtcNow;
                Task<StrybokHttpResponse> task = transport.SendAsync(Get(_baseUrl + "/slow"), cancellation.Token);
                yield return null;
                Assert.That(task.IsCompleted, Is.False);

                cancellation.Cancel();
                yield return Await(task.ContinueWith(_ => { }));

                Assert.That(task.IsFaulted || task.IsCanceled, Is.True);
                Assert.That((DateTime.UtcNow - started).TotalSeconds, Is.LessThan(2.5), "did not wait for the slow answer");
            }
        }

        [UnityTest]
        public IEnumerator Client_DeliversALaunchLinkOnTheMainThreadThroughTheRealTransport()
        {
            // End to end on the main thread: StrybokClient + UnityWebRequestTransport,
            // continuations pumped by the Editor's synchronization context.
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int handlerThread = -1;
            var received = new List<ResolvedDeepLink>();
            var errors = new List<StrybokException>();
            var client = new StrybokClient(new StrybokOptions
            {
                ApiBaseUrl = _baseUrl + "/api",
                AllowedHosts = { "my-app.strybok.link" },
                LinkSource = new Strybok.Testing.FakeLinkSource("https://my-app.strybok.link/invite777?token=secret"),
                HttpTransport = new UnityWebRequestTransport(),
                OnError = errors.Add,
                CallbackContext = SynchronizationContext.Current,
                Platform = StrybokPlatform.Android,
            });
            try
            {
                client.LinkReceived += link =>
                {
                    handlerThread = Thread.CurrentThread.ManagedThreadId;
                    received.Add(link);
                };
                yield return Await(client.InitializeAsync());

                Assert.That(errors, Is.Empty);
                Assert.That(received, Has.Count.EqualTo(1));
                Assert.That(received[0].RoutePath, Is.EqualTo("/invite/abc123"));
                Assert.That(received[0].GetString("invitationId"), Is.EqualTo("abc123"));
                Assert.That(handlerThread, Is.EqualTo(mainThread));

                string eventRequest = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (eventRequest == null && DateTime.UtcNow < deadline)
                {
                    eventRequest = Requests.Find(line => line.StartsWith("POST /api/v1/public/events ", StringComparison.Ordinal));
                    yield return null;
                }
                Assert.That(eventRequest, Is.Not.Null, "app_open event reached the server");
                Assert.That(eventRequest, Does.Contain("type=application/json"));
                Assert.That(eventRequest, Does.Contain("\"linkId\":\"link_1\",\"type\":\"app_open\",\"platform\":\"android\",\"sdkVersion\":\"0.3.0\"}"));
                Assert.That(
                    Requests,
                    Has.Member("GET /api/v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777 accept=application/json type= body="));
                Assert.That(string.Join("\n", Requests), Does.Not.Contain("secret"));
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
