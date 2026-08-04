#!/bin/bash
# Build the full macOS NAPLPS.app: publish the Avalonia app self-contained, declare the .nap
# UTType, embed the Quick Look thumbnail + preview extensions, and sign the whole bundle.
# Prerequisites and signing setup: see macos/README.md.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
APP="${1:-$HOME/Desktop/NAPLPS.app}"

# .NET SDK: honor $DOTNET, else take the first dotnet on PATH.
DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [ -z "$DOTNET" ]; then
  echo "error: dotnet not found. Install the .NET 10 SDK or set DOTNET=/path/to/dotnet" >&2
  exit 1
fi

# Host architecture -> .NET RID.
case "$(uname -m)" in
  arm64)  RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "error: unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac

# Signing identity: honor $CODESIGN_ID; in DEVID mode auto-detect a Developer ID Application
# identity; else use the local "NAPLPS Development" identity when it exists; else fall back to
# ad-hoc with a warning. Ad-hoc is fine for running the app itself, but the Quick Look host
# will NOT load ad-hoc extensions (see macos/README.md).
DEVID="${DEVID:-0}"
if [ -n "${CODESIGN_ID:-}" ]; then
  SIGN_ID="$CODESIGN_ID"
elif [ "$DEVID" = "1" ]; then
  SIGN_ID="$(security find-identity -v -p codesigning 2>/dev/null | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p' | head -1)"
  if [ -z "$SIGN_ID" ]; then
    echo "error: DEVID=1 but no 'Developer ID Application' identity in the keychain (or set CODESIGN_ID)" >&2
    exit 1
  fi
elif security find-identity -v -p codesigning 2>/dev/null | grep -q "NAPLPS Development"; then
  SIGN_ID="NAPLPS Development"
else
  SIGN_ID="-"
  echo "warning: no signing identity (set CODESIGN_ID); ad-hoc signing - Quick Look will not load the extensions" >&2
fi

# Mac App Store mode (MAS=1): distribution signing with sandbox entitlements and embedded
# provisioning profiles. Signing details: macos/README.md.
MAS="${MAS:-0}"
if [ "$MAS" = "1" ]; then
  : "${TEAM_ID:?MAS=1 requires TEAM_ID (Apple Developer Team ID)}"
  : "${MAS_PROFILE_APP:?MAS=1 requires MAS_PROFILE_APP (path to the main app Mac App Store provisioning profile)}"
  : "${MAS_PROFILE_QUICKLOOK:?MAS=1 requires MAS_PROFILE_QUICKLOOK (thumbnail extension profile)}"
  : "${MAS_PROFILE_PREVIEW:?MAS=1 requires MAS_PROFILE_PREVIEW (preview extension profile)}"
  if [ "$SIGN_ID" = "-" ]; then
    echo "error: MAS=1 needs a real identity - set CODESIGN_ID to your 'Apple Distribution: ...' identity" >&2
    exit 1
  fi
fi

# Bundle version comes from the library csproj so it cannot drift from the code.
VERSION="$(sed -n 's/.*<InformationalVersion>\([^<]*\).*/\1/p' "$ROOT/NAPLPS/NAPLPS.csproj" | head -1)"
VERSION="${VERSION:-0.0.0}"

PUB="$(mktemp -d)"

echo "== publish app ($RID) =="
"$DOTNET" publish "$ROOT/NAPLPSApp/NAPLPSApp.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$PUB" 2>&1 | tail -1

echo "== build Quick Look extensions =="
DOTNET="$DOTNET" CODESIGN_ID="$SIGN_ID" "$HERE/quicklook/build.sh" >/dev/null

echo "== assemble bundle =="
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/PlugIns"
cp -R "$PUB/." "$APP/Contents/MacOS/"
cp -R "$HERE/quicklook/build/NAPLPSQuickLook.appex" "$APP/Contents/PlugIns/"
cp -R "$HERE/quicklook/build/NAPLPSPreview.appex" "$APP/Contents/PlugIns/"

# App icon: pre-built iconset committed at macos/naplps.iconset - the pixel-art source
# upscaled nearest-neighbor at integer factors (512, 1024 stay pixel-crisp; sips' smooth
# scaling smears pixel art) with bicubic 16/32 for tiny-size legibility.
iconutil -c icns "$HERE/naplps.iconset" -o "$APP/Contents/Resources/naplps.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Telidraw</string>
  <key>CFBundleDisplayName</key><string>Telidraw</string>
  <key>CFBundleIdentifier</key><string>com.foxcouncil.naplps</string>
  <key>CFBundleVersion</key><string>0.0.0</string>
  <key>CFBundleShortVersionString</key><string>0.0.0</string>
  <key>CFBundleExecutable</key><string>NAPLPSApp</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>naplps</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <!-- App Store submission requirements: a primary category, a platform declaration, and the
       encryption-exemption answer (no non-exempt crypto) so uploads skip the compliance prompt. -->
  <key>LSApplicationCategoryType</key><string>public.app-category.graphics-design</string>
  <key>CFBundleSupportedPlatforms</key><array><string>MacOSX</string></array>
  <key>ITSAppUsesNonExemptEncryption</key><false/>
  <!-- com.foxcouncil.naplps is the one canonical identifier for NAPLPS pictures; other heads and
       platforms import the same id so .nap binds identically everywhere. Deliberately NOT
       conforming to public.image - that would let bitmap editors claim .nap; the Quick Look
       preview extension supplies the image experience and the app owns the type below. -->
  <key>UTExportedTypeDeclarations</key><array>
    <dict>
      <key>UTTypeIdentifier</key><string>com.foxcouncil.naplps</string>
      <key>UTTypeDescription</key><string>NAPLPS Picture</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string><string>public.content</string></array>
      <key>UTTypeTagSpecification</key><dict>
        <key>public.filename-extension</key><array><string>nap</string><string>NAP</string></array>
      </dict>
    </dict>
  </array>
  <!-- The app owns the .nap type (LSHandlerRank Owner) so it is the default opener, not some image
       editor that happened to be in the Open With list. -->
  <key>CFBundleDocumentTypes</key><array><dict>
    <key>CFBundleTypeName</key><string>NAPLPS Picture</string>
    <key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSHandlerRank</key><string>Owner</string>
    <key>LSItemContentTypes</key><array><string>com.foxcouncil.naplps</string></array>
  </dict></array>
</dict></plist>
PLIST
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" \
                        -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"

# App Store uploads require a unique, increasing CFBundleVersion per build (CI passes the run
# number), and every extension must carry the same CFBundleVersion as the containing app.
if [ -n "${BUILD_NUMBER:-}" ]; then
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$APP/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$APP/Contents/PlugIns/NAPLPSQuickLook.appex/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$APP/Contents/PlugIns/NAPLPSPreview.appex/Contents/Info.plist"
fi

chmod +x "$APP/Contents/MacOS/NAPLPSApp"
if [ "$MAS" = "1" ]; then
  # -- Mac App Store distribution signing --
  # App Sandbox is mandatory for store apps; hardened runtime is not, and stays OFF for the
  # CoreCLR payload (it JITs - see the dev-signing comment below). allow-jit is included so a
  # future hardened-runtime requirement doesn't brick the build. Every bundle carries its
  # Team-ID-scoped application-identifier because App Store validation cross-checks the
  # codesign entitlements against the embedded provisioning profiles.
  ENT="$(mktemp -d)"
  write_entitlements() { # <out-file> <bundle-id> <file-access> [jit]
    cat > "$1" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.app-sandbox</key><true/>
  <key>com.apple.security.$3</key><true/>
  <key>com.apple.application-identifier</key><string>$TEAM_ID.$2</string>
  <key>com.apple.developer.team-identifier</key><string>$TEAM_ID</string>
EOF
    if [ "${4:-}" = "jit" ]; then
      echo '  <key>com.apple.security.cs.allow-jit</key><true/>' >> "$1"
    fi
    printf '</dict></plist>\n' >> "$1"
  }
  write_entitlements "$ENT/app.entitlements"       com.foxcouncil.naplps           files.user-selected.read-write jit
  write_entitlements "$ENT/quicklook.entitlements" com.foxcouncil.naplps.quicklook files.user-selected.read-only
  write_entitlements "$ENT/preview.entitlements"   com.foxcouncil.naplps.preview   files.user-selected.read-only

  # createdump is the .NET crash-dump helper: an extra unsandboxed executable that App Store
  # validation rejects. A store build has no use for it.
  rm -f "$APP/Contents/MacOS/createdump"

  cp "$MAS_PROFILE_APP"       "$APP/Contents/embedded.provisionprofile"
  cp "$MAS_PROFILE_QUICKLOOK" "$APP/Contents/PlugIns/NAPLPSQuickLook.appex/Contents/embedded.provisionprofile"
  cp "$MAS_PROFILE_PREVIEW"   "$APP/Contents/PlugIns/NAPLPSPreview.appex/Contents/embedded.provisionprofile"

  # (1) deep pass signs every dylib in the payload, (2) each extension re-signed with hardened
  # runtime + its own sandbox/team entitlements, (3) top-level seal carries the app entitlements.
  codesign --force --deep --sign "$SIGN_ID" "$APP" 2>&1 | tail -1
  codesign --force --options runtime --sign "$SIGN_ID" "$APP/Contents/PlugIns/NAPLPSQuickLook.appex/Contents/Frameworks/libNAPLPS.dylib" 2>&1 | tail -1
  codesign --force --options runtime --sign "$SIGN_ID" --entitlements "$ENT/quicklook.entitlements" "$APP/Contents/PlugIns/NAPLPSQuickLook.appex" 2>&1 | tail -1
  codesign --force --options runtime --sign "$SIGN_ID" "$APP/Contents/PlugIns/NAPLPSPreview.appex/Contents/Frameworks/libNAPLPS.dylib" 2>&1 | tail -1
  codesign --force --options runtime --sign "$SIGN_ID" --entitlements "$ENT/preview.entitlements" "$APP/Contents/PlugIns/NAPLPSPreview.appex" 2>&1 | tail -1
  codesign --force --sign "$SIGN_ID" --entitlements "$ENT/app.entitlements" "$APP" 2>&1 | tail -1
  rm -rf "$ENT"
elif [ "$DEVID" = "1" ]; then
  # -- Developer ID (notarized direct-distribution) signing --
  # Notarization requires the hardened runtime and a secure timestamp on every Mach-O. The
  # CoreCLR payload JITs under the hardened runtime only with the relaxations Microsoft
  # documents for notarizing .NET apps; no App Sandbox outside the store, and no profiles.
  ENT="$(mktemp -d)"
  cat > "$ENT/app.entitlements" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
  <key>com.apple.security.cs.disable-library-validation</key><true/>
  <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
</dict></plist>
EOF
  cat > "$ENT/appex.entitlements" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.app-sandbox</key><true/>
  <key>com.apple.security.files.user-selected.read-only</key><true/>
</dict></plist>
EOF
  codesign --force --deep --options runtime --timestamp --sign "$SIGN_ID" "$APP" 2>&1 | tail -1
  for EXT in "$APP/Contents/PlugIns/NAPLPSQuickLook.appex" "$APP/Contents/PlugIns/NAPLPSPreview.appex"; do
    codesign --force --options runtime --timestamp --sign "$SIGN_ID" "$EXT/Contents/Frameworks/libNAPLPS.dylib" 2>&1 | tail -1
    codesign --force --options runtime --timestamp --sign "$SIGN_ID" --entitlements "$ENT/appex.entitlements" "$EXT" 2>&1 | tail -1
  done
  codesign --force --options runtime --timestamp --sign "$SIGN_ID" --entitlements "$ENT/app.entitlements" "$APP" 2>&1 | tail -1
  rm -rf "$ENT"
else
  # -- Development / ad-hoc signing --
  # The app is a .NET (CoreCLR) app: it JITs, so it must be signed WITHOUT the hardened runtime -
  # hardened runtime blocks executable-memory allocation and the app would die instantly at launch.
  # So: (1) deep-sign the whole app un-hardened (payload + a first pass over the extensions), then
  # (2) re-sign each embedded extension WITH hardened runtime + sandbox entitlements (required for the
  # Quick Look host to load it, fine since NativeAOT/no JIT), then (3) re-seal the app top-level.
  codesign --force --deep --sign "$SIGN_ID" "$APP" 2>&1 | tail -1
  for EXT in "$APP/Contents/PlugIns/NAPLPSQuickLook.appex" "$APP/Contents/PlugIns/NAPLPSPreview.appex"; do
    codesign --force --options runtime --sign "$SIGN_ID" "$EXT/Contents/Frameworks/libNAPLPS.dylib" 2>&1 | tail -1
    codesign --force --options runtime --sign "$SIGN_ID" --entitlements "$HERE/quicklook/NAPLPSQuickLook.entitlements" "$EXT" 2>&1 | tail -1
  done
  codesign --force --sign "$SIGN_ID" "$APP" 2>&1 | tail -1
fi
codesign --verify --verbose=1 "$APP" 2>&1 | tail -1
rm -rf "$PUB"
echo "built: $APP"
