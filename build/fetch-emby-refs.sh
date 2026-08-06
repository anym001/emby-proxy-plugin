#!/usr/bin/env bash
#
# Fetches the Emby reference assemblies this plugin compiles against.
#
# They are NOT committed to the repository: they are proprietary Emby binaries
# and redistributing them is not ours to do. This script pulls the official
# Emby Server .deb and extracts only the four assemblies needed to build.
#
# Usage:  ./build/fetch-emby-refs.sh [version]
# Default is the version pinned in build/emby-version.txt, which is the one this
# plugin was developed and verified against.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(tr -d '[:space:]' < "$REPO_ROOT/build/emby-version.txt")}"
LIB_DIR="$REPO_ROOT/lib"
URL="https://github.com/MediaBrowser/Emby.Releases/releases/download/${VERSION}/emby-server-deb_${VERSION}_amd64.deb"

# Referenced by the plugin at compile time.
NEEDED=(
  MediaBrowser.Common.dll
  MediaBrowser.Controller.dll
  MediaBrowser.Model.dll
  Emby.Web.GenericEdit.dll
  # Not referenced — this is the assembly the plugin patches. It is kept so the
  # patch target can be verified against a given Emby version without a second
  # 180 MB download. See build/verify-patch-target.sh.
  Emby.Server.Implementations.dll
)

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: '$1' is required but not installed." >&2; exit 1; }; }
need curl
need ar
need tar

missing=0
for f in "${NEEDED[@]}"; do
  [ -f "$LIB_DIR/$f" ] || missing=1
done
if [ "$missing" -eq 0 ] && [ "${FORCE:-0}" != "1" ]; then
  echo "Reference assemblies already present in $LIB_DIR (set FORCE=1 to re-fetch)."
  exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Downloading Emby Server ${VERSION} (~180 MB, reference assemblies only are kept)..."
curl -fSL --retry 3 --retry-delay 2 -o "$TMP/emby.deb" "$URL"

echo "Extracting..."
( cd "$TMP" && ar x emby.deb )
( cd "$TMP" && tar -xf data.tar.xz ./opt/emby-server/system/ )

mkdir -p "$LIB_DIR"
for f in "${NEEDED[@]}"; do
  src="$TMP/opt/emby-server/system/$f"
  [ -f "$src" ] || { echo "error: $f not found in the package — is version $VERSION correct?" >&2; exit 1; }
  cp "$src" "$LIB_DIR/$f"
  echo "  -> lib/$f"
done

echo "Done. Reference assemblies for Emby ${VERSION} are in $LIB_DIR."
