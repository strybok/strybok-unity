# Strybok SDK for Unity

[![openupm](https://img.shields.io/npm/v/com.strybok.sdk?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.strybok.sdk/) [![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](./LICENSE.md)

Receives iOS Universal Links / Android App Links, resolves them through the Strybok API to an
in-app route and payload, and delivers each link exactly once — on cold start, from the
background and in the foreground.

```csharp
using Strybok;

StrybokSdk.LinkReceived += link => Router.Open(link.RoutePath, link.Payload);
StrybokSdk.Initialize(new StrybokOptions
{
    ApiBaseUrl = "https://api.strybok.com",
    AllowedHosts = { "my-app.strybok.link" },
});
```

Create a link to share (needs the application's publishable client key `pk_…`, with "Links from
the app" switched on and route patterns listed in the dashboard — never a secret API key):

```csharp
// ClientKey = "pk_…" in the options or in the StrybokSettings asset
CreatedLink link = await StrybokSdk.CreateLinkAsync("/product/42", title: "Red shoes");
Share(link.Url);
```

Campaign values (`utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`) of the
open come with the link, as `link.Campaign.Source` and so on (`null` when not set).
Campaign values are the link's defaults (set in the dashboard) overridden by the `utm_*`
parameters of the tapped URL. They are labels chosen by whoever shared the link: use them for
your analytics, never to decide anything. Details:
[integration guide](./Documentation~/unity.md#campaign-values).

## Links without an API call (optional)

Path links (`https://my-app.strybok.link/product/42?id=7`) and Firebase long links
(`https://my-app.strybok.link/?link=…`) open a route without a link made in advance, once the
owner switched them on. Copy `parameterLinks` of the integration guide
(`GET /v1/apps/:appId/integration`, shown on the dashboard's setup screen) into the options:

```csharp
StrybokSdk.Initialize(new StrybokOptions
{
    ApiBaseUrl = "https://api.strybok.com",
    AllowedHosts = { "my-app.strybok.link" },
    ParameterLinks = new ParameterLinksOptions("id", "lang"), // parameterLinks.payloadKeys
});
```

or switch on **Parameter Links** and fill **Payload Keys** in the `StrybokSettings` asset. Only
the path and the values of these keys are sent, and `link.RoutePath` / `link.Payload` are what the
server read, never the tapped path. Off by default; without it nothing changes. New in 0.4.0,
covered by Edit Mode tests only. Details:
[integration guide](./Documentation~/unity.md#links-without-an-api-call-optional).

## Deferred links (optional)

Restoring a link that was tapped before the game was installed is optional, **off by default and
has never run on a device**. The package ships no store integration and never reads the
pasteboard; your game supplies what the platform has:

- **Android:** `DeferredLinks = true` plus at least one `InstallReferrerSources` entry: a function
  of yours that returns the store's raw install referrer, "unavailable for now" or "unavailable"
  (the guide has an untested Google Play example through `AndroidJavaObject`). Huawei AppGallery
  and Samsung Galaxy Store flows are unverified even in the native SDKs.
- **iOS:** `await StrybokSdk.RedeemPastedLinkAsync(text)` with the text the person pasted. Unity
  has no wrapper for Apple's paste button, so reading `GUIUtility.systemCopyBuffer` shows the iOS
  paste prompt; it costs the person extra taps and works only if they paste.
- **iOS probabilistic matching** (`AllowProbabilisticMatching`) is **forbidden by Apple's rules**
  (the App Review and legal risk is yours), can hand over another person's link on a shared
  network, fails behind Private Relay or a VPN, and has privacy-policy consequences. Check
  `link.IsProbabilisticMatch` before doing anything personal with such a link.

`StrybokSdk` keeps the two state values in `PlayerPrefs` and takes the package name / bundle id
from `Application.identifier`; a plain `StrybokClient` needs `DeferredLinkStore` and the
identifier from you, otherwise the status stays `Disabled`. Deferred links arrive through
`LinkReceived` with `link.IsDeferred`; `StrybokSdk.DeferredLinkStatus` says where the check stands.
Details: [integration guide](./Documentation~/unity.md#deferred-links-optional),
[Android](./Documentation~/android.md), [iOS](./Documentation~/ios.md).

Full guide, native setup and troubleshooting:
[`Documentation~/unity.md`](./Documentation~/unity.md).
Behaviour shared by all Strybok SDKs: [`Documentation~/sdk-contract.md`](./Documentation~/sdk-contract.md).

- Unity 2021.3 or newer, iOS and Android, Mono and IL2CPP. No third-party dependencies.
- Covered by Edit Mode tests (run on Unity 6000.5 and 6000.6). **Not yet verified on a physical
  device, nor on Unity 2021.3 / 2022.3**. Available from OpenUPM.
- A link tapped before the app is installed is not restored by default. Deferred links are
  optional, off by default and unverified on devices; the package ships no store integration, see
  [Deferred links](#deferred-links-optional).

## Install

_Window > Package Manager > + > Add package from git URL_:

```text
https://github.com/strybok/strybok-unity.git#0.4.0
```

or from [OpenUPM](https://openupm.com/packages/com.strybok.sdk/) with the openupm CLI:

```sh
openupm add com.strybok.sdk
```

or, from a local clone, in `Packages/manifest.json` (path relative to the `Packages` folder):

```json
"com.strybok.sdk": "file:../../strybok-unity"
```

## Layout

| Folder          | Contents                                                                                                                               |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `Runtime/Core`  | Plain C#, no `UnityEngine`: `StrybokClient`, options, `ResolvedDeepLink`, `LinkCampaign`, `CreatedLink`, exceptions, interfaces, JSON. |
| `Runtime/Unity` | `StrybokSdk`, `UnityLinkSource`, `UnityWebRequestTransport`, `StrybokSettings`, `StrybokBehaviour`.                                    |
| `Editor`        | Settings menu and the iOS / Android build post-processors.                                                                             |
| `Tests/Editor`  | Edit Mode tests of the SDK contract.                                                                                                   |

## Run the tests

Add the package to a project with `"testables": ["com.strybok.sdk"]` in `Packages/manifest.json`
and the `com.unity.test-framework` package, then use _Window > General > Test Runner > EditMode_
or:

```sh
Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode \
  -testResults results.xml -logFile -
```

## Licence

Apache 2.0, see [LICENSE.md](./LICENSE.md).
