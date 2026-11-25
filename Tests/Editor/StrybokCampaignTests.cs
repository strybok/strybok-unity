using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;

namespace Strybok.Tests
{
    /// <summary>Campaign values (SDK contract, "Campaign query"; decision 0018).</summary>
    public sealed class StrybokCampaignTests
    {
        private const string ResolveUrl = "https://api.strybok.com/v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777";

        private readonly List<StrybokClient> _clients = new List<StrybokClient>();

        [TearDown]
        public void DisposeClients()
        {
            foreach (StrybokClient client in _clients) client.Dispose();
            _clients.Clear();
        }

        private StrybokClient Client(FakeApi api, FakeLinkSource source = null, Action<StrybokOptions> configure = null)
        {
            StrybokOptions options = T.Options(api, source);
            if (configure != null) configure(options);
            var client = new StrybokClient(options);
            lock (_clients) _clients.Add(client);
            return client;
        }

        /// <summary>What would be sent to the API for <paramref name="url"/>; null when it is not ours.</summary>
        private string Lookup(string url)
        {
            string lookup;
            return Client(new FakeApi()).TryParseLink(url, out lookup) ? lookup : null;
        }

        private static Task<StrybokHttpResponse> Resolved(string campaign)
        {
            return T.Respond(
                200,
                "{\"linkId\":\"link_1\",\"appId\":\"app_1\",\"routePath\":\"/x\"" +
                (campaign == null ? string.Empty : ",\"campaign\":" + campaign) + "}");
        }

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        [Test]
        public void Lookup_TheExampleOfTheContract()
        {
            var options = new StrybokOptions
            {
                ApiBaseUrl = T.Api,
                AllowedHosts = { "myapp.strybok.link" },
                LinkSource = new FakeLinkSource(),
                HttpTransport = new FakeApi(),
            };
            using (var client = new StrybokClient(options))
            {
                string lookup;
                Assert.That(
                    client.TryParseLink(
                        "https://MyApp.strybok.link/invite777?utm_source=instagram&fbclid=abc&utm_medium=two%20words&utm_source=x#top",
                        out lookup),
                    Is.True);
                Assert.That(lookup, Is.EqualTo("https://myapp.strybok.link/invite777?utm_source=instagram"));
            }
        }

        [Test]
        public void Lookup_WithoutCampaignParametersIsWhatEarlierVersionsSent()
        {
            foreach (string url in new[]
            {
                T.Invite,
                T.Invite + "?",
                T.Invite + "#utm_source=fragment",
                T.Invite + "?#utm_source=fragment",
                T.Invite + "?token=secret&fbclid=abc&gclid=1&utm_id=7#f",
                T.Invite + "?a=1#?utm_source=fragment",
                T.Invite + "?utm_source=",
                T.Invite + "?utm_source",
                T.Invite + "?&&=&=x&",
            })
            {
                Assert.That(Lookup(url), Is.EqualTo(T.Invite), url);
            }
        }

        [Test]
        public void Lookup_KeepsTheFiveKeysInTheFixedOrderAndNothingElse()
        {
            Assert.That(
                Lookup(T.Invite + "?utm_content=n&x=1&utm_term=t&utm_campaign=c&token=secret&utm_medium=m&utm_source=s#utm_source=f"),
                Is.EqualTo(T.Invite + "?utm_source=s&utm_medium=m&utm_campaign=c&utm_term=t&utm_content=n"));
            Assert.That(
                Lookup(T.Invite + "?utm_content=n&&utm_medium=m&"),
                Is.EqualTo(T.Invite + "?utm_medium=m&utm_content=n"));
            Assert.That(Lookup(T.Invite + "?utm_source=a#b"), Is.EqualTo(T.Invite + "?utm_source=a"));
        }

        [Test]
        public void Lookup_KeyNamesAreExactAndLowerCase()
        {
            foreach (string query in new[]
            {
                "UTM_SOURCE=a",
                "Utm_Source=a",
                "utm_sourcex=a",
                "xutm_source=a",
                "utm_id=a",
                "utm_source%20=a",
                "+utm_source=a",
                " utm_source=a",
                "utm_source[]=a",
            })
            {
                Assert.That(Lookup(T.Invite + "?" + query), Is.EqualTo(T.Invite), query);
            }
        }

        [Test]
        public void Lookup_TheFirstOccurrenceOfAKeyCountsEvenWhenItsValueIsDropped()
        {
            Assert.That(
                Lookup(T.Invite + "?utm_source=first&utm_source=second"),
                Is.EqualTo(T.Invite + "?utm_source=first"));
            Assert.That(Lookup(T.Invite + "?utm_source=not%20valid&utm_source=second"), Is.EqualTo(T.Invite));
            Assert.That(Lookup(T.Invite + "?utm_source=&utm_source=second"), Is.EqualTo(T.Invite));
            Assert.That(Lookup(T.Invite + "?utm_source&utm_source=second"), Is.EqualTo(T.Invite));
            Assert.That(
                Lookup(T.Invite + "?utm_source=a%2Fb&utm_medium=m&utm_source=second"),
                Is.EqualTo(T.Invite + "?utm_medium=m"));
        }

        [Test]
        public void Lookup_ValuesOutsideTheAllowedCharactersOrLengthAreDropped()
        {
            foreach (string value in new[]
            {
                "two%20words",
                "two+words",
                "two words",
                "a@b.c",
                "a%40b.c",
                "a/b",
                "a%2Fb",
                "a=b",
                "a%26utm_medium=b",
                "a%3Db",
                "(other)",
                "a,b",
                "a:b",
                "a*b",
                "a!b",
                "a'b",
                "a\"b",
                "<script>",
                "кампанія",
                "%D0%BA%D0%B0",
                "caf%C3%A9",
                "a%00b",
                "a%0Ab",
                new string('a', 65),
            })
            {
                Assert.That(Lookup(T.Invite + "?utm_source=" + value), Is.EqualTo(T.Invite), value);
            }

            string longest = new string('a', 64);
            Assert.That(Lookup(T.Invite + "?utm_source=" + longest), Is.EqualTo(T.Invite + "?utm_source=" + longest));
            Assert.That(
                Lookup(T.Invite + "?utm_source=AZaz09._~-"),
                Is.EqualTo(T.Invite + "?utm_source=AZaz09._~-"));
        }

        [Test]
        public void Lookup_PercentEncodedKeysAndValuesAreDecoded()
        {
            Assert.That(
                Lookup(T.Invite + "?utm_source=%69nsta%2Dgram"),
                Is.EqualTo(T.Invite + "?utm_source=insta-gram"));
            Assert.That(Lookup(T.Invite + "?utm_source=a%2e%5f%7Eb"), Is.EqualTo(T.Invite + "?utm_source=a._~b"));
            Assert.That(Lookup(T.Invite + "?utm%5Fsource=x"), Is.EqualTo(T.Invite + "?utm_source=x"));
            Assert.That(Lookup(T.Invite + "?%75tm_medium=x"), Is.EqualTo(T.Invite + "?utm_medium=x"));

            // 64 characters after decoding.
            string encoded = string.Concat(Enumerable.Repeat("%61", 64));
            Assert.That(
                Lookup(T.Invite + "?utm_term=" + encoded),
                Is.EqualTo(T.Invite + "?utm_term=" + new string('a', 64)));
            Assert.That(Lookup(T.Invite + "?utm_term=" + encoded + "%61"), Is.EqualTo(T.Invite));
        }

        [Test]
        public void Lookup_MalformedEscapesAndInvalidUtf8DropTheValueAndNeverThrow()
        {
            foreach (string value in new[]
            {
                "%",
                "a%",
                "a%2",
                "%2",
                "%zz",
                "%G0",
                "%0G",
                "a%-1",
                "%+1",
                "%%",
                "%u0041",
                "%C3%28",
                "%FF",
                "%C0%AE",
                "%E2%82",
                "%ED%A0%80",
                "\uD800",
                "a\uDC00b",
            })
            {
                Assert.That(Lookup(T.Invite + "?utm_source=" + value), Is.EqualTo(T.Invite), value);
                Assert.That(
                    Lookup(T.Invite + "?utm_source=" + value + "&utm_medium=ok"),
                    Is.EqualTo(T.Invite + "?utm_medium=ok"),
                    value);
                Assert.That(
                    Lookup(T.Invite + "?" + value + "=x&utm_medium=ok"),
                    Is.EqualTo(T.Invite + "?utm_medium=ok"),
                    value);
            }
        }

        [Test]
        public void Lookup_CampaignQueryBuilderTakesAnyText()
        {
            Assert.That(CampaignQuery.Build(null), Is.EqualTo(string.Empty));
            Assert.That(CampaignQuery.Build(string.Empty), Is.EqualTo(string.Empty));
            Assert.That(CampaignQuery.Build("&"), Is.EqualTo(string.Empty));
            Assert.That(CampaignQuery.Build("="), Is.EqualTo(string.Empty));
            Assert.That(CampaignQuery.Build("utm_source=a\r\nb"), Is.EqualTo(string.Empty));
            Assert.That(CampaignQuery.Build("utm_term=t"), Is.EqualTo("?utm_term=t"));
        }

        [Test]
        public void Lookup_AQueryChangesNeitherWhichUrlsAreOursNorTheHostAndPathThatAreUsed()
        {
            const string query = "?utm_source=a&utm_medium=b";
            foreach (string url in new[]
            {
                "http://" + T.Host + "/invite777",
                "https://evil.example.com/invite777",
                "https://user:pw@" + T.Host + "/invite777",
                "https://user@" + T.Host + "/invite777",
                "https://" + T.Host + ":8443/invite777",
                "https://" + T.Host + "/a/b",
                "https://" + T.Host + "/invite777/",
                "https://" + T.Host + "/",
                "https://" + T.Host,
                "https://" + T.Host + "/" + new string('a', 65),
            })
            {
                Assert.That(Lookup(url), Is.Null, url);
                Assert.That(Lookup(url + query), Is.Null, url + query);
            }

            // Nothing in a query can stand in for the authority or the path.
            foreach (string url in new[]
            {
                "https://evil.example.com/x?utm_source=https://" + T.Host + "/invite777",
                "https://evil.example.com?utm_source=a@" + T.Host + "/invite777",
                "https://" + T.Host + "?utm_source=/invite777",
                "https://" + T.Host + "/?utm_source=a",
                T.Invite + "?utm_source=a\n",
            })
            {
                Assert.That(Lookup(url), Is.Null, url);
            }

            Assert.That(
                Lookup("HTTPS://My-App.STRYBOK.link:443/Invite777?utm_source=a&token=s#f"),
                Is.EqualTo("https://" + T.Host + "/Invite777?utm_source=a"));
        }

        // ------------------------------------------------------------------
        // Resolve
        // ------------------------------------------------------------------

        [Test]
        public void Resolve_SendsTheCampaignQueryAndNothingElseOfTheQuery()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                const string url =
                    "https://My-App.Strybok.Link/invite777?token=secret&utm_medium=social&utm_source=instagram&email=a%40b.c#private";

                ResolvedDeepLink link = await Client(api).ResolveAsync(url);

                Assert.That(link.Url, Is.EqualTo(url));
                StrybokHttpRequest request = api.Resolves.Single();
                Assert.That(request.Url, Is.EqualTo(ResolveUrl + "%3Futm_source%3Dinstagram%26utm_medium%3Dsocial"));
                foreach (string secret in new[] { "secret", "token", "email", "private", "b.c" })
                {
                    Assert.That(request.Url, Does.Not.Contain(secret));
                }
            });
        }

        [Test]
        public void Resolve_AUrlWithoutCampaignParametersIsRequestedExactlyAsBefore()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                StrybokClient sdk = Client(api);
                await sdk.ResolveAsync(T.Invite);
                await sdk.ResolveAsync(T.Invite + "?token=secret&utm_source=two%20words#f");
                Assert.That(api.Resolves.Select(request => request.Url), Is.EqualTo(new[] { ResolveUrl, ResolveUrl }));
            });
        }

        [Test]
        public void Resolve_CampaignHoldsTheFiveStringFieldsTheServerSent()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => Resolved(
                    "{\"source\":\"instagram\",\"medium\":\"social\",\"campaign\":\"spring\",\"term\":\"shoes\",\"content\":\"banner\",\"extra\":\"x\"}"));
                LinkCampaign campaign = (await Client(api).ResolveAsync(T.Invite)).Campaign;
                Assert.That(campaign.Source, Is.EqualTo("instagram"));
                Assert.That(campaign.Medium, Is.EqualTo("social"));
                Assert.That(campaign.Campaign, Is.EqualTo("spring"));
                Assert.That(campaign.Term, Is.EqualTo("shoes"));
                Assert.That(campaign.Content, Is.EqualTo("banner"));
                Assert.That(campaign.IsEmpty, Is.False);
                Assert.That(campaign, Is.EqualTo(new LinkCampaign("instagram", "social", "spring", "shoes", "banner")));
            });
        }

        [Test]
        public void Resolve_CampaignIsEmptyNeverNullWhenMissingOrNotAnObject()
        {
            T.Run(async () =>
            {
                foreach (string campaign in new[] { null, "null", "[]", "[\"a\"]", "\"instagram\"", "7", "true", "{}", "{\"other\":\"x\"}" })
                {
                    var api = new FakeApi(_ => Resolved(campaign));
                    ResolvedDeepLink link = await Client(api).ResolveAsync(T.Invite);
                    Assert.That(link.Campaign, Is.Not.Null, campaign);
                    Assert.That(link.Campaign.IsEmpty, Is.True, campaign);
                    Assert.That(link.Campaign, Is.SameAs(LinkCampaign.Empty), campaign);
                    Assert.That(link.RoutePath, Is.EqualTo("/x"));
                }
            });
        }

        [Test]
        public void Resolve_CampaignFieldsThatAreNotStringsAreIgnoredNotErrors()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => Resolved(
                    "{\"source\":7,\"medium\":null,\"campaign\":{\"a\":1},\"term\":[\"x\"],\"content\":\"banner\",\"utm_source\":\"x\"}"));
                LinkCampaign campaign = (await Client(api).ResolveAsync(T.Invite)).Campaign;
                Assert.That(campaign, Is.EqualTo(new LinkCampaign(content: "banner")));
                Assert.That(campaign.Source, Is.Null);
            });
        }

        [Test]
        public void Link_BuiltWithoutCampaignHasAnEmptyOne()
        {
            var plain = new ResolvedDeepLink(T.Invite, "l", "a", "/x", null);
            Assert.That(plain.Campaign, Is.SameAs(LinkCampaign.Empty));
            Assert.That(new ResolvedDeepLink(T.Invite, "l", "a", "/x", null, null).Campaign, Is.SameAs(LinkCampaign.Empty));

            var campaign = new LinkCampaign(source: "s");
            Assert.That(new ResolvedDeepLink(T.Invite, "l", "a", "/x", null, campaign).Campaign, Is.SameAs(campaign));
            Assert.That(new LinkCampaign(), Is.EqualTo(LinkCampaign.Empty));
            Assert.That(new LinkCampaign().GetHashCode(), Is.EqualTo(LinkCampaign.Empty.GetHashCode()));
            Assert.That(campaign, Is.Not.EqualTo(new LinkCampaign(medium: "s")));
            Assert.That(campaign.ToString(), Is.EqualTo("LinkCampaign(source: s, medium: , campaign: , term: , content: )"));
        }

        // ------------------------------------------------------------------
        // app_open
        // ------------------------------------------------------------------

        [Test]
        public void AppOpen_ReportsTheCampaignExactlyAsResolveReturnedIt()
        {
            T.Run(async () =>
            {
                // The server answers with other values than the URL has: the answer is what is reported.
                var api = new FakeApi(_ => Resolved("{\"source\":\"newsletter\",\"content\":\"footer\",\"ignored\":\"x\",\"term\":5}"));
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(api, source);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();
                source.Emit(T.Invite + "?utm_source=instagram&utm_medium=social");
                await T.Until(() => api.EventRequests.Count == 1, "app_open event");

                Dictionary<string, object> body = api.Events.Single();
                Assert.That(
                    body.Keys,
                    Is.EquivalentTo(new[] { "eventId", "linkId", "type", "platform", "sdkVersion", "campaign" }));
                var campaign = (Dictionary<string, object>)body["campaign"];
                Assert.That(campaign.Keys, Is.EquivalentTo(new[] { "source", "content" }));
                Assert.That(campaign["source"], Is.EqualTo("newsletter"));
                Assert.That(campaign["content"], Is.EqualTo("footer"));
                Assert.That(
                    api.EventRequests.Single().Body,
                    Does.EndWith(",\"campaign\":{\"source\":\"newsletter\",\"content\":\"footer\"}}"));
                Assert.That(
                    received.Items.Single().Campaign,
                    Is.EqualTo(new LinkCampaign(source: "newsletter", content: "footer")));
            });
        }

        [Test]
        public void AppOpen_CarriesAllFiveFieldsInOrder()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => Resolved(
                    "{\"content\":\"n\",\"term\":\"t\",\"campaign\":\"c\",\"medium\":\"m\",\"source\":\"s\"}"));
                var source = new FakeLinkSource();
                StrybokClient sdk = Client(api, source);
                sdk.LinkReceived += _ => { };
                await sdk.InitializeAsync();
                source.Emit(T.Invite);
                await T.Until(() => api.EventRequests.Count == 1, "app_open event");
                Assert.That(
                    api.EventRequests.Single().Body,
                    Does.EndWith(
                        ",\"campaign\":{\"source\":\"s\",\"medium\":\"m\",\"campaign\":\"c\",\"term\":\"t\",\"content\":\"n\"}}"));
            });
        }

        [Test]
        public void AppOpen_HasNoCampaignWhenResolveReturnedNoneWhateverTheUrlSays()
        {
            T.Run(async () =>
            {
                foreach (string campaign in new[] { null, "{}", "null", "[]", "{\"source\":1}" })
                {
                    var api = new FakeApi(_ => Resolved(campaign));
                    var source = new FakeLinkSource();
                    StrybokClient sdk = Client(api, source);
                    sdk.LinkReceived += _ => { };
                    await sdk.InitializeAsync();
                    source.Emit(T.Invite + "?utm_source=instagram");
                    await T.Until(() => api.EventRequests.Count == 1, "app_open event");
                    Assert.That(
                        api.Events.Single().Keys,
                        Is.EquivalentTo(new[] { "eventId", "linkId", "type", "platform", "sdkVersion" }),
                        campaign);
                }
            });
        }

        // ------------------------------------------------------------------
        // Dedupe
        // ------------------------------------------------------------------

        [Test]
        public void Running_TheSameSlugWithDifferentCampaignParametersIsTwoLinks()
        {
            T.Run(async () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var source = new FakeLinkSource();
                var api = new FakeApi();
                StrybokClient sdk = Client(api, source, o => o.Clock = () => now);
                var received = new Bag<ResolvedDeepLink>();
                sdk.LinkReceived += received.Add;
                await sdk.InitializeAsync();

                source.Emit(T.Invite + "?utm_source=a");
                source.Emit(T.Invite + "?utm_source=a"); // The same URL string: one tap reported twice.
                source.Emit(T.Invite + "?utm_source=b");
                source.Emit(T.Invite);
                // Compared as received: a parameter that is never sent still makes another URL.
                source.Emit(T.Invite + "?fbclid=1");
                source.Emit(T.Invite + "?fbclid=1");
                await T.Until(() => received.Count == 4, "four links");
                await T.Settle();

                Assert.That(
                    received.Items.Select(link => link.Url),
                    Is.EquivalentTo(new[]
                    {
                        T.Invite + "?utm_source=a", T.Invite + "?utm_source=b", T.Invite, T.Invite + "?fbclid=1",
                    }));
                Assert.That(
                    api.Resolves.Select(request => request.Url),
                    Is.EquivalentTo(new[]
                    {
                        ResolveUrl + "%3Futm_source%3Da", ResolveUrl + "%3Futm_source%3Db", ResolveUrl, ResolveUrl,
                    }));
            });
        }
    }
}
