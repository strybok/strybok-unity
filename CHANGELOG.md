# Changelog

## [0.1.0] - 2026-11-24

- Initial version: `StrybokSdk` (`Initialize`, `LinkReceived`, `LinkFailed`, `GetInitialLinkAsync`,
  `ResolveAsync`, `Shutdown`) on top of the Unity-independent `StrybokClient`; host allowlist;
  duplicate-delivery protection; timeout and retry; typed exceptions; best-effort `app_open`
  events; reflection-free JSON for payloads; `StrybokSettings` asset and `StrybokBehaviour`;
  build post-processors for the iOS Associated Domains entitlement and the Android App Links
  intent filter; `FakeLinkSource` test helper.
