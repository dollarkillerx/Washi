#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Builds a signed, notarized macOS (Apple silicon) release of Washi with Velopack. After MySSH's tools/release.
#
#   tools/release/release-macos.sh <version>          e.g. 0.1.0
#
# Environment:
#   WASHI_SIGN_IDENTITY   codesign identity: a "Developer ID Application: …" name or its SHA-1 (required;
#                         `security find-identity -v -p codesigning`)
#   WASHI_NOTARY_PROFILE  notarytool keychain profile (default: MySSH-notary, the same Apple team). Create one with
#                         `xcrun notarytool store-credentials <name> --key AuthKey_XXXX.p8 --key-id XXXX --issuer <uuid>`.
#                         "-" = sign only, no notarization (a dry run; not for publishing)
#   WASHI_RELEASE_OUT     output folder (default: build/out/release)
#
# Output: Washi-osx-Portable.zip (Washi.app), the full / delta .nupkg, releases.osx.json and assets.osx.json. Upload them
# all to the GitHub release; the app's updater reads releases.osx.json from there. The vpk CLI must be the same version
# as the Velopack package in src/Washi.App/Washi.App.csproj (`dotnet tool install -g vpk --version X`).
#
# The app is self-contained (no .NET to install) and runs on the JIT with ReadyToRun code, so the hardened runtime
# allows JIT (Washi.entitlements). washi-mcp sits in Contents/MacOS/mcp/, where the MCP settings page points.
set -euo pipefail
VERSION="${1:?usage: release-macos.sh <version>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IDENTITY="${WASHI_SIGN_IDENTITY:?set WASHI_SIGN_IDENTITY to a Developer ID Application identity}"
PROFILE="${WASHI_NOTARY_PROFILE:-MySSH-notary}"
REPO="https://github.com/dollarkillerx/Washi"
OUT="${WASHI_RELEASE_OUT:-$ROOT/build/out/release}"
VPK="$(command -v vpk || echo "$HOME/.dotnet/tools/vpk")"
RID=osx-arm64

want="$(sed -n 's/.*Include="Velopack" Version="\([^"]*\)".*/\1/p' "$ROOT/src/Washi.App/Washi.App.csproj")"
have="$("$VPK" -H 2>/dev/null | sed -n 's/.*Velopack CLI \([0-9.]*\).*/\1/p' | head -1)"
if [ -n "$have" ] && [ "$have" != "$want" ]; then echo "vpk $have does not match Velopack $want" >&2; exit 1; fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PUB="$WORK/publish"

echo "==> publish $VERSION ($RID, self-contained, ReadyToRun)"
dotnet publish "$ROOT/src/Washi.App" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -p:Version="$VERSION" \
  -o "$PUB" --artifacts-path "$WORK/artifacts" >/dev/null
dotnet publish "$ROOT/src/Washi.Mcp" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -p:Version="$VERSION" \
  -o "$PUB/mcp" --artifacts-path "$WORK/artifacts-mcp" >/dev/null
find "$PUB" \( -name '*.pdb' -o -name '*.dSYM' \) -prune -exec rm -rf {} +
test -x "$PUB/Washi.App" && test -x "$PUB/mcp/washi-mcp"

cat > "$WORK/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Washi</string>
  <key>CFBundleDisplayName</key><string>Washi</string>
  <key>CFBundleIdentifier</key><string>io.github.dollarkillerx.washi</string>
  <key>CFBundleExecutable</key><string>Washi.App</string>
  <key>CFBundleIconFile</key><string>Washi.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>PDF document</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>com.adobe.pdf</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

mkdir -p "$OUT"
echo "==> previous release (for delta updates)"
"$VPK" download github --repoUrl "$REPO" -o "$OUT" -c osx || echo "   none found; full package only"

NOTES=()
[ -f "$ROOT/docs/releases/$VERSION.md" ] && NOTES=(--releaseNotes "$ROOT/docs/releases/$VERSION.md")
NOTARY=(--notaryProfile "$PROFILE")
[ "$PROFILE" = "-" ] && NOTARY=()

echo "==> pack, sign, notarize (${PROFILE/#-/skipped})"
"$VPK" pack -u Washi -v "$VERSION" -p "$PUB" -e Washi.App -o "$OUT" -c osx \
  --packTitle Washi --packAuthors "Washi contributors" \
  --icon "$ROOT/assets/brand/washi.icns" --plist "$WORK/Info.plist" \
  --signAppIdentity "$IDENTITY" --signEntitlements "$ROOT/tools/release/Washi.entitlements" \
  "${NOTARY[@]}" --noInst "${NOTES[@]}"

echo "==> verify"
CHECK="$WORK/check"
mkdir -p "$CHECK"
ditto -x -k "$OUT/Washi-osx-Portable.zip" "$CHECK"
APP="$(find "$CHECK" -maxdepth 1 -name '*.app' | head -1)"
codesign --verify --deep --strict "$APP"
[ "$PROFILE" = "-" ] || xcrun stapler validate "$APP"
spctl --assess --type execute -vv "$APP" 2>&1 | sed 's/^/   /'
test -x "$APP/Contents/MacOS/mcp/washi-mcp"
echo "done: $OUT"
ls -lh "$OUT" | grep -i -E "osx|\.nupkg"
