using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;
using UnityEngine;

namespace Strybok.Tests
{
    /// <summary>One lookup case of the shared vectors.</summary>
    public sealed class LookupVector
    {
        public string Name;
        public string Url;

        /// <summary>Null: the option is not set.</summary>
        public List<string> PayloadKeys;

        /// <summary>Null: the URL is not ours.</summary>
        public string Lookup;

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>One configuration case of the shared vectors.</summary>
    public sealed class ConfigurationVector
    {
        public string Name;

        /// <summary>Null: the option is not set.</summary>
        public List<string> PayloadKeys;

        public bool Valid;

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// The shared test vectors of the SDK contract ("Links without an API call"), read from the
    /// byte-identical copy in <c>Tests/Editor/Fixtures</c>.
    /// </summary>
    internal static class ParameterLinkVectors
    {
        public const string FileName = "parameter-links-vectors.json";

        private static Dictionary<string, object> s_root;

        public static List<string> AllowedHosts
        {
            get { return Strings(Root["allowedHosts"]); }
        }

        public static List<LookupVector> Lookups
        {
            get
            {
                var vectors = new List<LookupVector>();
                foreach (object item in (List<object>)Root["lookups"])
                {
                    var entry = (Dictionary<string, object>)item;
                    vectors.Add(new LookupVector
                    {
                        Name = (string)entry["name"],
                        Url = (string)entry["url"],
                        PayloadKeys = PayloadKeysOf(entry),
                        Lookup = (string)entry["lookup"],
                    });
                }
                return vectors;
            }
        }

        public static List<ConfigurationVector> Configurations
        {
            get
            {
                var vectors = new List<ConfigurationVector>();
                foreach (object item in (List<object>)Root["configurations"])
                {
                    var entry = (Dictionary<string, object>)item;
                    vectors.Add(new ConfigurationVector
                    {
                        Name = (string)entry["name"],
                        PayloadKeys = PayloadKeysOf(entry),
                        Valid = (bool)entry["valid"],
                    });
                }
                return vectors;
            }
        }

        private static Dictionary<string, object> Root
        {
            get
            {
                if (s_root != null) return s_root;
                string path = FindFile();
                object parsed;
                if (!StrybokJson.TryParse(File.ReadAllText(path, Encoding.UTF8), out parsed) ||
                    !(parsed is Dictionary<string, object>))
                {
                    throw new InvalidDataException(path + " is not a JSON object");
                }
                s_root = (Dictionary<string, object>)parsed;
                return s_root;
            }
        }

        private static List<string> PayloadKeysOf(Dictionary<string, object> entry)
        {
            var option = entry["parameterLinks"] as Dictionary<string, object>;
            return option == null ? null : Strings(option["payloadKeys"]);
        }

        private static List<string> Strings(object list)
        {
            return ((List<object>)list).Select(item => (string)item).ToList();
        }

        /// <summary>Next to this file, in the package wherever Unity resolved it from.</summary>
        private static string FindFile([CallerFilePath] string thisFile = "")
        {
            var candidates = new List<string>();
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ParameterLinkVectors).Assembly);
            if (package != null)
            {
                candidates.Add(Path.Combine(package.resolvedPath, "Tests", "Editor", "Fixtures", FileName));
            }
            if (!string.IsNullOrEmpty(thisFile))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(thisFile)), "Fixtures", FileName));
            }
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(FileName + " not found at " + string.Join(", ", candidates.ToArray()));
        }
    }

    /// <summary>Links without an API call (SDK contract; decision 0035): the shared vectors and the behaviour.</summary>
    public sealed class StrybokParameterLinkTests
    {
        private const string ResolveBase = T.Api + "/v1/public/resolve?url=";
        private const string ClickId = "dc_0123456789abcdefghijABCDEFGHIJ01";

        private readonly List<StrybokClient> _clients = new List<StrybokClient>();

        [TearDown]
        public void DisposeClients()
        {
            lock (_clients)
            {
                foreach (StrybokClient client in _clients) client.Dispose();
                _clients.Clear();
            }
        }

        public static IEnumerable<TestCaseData> LookupCases()
        {
            int number = 0;
            foreach (LookupVector vector in ParameterLinkVectors.Lookups)
            {
                number++;
                yield return new TestCaseData(vector).SetName("Vector lookup " + number.ToString("00") + ": " + Readable(vector.Name));
            }
        }

        public static IEnumerable<TestCaseData> ConfigurationCases()
        {
            int number = 0;
            foreach (ConfigurationVector vector in ParameterLinkVectors.Configurations)
            {
                number++;
                yield return new TestCaseData(vector).SetName("Vector configuration " + number.ToString("00") + ": " + Readable(vector.Name));
            }
        }

        /// <summary>Test names without the quotes of some vector names.</summary>
        private static string Readable(string name)
        {
            return name.Replace('"', '\'');
        }

        private static ParameterLinksOptions Option(IEnumerable<string> payloadKeys)
        {
            return payloadKeys == null ? null : new ParameterLinksOptions { PayloadKeys = new List<string>(payloadKeys) };
        }

        /// <summary>What resolve is asked for <paramref name="lookup"/>, encoded as the SDK encodes <c>url</c>.</summary>
        private static string ResolveUrlOf(string lookup)
        {
            return ResolveBase + Uri.EscapeDataString(lookup);
        }

        private StrybokClient Client(
            FakeApi api,
            FakeLinkSource source,
            IEnumerable<string> payloadKeys,
            Action<StrybokOptions> configure = null,
            IEnumerable<string> allowedHosts = null)
        {
            StrybokOptions options = T.Options(api, source);
            options.AllowedHosts = new List<string>(allowedHosts ?? ParameterLinkVectors.AllowedHosts);
            options.ParameterLinks = Option(payloadKeys);
            if (configure != null) configure(options);
            var client = new StrybokClient(options);
            lock (_clients) _clients.Add(client);
            return client;
        }

        // ------------------------------------------------------------------
        // The shared vectors
        // ------------------------------------------------------------------

        [Test]
        public void Vectors_TheFileHasCasesOfBothKinds()
        {
            Assert.That(ParameterLinkVectors.AllowedHosts, Is.EquivalentTo(new[] { "myapp.strybok.link", "go.example.com" }));
            Assert.That(ParameterLinkVectors.Lookups, Is.Not.Empty);
            Assert.That(ParameterLinkVectors.Configurations, Is.Not.Empty);
            Assert.That(LookupCases().Count(), Is.EqualTo(ParameterLinkVectors.Lookups.Count), "every lookup case runs");
            Assert.That(ConfigurationCases().Count(), Is.EqualTo(ParameterLinkVectors.Configurations.Count), "every configuration case runs");
        }

        [TestCaseSource(nameof(LookupCases))]
        public void Vector_Lookup(LookupVector vector)
        {
            T.Run(async () =>
            {
                // resolve(url)
                var api = new FakeApi();
                StrybokClient client = Client(api, null, vector.PayloadKeys);
                Assert.That(client.IsStrybokUrl(vector.Url), Is.EqualTo(vector.Lookup != null), vector.Name);
                if (vector.Lookup == null)
                {
                    await T.Throws<UnsupportedLinkException>(() => client.ResolveAsync(vector.Url));
                    Assert.That(api.Resolves, Is.Empty, vector.Name);
                }
                else
                {
                    ResolvedDeepLink link = await client.ResolveAsync(vector.Url);
                    Assert.That(link.Url, Is.EqualTo(vector.Url), "the link keeps the URL as received");
                    Assert.That(api.Resolves.Select(request => request.Url), Is.EqualTo(new[] { ResolveUrlOf(vector.Lookup) }), vector.Name);
                }

                // The automatic flow.
                var source = new FakeLinkSource();
                var automaticApi = new FakeApi();
                var errors = new Bag<StrybokException>();
                StrybokClient automatic = Client(automaticApi, source, vector.PayloadKeys, options => options.OnError = errors.Add);
                var received = new Bag<ResolvedDeepLink>();
                automatic.LinkReceived += received.Add;
                await automatic.InitializeAsync();
                source.Emit(vector.Url);
                if (vector.Lookup == null)
                {
                    await T.Settle();
                    Assert.That(automaticApi.Resolves, Is.Empty, vector.Name);
                    Assert.That(received.Count, Is.EqualTo(0), vector.Name);
                    Assert.That(errors.Count, Is.EqualTo(0), "dropped silently: " + vector.Name);
                }
                else
                {
                    await T.Until(() => received.Count == 1, "the link of " + vector.Name);
                    Assert.That(
                        automaticApi.Resolves.Select(request => request.Url),
                        Is.EqualTo(new[] { ResolveUrlOf(vector.Lookup) }),
                        vector.Name);
                    Assert.That(errors.Count, Is.EqualTo(0), vector.Name);
                }
            });
        }

        [TestCaseSource(nameof(ConfigurationCases))]
        public void Vector_Configuration(ConfigurationVector vector)
        {
            StrybokOptions options = T.Options(new FakeApi());
            options.ParameterLinks = Option(vector.PayloadKeys);
            if (vector.Valid)
            {
                Assert.DoesNotThrow(() => new StrybokClient(options).Dispose(), vector.Name);
                return;
            }
            var error = Assert.Throws<ArgumentException>(() => new StrybokClient(options).Dispose(), vector.Name);
            Assert.That(error.ParamName, Is.EqualTo("options"), "rejected like an invalid ApiBaseUrl");
            Assert.That(error.Message, Does.StartWith("ParameterLinks: "));
        }

        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        [Test]
        public void Options_TheOptionIsAbsentByDefaultAndCloneCopiesItsKeys()
        {
            var options = new StrybokOptions();
            Assert.That(options.ParameterLinks, Is.Null);
            Assert.That(new ParameterLinksOptions().PayloadKeys, Is.Empty);
            Assert.That(new ParameterLinksOptions("id", "lang").PayloadKeys, Is.EqualTo(new[] { "id", "lang" }));

            options.ParameterLinks = new ParameterLinksOptions("id");
            StrybokOptions copy = options.Clone();
            Assert.That(copy.ParameterLinks, Is.Not.SameAs(options.ParameterLinks));
            copy.ParameterLinks.PayloadKeys.Add("lang");
            Assert.That(options.ParameterLinks.PayloadKeys, Is.EqualTo(new[] { "id" }), "Clone copies the keys");
            Assert.That(new StrybokOptions().Clone().ParameterLinks, Is.Null);
        }

        [Test]
        public void Options_NullKeysAreRejected()
        {
            foreach (ParameterLinksOptions option in new[]
            {
                new ParameterLinksOptions { PayloadKeys = null },
                new ParameterLinksOptions { PayloadKeys = new List<string> { "id", null } },
            })
            {
                StrybokOptions options = T.Options(new FakeApi());
                options.ParameterLinks = option;
                var error = Assert.Throws<ArgumentException>(() => new StrybokClient(options).Dispose());
                Assert.That(error.ParamName, Is.EqualTo("options"));
            }
        }

        [Test]
        public void Options_TheClientKeepsTheKeysItWasCreatedWith()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                var keys = new List<string> { "id" };
                StrybokOptions options = T.Options(api);
                options.ParameterLinks = new ParameterLinksOptions { PayloadKeys = keys };
                var client = new StrybokClient(options);
                lock (_clients) _clients.Add(client);
                keys.Add("lang");
                options.ParameterLinks = null;

                await client.ResolveAsync("https://" + T.Host + "/product/42?lang=uk&id=7");
                Assert.That(api.Resolves.Single().Url, Is.EqualTo(ResolveUrlOf("https://" + T.Host + "/product/42?id=7")));
            });
        }

        [Test]
        public void Options_ALinkCreatorDoesNotNeedAValidOption()
        {
            // Like the hosts: a client that only creates links never reads them.
            StrybokOptions options = T.Options(new FakeApi());
            options.AllowedHosts = new List<string>();
            options.ParameterLinks = new ParameterLinksOptions("utm_source");
            Assert.DoesNotThrow(() => new StrybokClient(options, true).Dispose());

            options.AllowedHosts = new List<string> { T.Host };
            var error = Assert.Throws<ArgumentException>(() => new StrybokClient(options).Dispose());
            Assert.That(error.Message, Does.StartWith("ParameterLinks: "));
        }

        [Test]
        public void Settings_TheAssetSetsTheOptionOnlyWhenSwitchedOn()
        {
            var settings = ScriptableObject.CreateInstance<StrybokSettings>();
            try
            {
                settings.LinkHosts = new[] { T.Host };
                Assert.That(settings.ParameterLinks, Is.False);
                Assert.That(settings.ToOptions().ParameterLinks, Is.Null, "off by default");

                settings.PayloadKeys = new[] { " id ", "", null, "lang", "  " };
                Assert.That(settings.ToOptions().ParameterLinks, Is.Null, "keys alone do not switch it on");

                settings.ParameterLinks = true;
                Assert.That(settings.ToOptions().ParameterLinks.PayloadKeys, Is.EqualTo(new[] { "id", "lang" }));

                settings.PayloadKeys = null;
                Assert.That(settings.ToOptions().ParameterLinks.PayloadKeys, Is.Empty, "no keys: path links without payload");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Sdk_InitializeRejectsAnInvalidOption()
        {
            StrybokSdk.Shutdown();
            try
            {
                StrybokOptions options = T.Options(new FakeApi(), new FakeLinkSource());
                options.ParameterLinks = new ParameterLinksOptions("id", "id");
                Assert.Throws<ArgumentException>(() => StrybokSdk.Initialize(options));
                Assert.That(StrybokSdk.IsInitialized, Is.False);
            }
            finally
            {
                StrybokSdk.Shutdown();
            }
        }

        // ------------------------------------------------------------------
        // Behaviour
        // ------------------------------------------------------------------

        [Test]
        public void Delivery_TheRouteAndPayloadComeFromTheAnswerNotFromTheTappedUrl()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(
                    200,
                    "{\"linkId\":\"plink_path\",\"appId\":\"app_1\",\"routePath\":\"/catalog/item\"," +
                    "\"payload\":{\"id\":\"from-server\"},\"campaign\":{\"source\":\"mail\"}}"));
                var source = new FakeLinkSource();
                StrybokClient client = Client(api, source, new[] { "id", "lang" }, null, new[] { T.Host });
                var received = new Bag<ResolvedDeepLink>();
                client.LinkReceived += received.Add;
                await client.InitializeAsync();

                const string tapped = "https://" + T.Host + "/product/42?id=7&utm_source=x#top";
                source.Emit(tapped);
                await T.Until(() => received.Count == 1, "the path link");

                ResolvedDeepLink link = received.Items.Single();
                Assert.That(link.Url, Is.EqualTo(tapped));
                Assert.That(link.RoutePath, Is.EqualTo("/catalog/item"), "never the tapped path");
                Assert.That(link.Payload["id"], Is.EqualTo("from-server"), "never the tapped value");
                Assert.That(link.Payload.Count, Is.EqualTo(1));
                Assert.That(link.LinkId, Is.EqualTo("plink_path"));
                Assert.That(link.Campaign, Is.EqualTo(new LinkCampaign(source: "mail")));
                Assert.That(link.IsDeferred, Is.False);

                // The same through resolve(url), for a long link.
                ResolvedDeepLink resolved = await client.ResolveAsync(
                    "https://" + T.Host + "/?link=https%3A%2F%2Fexample.com%2Fp%3Fid%3D7&apn=com.example.app");
                Assert.That(resolved.RoutePath, Is.EqualTo("/catalog/item"));
                Assert.That(resolved.Payload["id"], Is.EqualTo("from-server"));
            });
        }

        [Test]
        public void AppOpen_CarriesTheLinkIdTheServerReturned()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(
                    200, "{\"linkId\":\"plink_firebase\",\"appId\":\"app_1\",\"routePath\":\"/p\",\"payload\":{}}"));
                var source = new FakeLinkSource();
                StrybokClient client = Client(api, source, new[] { "id" }, null, new[] { T.Host });
                client.LinkReceived += _ => { };
                await client.InitializeAsync();

                source.Emit("https://" + T.Host + "/?link=https%3A%2F%2Fexample.com%2Fp");
                await T.Until(() => api.EventRequests.Count == 1, "the app_open event");

                Dictionary<string, object> body = api.Events.Single();
                Assert.That(body["linkId"], Is.EqualTo("plink_firebase"));
                Assert.That(body["type"], Is.EqualTo("app_open"));
                Assert.That(body["sdkVersion"], Is.EqualTo("0.4.0"));
                Assert.That(body.Keys, Is.EquivalentTo(new[] { "eventId", "linkId", "type", "platform", "sdkVersion" }));
            });
        }

        [Test]
        public void Delivery_APathThatIsNoLinkIsReportedAsNotFound()
        {
            T.Run(async () =>
            {
                var api = new FakeApi(_ => T.Respond(404, string.Empty));
                var source = new FakeLinkSource();
                var errors = new Bag<StrybokException>();
                StrybokClient client = Client(api, source, new string[0], options => options.OnError = errors.Add, new[] { T.Host });
                var received = new Bag<ResolvedDeepLink>();
                client.LinkReceived += received.Add;
                await client.InitializeAsync();

                source.Emit("https://" + T.Host + "/no/such/route");
                await T.Until(() => errors.Count == 1, "the error");
                Assert.That(errors.Items.Single(), Is.InstanceOf<LinkNotFoundException>());
                Assert.That(errors.Items.Single().Url, Is.EqualTo("https://" + T.Host + "/no/such/route"));
                Assert.That(received.Count, Is.EqualTo(0));
                Assert.That(api.Resolves, Has.Count.EqualTo(1), "a 404 is not retried");
            });
        }

        [Test]
        public void Dedupe_ComparesTheFullUrlAsReceived()
        {
            T.Run(async () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var api = new FakeApi();
                var source = new FakeLinkSource();
                StrybokClient client = Client(api, source, new[] { "id" }, options => options.Clock = () => now, new[] { T.Host });
                var received = new Bag<ResolvedDeepLink>();
                client.LinkReceived += received.Add;
                await client.InitializeAsync();

                source.Emit("https://" + T.Host + "/product/42?id=7");
                source.Emit("https://" + T.Host + "/product/42?id=7"); // one tap reported twice
                source.Emit("https://" + T.Host + "/product/42?id=7&ref=x"); // another URL, the same lookup
                await T.Until(() => received.Count == 2, "two links");
                await T.Settle();

                Assert.That(received.Count, Is.EqualTo(2));
                Assert.That(
                    api.Resolves.Select(request => request.Url),
                    Is.EqualTo(new[]
                    {
                        ResolveUrlOf("https://" + T.Host + "/product/42?id=7"),
                        ResolveUrlOf("https://" + T.Host + "/product/42?id=7"),
                    }));
            });
        }

        [Test]
        public void Deferred_AnAnswerWithSeveralPathSegmentsIsStillRefused()
        {
            T.Run(async () =>
            {
                foreach (string url in new[] { "https://" + T.Host + "/product/42", "https://" + T.Host + "/", "https://" + T.Host + "/?link=https%3A%2F%2Fexample.com%2Fp" })
                {
                    Rig rig = AndroidRig(
                        "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/product/42\",\"url\":\"" + url + "\",\"deferred\":true}");
                    await rig.Client.InitializeAsync();
                    await T.Until(() => rig.Client.DeferredLinkStatus == DeferredLinkStatus.Failed, "the check to fail for " + url);
                    await T.Settle();
                    Assert.That(rig.Received.Count, Is.EqualTo(0), url);
                    Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1), url);
                    Assert.That(rig.Store.Get("strybok.deferred.done"), Is.EqualTo("1"), url);
                }

                // The same answer with a slug is delivered, with the option set as well.
                Rig slug = AndroidRig(
                    "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/product/42\",\"url\":\"https://" + T.Host + "/Ab3xK9\",\"deferred\":true}");
                await slug.Client.InitializeAsync();
                await T.Until(() => slug.Received.Count == 1, "the deferred link");
                Assert.That(slug.Received.Items.Single().IsDeferred, Is.True);
                Assert.That(slug.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Delivered));
            });
        }

        [Test]
        public void Deferred_APastedPathLinkIsNotOurs()
        {
            T.Run(async () =>
            {
                var api = new FakeApi();
                api.OnDeferred = _ => T.Respond(404, string.Empty);
                StrybokClient client = Client(api, null, new[] { "id" }, options =>
                {
                    options.Platform = StrybokPlatform.IOS;
                    options.DeferredLinks = true;
                    options.IosBundleId = "com.example.App";
                    options.DeferredLinkStore = new MemoryStore();
                }, new[] { T.Host });
                await client.InitializeAsync();

                // The page before the App Store copies a stored link's URL; a path link never has a click id.
                Assert.That(
                    await client.RedeemPastedLinkAsync("https://" + T.Host + "/product/42?strybok_click=" + ClickId),
                    Is.EqualTo(PastedLinkResult.NotOurs));
                Assert.That(api.Deferred, Is.Empty);
                Assert.That(
                    await client.RedeemPastedLinkAsync("https://" + T.Host + "/invite777?strybok_click=" + ClickId),
                    Is.EqualTo(PastedLinkResult.NotFound));
                Assert.That(api.Deferred, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void Deferred_APathLinkTappedFirstSupersedesTheDeferredLink()
        {
            T.Run(async () =>
            {
                // A path link is a normal link of this app: the deferred link is dropped (one launch, one navigation).
                var source = new FakeLinkSource("https://" + T.Host + "/product/42?id=7");
                Rig rig = AndroidRig(
                    "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/invite\",\"url\":\"https://" + T.Host + "/Ab3xK9\",\"deferred\":true}",
                    source);
                await rig.Client.InitializeAsync();
                await T.Until(() => rig.Client.DeferredLinkStatus == DeferredLinkStatus.Superseded, "superseded");
                await T.Until(() => rig.Received.Count == 1, "the path link");
                Assert.That(rig.Received.Items.Single().IsDeferred, Is.False);
                Assert.That(rig.Api.Deferred, Is.Empty, "no request is made");
            });
        }

        // ------------------------------------------------------------------

        private sealed class Rig
        {
            public StrybokClient Client;
            public FakeApi Api;
            public MemoryStore Store;
            public readonly Bag<ResolvedDeepLink> Received = new Bag<ResolvedDeepLink>();
        }

        /// <summary>Android deferred links answered with <paramref name="redeemBody"/>, with the option set.</summary>
        private Rig AndroidRig(string redeemBody, FakeLinkSource source = null)
        {
            var rig = new Rig { Api = new FakeApi(), Store = new MemoryStore() };
            rig.Api.OnDeferred = _ => T.Respond(200, redeemBody);
            rig.Client = Client(rig.Api, source ?? new FakeLinkSource(), new[] { "id", "lang" }, options =>
            {
                options.Platform = StrybokPlatform.Android;
                options.DeferredLinks = true;
                options.AndroidPackageName = "com.example.app";
                options.DeferredLinkStore = rig.Store;
                options.InstallReferrerSources.Add(
                    _ => Task.FromResult(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId)));
            }, new[] { T.Host });
            rig.Client.LinkReceived += rig.Received.Add;
            return rig;
        }
    }
}
