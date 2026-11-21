#!/bin/sh
# SPDX-License-Identifier: AGPL-3.0-or-later
# Builds Washi.app (framework-dependent: needs the .NET 10 runtime) into build/out/.
#   build/macos/bundle.sh [Release|Debug]
#   open build/out/Washi.app            # or: open -a build/out/Washi.app form.pdf
set -eu
cd "$(dirname "$0")/../.."
CONFIG="${1:-Release}"
RID="osx-$(uname -m | sed 's/x86_64/x64/')"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
VERSION="${VERSION:-0.1.0}"
APP=build/out/Washi.app

rm -rf "$APP" build/out/publish
dotnet publish src/Washi.App -c "$CONFIG" -r "$RID" --self-contained false -o build/out/publish -p:UseAppHost=true
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R build/out/publish/. "$APP/Contents/MacOS/"
cp assets/brand/washi.icns "$APP/Contents/Resources/washi.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Washi</string>
  <key>CFBundleDisplayName</key><string>Washi</string>
  <key>CFBundleIdentifier</key><string>io.github.dollarkillerx.washi</string>
  <key>CFBundleExecutable</key><string>Washi.App</string>
  <key>CFBundleIconFile</key><string>washi</string>
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
echo "built $APP"
