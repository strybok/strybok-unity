using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;

namespace Strybok.Tests
{
    /// <summary>"Creating links" from the SDK contract (Documentation~/sdk-contract.md).</summary>
    public sealed class StrybokCreateLinkTests
    {
        private const string SecretMarker = "SECRET-PAYLOAD-MARKER";

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
            options.ClientKey = T.ClientKey;
            if (errors != null) options.OnError = errors.Add;
            if (configure != null) configure(options);
            var client = new StrybokClient(options);
            lock (_clients) _clients.Add(client);
            return client;
        }

        private static Dictionary<string, object> Payload()
        {
            return new Dictionary<string, object> { { "productId", 42 }, { "note", SecretMarker } };
        }

        private static Task<CreatedLink> CreateFull(StrybokClient sdk)
        {
            return sdk.CreateLinkAsync(
                "/product/42", Payload(), "Red shoes", "Look at these", "https://cdn.example.com/shoes.png");
        }

        private static Dictionary<string, object> Json(string text)
        {
            object parsed;
            Assert.That(StrybokJson.TryParse(text, out parsed), Is.True, "valid JSON: " + text);
            return (Dictionary<string, object>)parsed;
        }

        private static string Header(StrybokHttpRequest request, string name)
        {
            string found = null;
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                if (!string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                Assert.That(found, Is.Null, "header sent once: " + name);
                found = header.Value;
            }
            return found;
        }

        // ------------------------------------------------------------------
        // Success and the request
        // ------------------------------------------------------------------

        [Test]
        public void Status201_ReturnsTheCreatedLinkAndSendsTheContractRequest()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api);

                CreatedLink link = await CreateFull(sdk);

                Assert.That(link.Url, Is.EqualTo("https://my-app.strybok.link/Ab3xK9"));
                Assert.That(link.Uri.Host, Is.EqualTo(T.Host));
                Assert.That(link.Slug, Is.EqualTo("Ab3xK9"));
                Assert.That(link.RoutePath, Is.EqualTo("/product/42"));
                Assert.That(link.LinkId, Is.EqualTo("link_9"));

                Assert.That(api.Creates, Has.Count.EqualTo(1));
                StrybokHttpRequest request = api.Creates[0];
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.Url, Is.EqualTo("https://api.strybok.com/v1/public/links"));
                Assert.That(request.Headers, Has.Count.EqualTo(3));
                Assert.That(Header(request, "Content-Type"), Is.EqualTo("application/json"));
                Assert.That(Header(request, "Accept"), Is.EqualTo("application/json"));
                Assert.That(Header(request, "X-Strybok-Key"), Is.EqualTo(T.ClientKey));
                Assert.That(Header(request, "Authorization"), Is.Null);
                Assert.That(request.Url, Does.Not.Contain(T.ClientKey));
                Assert.That(request.Body, Does.Not.Contain(T.ClientKey));
                Assert.That(request.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(150)));

                Assert.That(
                    request.Body,
                    Is.EqualTo(
                        "{\"routePath\":\"/product/42\",\"payload\":{\"productId\":42,\"note\":\"SECRET-PAYLOAD-MARKER\"}," +
                        "\"title\":\"Red shoes\",\"description\":\"Look at these\"," +
                        "\"imageUrl\":\"https://cdn.example.com/shoes.png\"}"));
                Dictionary<string, object> body = Json(request.Body);
                Assert.That(
                    body.Keys,
                    Is.EquivalentTo(new[] { "routePath", "payload", "title", "description", "imageUrl" }));
                Assert.That(((Dictionary<string, object>)body["payload"])["productId"], Is.EqualTo(42L));

                Assert.That(api.Resolves, Is.Empty);
                Assert.That(api.EventRequests, Is.Empty);
            });
        }

        [Test]
        public void Status200_MeansAnIdenticalLinkExistedAndIsSuccessToo()
        {
            T.Run(async () =>
            {
                var api = new FakeApi { OnCreate = _ => T.Respond(200, T.CreatedBody()) };
                CreatedLink link = await Client(api).CreateLinkAsync("/product/42");
                Assert.That(link.Url, Is.EqualTo("https://my-app.strybok.link/Ab3xK9"));
                Assert.That(api.Creates, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void ArgumentsThatWereNotGiven_AreOmittedFromTheBody()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api);

                await sdk.CreateLinkAsync("/product/42");
                await sdk.CreateLinkAsync("/product/42", title: "Only a title");
                await sdk.CreateLinkAsync("/product/42", new Dictionary<string, object>());
                await sdk.CreateLinkAsync("/product/42", description: "d", imageUrl: "https://cdn.example.com/a.png");
                await sdk.CreateLinkWithPayloadJsonAsync("/product/42", null);

                List<StrybokHttpRequest> creates = api.Creates;
                Assert.That(creates[0].Body, Is.EqualTo("{\"routePath\":\"/product/42\"}"));
                Assert.That(creates[0].Body, Does.Not.Contain("null"));
                Assert.That(creates[1].Body, Is.EqualTo("{\"routePath\":\"/product/42\",\"title\":\"Only a title\"}"));
                Assert.That(creates[2].Body, Is.EqualTo("{\"routePath\":\"/product/42\",\"payload\":{}}"));
                Assert.That(
                    creates[3].Body,
                    Is.EqualTo("{\"routePath\":\"/product/42\",\"description\":\"d\",\"imageUrl\":\"https://cdn.example.com/a.png\"}"));
                Assert.That(creates[4].Body, Is.EqualTo("{\"routePath\":\"/product/42\"}"));
            });
        }

        [Test]
        public void TextAndNestedPayloads_AreWrittenAsJson()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var payload = new Dictionary<string, object>
                {
                    { "text", "quote \" and \\ and \n" },
                    { "flag", true },
                    { "nothing", null },
                    { "ratio", 1.5 },
                    { "list", new List<object> { 1, "two", new Dictionary<string, object> { { "three", 3L } } } },
                };

                await Client(api).CreateLinkAsync("/say/\"hi\"", payload, "line1\nline2");

                Dictionary<string, object> body = Json(api.Creates[0].Body);
                Assert.That(body["routePath"], Is.EqualTo("/say/\"hi\""));
                Assert.That(body["title"], Is.EqualTo("line1\nline2"));
                var sent = (Dictionary<string, object>)body["payload"];
                Assert.That(sent["text"], Is.EqualTo("quote \" and \\ and \n"));
                Assert.That(sent["flag"], Is.EqualTo(true));
                Assert.That(sent["nothing"], Is.Null);
                Assert.That(sent["ratio"], Is.EqualTo(1.5));
                var list = (List<object>)sent["list"];
                Assert.That(list[0], Is.EqualTo(1L));
                Assert.That(list[1], Is.EqualTo("two"));
                Assert.That(((Dictionary<string, object>)list[2])["three"], Is.EqualTo(3L));
            });
        }

        [Test]
        public void PayloadJson_IsSentAsWritten()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                await Client(api).CreateLinkWithPayloadJsonAsync(
                    "/product/42", " \n{\"big\":12345678901234567890123, \"a\":[1,{\"b\":null}]}\t", "T");
                Assert.That(
                    api.Creates[0].Body,
                    Is.EqualTo(
                        "{\"routePath\":\"/product/42\",\"payload\":{\"big\":12345678901234567890123, \"a\":[1,{\"b\":null}]}," +
                        "\"title\":\"T\"}"));
            });
        }

        [Test]
        public void KeepsAPathPrefixOfTheApiBaseUrl()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api, configure: o => o.ApiBaseUrl = "https://example.com/strybok/");
                await sdk.CreateLinkAsync("/product/42");
                Assert.That(api.Creates[0].Url, Is.EqualTo("https://example.com/strybok/v1/public/links"));
            });
        }

        [Test]
        public void TheCreatedUrl_IsNotCheckedAgainstAllowedHosts()
        {
            T.Run(async () =>
            {
                var api = new FakeApi
                {
                    OnCreate = _ => T.Respond(
                        201,
                        "{\"linkId\":\"l\",\"url\":\"https://links.custom-domain.example/Zz9\",\"slug\":\"Zz9\"," +
                        "\"routePath\":\"/product/42\",\"extra\":true}"),
                };
                CreatedLink link = await Client(api).CreateLinkAsync("/product/42");
                Assert.That(link.Url, Is.EqualTo("https://links.custom-domain.example/Zz9"));
            });
        }

        // ------------------------------------------------------------------
        // Before any network call
        // ------------------------------------------------------------------

        [Test]
        public void WithoutAClientKey_FailsAsUnauthorizedWithoutANetworkCall()
        {
            T.Run(async () =>
            {
                var keys = new[] { null, "", "   ", "\t\n", "pk_with space", "pk_line\nbreak", "pk_é" };
                foreach (string key in keys)
                {
                    var api = new FakeApi();
                    StrybokClient sdk = Client(api, configure: o => o.ClientKey = key);
                    StrybokUnauthorizedException error =
                        await T.Throws<StrybokUnauthorizedException>(() => CreateFull(sdk));
                    Assert.That(error.Url, Is.Null);
                    Assert.That(api.Creates, Is.Empty, "no network call for key: " + key);
                }
            });
        }

        [Test]
        public void TheClientKey_IsTrimmedAndCheckedBeforeTheRoute()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                await Client(api, configure: o => o.ClientKey = "  " + T.ClientKey + "\n").CreateLinkAsync("/a");
                Assert.That(Header(api.Creates[0], "X-Strybok-Key"), Is.EqualTo(T.ClientKey));

                StrybokClient keyless = Client(api, configure: o => o.ClientKey = null);
                await T.Throws<StrybokUnauthorizedException>(() => keyless.CreateLinkAsync("no-slash"));
            });
        }

        [Test]
        public void ABadRoutePath_FailsAsValidationWithoutANetworkCall()
        {
            T.Run(async () =>
            {
                var routes = new[]
                {
                    null, "", "product/42", "//evil.example.com/x", "//", "https://evil.example.com/x", " /product",
                    "/" + new string('a', 512),
                };
                var api = new FakeApi();
                StrybokClient sdk = Client(api);
                foreach (string route in routes)
                {
                    StrybokValidationException error = await T.Throws<StrybokValidationException>(
                        () => sdk.CreateLinkAsync(route, Payload(), "t"));
                    Assert.That(error.StatusCode, Is.EqualTo(0), "route: " + route);
                    Assert.That(error.Issues, Is.Empty);
                    Assert.That(error.Message, Does.Contain("routePath"));
                }
                Assert.That(api.Creates, Is.Empty);

                await sdk.CreateLinkAsync("/" + new string('a', 511));
                await sdk.CreateLinkAsync("/");
                Assert.That(api.Creates, Has.Count.EqualTo(2), "512 characters and the root route are fine");
            });
        }

        [Test]
        public void APayloadThatIsNotAJsonObject_FailsAsValidationWithoutANetworkCall()
        {
            T.Run(async () =>
            {
                var payloads = new[]
                {
                    "[1,2]", "42", "\"text\"", "true", "null", "", " ", "{\"a\":", "{'a':1}", "{\"a\":1} trailing",
                    "{\"note\":\"" + SecretMarker + "\"",
                };
                var api = new FakeApi();
                StrybokClient sdk = Client(api);
                foreach (string payload in payloads)
                {
                    StrybokValidationException error = await T.Throws<StrybokValidationException>(
                        () => sdk.CreateLinkWithPayloadJsonAsync("/product/42", payload));
                    Assert.That(error.Message, Is.EqualTo("payload must be a JSON object"), "payload: " + payload);
                    Assert.That(error.StatusCode, Is.EqualTo(0));
                    Assert.That(error.ToString(), Does.Not.Contain(SecretMarker));
                }

                // A dictionary nested deeper than JSON writers accept.
                var deep = new Dictionary<string, object>();
                Dictionary<string, object> current = deep;
                for (int i = 0; i < 100; i++)
                {
                    var next = new Dictionary<string, object>();
                    current["k"] = next;
                    current = next;
                }
                current["note"] = SecretMarker;
                StrybokValidationException tooDeep =
                    await T.Throws<StrybokValidationException>(() => sdk.CreateLinkAsync("/product/42", deep));
                Assert.That(tooDeep.ToString(), Does.Not.Contain(SecretMarker));
                Assert.That(tooDeep.InnerException, Is.Null);

                Assert.That(api.Creates, Is.Empty);
            });
        }

        // ------------------------------------------------------------------
        // Answers
        // ------------------------------------------------------------------

        [Test]
        public void AMalformedSuccessBody_IsAServerErrorWithTheStatusAndIsNotRetried()
        {
            T.Run(async () =>
            {
                var bodies = new[]
                {
                    "",
                    "not json",
                    "[]",
                    "\"https://my-app.strybok.link/Ab3xK9\"",
                    "{}",
                    "{\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\"}",
                    "{\"linkId\":9,\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":null,\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"http://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"myapp://Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":\"\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                    "{\"linkId\":\"link_9\",\"url\":[\"https://x\"],\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
                };
                foreach (int status in new[] { 200, 201 })
                {
                    foreach (string body in bodies)
                    {
                        var api = new FakeApi { OnCreate = _ => T.Respond(status, body) };
                        StrybokClient sdk = Client(api);
                        StrybokServerException error =
                            await T.Throws<StrybokServerException>(() => sdk.CreateLinkAsync("/product/42"));
                        Assert.That(error.StatusCode, Is.EqualTo(status), "body: " + body);
                        Assert.That(error.Url, Is.Null);
                        Assert.That(api.Creates, Has.Count.EqualTo(1), "not retried: " + body);
                    }
                }
            });
        }

        [Test]
        public void Status401_IsUnauthorizedAndIsNotRetried()
        {
            T.Run(async () =>
            {
                var api = new FakeApi
                {
                    OnCreate = _ => T.Respond(
                        401, "{\"error\":{\"code\":\"UNAUTHENTICATED\",\"message\":\"Authentication required\",\"details\":{}}}"),
                };
                StrybokClient sdk = Client(api);
                await T.Throws<StrybokUnauthorizedException>(() => CreateFull(sdk));
                Assert.That(api.Creates, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void Status403_IsRouteNotAllowedWithTheRouteAndIsNotRetried()
        {
            T.Run(async () =>
            {
                var api = new FakeApi
                {
                    OnCreate = _ => T.Respond(
                        403, "{\"error\":{\"code\":\"ROUTE_NOT_ALLOWED\",\"message\":\"Route not allowed\",\"details\":{}}}"),
                };
                StrybokClient sdk = Client(api);
                RouteNotAllowedException error = await T.Throws<RouteNotAllowedException>(() => CreateFull(sdk));
                Assert.That(error.RoutePath, Is.EqualTo("/product/42"));
                Assert.That(api.Creates, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void Status429_IsRateLimitedWithRetryAfterAndIsNeverRetried()
        {
            T.Run(async () =>
            {
                var api = new FakeApi
                {
                    OnCreate = _ => T.Respond(
                        429,
                        "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"Too many\",\"details\":{\"period\":\"day\"}}}",
                        "60"),
                };
                StrybokClient sdk = Client(api);
                StrybokRateLimitedException error = await T.Throws<StrybokRateLimitedException>(() => CreateFull(sdk));
                Assert.That(error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(60)));
                Assert.That(api.Creates, Has.Count.EqualTo(1));

                api.OnCreate = _ => T.Respond(429, string.Empty);
                error = await T.Throws<StrybokRateLimitedException>(() => sdk.CreateLinkAsync("/product/42"));
                Assert.That(error.RetryAfter, Is.Null, "no Retry-After header");

                api.OnCreate = _ => T.Respond(429, string.Empty, "Wed, 21 Oct 2026 07:28:00 GMT");
                error = await T.Throws<StrybokRateLimitedException>(() => sdk.CreateLinkAsync("/product/42"));
                Assert.That(error.RetryAfter, Is.Null, "only whole seconds are understood");
                Assert.That(api.Creates, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void RetryAfter_IsReadAsWholeSeconds()
        {
            Assert.That(StrybokClient.ParseRetryAfter("60"), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(StrybokClient.ParseRetryAfter(" 86400 "), Is.EqualTo(TimeSpan.FromSeconds(86400)));
            Assert.That(StrybokClient.ParseRetryAfter("0"), Is.EqualTo(TimeSpan.Zero));
            Assert.That(StrybokClient.ParseRetryAfter(null), Is.Null);
            Assert.That(StrybokClient.ParseRetryAfter(""), Is.Null);
            Assert.That(StrybokClient.ParseRetryAfter("  "), Is.Null);
            Assert.That(StrybokClient.ParseRetryAfter("-5"), Is.Null);
            Assert.That(StrybokClient.ParseRetryAfter("1.5"), Is.Null);
            Assert.That(StrybokClient.ParseRetryAfter("soon"), Is.Null);
            Assert.That(
                StrybokClient.ParseRetryAfter("99999999999999999999999999"),
                Is.EqualTo(TimeSpan.FromSeconds(int.MaxValue)));
        }

        [Test]
        public void ResponseHeaders_AreFoundWhateverTheirCase()
        {
            var response = new StrybokHttpResponse(
                429, null, new Dictionary<string, string> { { "Retry-After", "7" }, { "X-Other", "x" } });
            Assert.That(response.GetHeader("retry-after"), Is.EqualTo("7"));
            Assert.That(response.GetHeader("RETRY-AFTER"), Is.EqualTo("7"));
            Assert.That(response.GetHeader("missing"), Is.Null);
            Assert.That(response.GetHeader(null), Is.Null);
            Assert.That(response.Body, Is.EqualTo(string.Empty));

            var plain = new StrybokHttpResponse(200, "x");
            Assert.That(plain.Headers, Is.Empty);
            Assert.That(plain.GetHeader("Retry-After"), Is.Null);
        }

        [Test]
        public void Status400And413And422_AreValidationWithTheServersMessageAndIssues()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 400, 413, 422 })
                {
                    var api = new FakeApi
                    {
                        OnCreate = _ => T.Respond(
                            status,
                            "{\"error\":{\"code\":\"VALIDATION_ERROR\",\"message\":\"The request is invalid\",\"details\":{\"issues\":[" +
                            "{\"path\":\"title\",\"message\":\"Too long\"},{\"path\":\"\",\"message\":\"Unrecognized key\"}," +
                            "\"junk\",{\"path\":7}]}}}"),
                    };
                    StrybokClient sdk = Client(api);
                    StrybokValidationException error = await T.Throws<StrybokValidationException>(() => CreateFull(sdk));
                    Assert.That(error.StatusCode, Is.EqualTo(status));
                    Assert.That(error.Message, Is.EqualTo("The request is invalid"));
                    Assert.That(error.Issues, Has.Count.EqualTo(2));
                    Assert.That(error.Issues[0].Path, Is.EqualTo("title"));
                    Assert.That(error.Issues[0].Message, Is.EqualTo("Too long"));
                    Assert.That(error.Issues[0].ToString(), Is.EqualTo("title: Too long"));
                    Assert.That(error.Issues[1].Path, Is.EqualTo(string.Empty));
                    Assert.That(error.Issues[1].ToString(), Is.EqualTo("Unrecognized key"));
                    Assert.That(api.Creates, Has.Count.EqualTo(1), "not retried");
                }
            });
        }

        [Test]
        public void AValidationAnswerWithoutAReadableBody_StillIsValidation()
        {
            T.Run(async () =>
            {
                var bodies = new[] { "", "<html>", "[]", "{\"error\":\"nope\"}", "{\"error\":{\"message\":7,\"details\":[]}}" };
                foreach (string body in bodies)
                {
                    var api = new FakeApi { OnCreate = _ => T.Respond(413, body) };
                    StrybokClient sdk = Client(api);
                    StrybokValidationException error =
                        await T.Throws<StrybokValidationException>(() => sdk.CreateLinkAsync("/product/42"));
                    Assert.That(error.StatusCode, Is.EqualTo(413));
                    Assert.That(error.Message, Is.Not.Empty);
                    Assert.That(error.Issues, Is.Empty);
                }
            });
        }

        [Test]
        public void OtherClientErrorsAndUnexpectedStatuses_AreServerErrorsAndAreNotRetried()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 402, 404, 405, 409, 418, 202, 204, 301, 304 })
                {
                    var api = new FakeApi { OnCreate = _ => T.Respond(status, T.CreatedBody()) };
                    StrybokClient sdk = Client(api);
                    StrybokServerException error =
                        await T.Throws<StrybokServerException>(() => sdk.CreateLinkAsync("/product/42"));
                    Assert.That(error.StatusCode, Is.EqualTo(status));
                    Assert.That(api.Creates, Has.Count.EqualTo(1), "status " + status + " is not retried");
                }
            });
        }

        // ------------------------------------------------------------------
        // Timeout and retries
        // ------------------------------------------------------------------

        [Test]
        public void ServerErrors_AreRetriedWithTheSameBodyAndThenSucceed()
        {
            T.Run(async () =>
            {
                int calls = 0;
                var api = new FakeApi
                {
                    OnCreate = _ => Interlocked.Increment(ref calls) < 3
                        ? T.Respond(503, "unavailable")
                        : T.Respond(201, T.CreatedBody()),
                };
                CreatedLink link = await CreateFull(Client(api));

                Assert.That(link.LinkId, Is.EqualTo("link_9"));
                List<StrybokHttpRequest> creates = api.Creates;
                Assert.That(creates, Has.Count.EqualTo(3));
                foreach (StrybokHttpRequest request in creates)
                {
                    Assert.That(request.Body, Is.EqualTo(creates[0].Body));
                    Assert.That(request.Url, Is.EqualTo(creates[0].Url));
                    Assert.That(request.Method, Is.EqualTo("POST"));
                    Assert.That(Header(request, "X-Strybok-Key"), Is.EqualTo(T.ClientKey));
                }
            });
        }

        [Test]
        public void RetriesWaitRetryDelayAndDoubleIt()
        {
            T.Run(async () =>
            {
                var times = new Bag<DateTime>();
                var api = new FakeApi
                {
                    OnCreate = _ =>
                    {
                        times.Add(DateTime.UtcNow);
                        return T.Respond(500, string.Empty);
                    },
                };
                StrybokClient sdk = Client(api, configure: o => o.RetryDelay = TimeSpan.FromMilliseconds(80));
                await T.Throws<StrybokServerException>(() => sdk.CreateLinkAsync("/product/42"));

                List<DateTime> at = times.Items;
                Assert.That(at, Has.Count.EqualTo(3));
                Assert.That((at[1] - at[0]).TotalMilliseconds, Is.GreaterThanOrEqualTo(60), "first pause is retryDelay");
                Assert.That((at[2] - at[1]).TotalMilliseconds, Is.GreaterThanOrEqualTo(140), "second pause is doubled");
            });
        }

        [Test]
        public void AServerErrorAfterAllRetries_ReportsTheLastStatus()
        {
            T.Run(async () =>
            {
                int calls = 0;
                var api = new FakeApi
                {
                    OnCreate = _ => T.Respond(Interlocked.Increment(ref calls) < 3 ? 500 : 502, string.Empty),
                };
                StrybokClient sdk = Client(api);
                StrybokServerException error =
                    await T.Throws<StrybokServerException>(() => sdk.CreateLinkAsync("/product/42"));
                Assert.That(error.StatusCode, Is.EqualTo(502));
                Assert.That(api.Creates, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void TransportFailures_AreRetriedAndThenReportedAsNetwork()
        {
            T.Run(async () =>
            {
                var api = new FakeApi { OnCreate = _ => throw new IOException("connection reset") };
                StrybokClient sdk = Client(api);
                StrybokNetworkException error = await T.Throws<StrybokNetworkException>(() => CreateFull(sdk));
                Assert.That(error.InnerException, Is.InstanceOf<IOException>());
                Assert.That(error.Url, Is.Null);
                List<StrybokHttpRequest> creates = api.Creates;
                Assert.That(creates, Has.Count.EqualTo(3));
                Assert.That(creates[2].Body, Is.EqualTo(creates[0].Body));

                // A retry after a lost answer gets the existing link back (200).
                int calls = 0;
                api.OnCreate = _ => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<StrybokHttpResponse>(new IOException("lost"))
                    : T.Respond(200, T.CreatedBody());
                CreatedLink link = await sdk.CreateLinkAsync("/product/42");
                Assert.That(link.Slug, Is.EqualTo("Ab3xK9"));
                Assert.That(calls, Is.EqualTo(2));
            });
        }

        [Test]
        public void NoAnswerInTime_IsRetriedAndThenReportedAsTimeout()
        {
            T.Run(async () =>
            {
                var api = new FakeApi { OnCreate = _ => T.Never() };
                StrybokClient sdk = Client(api, configure: o => o.Timeout = TimeSpan.FromMilliseconds(60));
                StrybokTimeoutException error = await T.Throws<StrybokTimeoutException>(() => CreateFull(sdk));
                Assert.That(error.Url, Is.Null);
                Assert.That(error.Message, Does.Contain("create-link"));
                List<StrybokHttpRequest> creates = api.Creates;
                Assert.That(creates, Has.Count.EqualTo(3));
                Assert.That(creates[1].Body, Is.EqualTo(creates[0].Body));
                Assert.That(creates[2].Body, Is.EqualTo(creates[0].Body));
            });
        }

        [Test]
        public void ZeroRetries_MeansASingleAttempt()
        {
            T.Run(async () =>
            {
                var api = new FakeApi { OnCreate = _ => T.Respond(503, string.Empty) };
                StrybokClient sdk = Client(api, configure: o => o.MaxRetries = 0);
                StrybokServerException error =
                    await T.Throws<StrybokServerException>(() => sdk.CreateLinkAsync("/product/42"));
                Assert.That(error.StatusCode, Is.EqualTo(503));
                Assert.That(api.Creates, Has.Count.EqualTo(1));
            });
        }

        // ------------------------------------------------------------------
        // Errors, and independence from link handling
        // ------------------------------------------------------------------

        [Test]
        public void Errors_NeverContainTheClientKeyOrThePayload()
        {
            T.Run(async () =>
            {
                var answers = new List<Func<StrybokHttpRequest, Task<StrybokHttpResponse>>>
                {
                    _ => T.Respond(401, string.Empty),
                    _ => T.Respond(403, string.Empty),
                    _ => T.Respond(429, string.Empty, "5"),
                    _ => T.Respond(422, "{\"error\":{\"message\":\"The request is invalid\"}}"),
                    _ => T.Respond(404, string.Empty),
                    _ => T.Respond(500, string.Empty),
                    _ => T.Respond(201, "{}"),
                    _ => throw new IOException("offline"),
                    _ => T.Never(),
                };
                var seen = new List<Type>();
                foreach (Func<StrybokHttpRequest, Task<StrybokHttpResponse>> answer in answers)
                {
                    var api = new FakeApi { OnCreate = answer };
                    StrybokClient sdk = Client(api, configure: o =>
                    {
                        o.Timeout = TimeSpan.FromMilliseconds(40);
                        o.MaxRetries = 1;
                    });
                    StrybokException error = await T.Throws<StrybokException>(() => CreateFull(sdk));
                    seen.Add(error.GetType());
                    foreach (string text in new[] { error.Message, error.ToString(), error.Url ?? string.Empty })
                    {
                        Assert.That(text, Does.Not.Contain(T.ClientKey), error.GetType().Name);
                        Assert.That(text, Does.Not.Contain("pk_"), error.GetType().Name);
                        Assert.That(text, Does.Not.Contain(SecretMarker), error.GetType().Name);
                        Assert.That(text, Does.Not.Contain("productId"), error.GetType().Name);
                    }
                }
                Assert.That(
                    seen,
                    Is.EqualTo(new[]
                    {
                        typeof(StrybokUnauthorizedException), typeof(RouteNotAllowedException),
                        typeof(StrybokRateLimitedException), typeof(StrybokValidationException),
                        typeof(StrybokServerException), typeof(StrybokServerException), typeof(StrybokServerException),
                        typeof(StrybokNetworkException), typeof(StrybokTimeoutException),
                    }));

                // Rejected before sending.
                StrybokClient local = Client(new FakeApi());
                StrybokException badRoute = await T.Throws<StrybokValidationException>(
                    () => local.CreateLinkAsync("nope", Payload()));
                Assert.That(badRoute.ToString(), Does.Not.Contain(SecretMarker));
                Assert.That(badRoute.ToString(), Does.Not.Contain(T.ClientKey));
            });
        }

        [Test]
        public void WorksWithoutInitializeAndNeverTouchesLinkHandling()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var source = new FakeLinkSource(T.Invite);
                var errors = new Bag<StrybokException>();
                var received = new Bag<ResolvedDeepLink>();
                StrybokClient sdk = Client(api, source, errors);
                sdk.LinkReceived += received.Add;

                CreatedLink link = await sdk.CreateLinkAsync("/product/42");
                Assert.That(link.Slug, Is.EqualTo("Ab3xK9"));

                api.OnCreate = _ => T.Respond(401, string.Empty);
                await T.Throws<StrybokUnauthorizedException>(() => sdk.CreateLinkAsync("/product/42"));
                await T.Throws<StrybokValidationException>(() => sdk.CreateLinkAsync("nope"));
                await T.Settle();

                Assert.That(source.HasListener, Is.False, "creating a link does not start listening");
                Assert.That(api.Resolves, Is.Empty, "the launch link was not touched");
                Assert.That(api.EventRequests, Is.Empty);
                Assert.That(errors.Count, Is.EqualTo(0), "errors of CreateLinkAsync never go to OnError");
                Assert.That(received.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void IsNeverOnTheStartPath()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var source = new FakeLinkSource(T.Invite);
                var received = new Bag<ResolvedDeepLink>();
                StrybokClient sdk = Client(api, source, configure: o => o.Timeout = TimeSpan.FromSeconds(5));
                sdk.LinkReceived += received.Add;

                await sdk.InitializeAsync();
                source.Emit("https://" + T.Host + "/second");
                await T.Until(() => received.Count == 2, "two links");
                await T.Until(() => api.EventRequests.Count == 2, "two events");
                Assert.That(api.Creates, Is.Empty, "starting, resolving and delivering never create links");

                // A create call that hangs does not hold up a link that arrives meanwhile.
                api.OnCreate = _ => T.Never();
                Task<CreatedLink> hanging = sdk.CreateLinkAsync("/product/42");
                source.Emit("https://" + T.Host + "/third");
                await T.Until(() => received.Count == 3, "a link while a create call is in flight");
                Assert.That(hanging.IsCompleted, Is.False);
                Task ignored = hanging.ContinueWith(t => { Exception observed = t.Exception; });
            });
        }

        [Test]
        public void AClientOnlyForCreatingLinks_DoesNotNeedAllowedHosts()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var options = new StrybokOptions
                {
                    ApiBaseUrl = T.Api,
                    ClientKey = T.ClientKey,
                    LinkSource = new FakeLinkSource(),
                    HttpTransport = api,
                };
                Assert.That(() => new StrybokClient(options), Throws.InstanceOf<ArgumentException>());

                var sdk = new StrybokClient(options, true);
                lock (_clients) _clients.Add(sdk);
                CreatedLink link = await sdk.CreateLinkAsync("/product/42");
                Assert.That(link.LinkId, Is.EqualTo("link_9"));
                Assert.That(sdk.IsStrybokUrl(T.Invite), Is.False);
            });
        }

        [Test]
        public void AfterDispose_FailsWithObjectDisposedAndAbortsACallInFlight()
        {
            T.Run(async () =>
            {
                var api = new FakeApi { OnCreate = _ => T.Never() };
                StrybokClient sdk = Client(api);
                Task<CreatedLink> inFlight = sdk.CreateLinkAsync("/product/42");
                await T.Until(() => api.Creates.Count == 1, "the request was sent");

                sdk.Dispose();

                await T.Throws<ObjectDisposedException>(() => inFlight);
                await T.Throws<ObjectDisposedException>(() => sdk.CreateLinkAsync("/product/42"));
                Assert.That(api.Creates, Has.Count.EqualTo(1));
            });
        }
    }
}
