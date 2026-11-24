using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Strybok.Testing;
using UnityEngine;
using UnityEngine.TestTools;

namespace Strybok.Tests
{
    /// <summary>
    /// The Unity-facing layer, run on the Editor's main thread. The fake transport
    /// answers synchronously, so no frame has to pass.
    /// </summary>
    public sealed class UnityGlueTests
    {
        [SetUp]
        [TearDown]
        public void Reset()
        {
            StrybokSdk.Shutdown();
        }

        private static StrybokOptions Options(FakeApi api, FakeLinkSource source)
        {
            StrybokOptions options = T.Options(api, source);
            options.MaxRetries = 0;
            return options;
        }

        [Test]
        public void Sdk_QueuesTheLaunchLinkAndReplaysItToTheFirstSubscriber()
        {
            var api = new FakeApi();
            var source = new FakeLinkSource(T.Invite);
            var received = new List<ResolvedDeepLink>();
            Action<ResolvedDeepLink> handler = received.Add;

            Assert.That(StrybokSdk.IsInitialized, Is.False);
            var task = StrybokSdk.Initialize(Options(api, source));
            Assert.That(StrybokSdk.IsInitialized, Is.True);
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(received, Is.Empty);

            StrybokSdk.LinkReceived += handler;
            try
            {
                Assert.That(received, Has.Count.EqualTo(1), "replayed inside +=");
                Assert.That(received[0].RoutePath, Is.EqualTo("/invite/abc123"));

                source.Emit("https://" + T.Host + "/next");
                Assert.That(received, Has.Count.EqualTo(2));
                Assert.That(api.Events, Has.Count.EqualTo(2));
                Assert.That(api.Events[0]["platform"], Is.EqualTo("unknown"), "the Editor is neither iOS nor Android");
            }
            finally
            {
                StrybokSdk.LinkReceived -= handler;
            }
        }

        [Test]
        public void Sdk_SubscriptionMadeBeforeInitializeReceivesLinks()
        {
            var source = new FakeLinkSource(T.Invite);
            var received = new List<ResolvedDeepLink>();
            Action<ResolvedDeepLink> handler = received.Add;
            StrybokSdk.LinkReceived += handler;
            try
            {
                StrybokSdk.Initialize(Options(new FakeApi(), source));
                Assert.That(received, Has.Count.EqualTo(1));
                Assert.That(StrybokSdk.GetInitialLinkAsync().Result, Is.Null, "already delivered through the event");
            }
            finally
            {
                StrybokSdk.LinkReceived -= handler;
            }
        }

        [Test]
        public void Sdk_GetInitialLinkConsumesTheLaunchLink()
        {
            StrybokSdk.Initialize(Options(new FakeApi(), new FakeLinkSource(T.Invite)));
            ResolvedDeepLink initial = StrybokSdk.GetInitialLinkAsync().Result;
            Assert.That(initial, Is.Not.Null);

            var received = new List<ResolvedDeepLink>();
            Action<ResolvedDeepLink> handler = received.Add;
            StrybokSdk.LinkReceived += handler;
            StrybokSdk.LinkReceived -= handler;
            Assert.That(received, Is.Empty);
        }

        [Test]
        public void Sdk_ReportsFailuresThroughLinkFailedOrLogsThem()
        {
            var source = new FakeLinkSource();
            var api = new FakeApi(_ => T.Respond(404, string.Empty));
            var failures = new List<StrybokException>();
            Action<StrybokException> handler = failures.Add;
            StrybokSdk.Initialize(Options(api, source));

            StrybokSdk.LinkFailed += handler;
            source.Emit(T.Invite);
            StrybokSdk.LinkFailed -= handler;
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0], Is.InstanceOf<LinkNotFoundException>());

            LogAssert.Expect(LogType.Warning, new Regex(@"^\[Strybok\] Link failed: LinkNotFoundException"));
            source.Emit("https://" + T.Host + "/other");
        }

        [Test]
        public void Sdk_SecondInitializeKeepsTheFirstConfiguration()
        {
            var first = StrybokSdk.Initialize(Options(new FakeApi(), new FakeLinkSource()));
            StrybokClient client = StrybokSdk.Client;

            LogAssert.Expect(LogType.Warning, new Regex("Initialize was called again"));
            var second = StrybokSdk.Initialize(Options(new FakeApi(), new FakeLinkSource()));

            Assert.That(second, Is.SameAs(first));
            Assert.That(StrybokSdk.Client, Is.SameAs(client));
        }

        [Test]
        public void Sdk_ShutdownStopsDeliveryAndAllowsANewInitialize()
        {
            var source = new FakeLinkSource();
            var received = new List<ResolvedDeepLink>();
            Action<ResolvedDeepLink> handler = received.Add;
            StrybokSdk.LinkReceived += handler;
            try
            {
                StrybokSdk.Initialize(Options(new FakeApi(), source));
                StrybokSdk.Shutdown();
                Assert.That(StrybokSdk.IsInitialized, Is.False);
                Assert.That(source.HasListener, Is.False);
                source.Emit(T.Invite);
                Assert.That(received, Is.Empty);
                Assert.That(() => { StrybokSdk.ResolveAsync(T.Invite); }, Throws.InstanceOf<InvalidOperationException>());

                var next = new FakeLinkSource();
                StrybokSdk.Initialize(Options(new FakeApi(), next));
                next.Emit(T.Invite);
                Assert.That(received, Has.Count.EqualTo(1), "the subscription survives a restart");
            }
            finally
            {
                StrybokSdk.LinkReceived -= handler;
            }
        }

        [Test]
        public void Sdk_RejectsInvalidOptions()
        {
            Assert.That(() => { StrybokSdk.Initialize((StrybokOptions)null); }, Throws.InstanceOf<ArgumentNullException>());
            Assert.That(
                () => { StrybokSdk.Initialize(new StrybokOptions { ApiBaseUrl = T.Api }); },
                Throws.InstanceOf<ArgumentException>());
            Assert.That(StrybokSdk.IsInitialized, Is.False);
        }

        [Test]
        public void Settings_ProduceNormalizedOptions()
        {
            StrybokSettings settings = ScriptableObject.CreateInstance<StrybokSettings>();
            try
            {
                Assert.That(settings.ApiBaseUrl, Is.EqualTo("https://api.strybok.com"));
                Assert.That(settings.AddIosAssociatedDomains, Is.True);
                Assert.That(settings.AddAndroidIntentFilter, Is.True);

                settings.LinkHosts = new[] { " My-App.Strybok.Link ", "", null, "my-app.strybok.link", "other.example.com" };
                settings.TimeoutSeconds = 3f;
                settings.MaxRetries = 1;
                settings.ReportAppOpens = false;

                Assert.That(settings.GetNormalizedHosts(), Is.EqualTo(new[] { "my-app.strybok.link", "other.example.com" }));
                StrybokOptions options = settings.ToOptions();
                Assert.That(options.ApiBaseUrl, Is.EqualTo("https://api.strybok.com"));
                Assert.That(options.AllowedHosts, Is.EqualTo(new[] { "my-app.strybok.link", "other.example.com" }));
                Assert.That(options.Timeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
                Assert.That(options.MaxRetries, Is.EqualTo(1));
                Assert.That(options.ReportAppOpens, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void UnityLinkSource_HasNoLaunchLinkInTheEditor()
        {
            var source = new UnityLinkSource();
            Action<string> handler = _ => { };
            source.UrlReceived += handler;
            source.UrlReceived -= handler;
            Assert.That(source.GetInitialLinkAsync().Result, Is.Null);
            Assert.That(UnityLinkSource.CurrentPlatform, Is.EqualTo(StrybokPlatform.Unknown));
        }
    }
}
