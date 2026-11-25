# Changelog

## [0.3.0] - 2026-11-24

- Campaign values. `ResolvedDeepLink.Campaign` (`LinkCampaign`: `Source`, `Medium`, `Campaign`,
  `Term`, `Content`, each a string or `null`; `IsEmpty`; `LinkCampaign.Empty`) holds the link's
  default campaign values overridden by the `utm_*` parameters of the tapped URL, as the service
  returned them. It is never `null`. The values are untrusted labels chosen by whoever shared
  the link.
- The resolve request now carries the five parameters `utm_source`, `utm_medium`, `utm_campaign`,
  `utm_term` and `utm_content` of the tapped URL — exact lower-case names, the first occurrence
  of each, percent-decoded, and only values of 1–64 characters out of `A–Z a–z 0–9 . _ ~ -`, in
  a fixed order. Every other query parameter and the fragment are still never sent, and a URL
  without valid campaign parameters produces exactly the request of earlier versions. The filter
  is a small strict parser of its own (`System.Uri` has none).
- The `app_open` event reports `campaign` exactly as resolve returned it and leaves it out when
  there is none; it is never built from the URL.
- Additive: `ResolvedDeepLink` keeps its five-argument constructor and gains one with a
  `LinkCampaign`. Which URLs are accepted, duplicate detection (the whole URL string as
  received) and everything else are unchanged.
- Deferred links, optional and **off by default; nothing of it has run on a device**. Options
  `DeferredLinks` and `AllowProbabilisticMatching` (also in the `StrybokSettings` asset),
  `InstallReferrerSources`, `AndroidPackageName`, `IosBundleId`, `DeviceFamily`, `Locale`,
  `PasteboardMayContainLink`, `DeferredLinkStore`; `DeferredLinkStatus` /
  `DeferredLinkStatusChanged`, `EnableDeferredLinks()`, `RedeemPastedLinkAsync(text)` and
  `SkipPastedLink()` on `StrybokSdk` and `StrybokClient`; `ResolvedDeepLink.IsDeferred` and
  `IsProbabilisticMatch` (both `false` for every normal link). The whole flow of the SDK contract
  is implemented in the core: the attempt is stored before a source is asked, at most three
  checks per installation, "done" is stored before delivery, a normal link wins, no `app_open`,
  nothing through `LinkFailed`, never from `GetInitialLinkAsync`.
  - **The package ships no store integration and no native code.** On Android your game supplies
    the install referrer through `InstallReferrerSource` delegates (referrer string, unavailable
    for now, or unavailable); on iOS it supplies the pasted text. The SDK never reads the
    pasteboard and never logs or stores the referrer, the pasted text or the id.
  - `StrybokSdk` defaults: state in `PlayerPrefs` (`PlayerPrefsDeferredLinkStore`), the package
    name / bundle id from `Application.identifier`, device family from `SystemInfo.deviceModel`
    and language from `Application.systemLanguage`. A plain `StrybokClient` has no defaults and
    stays `Disabled` without a store and the identifier. In the Editor the status is always
    `Disabled`.
  - iOS probabilistic matching is forbidden by Apple's rules and stays off unless
    `AllowProbabilisticMatching` is set in code or in the asset; a matched link is marked
    `IsProbabilisticMatch`.

## [0.2.0] - 2026-11-24

- `StrybokSdk.CreateLinkAsync` / `StrybokClient.CreateLinkAsync` (and
  `CreateLinkWithPayloadJsonAsync`): create a short link to a screen of the game for a "Share"
  button. Needs the new `ClientKey` option (publishable key `pk_…`; **Client Key** in the
  `StrybokSettings` asset). Returns a `CreatedLink` (`Url`, `Slug`, `RoutePath`, `LinkId`).
  Timeouts, network errors and 5xx are retried with the same body; 4xx never are.
- New exceptions for creating links: `StrybokUnauthorizedException`, `RouteNotAllowedException`
  (`RoutePath`), `StrybokRateLimitedException` (`RetryAfter`), `StrybokValidationException`
  (`StatusCode`, `Issues`).
- `StrybokHttpResponse` has `Headers` and `GetHeader(name)`; `UnityWebRequestTransport` fills
  them. A custom `IHttpTransport` keeps working without changes (then `RetryAfter` is unknown).
- `StrybokException.Url` is null for errors of creating a link.

## [0.1.0] - 2026-11-24

- Initial version: `StrybokSdk` (`Initialize`, `LinkReceived`, `LinkFailed`, `GetInitialLinkAsync`,
  `ResolveAsync`, `Shutdown`) on top of the Unity-independent `StrybokClient`; host allowlist;
  duplicate-delivery protection; timeout and retry; typed exceptions; best-effort `app_open`
  events; reflection-free JSON for payloads; `StrybokSettings` asset and `StrybokBehaviour`;
  build post-processors for the iOS Associated Domains entitlement and the Android App Links
  intent filter; `FakeLinkSource` test helper.
