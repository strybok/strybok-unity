# Unity integration

The `com.strybok.sdk` package receives verified HTTPS links through Unity's own deep link API
(`Application.absoluteURL` and `Application.deepLinkActivated`), asks the Strybok API which
in-app route a link stands for, and hands your game each link exactly once. It follows the
shared [SDK contract](./sdk-contract.md).

> **Status:** covered by Edit Mode tests (Unity 6000.5 and 6000.6 editors on macOS), including the
> HTTP transport against a local server. Generating the Xcode project and exporting the Gradle
> project were checked for the entitlement and the manifest entry. **Not verified on a physical
> device or simulator, and not run on Unity 2021.3 / 2022.3 yet** (see the
> [manual test plan](./manual-test-plan.md)); not published to a registry.
>
> A link tapped before the app is installed is **not restored by default**. Deferred links are an
> option that is off by default and has never run on a device; the package ships no store
> integration, so your game supplies the install referrer (Android) or the pasted link (iOS). See
> [Deferred links](#deferred-links-optional).

Requirements: Unity 2021.3 or newer, iOS and Android players, Mono or IL2CPP. The package has no
third-party dependencies and uses no reflection, so it needs no `link.xml` for code stripping.

## 1. Prerequisites

1. Create an application in Strybok with your iOS bundle identifier and Android package name
   (_Player Settings > Other Settings > Identification_).
2. Read its setup data: `GET /v1/apps/:appId/integration` returns the managed hostname
   (for example `my-app.strybok.link`).
3. Do **step 1** of the [Android](./android.md) and [iOS](./ios.md) guides (team ID, bundle ID,
   package name, certificate fingerprints) so Strybok serves the association files. Step 2 of
   those guides — the manifest and the entitlement — is done by this package, see below.

## 2. Add the package

_Window > Package Manager > + > Add package from git URL_:

```text
https://github.com/strybok/strybok-unity.git#0.3.0
```

The part after `#` pins a tag or commit. The package is not in a registry (neither OpenUPM nor
the Asset Store). Or clone the repository and reference the clone in `Packages/manifest.json`
(the path is relative to the `Packages` folder):

```json
{
  "dependencies": {
    "com.strybok.sdk": "file:../../strybok-unity"
  }
}
```

## 3. Configure

Open _Tools > Strybok > Settings_. This creates `Assets/Resources/StrybokSettings.asset`:

| Field                          | Meaning                                                                       |
| ------------------------------ | ----------------------------------------------------------------------------- |
| **Api Base Url**               | `https://api.strybok.com`                                                     |
| **Link Hosts**                 | Your managed hostname(s), e.g. `my-app.strybok.link`. No `https://`, no path. |
| **Report App Opens**           | Send the best-effort `app_open` event (section 8).                            |
| **Timeout Seconds**            | Time for one resolve attempt (default 5).                                     |
| **Max Retries**                | Extra attempts after a timeout, network error or 5xx (default 2).             |
| **Client Key**                 | Publishable key `pk_…`, only for creating links to share (section 6).         |
| **Add Ios Associated Domains** | Let the build add the Associated Domains entitlement (section 4).             |
| **Add Android Intent Filter**  | Let the build add the App Links intent filter (section 5).                    |

The asset is used at runtime **and** by the build steps below, so the hostname lives in one
place. It must stay in a `Resources` folder under the name `StrybokSettings`.

## 4. iOS: Associated Domains

With **Add Ios Associated Domains** on, a post-process build step adds

```xml
<key>com.apple.developer.associated-domains</key>
<array>
    <string>applinks:my-app.strybok.link</string>
</array>
```

to the entitlements file of the `Unity-iPhone` target in the generated Xcode project (it creates
`Unity-iPhone.entitlements` when the target has none, and keeps domains other plugins added).
Nothing else is needed in Unity: Unity's app controller already forwards Universal Links to
`Application.absoluteURL` / `Application.deepLinkActivated`.

Still to do by you:

- The **Associated Domains** capability must be enabled for the App ID in the Apple Developer
  portal. Xcode's automatic signing does that; with manual signing, regenerate the provisioning
  profile after enabling it.
- Verify on a device as described in the [iOS guide](./ios.md#3-verify-on-a-device).

Manual alternative (turn the toggle off): in Xcode select the _Unity-iPhone_ target >
_Signing & Capabilities_ > _+ Capability_ > _Associated Domains_ and add
`applinks:my-app.strybok.link`. This has to be repeated whenever Unity regenerates the Xcode
project with _Replace_. The development-only `applinks:<host>?mode=developer` form is not written
by the build step; add it by hand in Xcode when you need it.

## 5. Android: App Links intent filter

With **Add Android Intent Filter** on, a build step adds this to the launcher activity
(`UnityPlayerActivity` and/or `UnityPlayerGameActivity`) of the generated Gradle project, once
per link host:

```xml
<intent-filter android:autoVerify="true">
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data android:scheme="https" android:host="my-app.strybok.link" />
</intent-filter>
```

Your project files are not modified; a custom `Assets/Plugins/Android/AndroidManifest.xml` keeps
working and is patched in the generated copy only. Hosts that the activity already declares are
skipped.

Manual alternative (turn the toggle off): enable _Player Settings > Publishing Settings >
Custom Main Manifest_ and paste the block above inside the Unity `<activity>` of
`Assets/Plugins/Android/AndroidManifest.xml`.

Then verify with `adb shell pm get-app-links <package>` as described in the
[Android guide](./android.md#3-verify-on-a-device). Remember the fingerprint of the certificate
that signs the build you test (the Unity debug keystore for development builds).

## 6. Use it

Either add the **Strybok Behaviour** component (_Add Component > Strybok > Strybok Behaviour_)
to an object in your first scene — it initializes the SDK from the settings asset and survives
scene loads — or initialize from code:

```csharp
using Strybok;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DeepLinkRouter : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        StrybokSdk.LinkReceived += OnLink;
        StrybokSdk.LinkFailed += error => Debug.LogWarning("Link failed: " + error);

        // Skip this call when a Strybok Behaviour is in the scene.
        StrybokSdk.Initialize(new StrybokOptions
        {
            ApiBaseUrl = "https://api.strybok.com",
            AllowedHosts = { "my-app.strybok.link" },
        });
        // or: StrybokSdk.Initialize();  // reads the StrybokSettings asset
    }

    private void OnDestroy()
    {
        StrybokSdk.LinkReceived -= OnLink;
    }

    // Runs on the main thread. link.RoutePath == "/invite/abc123",
    // link.Payload["invitationId"] == "abc123"
    private void OnLink(ResolvedDeepLink link)
    {
        string[] segments = link.RoutePath.Trim('/').Split('/');
        switch (segments[0])
        {
            case "invite" when segments.Length == 2:
                InviteScreen.PendingCode = segments[1];
                SceneManager.LoadScene("Invite");
                break;
            case "shop":
                ShopScreen.PendingItemId = link.GetString("itemId");
                SceneManager.LoadScene("Shop");
                break;
            default:
                Debug.Log("Unknown route " + link.RoutePath); // Stay where we are.
                break;
        }
    }
}
```

One subscription covers every case:

| Situation                                 | What happens                                                                                                  |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| **Cold start** (app launched by the link) | `Initialize` resolves the launch link. It is kept until you subscribe to `LinkReceived`, then delivered once. |
| **Background** (app resumed by the link)  | Delivered on `LinkReceived`.                                                                                  |
| **Foreground**                            | Delivered on `LinkReceived`.                                                                                  |

Links resolved while nobody is subscribed are queued and handed to the **first** subscriber,
synchronously inside its `+=`. Subscribe from an object that lives as long as link handling
should (hence `DontDestroyOnLoad` above), and unsubscribe when it is destroyed.

If the platform reports the same tap twice (launch URL _and_ `deepLinkActivated`), the duplicate
is dropped (`DedupeWindow`, 2 s by default). The same link tapped again later is a new delivery.

Call `Initialize` early, in the first scene. If it is called late, the most recent link is still
picked up from `Application.absoluteURL`, but an older one that arrived in between is lost.

Prefer to decide the first scene yourself? Await the launch link instead; it is returned at most
once and removed from `LinkReceived`, so combining both never navigates twice:

```csharp
private async void Start()
{
    await StrybokSdk.Initialize();
    ResolvedDeepLink launch = await StrybokSdk.GetInitialLinkAsync();
    SceneManager.LoadScene(launch != null && launch.RoutePath.StartsWith("/invite/") ? "Invite" : "Menu");
}
```

The API is `Task`-based and needs no coroutine or GameObject; continuations and handlers run on
Unity's main thread. In a coroutine, wait with
`yield return new WaitUntil(() => task.IsCompleted);`.

### API

| Member                             | Purpose                                                                                                               |
| ---------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `StrybokSdk.Initialize(options)`   | Start listening; the task completes after the launch link was handled. Calling it again is harmless.                  |
| `StrybokSdk.Initialize()`          | The same, configured from the `StrybokSettings` asset.                                                                |
| `StrybokSdk.LinkReceived`          | Event with a `ResolvedDeepLink` (`Url`, `Uri`, `LinkId`, `AppId`, `RoutePath`, `Payload`, `PayloadJson`, `Campaign`). |
| `StrybokSdk.LinkFailed`            | Event with a `StrybokException` for a link of yours that could not be resolved.                                       |
| `StrybokSdk.GetInitialLinkAsync()` | The launch link, if not delivered through `LinkReceived` yet; otherwise `null`.                                       |
| `StrybokSdk.ResolveAsync(url)`     | Resolve a URL explicitly (QR scanner, tests). Does not raise `LinkReceived` or report an app open.                    |
| `StrybokSdk.IsStrybokUrl(url)`     | Whether a URL is one of this app's links.                                                                             |
| `StrybokSdk.CreateLinkAsync(...)`  | Create a short link to share, see [below](#create-a-link-to-share).                                                   |
| `StrybokSdk.DeferredLinkStatus`    | Where the deferred-link check stands (`DeferredLinkStatusChanged`), see [Deferred links](#deferred-links-optional).   |
| `StrybokSdk.EnableDeferredLinks()` | Switch deferred links on after `Initialize`, for example after a consent screen.                                      |
| `StrybokSdk.RedeemPastedLinkAsync` | iOS: redeem a link the person pasted; `SkipPastedLink()` when they moved on.                                          |
| `StrybokSdk.Shutdown()`            | Stop listening and drop queued links.                                                                                 |

Options (`StrybokOptions`): `ApiBaseUrl`, `AllowedHosts`, `Timeout` (5 s per attempt),
`MaxRetries` (2; only timeouts, network errors and 5xx are retried — resolving is an idempotent
GET), `RetryDelay` (400 ms, doubling), `DedupeWindow` (2 s), `ReportAppOpens`, `OnError`,
`OnHandlerException`, `ClientKey` (only for creating links), plus `LinkSource` / `HttpTransport` / `Platform` / `CallbackContext` for
tests and custom set-ups. For [deferred links](#deferred-links-optional): `DeferredLinks` and
`AllowProbabilisticMatching` (both off), `InstallReferrerSources`, `AndroidPackageName`,
`IosBundleId`, `DeviceFamily`, `Locale`, `PasteboardMayContainLink` and `DeferredLinkStore`.

`StrybokSdk` is a thin static wrapper around `StrybokClient`, a plain C# class without any
`UnityEngine` dependency. Create your own `StrybokClient` when you need several instances or run
outside Unity; you then pass an `ILinkSource` and an `IHttpTransport` yourself
(`UnityLinkSource` and `UnityWebRequestTransport` are public).

### Payload

`link.Payload` is an `IReadOnlyDictionary<string, object>` produced by a small built-in JSON
reader (`JsonUtility` cannot read arbitrary objects). Values are `string`, `bool`, `long` for
integers, `double` for other numbers, `null`, `Dictionary<string, object>` for objects and
`List<object>` for arrays. `link.GetString("key")` returns a string value or `null`.

For a typed payload use the raw JSON with `JsonUtility` (or any JSON library you already ship):

```csharp
[System.Serializable]
private sealed class InvitePayload
{
    public string invitationId;
    public int bonus;
}

InvitePayload payload = JsonUtility.FromJson<InvitePayload>(link.PayloadJson);
```

### Errors

`ResolveAsync` fails with, and `LinkFailed` / `OnError` receive, a `StrybokException`. Its `Url`
is the link that was being handled; `ToString()` leaves the URL out because a query can hold
private data.

| Type                                                  | Meaning                                                                                                                                              |
| ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UnsupportedLinkException`                            | Not `https`, host not in `AllowedHosts`, or not a single-slug path. Raised before any network call. Ignored silently for links arriving from the OS. |
| `LinkNotFoundException`                               | Unknown, disabled or expired link (404).                                                                                                             |
| `StrybokTimeoutException` / `StrybokNetworkException` | No answer after all retries (e.g. offline). The game keeps working; the link is simply not opened.                                                   |
| `StrybokServerException`                              | Unexpected status or malformed response; `StatusCode` tells which.                                                                                   |

When neither `LinkFailed` nor `OnError` is set, failures are logged as warnings. An exception
thrown by one of your own handlers is logged with `Debug.LogException` and does not stop other
handlers or later links. After `Shutdown`, `ResolveAsync` on the old client fails with
`ObjectDisposedException`.

### Create a link to share

`CreateLinkAsync` asks the service for a short link to a screen of your game — the "Share"
button. It is new in 0.2.0.

1. In the dashboard, open the application, switch on **Links from the app** and list the route
   patterns the app may create links for (for example `/product/*`). Routes outside the list are
   refused.
2. Copy the **publishable client key** (`pk_…`) into **Client Key** of the settings asset, or
   set `ClientKey` in `StrybokOptions`. The key is safe to ship inside the game: it can only
   create links for those patterns. **Never put a secret API key (`lb_…`) in a game**; it is not
   accepted here.

```csharp
try
{
    CreatedLink link = await StrybokSdk.CreateLinkAsync(
        "/product/42",
        payload: new Dictionary<string, object> { { "productId", 42 } },
        title: "Red shoes");
    Share(link.Url); // https://my-app.strybok.link/Ab3xK9
}
catch (StrybokRateLimitedException error)
{
    ShowTryAgainLater(error.RetryAfter); // may be null
}
catch (StrybokException error)
{
    Debug.LogWarning("Could not create a link: " + error);
}
```

Only the route is required; `payload`, `title`, `description` and `imageUrl` are optional and
left out of the request when not given. `CreateLinkWithPayloadJsonAsync` takes the payload as
JSON text instead (`JsonUtility.ToJson(myPayload)`); it must be a JSON object. Asking again for
the same content returns the same link, so repeated taps do not pile up links.

It only runs when you call it and is independent of receiving links: it works before
`Initialize` (then from the settings asset, which does not need a link host for this), never
raises `LinkReceived` or `LinkFailed`, and failures come back from the awaited task:

| Type                                                                             | Meaning                                                                                                              | Retry later?            |
| -------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- | ----------------------- |
| `StrybokUnauthorizedException`                                                   | No `ClientKey` set (no network call), or the key was refused: wrong or replaced key, or the feature is switched off. | no                      |
| `RouteNotAllowedException`                                                       | `RoutePath` is not covered by the route patterns in the dashboard.                                                   | no                      |
| `StrybokValidationException`                                                     | Bad route or payload (no network call), or rejected by the service; `Issues` lists what it found.                    | no                      |
| `StrybokRateLimitedException`                                                    | Too many requests, or the app's daily limit of new links is reached.                                                 | yes, after `RetryAfter` |
| `StrybokNetworkException` / `StrybokTimeoutException` / `StrybokServerException` | No usable answer after the retries (`Timeout`, `MaxRetries`, `RetryDelay` as for resolving).                         | yes                     |

Errors never contain the client key or the payload. Do not put personal data in a route or
payload: whoever receives the link can read them.

### Deferred links (optional)

A deferred link is one a person tapped **before** the game was installed. The feature is off by
default, works very differently on the two platforms, and **nothing of it has run on a device**.
What the server does, what the person sees and what it costs is in the
[Android](./android.md#links-tapped-before-the-app-is-installed-deferred-links) and
[iOS](./ios.md#links-tapped-before-the-app-is-installed) guides; the exact behaviour is in the
[SDK contract](./sdk-contract.md#deferred-links-optional-android).

This package ships **no store integration and no native code**: it never asks a store for the
install referrer and never reads the pasteboard. Your game supplies both; the SDK does the rest
(the state, the request, the once-only delivery).

| What                                        | Who supplies it                                                                                           |
| ------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| Switch                                      | You: `DeferredLinks = true` (options or the settings asset), or `StrybokSdk.EnableDeferredLinks()` later. |
| The owner's setting                         | The application's owner, in the dashboard or the API (see the two native guides).                         |
| State (attempt counter, "done" flag)        | `StrybokSdk`: `PlayerPrefs` (`PlayerPrefsDeferredLinkStore`). Replace with `DeferredLinkStore`.           |
| Package name / bundle id                    | `StrybokSdk`: `Application.identifier`. Replace with `AndroidPackageName` / `IosBundleId`.                |
| Android: the store's install referrer       | **You**: one or more `InstallReferrerSources` (below).                                                    |
| iOS: the pasted text                        | **You**: a paste action of your game, handed to `StrybokSdk.RedeemPastedLinkAsync(text)`.                 |
| iOS matching: device family and language    | `StrybokSdk`: from `SystemInfo.deviceModel` and `Application.systemLanguage`. Replace with the callbacks. |
| iOS matching: "is there anything to paste?" | Optional, you: `PasteboardMayContainLink` (needs a native plugin; skipped when not set).                  |

A plain `StrybokClient` (without `StrybokSdk`) has none of these defaults: without a
`DeferredLinkStore` and the identifier of the platform the feature stays `Disabled`. Never pass a
store that only lives in memory; the link would be redeemed again on the next launch.

The link arrives through `LinkReceived` like any other, with `link.IsDeferred == true`, possibly a
second or two after your first scene is shown. `GetInitialLinkAsync` never returns it, no
`app_open` is reported for it, and if a normal link opened the game in the same launch the
deferred one is dropped. Nothing about the check is raised through `LinkFailed`: most people who
install a game never tapped a link. For QA there is `StrybokSdk.DeferredLinkStatus`
(`DeferredLinkStatusChanged`):

| Status           | Meaning                                                                                                |
| ---------------- | ------------------------------------------------------------------------------------------------------ |
| `Disabled`       | Off, or it cannot run: no store, no identifier, or not Android/iOS (always the case in the Editor).    |
| `Pending`        | Not started yet, or this launch could not finish (store or network unreachable); a later launch tries. |
| `Checking`       | Running now.                                                                                           |
| `NotFirstLaunch` | Finished in an earlier launch.                                                                         |
| `Unavailable`    | Android: no source, or none can ever answer. iOS: nothing automatic exists (see below).                |
| `NoReferrer`     | The store's referrer has no link of ours: an ordinary install.                                         |
| `NotFound`       | The server refused: unknown, older than 24 hours, already used, or another application's.              |
| `Superseded`     | A normal link arrived first.                                                                           |
| `Delivered`      | Handed to `LinkReceived`.                                                                              |
| `Failed`         | Three launches could not finish, or the answer was unusable.                                           |
| `AwaitingPaste`  | iOS matching is waiting for `RedeemPastedLinkAsync` or `SkipPastedLink`.                               |

The check runs at most three times in the life of an installation and delivers at most once; the
SDK stores only the counter and the flag, never the referrer, the pasted text or the id.

#### Android: supply the install referrer

A source is a function of your game that answers with the raw referrer string of one store, or
"unavailable for now" (try again on a later launch), or "unavailable" (never on this device). It
must not block, must not throw and has 10 seconds. Sources are asked in order; the first referrer
that holds a Strybok id wins.

```csharp
var options = settings.ToOptions();            // or new StrybokOptions { ... }
options.DeferredLinks = true;                  // or StrybokSdk.EnableDeferredLinks() after consent
options.InstallReferrerSources.Add(PlayInstallReferrer.Ask);
StrybokSdk.Initialize(options);
```

Initialize from code as above. The **Strybok Behaviour** component cannot be given a source, so
with **Deferred Links** ticked in the asset and no source the check ends as `Unavailable` — for
good on that installation.

The following is **app code, not part of the package, and has never been run**: a source for
Google Play written against Google's Install Referrer library through `AndroidJavaObject`. Your
project must add `com.android.installreferrer:installreferrer:2.2` to the Gradle build (custom
`mainTemplate.gradle`, or the dependency resolver you already use) and keep it from being
stripped by R8. Test it on a device with an internal-testing build before you rely on it.

```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Strybok;
using UnityEngine;

public static class PlayInstallReferrer
{
    public static Task<InstallReferrerAnswer> Ask(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<InstallReferrerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var clientClass = new AndroidJavaClass("com.android.installreferrer.api.InstallReferrerClient"))
            using (var builder = clientClass.CallStatic<AndroidJavaObject>("newBuilder", activity))
            {
                AndroidJavaObject client = builder.Call<AndroidJavaObject>("build");
                // The SDK gave up waiting (10 s) or was shut down. StrybokSdk cancels on Unity's
                // main thread, which may call into Java.
                cancellationToken.Register(() =>
                {
                    if (done.TrySetResult(InstallReferrerAnswer.UnavailableForNow)) End(client);
                });
                client.Call("startConnection", new Listener(client, done));
            }
        }
        catch (Exception)
        {
            // The library is not in the build (class not found), or Play is missing.
            done.TrySetResult(InstallReferrerAnswer.Unavailable);
        }
        return done.Task;
    }

    private static void End(AndroidJavaObject client)
    {
        try
        {
            client.Call("endConnection");
        }
        catch (Exception)
        {
            // Already closed.
        }
    }

    private sealed class Listener : AndroidJavaProxy
    {
        private readonly AndroidJavaObject _client;
        private readonly TaskCompletionSource<InstallReferrerAnswer> _done;

        public Listener(AndroidJavaObject client, TaskCompletionSource<InstallReferrerAnswer> done)
            : base("com.android.installreferrer.api.InstallReferrerStateListener")
        {
            _client = client;
            _done = done;
        }

        // Called by the library on Android's main thread.
        public void onInstallReferrerSetupFinished(int responseCode)
        {
            InstallReferrerAnswer answer;
            try
            {
                if (responseCode == 0) // OK
                {
                    using (AndroidJavaObject details = _client.Call<AndroidJavaObject>("getInstallReferrer"))
                    {
                        // Only the referrer string is read; the timestamps are not.
                        answer = InstallReferrerAnswer.Referrer(details.Call<string>("getInstallReferrer"));
                    }
                }
                else if (responseCode == 2 || responseCode == 3) // FEATURE_NOT_SUPPORTED, DEVELOPER_ERROR
                {
                    answer = InstallReferrerAnswer.Unavailable;
                }
                else // SERVICE_UNAVAILABLE (1), SERVICE_DISCONNECTED (-1)
                {
                    answer = InstallReferrerAnswer.UnavailableForNow;
                }
            }
            catch (Exception) // a remote exception
            {
                answer = InstallReferrerAnswer.UnavailableForNow;
            }

            End(_client); // Always release the connection.
            _done.TrySetResult(answer);
        }

        public void onInstallReferrerServiceDisconnected()
        {
            _done.TrySetResult(InstallReferrerAnswer.UnavailableForNow);
        }
    }
}
#endif
```

Huawei AppGallery and Samsung Galaxy Store hand the referrer over in their own ways (a content
provider and Samsung's library). **Those two flows are unverified even in the native Strybok
SDKs** — nobody has tried how the stores treat the value — so if you write sources for them,
treat them as experiments. Do not log the referrer: it can hold other vendors' data.

Reading the install referrer is access to information stored on the device. Whether you need the
person's consent first is your decision; the SDK supports it by doing nothing until
`DeferredLinks` is on — leave it off and call `StrybokSdk.EnableDeferredLinks()` after your
consent screen.

#### iOS: the pasted link

The App Store passes nothing to a newly installed app. With the owner's "copy link" option the
person copies the link on a page before the App Store; your game offers a **Paste** action and
hands the text to the SDK:

```csharp
// From the tap on your own "Paste link" button — never automatically.
PastedLinkResult result = await StrybokSdk.RedeemPastedLinkAsync(GUIUtility.systemCopyBuffer);
```

`Delivered` means the link is on its way through `LinkReceived`. `NotOurs` means the text was not
a copied link of this game: nothing was sent and the text is dropped. `NotFound`: the server
refused it (older than 24 hours, already used). `AlreadyHandled`: a deferred link was handled
before on this installation. `Failed`: no answer (the person can tap again) or an unusable one.
`Disabled`: off, or not iOS.

Be honest with yourself about this step. Unity has **no wrapper for Apple's paste button**
(`UIPasteControl`, iOS 16 or later): reading `GUIUtility.systemCopyBuffer` is a programmatic read
of the pasteboard, so iOS shows its "Allow Paste" prompt each time. To avoid the prompt you need a
native plugin of your own that shows `UIPasteControl` and passes the string to C#. Either way:
place the action on the first screen or in onboarding with a sentence that says what it does,
describe it in the App Review notes, and never read the pasteboard at launch. The costs of this
option: one extra page and tap for every iOS visitor without the game, it overwrites the person's
clipboard, and it works only if they copy there **and** paste here.

#### iOS: probabilistic matching (not recommended)

With `AllowProbabilisticMatching = true` (and the owner's setting, and an opt-in on each link) the
SDK asks the server on the first launch whether a link was tapped from the same network address,
device family and language. It sends the bundle id, `iphone`/`ipad`/`ipod`, a language tag and the
SDK version — nothing else; the server uses the connection's IP address.

- **Apple forbids it.** This is fingerprinting in the sense of the App Store Review Guidelines and
  the Apple Developer Program License Agreement, with or without consent. The App Review and
  legal risk is the app owner's.
- **A match is a guess.** On a shared network (Wi-Fi, office, carrier NAT) it can be **another
  person's link**. Check `link.IsProbabilisticMatch`: such a link must never by itself sign anyone
  in, accept an invitation, apply a personal discount or show personal content.
- **It often cannot work**: iCloud Private Relay and VPNs give the browser and the game different
  addresses, and so does changing network between the tap and the first launch.
- **Privacy consequences**: the service processes IP addresses for your application; your privacy
  policy and App Store privacy labels must cover it, and consent may be needed.
- **Per-link opt-in**: only links explicitly allowed for matching can be matched.
- **Accuracy is unknown.** Nobody can measure how often a match was wrong.

If you also offer the paste action, set `PasteboardMayContainLink` (a native
`UIPasteboard.general.detectPatterns` call, which reads no content): when it answers `true` the
status becomes `AwaitingPaste` and matching starts only after `RedeemPastedLinkAsync` returned
`NotOurs` or you called `StrybokSdk.SkipPastedLink()`. Without that callback matching does not
wait. The default language comes from `Application.systemLanguage`; set `Locale` yourself (for
example from a native `NSLocale.preferredLanguages` call) if you need the exact tag.

## 7. Treat routes as untrusted input

`RoutePath` and `Payload` are data from the network, chosen by whoever created the link. Match
the route against screens your game knows before navigating (the `switch` above), validate ids
before using them, and never pass payload contents to `SceneManager.LoadScene`,
`Application.OpenURL`, `Resources.Load` or similar directly.

If a link needs a signed-in player or finished loading, remember the route and open it once the
game is ready instead of dropping it.

## 8. Analytics and privacy

After a link is delivered (or queued) the SDK sends one best-effort `app_open` event
(`POST /v1/public/events`) containing a random event id, the link id, the platform
(`ios` / `android`, `unknown` in the Editor and on other platforms) and the SDK version — no
device identifiers. It never delays navigation and all failures are ignored. Disable it with
**Report App Opens** / `ReportAppOpens = false`.

The dashboard shows these as **App opens**, separate from browser fallbacks counted by the
server. The number is self-reported by the app and is not a count of unique people.

Only the link's host and slug are sent to `resolve`, plus the five `utm_*` campaign parameters of
the tapped URL when they are valid (see below); every other query parameter and the fragment
stay on the device.

With [deferred links](#deferred-links-optional) switched on the SDK additionally sends, at most a
few times per installation: on Android the single-use id found in the install referrer your code
handed over, the package name and the SDK version; on iOS the id found in a pasted link, the
bundle id and the SDK version — or, only with `AllowProbabilisticMatching`, the bundle id, the
device family, a language tag and the SDK version, to a server that then uses the connection's IP
address. That last case is the one exception to "no fingerprint".

### Campaign values

A resolved link has `Campaign` (`LinkCampaign`): the `utm_source`, `utm_medium`, `utm_campaign`,
`utm_term` and `utm_content` of this open as `Source`, `Medium`, `Campaign`, `Term` and
`Content`. It is never `null`; a property without a value is `null`, and `Campaign.IsEmpty` is
`true` when there is none at all.

```csharp
StrybokSdk.LinkReceived += link =>
{
    if (link.Campaign.Source != null) MyAnalytics.SetLabel("link_source", link.Campaign.Source); // a label, nothing more
    Open(link.RoutePath);
};
```

- **Where the values come from.** The link's defaults — set in the dashboard (link form →
  "Campaign (optional)") or through the management API — with the `utm_*` parameters of the
  tapped URL on top, key by key. `https://my-app.strybok.link/invite777?utm_source=instagram`
  opens with source `instagram` whatever the default is; a link tapped without parameters gives
  the defaults. A URL can replace a default but cannot clear it.
- **Untrusted labels.** Whoever shares a link can write any `utm_*` value into its URL. Use the
  values as labels for your own analytics; never decide access, prices, rewards or navigation by
  them.
- **What is accepted.** Only these five lower-case names, and only values of 1–64 characters
  out of `A–Z a–z 0–9 . _ ~ -`. Anything else (`spring sale`, an e-mail address, non-Latin
  letters) is dropped silently, as if the parameter were absent — by the SDK and again by the
  server.
- **What is sent.** With the resolve request the SDK sends those five parameters when they are
  valid, and nothing else of the query and never the fragment. With the `app_open` event it
  reports the values exactly as resolve returned them. The dashboard's link analytics then
  shows a "Campaigns" breakdown by source, medium and campaign, with app opens and browser opens
  counted separately (term and content are not broken down).
- **Versions.** New in 0.3.0. Older versions send no query: their links carry no `campaign` and
  their app opens are counted under the link's defaults.

## 9. Testing your integration

In the Editor `Application.absoluteURL` is empty and no links arrive, so simulate them with
`Strybok.Testing.FakeLinkSource`:

```csharp
var source = new FakeLinkSource(initialLink: "https://my-app.strybok.link/invite777");
StrybokSdk.Initialize(new StrybokOptions
{
    ApiBaseUrl = "https://api.strybok.com",
    AllowedHosts = { "my-app.strybok.link" },
    LinkSource = source,
});
source.Emit("https://my-app.strybok.link/other"); // a link arriving while the game runs
```

For unit tests without a network, also pass your own `IHttpTransport`:

```csharp
private sealed class FakeTransport : IHttpTransport
{
    public Task<StrybokHttpResponse> SendAsync(StrybokHttpRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new StrybokHttpResponse(
            200,
            "{\"linkId\":\"l1\",\"appId\":\"a1\",\"routePath\":\"/invite/abc123\",\"payload\":{}}"));
    }
}
```

The package's own tests (`Tests/Editor`) show complete examples. To run them, add
`"testables": ["com.strybok.sdk"]` to `Packages/manifest.json` and open
_Window > General > Test Runner > EditMode_.

On devices, send a link the way the native guides describe: tap it in Notes or Messages on iOS;
`adb shell am start -a android.intent.action.VIEW -c android.intent.category.BROWSABLE -d "https://my-app.strybok.link/<slug>"`
on Android.

## Troubleshooting

| Symptom                                                     | Check                                                                                                                                                                       |
| ----------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Link opens the browser/store instead of the game            | Native association is not verified: see the [Android](./android.md) and [iOS](./ios.md) guides. The SDK is not involved yet at this point.                                  |
| No entitlement / intent filter in the built project         | `StrybokSettings.asset` is not in a `Resources` folder, **Link Hosts** is empty, or the toggle is off. Look for `[Strybok]` warnings in the build log.                      |
| iOS build fails to sign after adding the entitlement        | The App ID / provisioning profile lacks the Associated Domains capability.                                                                                                  |
| Game opens but nothing happens                              | Host missing from `AllowedHosts` / **Link Hosts**; `LinkFailed` output or `[Strybok]` warnings in the log; API reachable from the device? `Initialize` called?              |
| Works when the game is running, not on cold start           | Subscribe to `LinkReceived` (or call `GetInitialLinkAsync`) at some point after start; the launch link waits in the queue until then. Call `Initialize` in the first scene. |
| Screen opens twice                                          | You navigate from both `GetInitialLinkAsync()` and your own `Application.deepLinkActivated` handler. Use the SDK events only.                                               |
| Handler never runs after a scene change                     | The subscribing object was destroyed and unsubscribed. Subscribe from a `DontDestroyOnLoad` object.                                                                         |
| `Initialize was called again` warning                       | A Strybok Behaviour and your own code both initialize. Keep one.                                                                                                            |
| Works on tap in Notes/Messages, not from Instagram/Telegram | In-app browsers often do not hand links to apps. Users get the fallback page with a manual button.                                                                          |
