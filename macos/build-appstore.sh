#!/bin/bash
# Build the Mac App Store deliverable: runs build-app.sh in MAS mode (sandbox entitlements,
# embedded provisioning profiles, Apple Distribution signing), then wraps the app in a signed
# installer pkg ready for TestFlight / App Store upload. Signing details: macos/README.md.
#
# Required environment:
#   TEAM_ID                Apple Developer Team ID
#   MAS_PROFILE_APP        path to the app's Mac App Store provisioning profile
#   MAS_PROFILE_QUICKLOOK  path to the thumbnail extension's profile
#   MAS_PROFILE_PREVIEW    path to the preview extension's profile
# Optional:
#   CODESIGN_ID            "Apple Distribution: ..." identity (default: first one in the keychain)
#   INSTALLER_ID           "3rd Party Mac Developer Installer: ..." identity (default: first found)
#   BUILD_NUMBER           CFBundleVersion override (CI passes the run number)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/build}"

# Distribution identities: honor the env overrides, else take the first matching identity in
# the keychain. The installer cert is not a codesigning identity, so no -p codesigning filter.
if [ -z "${CODESIGN_ID:-}" ]; then
  CODESIGN_ID="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Apple Distribution: [^"]*\)".*/\1/p' | head -1)"
fi
if [ -z "${INSTALLER_ID:-}" ]; then
  INSTALLER_ID="$(security find-identity -v | sed -n 's/.*"\(3rd Party Mac Developer Installer: [^"]*\)".*/\1/p' | head -1)"
fi
if [ -z "$CODESIGN_ID" ] || [ -z "$INSTALLER_ID" ]; then
  echo "error: distribution identities not found - need 'Apple Distribution' and '3rd Party Mac Developer Installer' certs in the keychain (or set CODESIGN_ID / INSTALLER_ID)" >&2
  exit 1
fi

mkdir -p "$OUT"
MAS=1 CODESIGN_ID="$CODESIGN_ID" "$HERE/build-app.sh" "$OUT/Telidraw.app"

echo "== package installer pkg =="
productbuild --component "$OUT/Telidraw.app" /Applications --sign "$INSTALLER_ID" "$OUT/Telidraw.pkg"
echo "built: $OUT/Telidraw.pkg"
