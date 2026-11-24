# Strybok SDK for Unity

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

Full guide, native setup and troubleshooting:
[`Documentation~/unity.md`](./Documentation~/unity.md).
Behaviour shared by all Strybok SDKs: [`Documentation~/sdk-contract.md`](./Documentation~/sdk-contract.md).

- Unity 2021.3 or newer, iOS and Android, Mono and IL2CPP. No third-party dependencies.
- Covered by Edit Mode tests (run on Unity 6000.5 and 6000.6). **Not yet verified on a physical
  device, nor on Unity 2021.3 / 2022.3**, and not published to a registry.
- Deferred deep linking is not supported: a link tapped before the app is installed is not
  restored after installation.

## Install

_Window > Package Manager > + > Add package from git URL_:

```text
https://github.com/strybok/strybok-unity.git#0.1.0
```

or, from a local clone, in `Packages/manifest.json` (path relative to the `Packages` folder):

```json
"com.strybok.sdk": "file:../../strybok-unity"
```

## Layout

| Folder          | Contents                                                                                                |
| --------------- | ------------------------------------------------------------------------------------------------------- |
| `Runtime/Core`  | Plain C#, no `UnityEngine`: `StrybokClient`, options, `ResolvedDeepLink`, exceptions, interfaces, JSON. |
| `Runtime/Unity` | `StrybokSdk`, `UnityLinkSource`, `UnityWebRequestTransport`, `StrybokSettings`, `StrybokBehaviour`.     |
| `Editor`        | Settings menu and the iOS / Android build post-processors.                                              |
| `Tests/Editor`  | Edit Mode tests of the SDK contract.                                                                    |

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
