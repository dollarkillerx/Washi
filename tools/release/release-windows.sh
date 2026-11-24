#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Builds an UNSIGNED Windows (x64) release of Washi with Velopack; runs on macOS or Linux. After MySSH's tools/release.
#
#   tools/release/release-windows.sh <version>          e.g. 0.1.0
#
# Self-contained with ReadyToRun code (no .NET to install). Without a code-signing certificate SmartScreen shows
# "Windows protected your PC" on the first run (More info → Run anyway).
#
# Output (WASHI_RELEASE_OUT, default build/out/release): Washi-win-Setup.exe, Washi-win-Portable.zip, the .nupkg,
# releases.win.json, assets.win.json and RELEASES. Upload them to the same GitHub release as the macOS files; the
# Windows updater reads releases.win.json.
set -euo pipefail
VERSION="${1:?usage: release-windows.sh <version>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
REPO="https://github.com/dollarkillerx/Washi"
OUT="${WASHI_RELEASE_OUT:-$ROOT/build/out/release}"
VPK="$(command -v vpk || echo "$HOME/.dotnet/tools/vpk")"
RID=win-x64

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PUB="$WORK/publish"

echo "==> publish $VERSION ($RID, self-contained, ReadyToRun)"
dotnet publish "$ROOT/src/Washi.App" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -p:Version="$VERSION" \
  -o "$PUB" --artifacts-path "$WORK/artifacts" >/dev/null
dotnet publish "$ROOT/src/Washi.Mcp" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -p:Version="$VERSION" \
  -o "$PUB/mcp" --artifacts-path "$WORK/artifacts-mcp" >/dev/null
find "$PUB" -name '*.pdb' -delete
test -f "$PUB/Washi.App.exe" && test -f "$PUB/mcp/washi-mcp.exe" && test -f "$PUB/pdfium.dll"

mkdir -p "$OUT"
echo "==> previous release (for delta updates)"
"$VPK" "[win]" download github --repoUrl "$REPO" -o "$OUT" -c win || echo "   none found; full package only"

NOTES=()
[ -f "$ROOT/docs/releases/$VERSION.md" ] && NOTES=(--releaseNotes "$ROOT/docs/releases/$VERSION.md")

echo "==> pack (unsigned)"
"$VPK" "[win]" pack -u Washi -v "$VERSION" -p "$PUB" -e Washi.App.exe -o "$OUT" -c win -r "$RID" \
  --packTitle Washi --packAuthors "Washi contributors" --icon "$ROOT/assets/brand/washi.ico" "${NOTES[@]}"
echo "done: $OUT"
ls -lh "$OUT" | grep -i -E "win|RELEASES"
