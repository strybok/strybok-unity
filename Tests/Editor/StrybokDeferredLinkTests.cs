using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Strybok.Testing;

namespace Strybok.Tests
{
    /// <summary>A persistent store as the game would supply it; survives a "restart" by being reused.</summary>
    internal sealed class MemoryStore : IDeferredLinkStore
    {
        public const string Attempts = "strybok.deferred.attempts";
        public const string Done = "strybok.deferred.done";

        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        private readonly List<string> _writes = new List<string>();

        public bool FailReads { get; set; }

        /// <summary>Writes of this key throw.</summary>
        public string FailWritesOf { get; set; }

        /// <summary>Every write as <c>key=value</c>, in order.</summary>
        public List<string> Writes
        {
            get { lock (_gate) return new List<string>(_writes); }
        }

        public string Get(string key)
        {
            lock (_gate)
            {
                string value;
                return _values.TryGetValue(key, out value) ? value : null;
            }
        }

        public MemoryStore With(string key, string value)
        {
            lock (_gate) _values[key] = value;
            return this;
        }

        public string Read(string key)
        {
            if (FailReads) throw new InvalidOperationException("storage is broken");
            return Get(key);
        }

        public void Write(string key, string value)
        {
            if (FailWritesOf == key) throw new InvalidOperationException("storage is full");
            lock (_gate)
            {
                _values[key] = value;
                _writes.Add(key + "=" + value);
            }
        }
    }

    /// <summary>
    /// Deferred links: the two sections of the SDK contract, with a fake referrer source, an
    /// in-memory store and the stub transport.
    /// </summary>
    public sealed class StrybokDeferredLinkTests
    {
        private const string ClickId = "dc_0123456789abcdefghijABCDEFGHIJ01";
        private const string OtherClickId = "dc_ZZZZ456789abcdefghijABCDEFGHIJ99";
        private const string RedeemUrl = T.Api + "/v1/public/deferred-links/redeem";
        private const string MatchUrl = T.Api + "/v1/public/deferred-links/match";
        private const string PastedUrl = T.Invite + "?strybok_click=" + ClickId;

        private const string LinkBody =
            "{\"linkId\":\"link_d\",\"appId\":\"app_1\",\"routePath\":\"/invite/deferred\"," +
            "\"payload\":{\"invitationId\":\"abc123\"},\"campaign\":{\"source\":\"instagram\",\"other\":\"x\"}," +
            "\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true,\"somethingNew\":1}";

        private const string MatchBody =
            "{\"linkId\":\"link_m\",\"appId\":\"app_1\",\"routePath\":\"/promo\"," +
            "\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true,\"matched\":\"probabilistic\"}";

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

        /// <summary>A client and everything the tests observe.</summary>
        private sealed class Rig
        {
            public StrybokClient Client;
            public FakeApi Api;
            public FakeLinkSource Source;
            public readonly Bag<ResolvedDeepLink> Received = new Bag<ResolvedDeepLink>();
            public readonly Bag<StrybokException> Errors = new Bag<StrybokException>();
            public readonly Bag<DeferredLinkStatus> Statuses = new Bag<DeferredLinkStatus>();

            public void Listen()
            {
                Client.LinkReceived += Received.Add;
            }

            public Task Status(DeferredLinkStatus status)
            {
                return T.Until(() => Client.DeferredLinkStatus == status, "status " + status);
            }

            /// <summary>Starts the client and waits until the check has reached <paramref name="status"/>.</summary>
            public async Task Started(DeferredLinkStatus status)
            {
                await Client.InitializeAsync();
                if (status == DeferredLinkStatus.Pending)
                {
                    // "Pending" is also the status before the check starts: wait until it has run.
                    await T.Until(() => Statuses.Items.Contains(DeferredLinkStatus.Checking), "the check to start");
                }
                await Status(status);
            }
        }

        private Rig Make(Action<StrybokOptions> configure, FakeLinkSource source = null)
        {
            var rig = new Rig { Api = new FakeApi(), Source = source ?? new FakeLinkSource() };
            rig.Api.OnDeferred = request => T.Respond(
                200, request.Url.EndsWith("/match", StringComparison.Ordinal) ? MatchBody : LinkBody);
            StrybokOptions options = T.Options(rig.Api, rig.Source);
            options.OnError = rig.Errors.Add;
            configure(options);
            rig.Client = new StrybokClient(options);
            rig.Client.DeferredLinkStatusChanged += rig.Statuses.Add;
            lock (_clients) _clients.Add(rig.Client);
            return rig;
        }

        private Rig Android(MemoryStore store, params InstallReferrerSource[] sources)
        {
            return Android(store, null, sources);
        }

        private Rig Android(MemoryStore store, FakeLinkSource linkSource, params InstallReferrerSource[] sources)
        {
            return Make(
                options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.DeferredLinks = true;
                    options.AndroidPackageName = "com.example.app";
                    options.DeferredLinkStore = store;
                    foreach (InstallReferrerSource source in sources) options.InstallReferrerSources.Add(source);
                },
                linkSource);
        }

        private Rig Ios(MemoryStore store, Action<StrybokOptions> configure = null, FakeLinkSource linkSource = null)
        {
            return Make(
                options =>
                {
                    options.Platform = StrybokPlatform.IOS;
                    options.DeferredLinks = true;
                    options.IosBundleId = "com.example.App";
                    options.DeferredLinkStore = store;
                    if (configure != null) configure(options);
                },
                linkSource);
        }

        private static Action<StrybokOptions> Matching(Action<StrybokOptions> more = null)
        {
            return options =>
            {
                options.AllowProbabilisticMatching = true;
                options.DeviceFamily = () => "iphone";
                options.Locale = () => "en-GB";
                if (more != null) more(options);
            };
        }

        private static Action<StrybokOptions> MatchingAfterPasteCheck()
        {
            return Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(true));
        }

        private static InstallReferrerSource Source(InstallReferrerAnswer answer, Bag<string> calls = null, string name = "a")
        {
            return _ =>
            {
                if (calls != null) calls.Add(name);
                return Task.FromResult(answer);
            };
        }

        private static InstallReferrerSource Referrer(string referrer = "utm_source=x&strybok_click=" + ClickId)
        {
            return Source(InstallReferrerAnswer.Referrer(referrer));
        }

        private static Task<InstallReferrerAnswer> NeverAnswers()
        {
            return new TaskCompletionSource<InstallReferrerAnswer>().Task;
        }

        // ------------------------------------------------------------------
        // Switched off
        // ------------------------------------------------------------------

        [Test]
        public void Options_AreOffByDefault()
        {
            var options = new StrybokOptions();
            Assert.That(options.DeferredLinks, Is.False);
            Assert.That(options.AllowProbabilisticMatching, Is.False);
            Assert.That(options.InstallReferrerSources, Is.Empty);
            Assert.That(options.DeferredLinkStore, Is.Null);
            Assert.That(options.InstallReferrerSourceTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));

            options.InstallReferrerSources.Add(Referrer());
            StrybokOptions copy = options.Clone();
            copy.InstallReferrerSources.Clear();
            Assert.That(options.InstallReferrerSources, Has.Count.EqualTo(1), "Clone copies the list");
        }

        [Test]
        public void OffByDefault_NothingIsAskedSentOrStored()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                var calls = new Bag<string>();
                Rig rig = Make(options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.AndroidPackageName = "com.example.app";
                    options.IosBundleId = "com.example.App";
                    options.DeferredLinkStore = store;
                    options.InstallReferrerSources.Add(Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                });
                rig.Listen();

                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
                await rig.Client.InitializeAsync();
                await T.Settle();

                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Writes, Is.Empty);
                Assert.That(rig.Received.Count, Is.EqualTo(0));
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Disabled));
                Assert.That(rig.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void WithoutAStore_ItStaysDisabled()
        {
            T.Run(async () =>
            {
                var calls = new Bag<string>();
                Rig android = Android(null, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                await android.Client.InitializeAsync();
                android.Client.EnableDeferredLinks();
                await T.Settle();
                Assert.That(android.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(android.Api.Deferred, Is.Empty);

                Rig ios = Ios(null);
                await ios.Client.InitializeAsync();
                Assert.That(await ios.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Disabled));
                Assert.That(ios.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
                Assert.That(ios.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void WithoutTheIdentifierOrOnAnotherPlatform_ItStaysDisabled()
        {
            T.Run(async () =>
            {
                var cases = new List<KeyValuePair<StrybokPlatform, string>>
                {
                    new KeyValuePair<StrybokPlatform, string>(StrybokPlatform.Android, null),
                    new KeyValuePair<StrybokPlatform, string>(StrybokPlatform.Android, "  "),
                    new KeyValuePair<StrybokPlatform, string>(StrybokPlatform.IOS, null),
                    new KeyValuePair<StrybokPlatform, string>(StrybokPlatform.Unknown, "com.example.app"),
                };
                foreach (KeyValuePair<StrybokPlatform, string> item in cases)
                {
                    var store = new MemoryStore();
                    var calls = new Bag<string>();
                    Rig rig = Make(options =>
                    {
                        options.Platform = item.Key;
                        options.DeferredLinks = true;
                        options.AllowProbabilisticMatching = true;
                        options.AndroidPackageName = item.Value;
                        options.IosBundleId = item.Value;
                        options.DeferredLinkStore = store;
                        options.InstallReferrerSources.Add(Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                    });
                    await rig.Client.InitializeAsync();
                    await T.Settle();

                    Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled), item.Key + " " + item.Value);
                    Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Disabled));
                    Assert.That(calls.Count, Is.EqualTo(0));
                    Assert.That(rig.Api.Deferred, Is.Empty);
                    Assert.That(store.Writes, Is.Empty);
                }
            });
        }

        // ------------------------------------------------------------------
        // Android: the flow
        // ------------------------------------------------------------------

        [Test]
        public void Android_RedeemsTheClickIdAndDeliversTheLinkOnce()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(store, Referrer());
                rig.Listen();

                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Pending));
                await rig.Started(DeferredLinkStatus.Delivered);

                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                StrybokHttpRequest request = rig.Api.Deferred[0];
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.Url, Is.EqualTo(RedeemUrl));
                Assert.That(request.Headers.Count, Is.EqualTo(2));
                Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/json"));
                Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
                Assert.That(
                    request.Body,
                    Is.EqualTo("{\"platform\":\"android\",\"clickId\":\"" + ClickId +
                               "\",\"packageName\":\"com.example.app\",\"sdkVersion\":\"" + StrybokClient.SdkVersion + "\"}"));

                await T.Until(() => rig.Received.Count == 1, "the deferred link");
                ResolvedDeepLink link = rig.Received.Items[0];
                Assert.That(link.IsDeferred, Is.True);
                Assert.That(link.IsProbabilisticMatch, Is.False);
                Assert.That(link.Url, Is.EqualTo("https://my-app.strybok.link/invite777"));
                Assert.That(link.LinkId, Is.EqualTo("link_d"));
                Assert.That(link.AppId, Is.EqualTo("app_1"));
                Assert.That(link.RoutePath, Is.EqualTo("/invite/deferred"));
                Assert.That(link.GetString("invitationId"), Is.EqualTo("abc123"));
                Assert.That(link.Campaign.Source, Is.EqualTo("instagram"));
                Assert.That(link.Campaign.Medium, Is.Null);

                Assert.That(store.Writes, Is.EqualTo(new[] { "strybok.deferred.attempts=1", "strybok.deferred.done=1" }));
                Assert.That(rig.Statuses.Items, Is.EqualTo(new[] { DeferredLinkStatus.Checking, DeferredLinkStatus.Delivered }));

                await T.Settle();
                Assert.That(rig.Api.EventRequests, Is.Empty, "no app_open for a deferred link");
                Assert.That(rig.Api.Resolves, Is.Empty);
                Assert.That(rig.Errors.Count, Is.EqualTo(0));
                Assert.That(rig.Received.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void NormalLinks_AreNotDeferred()
        {
            T.Run(async () =>
            {
                Rig rig = Make(options => { }, new FakeLinkSource(T.Invite));
                rig.Listen();
                await rig.Client.InitializeAsync();
                await T.Until(() => rig.Received.Count == 1, "the launch link");
                Assert.That(rig.Received.Items[0].IsDeferred, Is.False);
                Assert.That(rig.Received.Items[0].IsProbabilisticMatch, Is.False);

                ResolvedDeepLink resolved = await rig.Client.ResolveAsync(T.Invite);
                Assert.That(resolved.IsDeferred, Is.False);
                Assert.That(resolved.IsProbabilisticMatch, Is.False);
                Assert.That(new ResolvedDeepLink("u", "l", "a", "/r", null).IsDeferred, Is.False);
            });
        }

        [Test]
        public void StartCompletesWhileASourceBlocks_AndTheAttemptIsStoredFirst()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                string attemptsWhenAsked = null;
                var asked = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<InstallReferrerAnswer>();
                Rig rig = Android(store, _ =>
                {
                    attemptsWhenAsked = store.Get(MemoryStore.Attempts);
                    asked.TrySetResult(true);
                    return release.Task;
                });

                await rig.Client.InitializeAsync(); // Does not wait for the source.
                Assert.That(await rig.Client.GetInitialLinkAsync(), Is.Null);
                await asked.Task;

                Assert.That(attemptsWhenAsked, Is.EqualTo("1"));
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Checking));
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(rig.Api.Deferred, Is.Empty);

                release.SetResult(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId));
                await rig.Status(DeferredLinkStatus.Delivered);
            });
        }

        [Test]
        public void ThreeAttempts_ThenFailed()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                var calls = new Bag<string>();
                for (int launch = 1; launch <= 3; launch++)
                {
                    Rig rig = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
                    await rig.Started(DeferredLinkStatus.Pending);
                    Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo(launch.ToString()));
                    Assert.That(store.Get(MemoryStore.Done), Is.Null);
                }

                Rig fourth = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
                await fourth.Started(DeferredLinkStatus.Failed);
                Assert.That(calls.Count, Is.EqualTo(3), "not asked a fourth time");
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo("3"));

                Rig fifth = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
                await fifth.Started(DeferredLinkStatus.NotFirstLaunch);
                Assert.That(calls.Count, Is.EqualTo(3));
                Assert.That(fifth.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void ACounterThatIsNotOurs_EndsTheCheck()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore().With(MemoryStore.Attempts, "many");
                var calls = new Bag<string>();
                Rig rig = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
                await rig.Started(DeferredLinkStatus.Failed);
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
            });
        }

        [Test]
        public void DoneInAnEarlierLaunch_IsNotFirstLaunch()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore().With(MemoryStore.Done, "1");
                var calls = new Bag<string>();
                Rig rig = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                await rig.Started(DeferredLinkStatus.NotFirstLaunch);
                await T.Settle();
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Writes, Is.Empty);
            });
        }

        [Test]
        public void NoSourcesOrSourcesThatCanNeverAnswer_AreUnavailable()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig none = Android(store);
                await none.Started(DeferredLinkStatus.Unavailable);
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(none.Api.Deferred, Is.Empty);

                var second = new MemoryStore();
                Rig never = Android(second, Source(InstallReferrerAnswer.Unavailable), null, Source(InstallReferrerAnswer.Unavailable));
                await never.Started(DeferredLinkStatus.Unavailable);
                Assert.That(second.Get(MemoryStore.Done), Is.EqualTo("1"));
            });
        }

        [Test]
        public void AReferrerWithoutOurId_IsNoReferrer()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(
                    store,
                    Source(InstallReferrerAnswer.Unavailable),
                    Source(InstallReferrerAnswer.Referrer("utm_source=google-play&utm_medium=organic")));
                await rig.Started(DeferredLinkStatus.NoReferrer);
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(rig.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void UnavailableForNow_KeepsTheCheckPendingEvenNextToAReferrerWithoutId()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(
                    store,
                    Source(InstallReferrerAnswer.Referrer(null)),
                    Source(default(InstallReferrerAnswer)),
                    Source(InstallReferrerAnswer.Unavailable));
                await rig.Started(DeferredLinkStatus.Pending);
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(rig.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void Sources_AreAskedInOrderAndTheFirstIdWins()
        {
            T.Run(async () =>
            {
                var calls = new Bag<string>();
                Rig rig = Android(
                    new MemoryStore(),
                    Source(InstallReferrerAnswer.Unavailable, calls, "play"),
                    Source(InstallReferrerAnswer.Referrer("utm_source=x"), calls, "galaxy"),
                    Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls, "appgallery"),
                    Source(InstallReferrerAnswer.Referrer("strybok_click=" + OtherClickId), calls, "fourth"));
                await rig.Started(DeferredLinkStatus.Delivered);

                Assert.That(calls.Items, Is.EqualTo(new[] { "play", "galaxy", "appgallery" }));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Api.Deferred[0].Body, Does.Contain(ClickId));
            });
        }

        [Test]
        public void ASourceThatTakesTooLong_IsUnavailableForNowAndIsAbandoned()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                CancellationToken token = default(CancellationToken);
                Rig rig = Make(options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.DeferredLinks = true;
                    options.AndroidPackageName = "com.example.app";
                    options.DeferredLinkStore = store;
                    options.InstallReferrerSourceTimeout = TimeSpan.FromMilliseconds(40);
                    options.InstallReferrerSources.Add(cancellationToken =>
                    {
                        token = cancellationToken;
                        return NeverAnswers();
                    });
                });

                await rig.Started(DeferredLinkStatus.Pending);
                await T.Until(() => token.IsCancellationRequested, "the source is told to give up");
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(rig.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void ASourceThatThrows_IsUnavailableForNow()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(
                    store,
                    _ => { throw new InvalidOperationException("boom"); },
                    _ => Task.FromException<InstallReferrerAnswer>(new InvalidOperationException("boom")),
                    _ => null);
                await rig.Started(DeferredLinkStatus.Pending);
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(rig.Errors.Count, Is.EqualTo(0));
            });
        }

        // ------------------------------------------------------------------
        // The referrer and the pasted link
        // ------------------------------------------------------------------

        [Test]
        public void Referrer_TheClickIdIsFound()
        {
            var referrers = new[]
            {
                "strybok_click=" + ClickId,
                "utm_source=x&strybok_click=" + ClickId + "&utm_medium=y",
                "&&strybok_click=" + ClickId + "&strybok_click=" + OtherClickId,
                "xstrybok_click=" + OtherClickId + "&strybok_click=" + ClickId,
                "strybok_click%3D" + ClickId + "%26utm_source%3Dx",
                "strybok_click%3d" + ClickId,
                "utm_source%3Dx%26strybok_click%3D" + ClickId,
            };
            foreach (string referrer in referrers)
            {
                Assert.That(DeferredLinkParsing.FindClickIdInReferrer(referrer), Is.EqualTo(ClickId), referrer);
            }
        }

        [Test]
        public void Referrer_NoClickIdIsNeverAnError()
        {
            var referrers = new[]
            {
                null,
                "",
                "strybok_click",
                "strybok_click=",
                "strybok_click=dc_short",
                "strybok_click=dc_0123456789abcdefghijABCDEFGHIJ0!",
                "strybok_click=" + ClickId + "2",
                "strybok_click=DC_0123456789abcdefghijABCDEFGHIJ01",
                "Strybok_Click=" + ClickId,
                "xstrybok_click=" + ClickId,
                "strybok_click_x=" + ClickId,
                // The first part with the key decides, even when a later one is valid.
                "strybok_click=nope&strybok_click=" + ClickId,
                // A part with the key exists, so the still-encoded form is not tried.
                "strybok_click=nope&x=strybok_click%3D" + ClickId,
                // Still encoded, with the key in another letter case: decoded, and then not our key.
                "STRYBOK_CLICK%3D" + ClickId,
                // Still encoded, but malformed.
                "strybok_click%3D" + ClickId + "%2",
                "strybok_click%3D" + ClickId + "%zz",
                // Encoded twice: decoded once only.
                "strybok_click%253D" + ClickId,
                "strybok_click%3Ddc_short",
                "utm_source=é&utm_medium=%FF",
            };
            foreach (string referrer in referrers)
            {
                Assert.That(DeferredLinkParsing.FindClickIdInReferrer(referrer), Is.Null, referrer ?? "null");
            }
        }

        [Test]
        public void Android_AReferrerWithoutIdEndsWithoutARequest()
        {
            T.Run(async () =>
            {
                foreach (string referrer in new[] { "", "strybok_click=nope&strybok_click=" + ClickId, "strybok_click%3D" + ClickId + "%zz" })
                {
                    var store = new MemoryStore();
                    Rig rig = Android(store, Referrer(referrer));
                    await rig.Started(DeferredLinkStatus.NoReferrer);
                    Assert.That(rig.Api.Deferred, Is.Empty);
                    Assert.That(rig.Errors.Count, Is.EqualTo(0));
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                }

                Rig encoded = Android(new MemoryStore(), Referrer("utm_source%3Dx%26strybok_click%3D" + ClickId));
                await encoded.Started(DeferredLinkStatus.Delivered);
                Assert.That(encoded.Api.Deferred[0].Body, Does.Contain("\"clickId\":\"" + ClickId + "\""));
            });
        }

        [Test]
        public void PastedLink_TheClickIdIsReadLikeAQueryParameter()
        {
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(PastedUrl), Is.EqualTo(ClickId));
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?utm_source=x&strybok_click=" + ClickId + "#top"), Is.EqualTo(ClickId));
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?strybok%5Fclick=dc%5F0123456789abcdefghijABCDEFGHIJ01"), Is.EqualTo(ClickId));
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?strybok_click=" + ClickId + "&strybok_click=nope"), Is.EqualTo(ClickId));

            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(null), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?"), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "#?strybok_click=" + ClickId), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?a=1#strybok_click=" + ClickId), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?strybok_click=nope&strybok_click=" + ClickId), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?xstrybok_click=" + ClickId), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?strybok_click=dc_short"), Is.Null);
            Assert.That(DeferredLinkParsing.FindClickIdInLinkQuery(T.Invite + "?strybok_click=%zz"), Is.Null);
        }

        // ------------------------------------------------------------------
        // Android: the request
        // ------------------------------------------------------------------

        [Test]
        public void ServerErrorsAndNetworkFailures_AreRetriedWithTheIdenticalBody()
        {
            T.Run(async () =>
            {
                Rig rig = Android(new MemoryStore(), Referrer());
                int calls = 0;
                rig.Api.OnDeferred = _ =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1) return T.Respond(503, string.Empty);
                    if (call == 2) throw new InvalidOperationException("offline");
                    return T.Respond(200, LinkBody);
                };
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Delivered);

                List<StrybokHttpRequest> requests = rig.Api.Deferred;
                Assert.That(requests, Has.Count.EqualTo(3));
                Assert.That(requests[1].Body, Is.EqualTo(requests[0].Body));
                Assert.That(requests[2].Body, Is.EqualTo(requests[0].Body));
                await T.Until(() => rig.Received.Count == 1, "the deferred link");
            });
        }

        [Test]
        public void ServerErrorsAndTimeoutsAfterAllRetries_LeaveTheCheckPending()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig failing = Android(store, Referrer());
                failing.Api.OnDeferred = _ => T.Respond(500, string.Empty);
                await failing.Started(DeferredLinkStatus.Pending);
                Assert.That(failing.Api.Deferred, Has.Count.EqualTo(3));
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo("1"));
                Assert.That(failing.Errors.Count, Is.EqualTo(0));

                Rig silent = Android(store, Referrer());
                silent.Api.OnDeferred = _ => T.Never();
                await silent.Started(DeferredLinkStatus.Pending);
                Assert.That(silent.Api.Deferred, Has.Count.EqualTo(3));
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
                Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo("2"));

                // The next launch tries again with the same id.
                Rig next = Android(store, Referrer());
                next.Listen();
                await next.Started(DeferredLinkStatus.Delivered);
                Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo("3"));
            });
        }

        [Test]
        public void RateLimited_IsPendingWithoutARetry()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(store, Referrer());
                rig.Api.OnDeferred = _ => T.Respond(429, string.Empty);
                await rig.Started(DeferredLinkStatus.Pending);
                await T.Settle();
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(store.Get(MemoryStore.Done), Is.Null);
            });
        }

        [Test]
        public void ARefusal_IsNotFoundAndFinal()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 404, 400, 410, 422 })
                {
                    var store = new MemoryStore();
                    Rig rig = Android(store, Referrer());
                    rig.Api.OnDeferred = _ => T.Respond(status, "{\"error\":{\"code\":\"DEFERRED_LINK_NOT_FOUND\"}}");
                    rig.Listen();
                    await rig.Started(DeferredLinkStatus.NotFound);
                    Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1), "status " + status);
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                    Assert.That(rig.Received.Count, Is.EqualTo(0));
                    Assert.That(rig.Errors.Count, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void AnUnusableAnswer_IsFailedAndNothingIsDelivered()
        {
            const string Head = "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",";
            const string Ours = "\"url\":\"https://my-app.strybok.link/invite777\"";
            var bodies = new[]
            {
                "not json",
                "[]",
                "{}",
                // deferred missing, false, or not a boolean
                Head + Ours + "}",
                Head + Ours + ",\"deferred\":false}",
                Head + Ours + ",\"deferred\":\"true\"}",
                // url missing, on a foreign host, or not a link of ours
                Head + "\"deferred\":true}",
                Head + "\"url\":\"https://evil.example/invite777\",\"deferred\":true}",
                Head + "\"url\":\"http://my-app.strybok.link/invite777\",\"deferred\":true}",
                Head + "\"url\":\"https://my-app.strybok.link/a/b\",\"deferred\":true}",
                // the fields of resolving
                "{\"appId\":\"a\",\"routePath\":\"/r\"," + Ours + ",\"deferred\":true}",
                "{\"linkId\":\"l\",\"routePath\":\"/r\"," + Ours + ",\"deferred\":true}",
                "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"r\"," + Ours + ",\"deferred\":true}",
                "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"//evil.example\"," + Ours + ",\"deferred\":true}",
            };
            T.Run(async () =>
            {
                foreach (string body in bodies)
                {
                    var store = new MemoryStore();
                    Rig rig = Android(store, Referrer());
                    rig.Api.OnDeferred = _ => T.Respond(200, body);
                    rig.Listen();
                    await rig.Started(DeferredLinkStatus.Failed);
                    Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1), body);
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"), body);
                    Assert.That(rig.Received.Count, Is.EqualTo(0), body);
                    Assert.That(rig.Errors.Count, Is.EqualTo(0), body);
                }
            });
        }

        // ------------------------------------------------------------------
        // A normal link wins
        // ------------------------------------------------------------------

        [Test]
        public void TheLaunchLinkWins_AndNoRequestIsMade()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(store, new FakeLinkSource(T.Invite), Referrer());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Superseded);
                await T.Settle();

                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(rig.Received.Count, Is.EqualTo(1));
                Assert.That(rig.Received.Items[0].IsDeferred, Is.False);
            });
        }

        [Test]
        public void ALaunchLinkThatFailedToResolve_StillWins()
        {
            T.Run(async () =>
            {
                Rig rig = Android(new MemoryStore(), new FakeLinkSource(T.Invite), Referrer());
                rig.Api.OnResolve = _ => T.Respond(404, string.Empty);
                await rig.Started(DeferredLinkStatus.Superseded);
                Assert.That(rig.Api.Deferred, Is.Empty);
                await T.Until(() => rig.Errors.Count == 1, "the normal link's own error");
            });
        }

        [Test]
        public void ALinkThatIsNotOurs_DoesNotSupersede()
        {
            T.Run(async () =>
            {
                Rig rig = Android(new MemoryStore(), new FakeLinkSource("https://elsewhere.example/invite777"), Referrer());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Delivered);
                await T.Until(() => rig.Received.Count == 1, "the deferred link");
                Assert.That(rig.Received.Items[0].IsDeferred, Is.True);
            });
        }

        [Test]
        public void ALinkArrivingWhileTheRequestIsInFlight_Wins()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(store, Referrer());
                var answer = new TaskCompletionSource<StrybokHttpResponse>();
                rig.Api.OnDeferred = _ => answer.Task;
                rig.Listen();
                await rig.Client.InitializeAsync();
                await T.Until(() => rig.Api.Deferred.Count == 1, "the redeem request");

                rig.Source.Emit(T.Invite);
                await T.Until(() => rig.Received.Count == 1, "the normal link");
                answer.SetResult(new StrybokHttpResponse(200, LinkBody));

                await rig.Status(DeferredLinkStatus.Superseded);
                await T.Settle();
                Assert.That(rig.Received.Count, Is.EqualTo(1), "dropped, not queued behind it");
                Assert.That(rig.Received.Items[0].IsDeferred, Is.False);
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
            });
        }

        // ------------------------------------------------------------------
        // Delivery
        // ------------------------------------------------------------------

        [Test]
        public void Done_IsStoredBeforeTheLinkIsDelivered()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Android(store, Referrer());
                string doneAtDelivery = null;
                var delivered = new TaskCompletionSource<bool>();
                rig.Client.LinkReceived += link =>
                {
                    doneAtDelivery = store.Get(MemoryStore.Done);
                    delivered.TrySetResult(true);
                };
                await rig.Client.InitializeAsync();
                await delivered.Task;

                Assert.That(doneAtDelivery, Is.EqualTo("1"));
                await rig.Status(DeferredLinkStatus.Delivered);
            });
        }

        [Test]
        public void ItIsQueuedUntilAHandlerAttaches_AndIsNeverTheInitialLink()
        {
            T.Run(async () =>
            {
                Rig rig = Android(new MemoryStore(), Referrer());
                await rig.Started(DeferredLinkStatus.Delivered);

                Assert.That(await rig.Client.GetInitialLinkAsync(), Is.Null);
                Assert.That(await rig.Client.GetInitialLinkAsync(), Is.Null);
                Assert.That(rig.Received.Count, Is.EqualTo(0));

                rig.Listen();
                await T.Until(() => rig.Received.Count == 1, "the queued deferred link");
                Assert.That(rig.Received.Items[0].IsDeferred, Is.True);
                await T.Settle();
                Assert.That(rig.Api.EventRequests, Is.Empty);
            });
        }

        [Test]
        public void AfterDelivery_ARestartDoesNothing()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig first = Android(store, Referrer());
                first.Listen();
                await first.Started(DeferredLinkStatus.Delivered);
                first.Client.Dispose();

                var calls = new Bag<string>();
                Rig second = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                second.Listen();
                await second.Started(DeferredLinkStatus.NotFirstLaunch);
                await T.Settle();
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(second.Api.Deferred, Is.Empty);
                Assert.That(second.Received.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void ALaunchKilledWhileTheSourceIsAsked_HasUsedOneAttempt()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig first = Android(store, _ => NeverAnswers());
                await first.Started(DeferredLinkStatus.Checking);
                await T.Until(() => store.Get(MemoryStore.Attempts) == "1", "the attempt");
                first.Client.Dispose();
                Assert.That(store.Get(MemoryStore.Done), Is.Null);

                Rig second = Android(store, Referrer());
                second.Listen();
                await second.Started(DeferredLinkStatus.Delivered);
                Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo("2"));
            });
        }

        [Test]
        public void ALaunchKilledDuringTheRequest_SendsTheSameIdAgain_AndNothingIsDeliveredAfterDispose()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig first = Android(store, Referrer());
                var answer = new TaskCompletionSource<StrybokHttpResponse>();
                first.Api.OnDeferred = _ => answer.Task;
                first.Listen();
                await first.Client.InitializeAsync();
                await T.Until(() => first.Api.Deferred.Count == 1, "the redeem request");

                first.Client.Dispose();
                answer.SetResult(new StrybokHttpResponse(200, LinkBody)); // The transport ignored the abort.
                await T.Settle();
                Assert.That(first.Received.Count, Is.EqualTo(0));
                Assert.That(store.Get(MemoryStore.Done), Is.Null);

                Rig second = Android(store, Referrer());
                second.Listen();
                await second.Started(DeferredLinkStatus.Delivered);
                Assert.That(second.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(second.Api.Deferred[0].Body, Does.Contain(ClickId));
            });
        }

        // ------------------------------------------------------------------
        // Switching on later
        // ------------------------------------------------------------------

        [Test]
        public void EnableDeferredLinks_AfterStartRunsTheCheckOnce()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Make(options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.AndroidPackageName = "com.example.app";
                    options.DeferredLinkStore = store;
                    options.InstallReferrerSources.Add(Referrer());
                });
                rig.Listen();
                await rig.Client.InitializeAsync();
                await T.Settle();
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Disabled));
                Assert.That(store.Writes, Is.Empty);

                rig.Client.EnableDeferredLinks();
                await rig.Status(DeferredLinkStatus.Delivered);
                rig.Client.EnableDeferredLinks();
                rig.Client.EnableDeferredLinks();
                await T.Settle();

                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Received.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void EnableDeferredLinks_BeforeStartWaitsForStart()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Make(options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.AndroidPackageName = "com.example.app";
                    options.DeferredLinkStore = store;
                    options.InstallReferrerSources.Add(Referrer());
                });

                rig.Client.EnableDeferredLinks();
                await T.Settle();
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Pending));
                Assert.That(store.Writes, Is.Empty);

                await rig.Started(DeferredLinkStatus.Delivered);
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
            });
        }

        // ------------------------------------------------------------------
        // A failing store
        // ------------------------------------------------------------------

        [Test]
        public void AStoreThatFails_StopsTheCheckRatherThanDeliverTwice()
        {
            T.Run(async () =>
            {
                var calls = new Bag<string>();
                Rig unreadable = Android(
                    new MemoryStore { FailReads = true },
                    Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                await unreadable.Started(DeferredLinkStatus.Pending);
                await T.Settle();
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(unreadable.Api.Deferred, Is.Empty);
                Assert.That(unreadable.Errors.Count, Is.EqualTo(0));

                Rig noAttempt = Android(
                    new MemoryStore { FailWritesOf = MemoryStore.Attempts },
                    Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
                await noAttempt.Started(DeferredLinkStatus.Pending);
                await T.Settle();
                Assert.That(calls.Count, Is.EqualTo(0));
                Assert.That(noAttempt.Api.Deferred, Is.Empty);

                Rig noDone = Android(new MemoryStore { FailWritesOf = MemoryStore.Done }, Referrer());
                noDone.Listen();
                await noDone.Started(DeferredLinkStatus.Failed);
                await T.Settle();
                Assert.That(noDone.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(noDone.Received.Count, Is.EqualTo(0), "not delivered when done cannot be stored");
            });
        }

        // ------------------------------------------------------------------
        // iOS: pasted link
        // ------------------------------------------------------------------

        [Test]
        public void Ios_WithoutMatchingDoesNothingAutomatic()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store);
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Pending));
                await rig.Started(DeferredLinkStatus.Unavailable);
                await T.Settle();
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Writes, Is.Empty, "not done: pasting must keep working");
            });
        }

        [Test]
        public void APastedLink_IsRedeemedAndDeliveredEvenAfterANormalLink()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store, null, new FakeLinkSource("https://" + T.Host + "/first"));
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Unavailable);
                await T.Until(() => rig.Received.Count == 1, "the launch link");
                await T.Until(() => rig.Api.EventRequests.Count == 1, "its app_open");

                PastedLinkResult result = await rig.Client.RedeemPastedLinkAsync("  " + PastedUrl + "\n");

                Assert.That(result, Is.EqualTo(PastedLinkResult.Delivered));
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Delivered));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                StrybokHttpRequest request = rig.Api.Deferred[0];
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.Url, Is.EqualTo(RedeemUrl));
                Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/json"));
                Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
                Assert.That(
                    request.Body,
                    Is.EqualTo("{\"platform\":\"ios\",\"clickId\":\"" + ClickId +
                               "\",\"bundleId\":\"com.example.App\",\"sdkVersion\":\"" + StrybokClient.SdkVersion + "\"}"));

                await T.Until(() => rig.Received.Count == 2, "the pasted link");
                ResolvedDeepLink link = rig.Received.Items[1];
                Assert.That(link.IsDeferred, Is.True);
                Assert.That(link.IsProbabilisticMatch, Is.False);
                Assert.That(link.Url, Is.EqualTo("https://my-app.strybok.link/invite777"), "without the click id");
                Assert.That(store.Writes, Is.EqualTo(new[] { "strybok.deferred.done=1" }));

                await T.Settle();
                Assert.That(rig.Api.EventRequests, Has.Count.EqualTo(1), "no app_open for the pasted link");
                Assert.That(rig.Errors.Count, Is.EqualTo(0));

                // Once only.
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.AlreadyHandled));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Received.Count, Is.EqualTo(2));
            });
        }

        [Test]
        public void APastedLink_WorksBeforeStartAndIsQueued()
        {
            T.Run(async () =>
            {
                Rig rig = Ios(new MemoryStore());
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl + "&x=1#top"), Is.EqualTo(PastedLinkResult.Delivered));
                Assert.That(await rig.Client.GetInitialLinkAsync(), Is.Null);
                rig.Listen();
                await T.Until(() => rig.Received.Count == 1, "the queued pasted link");
                Assert.That(rig.Received.Items[0].IsDeferred, Is.True);
            });
        }

        [Test]
        public void TextThatIsNotACopiedLinkOfOurs_IsDroppedWithoutARequest()
        {
            var texts = new[]
            {
                null,
                "",
                "my grocery list",
                T.Invite,
                T.Invite + "?utm_source=x",
                T.Invite + "#strybok_click=" + ClickId,
                "https://elsewhere.example/invite777?strybok_click=" + ClickId,
                "http://my-app.strybok.link/invite777?strybok_click=" + ClickId,
                "https://my-app.strybok.link/a/b?strybok_click=" + ClickId,
                "https://user@my-app.strybok.link/invite777?strybok_click=" + ClickId,
                T.Invite + "?strybok_click=dc_short",
                T.Invite + "?strybok_click=nope&strybok_click=" + ClickId,
                T.Invite + "?xstrybok_click=" + ClickId,
                "see " + PastedUrl,
            };
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store);
                rig.Listen();
                await rig.Client.InitializeAsync();
                foreach (string text in texts)
                {
                    Assert.That(await rig.Client.RedeemPastedLinkAsync(text), Is.EqualTo(PastedLinkResult.NotOurs), text ?? "null");
                }
                await T.Settle();
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Writes, Is.Empty);
                Assert.That(rig.Received.Count, Is.EqualTo(0));
                Assert.That(rig.Errors.Count, Is.EqualTo(0));

                // Nothing was used up: the right text still works.
                Assert.That(
                    await rig.Client.RedeemPastedLinkAsync("https://MY-APP.strybok.link:443/invite777?utm_source=x&strybok_click=" + ClickId),
                    Is.EqualTo(PastedLinkResult.Delivered));
                Assert.That(rig.Api.Deferred[0].Body, Does.Contain("\"clickId\":\"" + ClickId + "\""));
            });
        }

        [Test]
        public void ARefusedPastedLink_IsNotFoundAndFinal()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 404, 400, 422 })
                {
                    var store = new MemoryStore();
                    Rig rig = Ios(store);
                    rig.Api.OnDeferred = _ => T.Respond(status, string.Empty);
                    rig.Listen();

                    Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.NotFound));
                    Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.NotFound));
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                    Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.AlreadyHandled));
                    Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                    Assert.That(rig.Received.Count, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void AnUnusableAnswerToAPastedLink_IsFailedAndFinal()
        {
            T.Run(async () =>
            {
                var bodies = new[]
                {
                    "oops",
                    "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://evil.example/invite777\",\"deferred\":true}",
                };
                foreach (string body in bodies)
                {
                    var store = new MemoryStore();
                    Rig rig = Ios(store);
                    rig.Api.OnDeferred = _ => T.Respond(200, body);
                    rig.Listen();

                    Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                    Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Failed));
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                    Assert.That(rig.Received.Count, Is.EqualTo(0));
                    Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.AlreadyHandled));
                }
            });
        }

        [Test]
        public void AfterANetworkFailure_ThePersonCanPasteAgain()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store);
                rig.Api.OnDeferred = _ => T.Respond(503, string.Empty);
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Unavailable);

                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(3));
                Assert.That(rig.Api.Deferred[2].Body, Is.EqualTo(rig.Api.Deferred[0].Body));
                Assert.That(store.Writes, Is.Empty);
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Unavailable));

                rig.Api.OnDeferred = _ => T.Respond(429, string.Empty);
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(4), "429 is not retried");
                Assert.That(store.Writes, Is.Empty);

                rig.Api.OnDeferred = _ => T.Respond(200, LinkBody);
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Delivered));
                await T.Until(() => rig.Received.Count == 1, "the pasted link");
                Assert.That(rig.Errors.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void PastingOnAndroidOrAfterDispose_IsDisabled()
        {
            T.Run(async () =>
            {
                Rig android = Android(new MemoryStore(), Referrer("x=1"));
                Assert.That(await android.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Disabled));
                Assert.That(android.Api.Deferred, Is.Empty);

                Rig ios = Ios(new MemoryStore());
                ios.Client.Dispose();
                Assert.That(await ios.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Disabled));
                Assert.That(ios.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void PasteCalls_AreHandledOneAfterAnother()
        {
            T.Run(async () =>
            {
                Rig rig = Ios(new MemoryStore());
                var answer = new TaskCompletionSource<StrybokHttpResponse>();
                rig.Api.OnDeferred = _ => answer.Task;
                rig.Listen();

                Task<PastedLinkResult> first = rig.Client.RedeemPastedLinkAsync(PastedUrl);
                Task<PastedLinkResult> second = rig.Client.RedeemPastedLinkAsync(PastedUrl);
                await T.Until(() => rig.Api.Deferred.Count == 1, "the first request");
                await T.Settle();
                Assert.That(second.IsCompleted, Is.False);
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));

                answer.SetResult(new StrybokHttpResponse(200, LinkBody));
                Assert.That(await first, Is.EqualTo(PastedLinkResult.Delivered));
                Assert.That(await second, Is.EqualTo(PastedLinkResult.AlreadyHandled));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                await T.Until(() => rig.Received.Count == 1, "the pasted link");
            });
        }

        [Test]
        public void APastedLink_FailsSafelyWhenTheStoreFails()
        {
            T.Run(async () =>
            {
                Rig unreadable = Ios(new MemoryStore { FailReads = true });
                Assert.That(await unreadable.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                Assert.That(unreadable.Api.Deferred, Is.Empty);

                Rig noDone = Ios(new MemoryStore { FailWritesOf = MemoryStore.Done });
                noDone.Listen();
                Assert.That(await noDone.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                await T.Settle();
                Assert.That(noDone.Received.Count, Is.EqualTo(0));
                Assert.That(noDone.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Failed));
            });
        }

        // ------------------------------------------------------------------
        // iOS: probabilistic matching
        // ------------------------------------------------------------------

        [Test]
        public void Matching_SendsOnlyTheFiveFieldsAndMarksTheLinkAsAGuess()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store, Matching());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.Delivered);

                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                StrybokHttpRequest request = rig.Api.Deferred[0];
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.Url, Is.EqualTo(MatchUrl));
                Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/json"));
                Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
                Assert.That(
                    request.Body,
                    Is.EqualTo("{\"platform\":\"ios\",\"bundleId\":\"com.example.App\",\"deviceFamily\":\"iphone\"," +
                               "\"locale\":\"en-GB\",\"sdkVersion\":\"" + StrybokClient.SdkVersion + "\"}"));

                await T.Until(() => rig.Received.Count == 1, "the matched link");
                ResolvedDeepLink link = rig.Received.Items[0];
                Assert.That(link.IsDeferred, Is.True);
                Assert.That(link.IsProbabilisticMatch, Is.True);
                Assert.That(link.RoutePath, Is.EqualTo("/promo"));
                Assert.That(store.Writes, Is.EqualTo(new[] { "strybok.deferred.attempts=1", "strybok.deferred.done=1" }));
                await T.Settle();
                Assert.That(rig.Api.EventRequests, Is.Empty);

                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.AlreadyHandled));
            });
        }

        [Test]
        public void Matching_AcceptsTheThreeFamiliesAndLanguageTags()
        {
            T.Run(async () =>
            {
                var cases = new[] { new[] { "ipad", "de" }, new[] { "ipod", "zh_Hans_CN" }, new[] { "iphone", "sr-Latn-RS" } };
                foreach (string[] item in cases)
                {
                    Rig rig = Ios(new MemoryStore(), Matching(options =>
                    {
                        options.DeviceFamily = () => item[0];
                        options.Locale = () => item[1];
                    }));
                    await rig.Started(DeferredLinkStatus.Delivered);
                    Assert.That(rig.Api.Deferred[0].Body, Does.Contain("\"deviceFamily\":\"" + item[0] + "\""));
                    Assert.That(rig.Api.Deferred[0].Body, Does.Contain("\"locale\":\"" + item[1] + "\""));
                }
            });
        }

        [Test]
        public void WithoutAUsableFamilyOrLocale_MatchingIsUnavailable()
        {
            T.Run(async () =>
            {
                var cases = new[]
                {
                    new[] { "mac", "en-GB" },
                    new[] { "iPhone", "en-GB" },
                    new[] { "", "en-GB" },
                    new[] { null, "en-GB" },
                    new[] { "iphone", null },
                    new[] { "iphone", "" },
                    new[] { "iphone", "e" },
                    new[] { "iphone", "en GB" },
                    new[] { "iphone", "en-GB\n" },
                    new[] { "iphone", "english" },
                };
                foreach (string[] item in cases)
                {
                    var store = new MemoryStore();
                    Rig rig = Ios(store, Matching(options =>
                    {
                        options.DeviceFamily = () => item[0];
                        options.Locale = () => item[1];
                    }));
                    await rig.Started(DeferredLinkStatus.Unavailable);
                    await T.Until(() => store.Get(MemoryStore.Done) == "1", "done for " + item[0] + "/" + item[1]);
                    Assert.That(rig.Api.Deferred, Is.Empty);
                }

                var missingStore = new MemoryStore();
                Rig missing = Ios(missingStore, options => options.AllowProbabilisticMatching = true);
                await missing.Started(DeferredLinkStatus.Unavailable);
                await T.Until(() => missingStore.Get(MemoryStore.Done) == "1", "done without callbacks");
                Assert.That(missing.Api.Deferred, Is.Empty);

                var throwingStore = new MemoryStore();
                Rig throwing = Ios(throwingStore, Matching(options => options.Locale = () => { throw new InvalidOperationException("boom"); }));
                await throwing.Started(DeferredLinkStatus.Unavailable);
                await T.Until(() => throwingStore.Get(MemoryStore.Done) == "1", "done with a throwing callback");
                Assert.That(throwing.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void AnUnusableMatch_IsFailed()
        {
            const string Head = "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true";
            var bodies = new[]
            {
                Head + "}",
                Head + ",\"matched\":\"exact\"}",
                Head + ",\"matched\":true}",
                "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://evil.example/promo\",\"deferred\":true,\"matched\":\"probabilistic\"}",
                "nope",
            };
            T.Run(async () =>
            {
                foreach (string body in bodies)
                {
                    var store = new MemoryStore();
                    Rig rig = Ios(store, Matching());
                    rig.Api.OnDeferred = _ => T.Respond(200, body);
                    rig.Listen();
                    await rig.Started(DeferredLinkStatus.Failed);
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"), body);
                    Assert.That(rig.Received.Count, Is.EqualTo(0), body);
                }
            });
        }

        [Test]
        public void NoMatch_IsNotFound()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store, Matching());
                rig.Api.OnDeferred = _ => T.Respond(404, string.Empty);
                await rig.Started(DeferredLinkStatus.NotFound);
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                Assert.That(rig.Errors.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void AMatchThatCouldNotBeAsked_IsPendingUntilThreeAttempts()
        {
            T.Run(async () =>
            {
                foreach (int status in new[] { 429, 500 })
                {
                    var store = new MemoryStore();
                    for (int launch = 1; launch <= 3; launch++)
                    {
                        Rig rig = Ios(store, Matching());
                        rig.Api.OnDeferred = _ => T.Respond(status, string.Empty);
                        await rig.Started(DeferredLinkStatus.Pending);
                        Assert.That(rig.Api.Deferred, Has.Count.EqualTo(status == 429 ? 1 : 3));
                        Assert.That(store.Get(MemoryStore.Attempts), Is.EqualTo(launch.ToString()));
                        Assert.That(store.Get(MemoryStore.Done), Is.Null);
                    }

                    Rig fourth = Ios(store, Matching());
                    await fourth.Started(DeferredLinkStatus.Failed);
                    Assert.That(fourth.Api.Deferred, Is.Empty);
                    Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));
                }
            });
        }

        [Test]
        public void ANormalLink_WinsOverAMatchBeforeAndDuringTheRequest()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig before = Ios(store, Matching(), new FakeLinkSource(T.Invite));
                before.Listen();
                await before.Started(DeferredLinkStatus.Superseded);
                Assert.That(before.Api.Deferred, Is.Empty);
                Assert.That(store.Get(MemoryStore.Done), Is.EqualTo("1"));

                var second = new MemoryStore();
                Rig during = Ios(second, Matching());
                var answer = new TaskCompletionSource<StrybokHttpResponse>();
                during.Api.OnDeferred = _ => answer.Task;
                during.Listen();
                await during.Client.InitializeAsync();
                await T.Until(() => during.Api.Deferred.Count == 1, "the match request");
                during.Source.Emit(T.Invite);
                await T.Until(() => during.Received.Count == 1, "the normal link");
                answer.SetResult(new StrybokHttpResponse(200, MatchBody));
                await during.Status(DeferredLinkStatus.Superseded);
                await T.Settle();
                Assert.That(during.Received.Count, Is.EqualTo(1));
                Assert.That(second.Get(MemoryStore.Done), Is.EqualTo("1"));
            });
        }

        [Test]
        public void Matching_WaitsForThePasteAndStartsWhenTheTextIsNotOurs()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store, MatchingAfterPasteCheck());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.AwaitingPaste);
                await T.Settle();
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(store.Writes, Is.Empty);

                Assert.That(await rig.Client.RedeemPastedLinkAsync("https://example.com/"), Is.EqualTo(PastedLinkResult.NotOurs));
                await rig.Status(DeferredLinkStatus.Delivered);
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Api.Deferred[0].Url, Is.EqualTo(MatchUrl));
                await T.Until(() => rig.Received.Count == 1, "the matched link");
                Assert.That(rig.Received.Items[0].IsProbabilisticMatch, Is.True);
            });
        }

        [Test]
        public void Matching_StartsWhenThePasteIsSkippedAndOnlyOnce()
        {
            T.Run(async () =>
            {
                Rig rig = Ios(new MemoryStore(), MatchingAfterPasteCheck());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.AwaitingPaste);

                rig.Client.SkipPastedLink();
                rig.Client.SkipPastedLink();
                await rig.Status(DeferredLinkStatus.Delivered);
                rig.Client.SkipPastedLink();
                await T.Settle();
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Received.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void APastedLinkOfOurs_MeansMatchingNeverRuns()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore();
                Rig rig = Ios(store, MatchingAfterPasteCheck());
                rig.Listen();
                await rig.Started(DeferredLinkStatus.AwaitingPaste);

                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Delivered));
                rig.Client.SkipPastedLink();
                await T.Settle();

                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));
                Assert.That(rig.Api.Deferred[0].Url, Is.EqualTo(RedeemUrl));
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Delivered));
                Assert.That(rig.Received.Count, Is.EqualTo(1));
                Assert.That(rig.Received.Items[0].IsProbabilisticMatch, Is.False);
                Assert.That(store.Get(MemoryStore.Attempts), Is.Null);
            });
        }

        [Test]
        public void AFailedPaste_KeepsWaiting_AndSkipIsANoOpWhenNotWaiting()
        {
            T.Run(async () =>
            {
                Rig rig = Ios(new MemoryStore(), MatchingAfterPasteCheck());
                rig.Api.OnDeferred = _ => T.Respond(429, string.Empty);
                await rig.Started(DeferredLinkStatus.AwaitingPaste);
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.Failed));
                await T.Settle();
                Assert.That(rig.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.AwaitingPaste));
                Assert.That(rig.Api.Deferred, Has.Count.EqualTo(1));

                Rig plain = Ios(new MemoryStore());
                await plain.Started(DeferredLinkStatus.Unavailable);
                plain.Client.SkipPastedLink();
                await T.Settle();
                Assert.That(plain.Api.Deferred, Is.Empty);
                Assert.That(plain.Client.DeferredLinkStatus, Is.EqualTo(DeferredLinkStatus.Unavailable));
            });
        }

        [Test]
        public void APasteboardCheckThatAnswersNoOrThrows_DoesNotDelayMatching()
        {
            T.Run(async () =>
            {
                Rig no = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(false)));
                await no.Started(DeferredLinkStatus.Delivered);
                Assert.That(no.Api.Deferred[0].Url, Is.EqualTo(MatchUrl));

                Rig throwing = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => { throw new InvalidOperationException("boom"); }));
                await throwing.Started(DeferredLinkStatus.Delivered);
                Assert.That(throwing.Api.Deferred, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void Matching_DoesNotRunOnALaterLaunch()
        {
            T.Run(async () =>
            {
                var store = new MemoryStore().With(MemoryStore.Done, "1");
                Rig rig = Ios(store, Matching());
                await rig.Started(DeferredLinkStatus.NotFirstLaunch);
                await T.Settle();
                Assert.That(rig.Api.Deferred, Is.Empty);
                Assert.That(await rig.Client.RedeemPastedLinkAsync(PastedUrl), Is.EqualTo(PastedLinkResult.AlreadyHandled));
                Assert.That(rig.Api.Deferred, Is.Empty);
            });
        }

        [Test]
        public void Requests_NeverCarryAnythingElse()
        {
            T.Run(async () =>
            {
                Rig android = Android(new MemoryStore(), Referrer("utm_source=secret-campaign&strybok_click=" + ClickId + "&gclid=private"));
                await android.Started(DeferredLinkStatus.Delivered);
                object parsed;
                Assert.That(StrybokJson.TryParse(android.Api.Deferred[0].Body, out parsed), Is.True);
                Assert.That(((Dictionary<string, object>)parsed).Keys, Is.EquivalentTo(new[] { "platform", "clickId", "packageName", "sdkVersion" }));
                Assert.That(android.Api.Deferred[0].Body, Does.Not.Contain("secret-campaign"));

                Rig ios = Ios(new MemoryStore());
                await ios.Client.RedeemPastedLinkAsync(T.Invite + "?email=someone%40example.com&strybok_click=" + ClickId);
                Assert.That(ios.Api.Deferred[0].Body, Does.Not.Contain("someone"));
                Assert.That(ios.Api.Deferred[0].Body, Does.Not.Contain("invite777"));
            });
        }

        [Test]
        public void StatusHandlers_AThrowingOneDoesNotStopTheOthersOrTheCheck()
        {
            T.Run(async () =>
            {
                var handlerErrors = new Bag<Exception>();
                var seen = new Bag<DeferredLinkStatus>();
                Rig rig = Make(options =>
                {
                    options.Platform = StrybokPlatform.Android;
                    options.DeferredLinks = true;
                    options.AndroidPackageName = "com.example.app";
                    options.DeferredLinkStore = new MemoryStore();
                    options.InstallReferrerSources.Add(Referrer());
                    options.OnHandlerException = handlerErrors.Add;
                });
                rig.Client.DeferredLinkStatusChanged += _ => { throw new InvalidOperationException("faulty"); };
                rig.Client.DeferredLinkStatusChanged += seen.Add;
                await rig.Started(DeferredLinkStatus.Delivered);
                Assert.That(seen.Items, Is.EqualTo(new[] { DeferredLinkStatus.Checking, DeferredLinkStatus.Delivered }));
                Assert.That(handlerErrors.Count, Is.EqualTo(2));
                Assert.That(rig.Errors.Count, Is.EqualTo(0));
            });
        }
    }
}
