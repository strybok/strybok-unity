using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;
using UnityEngine;

namespace Strybok.Tests
{
    /// <summary>
    /// Deferred links in the Unity-facing layer, run on the Editor's main thread. The fake
    /// transport and the fake referrer source answer synchronously, so no frame has to pass.
    /// </summary>
    public sealed class StrybokDeferredGlueTests
    {
        private const string ClickId = "dc_0123456789abcdefghijABCDEFGHIJ01";
        private const string TestKey = "strybok.tests.deferred.scratch";

        private const string LinkBody =
            "{\"linkId\":\"link_d\",\"appId\":\"app_1\",\"routePath\":\"/invite/deferred\"," +
            "\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true}";

        [SetUp]
        [TearDown]
        public void Reset()
        {
            StrybokSdk.Shutdown();
            PlayerPrefs.DeleteKey(TestKey);
            PlayerPrefs.DeleteKey(MemoryStore.Attempts);
            PlayerPrefs.DeleteKey(MemoryStore.Done);
        }

        private static StrybokOptions Options(FakeApi api, StrybokPlatform platform)
        {
            StrybokOptions options = T.Options(api, new FakeLinkSource());
            options.MaxRetries = 0;
            options.Platform = platform;
            options.DeferredLinks = true;
            options.InstallReferrerSources.Add(
                _ => Task.FromResult(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId)));
            return options;
        }

        [Test]
        public void PlayerPrefsStore_ReadsBackWhatItWrote()
        {
            var store = new PlayerPrefsDeferredLinkStore();
            Assert.That(store.Read(TestKey), Is.Null);
            store.Write(TestKey, "2");
            Assert.That(store.Read(TestKey), Is.EqualTo("2"));
            Assert.That(PlayerPrefs.GetString(TestKey), Is.EqualTo("2"));
            Assert.That(new PlayerPrefsDeferredLinkStore(null).Read(TestKey), Is.EqualTo("2"));
        }

        [Test]
        public void Sdk_IsDisabledBeforeInitializeAndInTheEditor()
        {
            Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));

            // The Editor is neither Android nor iOS: the option alone does nothing.
            var api = new FakeApi();
            StrybokSdk.Initialize(Options(api, StrybokPlatform.Unknown));
            Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
            Assert.That(StrybokSdk.RedeemPastedLinkAsync(T.Invite + "?strybok_click=" + ClickId).Result, Is.EqualTo(PastedLinkResult.Disabled));
            StrybokSdk.EnableDeferredLinks();
            StrybokSdk.SkipPastedLink();
            Assert.That(api.Deferred, Is.Empty);
            Assert.That(PlayerPrefs.HasKey(MemoryStore.Attempts), Is.False);
            Assert.That(PlayerPrefs.HasKey(MemoryStore.Done), Is.False);
        }

        [Test]
        public void Sdk_UsesPlayerPrefsAndTheApplicationIdentifierByDefault()
        {
            var api = new FakeApi();
            api.OnDeferred = _ => T.Respond(200, LinkBody);
            var received = new List<ResolvedDeepLink>();
            var statuses = new List<DeferredLinkStatus>();
            Action<ResolvedDeepLink> onLink = received.Add;
            Action<DeferredLinkStatus> onStatus = statuses.Add;
            StrybokSdk.LinkReceived += onLink;
            StrybokSdk.DeferredLinkStatusChanged += onStatus;
            try
            {
                StrybokSdk.Initialize(Options(api, StrybokPlatform.Android));

                Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Delivered));
                Assert.That(statuses, Is.EqualTo(new[] { DeferredLinkStatus.Checking, DeferredLinkStatus.Delivered }));
                Assert.That(received, Has.Count.EqualTo(1));
                Assert.That(received[0].IsDeferred, Is.True);
                Assert.That(received[0].IsProbabilisticMatch, Is.False);

                Assert.That(api.Deferred, Has.Count.EqualTo(1));
                object parsed;
                Assert.That(StrybokJson.TryParse(api.Deferred[0].Body, out parsed), Is.True);
                var body = (Dictionary<string, object>)parsed;
                Assert.That(body["packageName"], Is.EqualTo(Application.identifier));
                Assert.That(body["clickId"], Is.EqualTo(ClickId));

                Assert.That(PlayerPrefs.GetString(MemoryStore.Attempts), Is.EqualTo("1"));
                Assert.That(PlayerPrefs.GetString(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(api.EventRequests, Is.Empty, "no app_open for a deferred link");

                // A second launch on the same installation does nothing.
                StrybokSdk.Shutdown();
                var again = new FakeApi();
                StrybokSdk.Initialize(Options(again, StrybokPlatform.Android));
                Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.NotFirstLaunch));
                Assert.That(again.Deferred, Is.Empty);
                Assert.That(received, Has.Count.EqualTo(1));
            }
            finally
            {
                StrybokSdk.LinkReceived -= onLink;
                StrybokSdk.DeferredLinkStatusChanged -= onStatus;
            }
        }

        [Test]
        public void Sdk_EnableDeferredLinksRunsTheCheckLater()
        {
            var api = new FakeApi();
            api.OnDeferred = _ => T.Respond(200, LinkBody);
            StrybokOptions options = Options(api, StrybokPlatform.Android);
            options.DeferredLinks = false;
            options.DeferredLinkStore = new MemoryStore();
            StrybokSdk.Initialize(options);
            Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
            Assert.That(api.Deferred, Is.Empty);

            StrybokSdk.EnableDeferredLinks();
            Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Delivered));
            Assert.That(api.Deferred, Has.Count.EqualTo(1));
            Assert.That(PlayerPrefs.HasKey(MemoryStore.Done), Is.False, "the store from the options is used");
        }

        [Test]
        public void Sdk_RedeemsAPastedLinkOnIos()
        {
            var api = new FakeApi();
            api.OnDeferred = _ => T.Respond(200, LinkBody);
            StrybokOptions options = Options(api, StrybokPlatform.IOS);
            options.DeferredLinkStore = new MemoryStore();
            options.IosBundleId = "com.example.App";
            var received = new List<ResolvedDeepLink>();
            Action<ResolvedDeepLink> onLink = received.Add;
            StrybokSdk.LinkReceived += onLink;
            try
            {
                StrybokSdk.Initialize(options);
                Assert.That(StrybokSdk.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Unavailable), "nothing automatic without matching");

                PastedLinkResult result = StrybokSdk.RedeemPastedLinkAsync(T.Invite + "?strybok_click=" + ClickId).Result;
                Assert.That(result, Is.EqualTo(PastedLinkResult.Delivered));
                Assert.That(received, Has.Count.EqualTo(1));
                Assert.That(received[0].IsDeferred, Is.True);
                Assert.That(api.Deferred[0].Body, Does.Contain("\"bundleId\":\"com.example.App\""));
            }
            finally
            {
                StrybokSdk.LinkReceived -= onLink;
            }
        }

        [Test]
        public void DeviceFamilyAndLanguage_AreDerivedWithoutSendingTheModel()
        {
            Assert.That(StrybokSdk.DeviceFamilyOf("iPhone15,2"), Is.EqualTo("iphone"));
            Assert.That(StrybokSdk.DeviceFamilyOf("iPad13,4"), Is.EqualTo("ipad"));
            Assert.That(StrybokSdk.DeviceFamilyOf("iPod9,1"), Is.EqualTo("ipod"));
            Assert.That(StrybokSdk.DeviceFamilyOf("MacBookPro18,3"), Is.Null);
            Assert.That(StrybokSdk.DeviceFamilyOf("AppleTV11,1"), Is.Null);
            Assert.That(StrybokSdk.DeviceFamilyOf(""), Is.Null);
            Assert.That(StrybokSdk.DeviceFamilyOf(null), Is.Null);

            Assert.That(StrybokSdk.LanguageCodeOf(SystemLanguage.English), Is.EqualTo("en"));
            Assert.That(StrybokSdk.LanguageCodeOf(SystemLanguage.Ukrainian), Is.EqualTo("uk"));
            Assert.That(StrybokSdk.LanguageCodeOf(SystemLanguage.ChineseSimplified), Is.EqualTo("zh-Hans"));
            Assert.That(StrybokSdk.LanguageCodeOf(SystemLanguage.Unknown), Is.Null);
        }

        [Test]
        public void Settings_CarryTheTwoSwitchesAndAreOffByDefault()
        {
            StrybokSettings settings = ScriptableObject.CreateInstance<StrybokSettings>();
            try
            {
                settings.LinkHosts = new[] { T.Host };
                Assert.That(settings.DeferredLinks, Is.False);
                Assert.That(settings.AllowProbabilisticMatching, Is.False);
                Assert.That(settings.ToOptions().DeferredLinks, Is.False);
                Assert.That(settings.ToOptions().AllowProbabilisticMatching, Is.False);

                settings.DeferredLinks = true;
                settings.AllowProbabilisticMatching = true;
                StrybokOptions options = settings.ToOptions();
                Assert.That(options.DeferredLinks, Is.True);
                Assert.That(options.AllowProbabilisticMatching, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
