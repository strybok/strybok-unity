using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;

namespace Strybok.Tests
{
    /// <summary>
    /// The SDK contract (Documentation~/sdk-contract.md), ported from the Flutter SDK's
    /// test suite (github.com/strybok/strybok-flutter).
    /// </summary>
    public sealed class StrybokClientTests
    {
        private readonly List<StrybokClient> _clients = new List<StrybokClient>();

        [TearDown]
        public void DisposeClients()
        {
            foreach (StrybokClient client in _clients) client.Dispose();
            _clients.Clear();
        }

        private StrybokClient Client(
            FakeApi api,
            FakeLinkSource source = null,
            Bag<StrybokException> errors = null,
            Action<StrybokOptions> configure = null)
        {
            StrybokOptions options = T.Options(api, source);
            if (errors != null) options.OnError = errors.Add;
            if (configure != null) configure(options);
            var client = new StrybokClient(options);
            lock (_clients) _clients.Add(client);
            return client;
        }

        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        [Test]
        public void Options_HaveTheContractDefaults()
        {
            var options = new StrybokOptions();
            Assert.That(options.Timeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(options.MaxRetries, Is.EqualTo(2));
            Assert.That(options.RetryDelay, Is.EqualTo(TimeSpan.FromMilliseconds(400)));
            Assert.That(options.DedupeWindow, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(options.ReportAppOpens, Is.True);
            Assert.That(options.OnError, Is.Null);
            Assert.That(options.ClientKey, Is.Null);
            Assert.That(options.ParameterLinks, Is.Null);
            Assert.That(StrybokClient.SdkVersion, Is.EqualTo("0.4.0"));
        }

        [Test]
        public void Constructor_RejectsInvalidOptions()
        {
            var api = new FakeApi();
            Assert.Throws<ArgumentNullException>(() => new StrybokClient(null));

            var invalid = new List<Action<StrybokOptions>>
            {
                o => o.ApiBaseUrl = null,
                o => o.ApiBaseUrl = "api.strybok.com",
                o => o.ApiBaseUrl = "ftp://api.strybok.com",
                o => o.ApiBaseUrl = "https://api.strybok.com?x=1",
                o => o.AllowedHosts = new List<string>(),
                o => o.AllowedHosts = new List<string> { "  ", null },
                o => o.AllowedHosts = null,
                o => o.LinkSource = null,
                o => o.HttpTransport = null,
                o => o.Timeout = TimeSpan.Zero,
                o => o.MaxRetries = -1,
                o => o.RetryDelay = TimeSpan.FromSeconds(-1),
            };
            for (int i = 0; i < invalid.Count; i++)
            {
                StrybokOptions options = T.Options(api);
                invalid[i](options);
                Assert.That(() => new StrybokClient(options), Throws.InstanceOf<ArgumentException>(), "case " + i);
            }
        }

        // ------------------------------------------------------------------
        // Host allowlist and parsing
        // ------------------------------------------------------------------

        [Test]
        public void IsStrybokUrl_AcceptsHttpsLinksOnAnAllowedHostWithASingleSlug()
        {
            StrybokClient sdk = Client(
                new FakeApi(),
                configure: o => o.AllowedHosts = new List<string> { " My-App.Strybok.Link " });

            Assert.That(sdk.IsStrybokUrl(T.Invite), Is.True);
            Assert.That(sdk.IsStrybokUrl("https://MY-APP.strybok.link/AbC_7-x"), Is.True);
            Assert.That(sdk.IsStrybokUrl("https://" + T.Host + "/abc?utm=1#frag"), Is.True);
            Assert.That(sdk.IsStrybokUrl("https://" + T.Host + "/abc#frag"), Is.True);
            Assert.That(sdk.IsStrybokUrl("HTTPS://" + T.Host + "/abc"), Is.True);
            Assert.That(sdk.IsStrybokUrl("https://" + T.Host + ":443/abc"), Is.True);
            Assert.That(sdk.IsStrybokUrl("https://" + T.Host + "/" + new string('a', 64)), Is.True);
        }

        [Test]
        public void IsStrybokUrl_RejectsEverythingElse()
        {
            StrybokClient sdk = Client(new FakeApi());
            string host = T.Host;
            var urls = new[]
            {
                null,
                "",
                "not a url",
                "http://" + host + "/invite777",
                "myapp://" + host + "/invite777",
                "https://evil.example.com/invite777",
                "https://" + host + ".evil.example.com/invite777",
                "https://evil-" + host + "/invite777",
                "https://user:pw@" + host + "/invite777",
                "https://user@" + host + "/invite777",
                "https://" + host + "@evil.example.com/invite777",
                "https://" + host + ":8443/invite777",
                "https://" + host + ":80/invite777",
                "https://" + host,
                "https://" + host + "/",
                "https://" + host + "/a/b",
                "https://" + host + "/abc/",
                "https://" + host + "/.well-known/assetlinks.json",
                "https://" + host + "/a%2Fb",
                "https://" + host + "/a b",
                "https://" + host + "/" + new string('a', 65),
                "https://" + host + "/abc\n",
                "https://" + host + "/abc?x=1\nhttps://evil.example.com/abc",
                " https://" + host + "/abc",
                "https://" + host + "\\@evil.example.com/abc",
                "https:/" + host + "/abc",
            };
            foreach (string url in urls)
            {
                Assert.That(sdk.IsStrybokUrl(url), Is.False, url ?? "(null)");
            }
        }

        [Test]
        public void Resolve_RefusesForeignLinksWithoutAnyNetworkCall()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api);
                const string foreign = "https://evil.example.com/invite777";

                UnsupportedLinkException error =
                    await T.Throws<UnsupportedLinkException>(() => sdk.ResolveAsync(foreign));

                Assert.That(error.Url, Is.EqualTo(foreign));
                Assert.That(api.Resolves, Is.Empty);
            });
        }

        // ------------------------------------------------------------------
        // Resolving
        // ------------------------------------------------------------------

        [Test]
        public void Resolve_ReturnsRouteAndPayloadAndSendsOnlyHostAndSlug()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                const string url = "https://MY-APP.strybok.link/invite777?token=secret#f";

                ResolvedDeepLink link = await Client(api).ResolveAsync(url);

                Assert.That(link.RoutePath, Is.EqualTo("/invite/abc123"));
                Assert.That(link.Payload.Count, Is.EqualTo(1));
                Assert.That(link.Payload["invitationId"], Is.EqualTo("abc123"));
                Assert.That(link.GetString("invitationId"), Is.EqualTo("abc123"));
                Assert.That(link.GetString("missing"), Is.Null);
                Assert.That(link.PayloadJson, Is.EqualTo("{\"invitationId\":\"abc123\"}"));
                Assert.That(link.LinkId, Is.EqualTo("link_1"));
                Assert.That(link.AppId, Is.EqualTo("app_1"));
                Assert.That(link.Url, Is.EqualTo(url), "the original URL is kept, with its query");
                Assert.That(link.Uri.Query, Is.EqualTo("?token=secret"));

                StrybokHttpRequest request = api.Resolves.Single();
                Assert.That(request.Method, Is.EqualTo("GET"));
                Assert.That(
                    request.Url,
                    Is.EqualTo("https://api.strybok.com/v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777"));
                Assert.That(request.Url, Does.Not.Contain("secret"));
                Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
                Assert.That(request.Body, Is.Null);

                await T.Settle();
                Assert.That(api.EventRequests, Is.Empty, "explicit resolution is not an app open");
            });
        }

        [Test]
        public void Resolve_KeepsAPathPrefixOfTheApiBaseUrl()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, configure: o => o.ApiBaseUrl = "https://example.com/strybok/");
                await sdk.ResolveAsync(T.Invite);
                Assert.That(new Uri(api.Resolves.Single().Url).AbsolutePath, Is.EqualTo("/strybok/v1/public/resolve"));
            });
        }

        [Test]
        public void Resolve_404BecomesLinkNotFoundAndIsNotRetried()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(404, "{\"error\":{\"code\":\"LINK_NOT_FOUND\"}}"));
                LinkNotFoundException error =
                    await T.Throws<LinkNotFoundException>(() => Client(api).ResolveAsync(T.Invite));
                Assert.That(error.Url, Is.EqualTo(T.Invite));
                Assert.That(api.Resolves, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void Resolve_OtherClientErrorsAreNotRetried()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 400, 422, 429, 301 })
                {
                    var api = new FakeApi(_ => T.Respond(status, "{}"));
                    StrybokServerException error =
                        await T.Throws<StrybokServerException>(() => Client(api).ResolveAsync(T.Invite));
                    Assert.That(error.StatusCode, Is.EqualTo(status));
                    Assert.That(error.Url, Is.EqualTo(T.Invite));
                    Assert.That(api.Resolves, Has.Count.EqualTo(1), "status " + status);
                }
            });
        }

        [Test]
        public void Resolve_RetriesNetworkErrorsAndThenSucceeds()
        {
            T.Run(async () =>
            {
                int calls = 0;
                var api = new FakeApi(_ =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1) throw new IOException("connection reset"); // thrown synchronously
                    if (call == 2) return Task.FromException<StrybokHttpResponse>(new IOException("connection reset"));
                    return T.Ok();
                });
                ResolvedDeepLink link = await Client(api).ResolveAsync(T.Invite);
                Assert.That(link.RoutePath, Is.EqualTo("/invite/abc123"));
                Assert.That(calls, Is.EqualTo(3));
            });
        }

        [Test]
        public void Resolve_GivesUpWithANetworkExceptionAfterMaxRetries()
        {
            T.Run(async () =>
            {
                var cause = new IOException("offline");
                var api = new FakeApi(_ => throw cause);
                StrybokNetworkException error = await T.Throws<StrybokNetworkException>(
                    () => Client(api, configure: o => o.MaxRetries = 1).ResolveAsync(T.Invite));
                Assert.That(error.InnerException, Is.SameAs(cause));
                Assert.That(error.Url, Is.EqualTo(T.Invite));
                Assert.That(api.Resolves, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void Resolve_Retries5xxResponses()
        {
            T.Run(async () =>
            {
                int calls = 0;
                var api = new FakeApi(_ => Interlocked.Increment(ref calls) == 1 ? T.Respond(503, string.Empty) : T.Ok());
                await Client(api).ResolveAsync(T.Invite);
                Assert.That(calls, Is.EqualTo(2));
            });
        }

        [Test]
        public void Resolve_ReportsTheLast5xxAfterAllRetries()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(502, "bad gateway"));
                StrybokServerException error =
                    await T.Throws<StrybokServerException>(() => Client(api).ResolveAsync(T.Invite));
                Assert.That(error.StatusCode, Is.EqualTo(502));
                Assert.That(api.Resolves, Has.Count.EqualTo(3), "1 attempt + maxRetries (2)");
            });
        }

        [Test]
        public void Resolve_TimesOutWithATypedExceptionAndCancelsTheCall()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Never());
                StrybokClient sdk = Client(api, configure: o =>
                {
                    o.MaxRetries = 1;
                    o.Timeout = TimeSpan.FromMilliseconds(40);
                });
                StrybokTimeoutException error =
                    await T.Throws<StrybokTimeoutException>(() => sdk.ResolveAsync(T.Invite));
                Assert.That(error.Url, Is.EqualTo(T.Invite));
                Assert.That(api.Resolves, Has.Count.EqualTo(2));
                Assert.That(api.Resolves[0].Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(40)));
                Assert.That(api.ResolveTokens.All(token => token.IsCancellationRequested), Is.True);
            });
        }

        [Test]
        public void Resolve_DoublesTheRetryDelay()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(500, string.Empty));
                StrybokClient sdk = Client(api, configure: o => o.RetryDelay = TimeSpan.FromMilliseconds(40));
                Stopwatch watch = Stopwatch.StartNew();
                await T.Throws<StrybokServerException>(() => sdk.ResolveAsync(T.Invite));
                watch.Stop();
                // 40 ms before the first retry + 80 ms before the second.
                Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(110));
                Assert.That(api.Resolves, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void Resolve_RejectsMalformedOrUnsafeResponses()
        {
            T.Run(async () =>
            {
                var bodies = new[]
                {
                    "",
                    "not json",
                    "[]",
                    "null",
                    "\"text\"",
                    "{\"linkId\":\"l\",\"appId\":\"a\"}",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"https://evil.example.com\"}",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"//evil.example.com\"}",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"\"}",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"relative\"}",
                    "{\"linkId\":1,\"appId\":\"a\",\"routePath\":\"/x\"}",
                    "{\"linkId\":\"l\",\"appId\":null,\"routePath\":\"/x\"}",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\"",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\"} trailing",
                };
                foreach (string body in bodies)
                {
                    var api = new FakeApi(_ => T.Respond(200, body));
                    StrybokServerException error =
                        await T.Throws<StrybokServerException>(() => Client(api).ResolveAsync(T.Invite));
                    Assert.That(error.StatusCode, Is.EqualTo(200), body);
                    Assert.That(api.Resolves, Has.Count.EqualTo(1), "a malformed answer is not retried: " + body);
                }
            });
        }

        [Test]
        public void Resolve_PayloadIsEmptyWhenMissingOrNotAnObject()
        {
            T.Run(async () =>
            {
                foreach (string payload in new[] { "", ",\"payload\":null", ",\"payload\":[1,2]", ",\"payload\":\"x\"", ",\"payload\":7" })
                {
                    string body = "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\"" + payload + "}";
                    var api = new FakeApi(_ => T.Respond(200, body));
                    ResolvedDeepLink link = await Client(api).ResolveAsync(T.Invite);
                    Assert.That(link.Payload, Is.Not.Null, body);
                    Assert.That(link.Payload.Count, Is.EqualTo(0), body);
                    Assert.That(link.PayloadJson, Is.EqualTo("{}"), body);
                }
            });
        }

        [Test]
        public void Resolve_DecodesNestedPayloadValues()
        {
            T.Run(async () =>
            {
                const string body =
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/shop/item\",\"extra\":true,\"payload\":" +
                    "{\"name\":\"Sw\\u00f6rd \\\"of\\\" fire\",\"level\":12,\"price\":9.5,\"rare\":true,\"owner\":null," +
                    "\"tags\":[\"a\",2,false],\"stats\":{\"attack\":7}}}";
                var api = new FakeApi(_ => T.Respond(200, body));

                ResolvedDeepLink link = await Client(api).ResolveAsync(T.Invite);

                Assert.That(link.Payload["name"], Is.EqualTo("Sw\u00f6rd \"of\" fire"));
                Assert.That(link.Payload["level"], Is.EqualTo(12L));
                Assert.That(link.Payload["level"], Is.TypeOf<long>());
                Assert.That(link.Payload["price"], Is.EqualTo(9.5d));
                Assert.That(link.Payload["rare"], Is.EqualTo(true));
                Assert.That(link.Payload["owner"], Is.Null);
                Assert.That((List<object>)link.Payload["tags"], Is.EqualTo(new object[] { "a", 2L, false }));
                Assert.That(((Dictionary<string, object>)link.Payload["stats"])["attack"], Is.EqualTo(7L));

                object reparsed;
                Assert.That(StrybokJson.TryParse(link.PayloadJson, out reparsed), Is.True);
                Assert.That(((Dictionary<string, object>)reparsed)["name"], Is.EqualTo("Sw\u00f6rd \"of\" fire"));
            });
        }

        // ------------------------------------------------------------------
        // Cold start
        // ------------------------------------------------------------------

        [Test]
        public void ColdStart_DeliversTheLaunchLinkOnceEvenIfTheListenerAttachesLate()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, new FakeLinkSource(T.Invite));
                await sdk.InitializeAsync();

                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await T.Settle();

                Assert.That(received.Items.Select(link => link.RoutePath), Is.EqualTo(new[] { "/invite/abc123" }));
                Assert.That(received.Items[0].Url, Is.EqualTo(T.Invite));
                Assert.That(api.Resolves, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void ColdStart_QueuedLinksGoToTheFirstSubscriberOnly()
        {
            T.Run(async () =>
            {
                var source = new FakeLinkSource(T.Invite);
                StrybokClient sdk = Client(new FakeApi(), source);
                await sdk.InitializeAsync();
                source.Emit("https://" + T.Host + "/second");
                await T.Settle();

                var first = new Bag<ResolvedDeepLink>();
                var second = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += first.Add;
                sdk.LinkReceived += second.Add;
                await T.Settle();

                Assert.That(first.Items.Select(link => link.Url), Is.EqualTo(new[] { T.Invite, "https://" + T.Host + "/second" }));
                Assert.That(second.Items, Is.Empty);
            });
        }

        [Test]
        public void ColdStart_DeduplicatesALaunchLinkThatIsReplayedOnTheStream()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, new FakeLinkSource(T.Invite, replayInitialLinkOnStream: true));
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();
                await T.Settle();

                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(api.Resolves, Has.Count.EqualTo(1));
                Assert.That(api.EventRequests, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void ColdStart_SubscribesToTheStreamBeforeReadingTheLaunchLink()
        {
            T.Run(async () =>
            {
                var source = new OrderRecordingSource();
                StrybokClient sdk = Client(new FakeApi(), configure: o => o.LinkSource = source);
                await sdk.InitializeAsync();
                Assert.That(source.Calls, Is.EqualTo(new[] { "subscribe", "getInitialLink" }));
            });
        }

        [Test]
        public void ColdStart_GetInitialLinkConsumesTheLaunchLinkSoTheEventDoesNotRepeatIt()
        {
            T.Run(async () =>
            {
                StrybokClient sdk = Client(new FakeApi(), new FakeLinkSource(T.Invite));
                ResolvedDeepLink initial = await sdk.GetInitialLinkAsync();
                Assert.That(initial, Is.Not.Null);
                Assert.That(initial.RoutePath, Is.EqualTo("/invite/abc123"));
                Assert.That(await sdk.GetInitialLinkAsync(), Is.Null);

                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await T.Settle();
                Assert.That(received.Items, Is.Empty);
            });
        }

        [Test]
        public void ColdStart_GetInitialLinkReturnsNullOnceTheEventAlreadyDeliveredIt()
        {
            T.Run(async () =>
            {
                StrybokClient sdk = Client(new FakeApi(), new FakeLinkSource(T.Invite));
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();
                await T.Settle();
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(await sdk.GetInitialLinkAsync(), Is.Null);
            });
        }

        [Test]
        public void ColdStart_NoLaunchLinkForeignLaunchLinkAndRepeatedInitializeAreHarmless()
        {
            T.Run(async () =>
            {
                foreach (string launchUrl in new[] { null, "", "https://other.example.com/x", "unitydl://whatever" })
                {
                    var api = new FakeApi();
                    var errors = new Bag<StrybokException>();
                    StrybokClient sdk = Client(api, new FakeLinkSource(launchUrl), errors);

                    Task first = sdk.InitializeAsync();
                    Task second = sdk.InitializeAsync();
                    Assert.That(second, Is.SameAs(first));
                    await Task.WhenAll(first, second);

                    Assert.That(await sdk.GetInitialLinkAsync(), Is.Null);
                    Assert.That(api.Resolves, Is.Empty);
                    Assert.That(errors.Items, Is.Empty);
                }
            });
        }

        [Test]
        public void ColdStart_AFailingLaunchLinkIsReportedThroughOnErrorAndInitializeCompletes()
        {
            T.Run(async () =>
            {
                var errors = new Bag<StrybokException>();
                StrybokClient sdk = Client(
                    new FakeApi(_ => T.Respond(404, string.Empty)), new FakeLinkSource(T.Invite), errors);

                await sdk.InitializeAsync();

                Assert.That(errors.Items.Single(), Is.InstanceOf<LinkNotFoundException>());
                Assert.That(errors.Items.Single().Url, Is.EqualTo(T.Invite));
                Assert.That(await sdk.GetInitialLinkAsync(), Is.Null);
            });
        }

        [Test]
        public void ColdStart_ASourceThatCannotTellTheLaunchLinkIsTreatedAsNoLaunchLink()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var source = new OrderRecordingSource { FailInitialLink = true };
                StrybokClient sdk = Client(api, configure: o => o.LinkSource = source);
                await sdk.InitializeAsync();
                Assert.That(await sdk.GetInitialLinkAsync(), Is.Null);

                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                source.Emit(T.Invite);
                await T.Until(() => received.Count == 1, "later links still work");
            });
        }

        // ------------------------------------------------------------------
        // Running app
        // ------------------------------------------------------------------

        [Test]
        public void Running_DeliversEachForegroundLinkExactlyOnce()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(request =>
                {
                    string lookup = Uri.UnescapeDataString(request.Url.Substring(request.Url.IndexOf("?url=", StringComparison.Ordinal) + 5));
                    string slug = new Uri(lookup).AbsolutePath.Substring(1);
                    return T.Ok("/p/" + slug, "link_" + slug);
                });
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(api, source);
                var received = new Bag<string>();
                sdk.LinkReceived += link => received.Add(link.RoutePath);
                await sdk.InitializeAsync();

                source.Emit("https://" + T.Host + "/first");
                source.Emit("https://" + T.Host + "/second");
                source.Emit("https://evil.example.com/third");
                source.Emit(null);
                await T.Settle();

                Assert.That(received.Items, Is.EquivalentTo(new[] { "/p/first", "/p/second" }));
                Assert.That(api.Resolves, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void Running_DropsAnImmediateDuplicateButHonoursTheSameLinkTappedAgainLater()
        {
            T.Run(async () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var source = new FakeLinkSource();
                var api = new FakeApi();
                StrybokClient sdk = Client(api, source, configure: o => o.Clock = () => now);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();

                source.Emit(T.Invite);
                source.Emit(T.Invite);
                now = now.AddSeconds(1.9);
                source.Emit(T.Invite);
                await T.Settle();
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(api.Resolves, Has.Count.EqualTo(1));

                // App paused, user taps the same link again a minute later (resume).
                now = now.AddMinutes(1);
                source.Emit(T.Invite);
                await T.Settle();
                Assert.That(received.Count, Is.EqualTo(2));
                Assert.That(api.Resolves, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void Running_DifferentQueryIsADifferentUrlAndNotADuplicate()
        {
            T.Run(async () =>
            {
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(new FakeApi(), source);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();

                source.Emit(T.Invite + "?a=1");
                source.Emit(T.Invite + "?a=2");
                await T.Settle();
                Assert.That(received.Items.Select(link => link.Url), Is.EquivalentTo(new[] { T.Invite + "?a=1", T.Invite + "?a=2" }));
            });
        }

        [Test]
        public void Running_MultipleListenersEachSeeALinkOnce()
        {
            T.Run(async () =>
            {
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(new FakeApi(), source);
                var a = new Bag<ResolvedDeepLink>();
                var b = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += a.Add;
                sdk.LinkReceived += b.Add;
                await sdk.InitializeAsync();
                source.Emit(T.Invite);
                await T.Settle();
                Assert.That(a.Count, Is.EqualTo(1));
                Assert.That(b.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Running_LinksAreQueuedAgainWhileNobodyListens()
        {
            T.Run(async () =>
            {
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(new FakeApi(), source);
                var received = new Bag<ResolvedDeepLink>();
                Action<ResolvedDeepLink> handler = received.Add;
                sdk.LinkReceived += handler;
                await sdk.InitializeAsync();
                sdk.LinkReceived -= handler;

                source.Emit(T.Invite);
                await T.Settle();
                Assert.That(received.Items, Is.Empty);

                sdk.LinkReceived += handler;
                await T.Until(() => received.Count == 1, "queued link delivered on re-subscribe");
            });
        }

        [Test]
        public void Running_ErrorsGoToOnErrorAndLaterLinksKeepWorking()
        {
            T.Run(async () =>
            {
                bool fail = true;
                var errors = new Bag<StrybokException>();
                var source = new FakeLinkSource();
                var api = new FakeApi(_ =>
                {
                    if (Volatile.Read(ref fail)) throw new IOException("offline");
                    return T.Ok();
                });
                StrybokClient sdk = Client(api, source, errors, o => o.MaxRetries = 0);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();

                source.Emit("https://" + T.Host + "/offline1");
                await T.Settle();
                Assert.That(errors.Items.Single(), Is.InstanceOf<StrybokNetworkException>());
                Assert.That(errors.Items.Single().Url, Is.EqualTo("https://" + T.Host + "/offline1"));
                Assert.That(received.Items, Is.Empty);
                Assert.That(api.EventRequests, Is.Empty, "a failed link is not an app open");

                Volatile.Write(ref fail, false);
                source.Emit("https://" + T.Host + "/online1");
                await T.Settle();
                Assert.That(received.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Running_AThrowingHandlerDoesNotBlockOtherHandlersOrLaterLinks()
        {
            T.Run(async () =>
            {
                var source = new FakeLinkSource();
                var handlerErrors = new Bag<Exception>();
                var errors = new Bag<StrybokException>();
                StrybokClient sdk = Client(new FakeApi(), source, errors, o => o.OnHandlerException = handlerErrors.Add);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += _ => throw new InvalidOperationException("game bug");
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();

                source.Emit("https://" + T.Host + "/one");
                source.Emit("https://" + T.Host + "/two");
                await T.Settle();

                Assert.That(received.Count, Is.EqualTo(2));
                Assert.That(handlerErrors.Count, Is.EqualTo(2));
                Assert.That(handlerErrors.Items[0], Is.InstanceOf<InvalidOperationException>());
                Assert.That(errors.Items, Is.Empty, "a handler bug is not a link error");
            });
        }

        [Test]
        public void Running_HandlersArePostedToTheCallbackContext()
        {
            T.Run(async () =>
            {
                var context = new RecordingContext();
                var errors = new Bag<StrybokException>();
                var source = new FakeLinkSource(T.Invite);
                int calls = 0;
                var api = new FakeApi(_ => Interlocked.Increment(ref calls) == 1 ? T.Ok() : T.Respond(404, string.Empty));
                StrybokClient sdk = Client(api, source, errors, o => o.CallbackContext = context);
                await sdk.InitializeAsync();

                var received = new Bag<ResolvedDeepLink>();
                var onContext = new Bag<bool>();
                sdk.LinkReceived += link =>
                {
                    onContext.Add(RecordingContext.IsExecuting);
                    received.Add(link);
                };
                source.Emit("https://" + T.Host + "/missing");
                await T.Settle();

                Assert.That(received.Items, Is.Empty, "nothing runs until the context does");
                Assert.That(errors.Items, Is.Empty);
                Assert.That(context.Pending, Is.EqualTo(2), "queued link replay + error");

                context.RunAll();
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(onContext.Items, Is.EqualTo(new[] { true }));
                Assert.That(errors.Items.Single(), Is.InstanceOf<LinkNotFoundException>());
            });
        }

        // ------------------------------------------------------------------
        // app_open events
        // ------------------------------------------------------------------

        [Test]
        public void AppOpen_SendsOneMinimalBestEffortEventPerDeliveredLink()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(api, source);
                sdk.LinkReceived += _ => { };
                await sdk.InitializeAsync();
                source.Emit(T.Invite);
                await T.Until(() => api.EventRequests.Count == 1, "app_open event");
                await T.Settle();

                StrybokHttpRequest request = api.EventRequests.Single();
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.Url, Is.EqualTo("https://api.strybok.com/v1/public/events"));
                Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/json"));

                Dictionary<string, object> body = api.Events.Single();
                Assert.That(body.Keys, Is.EquivalentTo(new[] { "eventId", "linkId", "type", "platform", "sdkVersion" }));
                Assert.That(body["type"], Is.EqualTo("app_open"));
                Assert.That(body["linkId"], Is.EqualTo("link_1"));
                Assert.That(body["platform"], Is.EqualTo("unknown"));
                Assert.That(body["sdkVersion"], Is.EqualTo("0.4.0"));
                Assert.That(((string)body["sdkVersion"]).Length, Is.LessThanOrEqualTo(32));
                Assert.That(
                    Regex.IsMatch((string)body["eventId"], "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"),
                    Is.True,
                    (string)body["eventId"]);
            });
        }

        [Test]
        public void AppOpen_ReportsTheConfiguredPlatformAndAFreshEventIdEachTime()
        {
            T.Run(async () =>
            {
                var expected = new Dictionary<StrybokPlatform, string>
                {
                    { StrybokPlatform.IOS, "ios" },
                    { StrybokPlatform.Android, "android" },
                    { StrybokPlatform.Unknown, "unknown" },
                };
                var eventIds = new HashSet<string>();
                foreach (KeyValuePair<StrybokPlatform, string> pair in expected)
                {
                    var api = new FakeApi();
                    StrybokClient sdk = Client(api, new FakeLinkSource(T.Invite), configure: o => o.Platform = pair.Key);
                    await sdk.InitializeAsync();
                    await T.Until(() => api.EventRequests.Count == 1, "app_open event");
                    Assert.That(api.Events.Single()["platform"], Is.EqualTo(pair.Value));
                    eventIds.Add((string)api.Events.Single()["eventId"]);
                }
                Assert.That(eventIds, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void AppOpen_IsSentForAQueuedLinkToo()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, new FakeLinkSource(T.Invite));
                await sdk.InitializeAsync(); // nobody listens: the link is queued
                await T.Until(() => api.EventRequests.Count == 1, "app_open event");
            });
        }

        [Test]
        public void AppOpen_ASlowOrFailingEventsEndpointNeverDelaysOrBreaksDelivery()
        {
            T.Run(async () =>
            {
                var behaviours = new Func<Task<StrybokHttpResponse>>[]
                {
                    () => T.Never(),
                    () => throw new IOException("down"),
                    () => Task.FromException<StrybokHttpResponse>(new IOException("down")),
                    () => T.Respond(500, string.Empty),
                    () => null,
                };
                foreach (Func<Task<StrybokHttpResponse>> onEvent in behaviours)
                {
                    var api = new FakeApi { OnEvent = onEvent };
                    var source = new FakeLinkSource();
                    var errors = new Bag<StrybokException>();
                    StrybokClient sdk = Client(api, source, errors);
                    var received = new Bag<ResolvedDeepLink>();
                    sdk.LinkReceived += received.Add;
                    await sdk.InitializeAsync();
                    source.Emit(T.Invite);
                    await T.Settle();
                    Assert.That(received.Count, Is.EqualTo(1));
                    Assert.That(errors.Items, Is.Empty);
                    Assert.That(api.EventRequests, Has.Count.EqualTo(1), "events are not retried");
                }
            });
        }

        [Test]
        public void AppOpen_CanBeTurnedOff()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, new FakeLinkSource(T.Invite), configure: o => o.ReportAppOpens = false);
                await sdk.InitializeAsync();
                await T.Settle();
                Assert.That(api.Resolves, Has.Count.EqualTo(1));
                Assert.That(api.EventRequests, Is.Empty);
            });
        }

        // ------------------------------------------------------------------
        // Dispose
        // ------------------------------------------------------------------

        [Test]
        public void Dispose_StopsDeliveryAndRejectsFurtherUse()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(api, source);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();
                Assert.That(source.HasListener, Is.True);

                sdk.Dispose();
                sdk.Dispose(); // idempotent
                Assert.That(source.HasListener, Is.False);

                source.Emit(T.Invite);
                await T.Settle();
                Assert.That(received.Items, Is.Empty);
                Assert.That(api.Resolves, Is.Empty);
                await T.Throws<ObjectDisposedException>(() => sdk.ResolveAsync(T.Invite));
                Assert.That(() => { sdk.InitializeAsync(); }, Throws.InstanceOf<ObjectDisposedException>());
                sdk.LinkReceived += received.Add; // harmless
            });
        }

        [Test]
        public void Dispose_DropsQueuedLinks()
        {
            T.Run(async () =>
            {
                StrybokClient sdk = Client(new FakeApi(), new FakeLinkSource(T.Invite));
                await sdk.InitializeAsync();
                sdk.Dispose();

                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await T.Settle();
                Assert.That(received.Items, Is.Empty);
            });
        }

        [Test]
        public void Dispose_ALinkStillResolvingWhenDisposedIsNotDelivered()
        {
            T.Run(async () =>
            {
                var pending = new TaskCompletionSource<StrybokHttpResponse>();
                var source = new FakeLinkSource();
                var errors = new Bag<StrybokException>();
                var api = new FakeApi(_ => pending.Task);
                StrybokClient sdk = Client(api, source, errors, o => o.Timeout = TimeSpan.FromSeconds(5));
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();
                source.Emit(T.Invite);
                await T.Until(() => api.Resolves.Count == 1, "request in flight");

                sdk.Dispose();
                Assert.That(api.ResolveTokens.Single().IsCancellationRequested, Is.True, "the request is aborted");
                pending.SetResult(T.Ok().Result);
                await T.Settle();

                Assert.That(received.Items, Is.Empty);
                Assert.That(errors.Items, Is.Empty);
                Assert.That(api.EventRequests, Is.Empty);
            });
        }

        [Test]
        public void Dispose_DuringARetryPauseStopsRetrying()
        {
            T.Run(async () =>
            {
                var errors = new Bag<StrybokException>();
                var source = new FakeLinkSource();
                var api = new FakeApi(_ => T.Respond(503, string.Empty));
                StrybokClient sdk = Client(api, source, errors, o => o.RetryDelay = TimeSpan.FromSeconds(30));
                await sdk.InitializeAsync();
                source.Emit(T.Invite);
                await T.Until(() => api.Resolves.Count == 1, "first attempt");

                sdk.Dispose();
                await T.Settle();
                Assert.That(api.Resolves, Has.Count.EqualTo(1));
                Assert.That(errors.Items, Is.Empty);
            });
        }

        // ------------------------------------------------------------------

        private sealed class OrderRecordingSource : ILinkSource
        {
            private readonly Bag<string> _calls = new Bag<string>();
            private Action<string> _handlers;

            public bool FailInitialLink { get; set; }

            public List<string> Calls
            {
                get { return _calls.Items; }
            }

            public event Action<string> UrlReceived
            {
                add
                {
                    _calls.Add("subscribe");
                    _handlers += value;
                }
                remove
                {
                    _handlers -= value;
                }
            }

            public void Emit(string url)
            {
                Action<string> handlers = _handlers;
                if (handlers != null) handlers(url);
            }

            public Task<string> GetInitialLinkAsync()
            {
                _calls.Add("getInitialLink");
                if (FailInitialLink) throw new InvalidOperationException("platform error");
                return Task.FromResult<string>(null);
            }
        }

        /// <summary>A synchronization context that only runs work when told to.</summary>
        private sealed class RecordingContext : SynchronizationContext
        {
            [ThreadStatic] private static bool t_executing;

            private readonly Queue<Action> _work = new Queue<Action>();

            public static bool IsExecuting
            {
                get { return t_executing; }
            }

            public int Pending
            {
                get { lock (_work) return _work.Count; }
            }

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_work) _work.Enqueue(() => callback(state));
            }

            public void RunAll()
            {
                while (true)
                {
                    Action next;
                    lock (_work)
                    {
                        if (_work.Count == 0) return;
                        next = _work.Dequeue();
                    }
                    t_executing = true;
                    try
                    {
                        next();
                    }
                    finally
                    {
                        t_executing = false;
                    }
                }
            }
        }
    }
}
