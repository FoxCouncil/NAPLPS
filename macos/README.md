# macOS app + Quick Look build

Builds `NAPLPS.app` (the Avalonia desktop app, self-contained) with embedded Quick Look
thumbnail and preview extensions that render `.nap` files natively in Finder. The renderer
detects the system type from the stream header (`A1 C8` -> Prodigy palette and pipeline,
`0x0E` -> Telidon, otherwise standard NAPLPS).

## Prerequisites

- macOS 15+ on Apple Silicon or Intel (the scripts detect the host architecture).
- .NET 10 SDK. The scripts use the first `dotnet` on `PATH`; override with
  `DOTNET=/path/to/dotnet` if you have multiple SDKs installed.
- Xcode or the Command Line Tools (`swiftc` and the Quick Look frameworks).

## Build

```
./macos/build-app.sh [/path/to/NAPLPS.app]     # full app + extensions (default: ~/Desktop)
./macos/quicklook/build.sh                     # just the .appex bundles (macos/quicklook/build/)
```

The bundle version is read from `NAPLPS/NAPLPS.csproj` (`InformationalVersion`), and the app
icon is generated from `NAPLPSApp/Assets/naplps.ico` at build time - nothing host-specific is
required.

## Signing

Signing resolves in this order:

1. `CODESIGN_ID` environment variable, if set.
2. A local identity named `NAPLPS Development`, if one exists in the keychain.
3. Ad-hoc (`-`), with a warning.

Ad-hoc is fine for building and for running the app itself, but **the Quick Look host refuses
to load ad-hoc-signed extensions** - thumbnails and previews need a real identity. Any
self-signed code-signing certificate works for local development: Keychain Access >
Certificate Assistant > Create a Certificate, name it (e.g. `NAPLPS Development`), type
"code signing"; then either name it exactly `NAPLPS Development` or export
`CODESIGN_ID="Your Name"`.

Two signing rules the scripts encode (learned the hard way):

- The .NET app must be signed *without* the hardened runtime - CoreCLR JITs, and the hardened
  runtime kills it at launch.
- Each extension is then re-signed *with* the hardened runtime + sandbox entitlements (the
  Quick Look host requires it; fine for the extensions since the renderer dylib is NativeAOT,
  no JIT). The `disable-library-validation` entitlement is needed with self-signed certs
  because they carry no Team ID; with a real Developer ID (same team for app and dylib) it
  could be dropped.

## Install / activation

Quick Look extensions activate only for apps in `/Applications` that have been launched once:

```
./macos/build-app.sh /Applications/NAPLPS.app
open /Applications/NAPLPS.app        # once, to register the extensions
qlmanage -r && qlmanage -r cache     # reset Quick Look after a rebuild
```

`pluginkit -m | grep naplps` should list `com.foxcouncil.naplps.quicklook` and `.preview`.

## CI notes

`.github/workflows/macos.yml` builds on every push/PR using a `macos-15` runner: ad-hoc
signing, compile validation only (extensions will not load in a GUI-less runner anyway), app
bundle uploaded as an artifact. No secrets are used on these runs.

## Mac App Store

`workflow_dispatch` on the same workflow runs the distribution pipeline: `build-appstore.sh`
signs the app in MAS mode (App Sandbox entitlements on every bundle, embedded provisioning
profiles, Apple Distribution identity), wraps it in a signed installer pkg, and uploads it to
TestFlight via the App Store Connect API.

MAS-mode signing differs from dev signing in three ways: the main app gains sandbox +
`allow-jit` + Team-ID entitlements (still no hardened runtime - CoreCLR JITs), each extension
gains Team-ID entitlements and drops `disable-library-validation` (a real team signs the dylib
and the appex alike), and .NET's `createdump` helper is removed (an unsandboxed extra
executable fails App Store validation).

The workflow's `appstore` environment must provide the distribution-certificate,
provisioning-profile, and App Store Connect API key secrets referenced in
`.github/workflows/macos.yml`, and should be configured with required reviewers so each
run is human-approved before any secret is released. Fork PRs never receive secrets.

### Security posture

Secrets are encrypted at rest, redacted in logs, released only to environment-approved runs,
and live in an ephemeral keychain on a VM that is destroyed after the job. The workflow uses
only first-party `actions/*` actions, SHA-pinned. No Apple ID credential is ever in CI - the
ASC API key is the only account-level material and it is role-scoped and revocable. If
anything leaks anyway: revoke the certificates and the API key in the portal, rotate the
secrets; distribution certs cannot ship anything without also passing through the ASC account
and App Review.

### Versioning

`CFBundleShortVersionString` comes from `NAPLPS/NAPLPS.csproj` as always; `CFBundleVersion`
is stamped with the workflow run number (`BUILD_NUMBER`) so every upload is unique and
increasing, on the app and both extensions alike.
