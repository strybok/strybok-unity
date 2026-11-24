# Changelog

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
