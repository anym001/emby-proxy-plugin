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
PINNED_VERSION="$(tr -d '[:space:]' < "$REPO_ROOT/build/emby-version.txt")"
VERSION="${1:-$PINNED_VERSION}"
LIB_DIR="$REPO_ROOT/lib"
PACKAGE="emby-server-deb_${VERSION}_amd64.deb"
URL="https://github.com/MediaBrowser/Emby.Releases/releases/download/${VERSION}/${PACKAGE}"

# The checksum of the pinned package, and of that one only. The plugin's release DLL is compiled
# against the assemblies inside it and build/verify-patch-target.sh reads its patch target out of
# the same file, so this download decides what ships. HTTPS authenticates the host; it says nothing
# about the artefact still being the one this repository was verified against.
#
# Only the pinned version can be checked: ci.yml dispatches this script against newer Emby releases
# to see whether they still work, and those have no checksum here by definition. Such a run reports
# that it is unverified rather than failing — that is the case the workflow exists for — but it also
# never publishes. release.yml has no version input at all and therefore always takes this path.
SHA_FILE="$REPO_ROOT/build/emby-sha256.txt"

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
need sha256sum

missing=0
for f in "${NEEDED[@]}"; do
  [ -f "$LIB_DIR/$f" ] || missing=1
done
if [ "$missing" -eq 0 ] && [ "${FORCE:-0}" != "1" ]; then
  echo "Reference assemblies already present in $LIB_DIR (set FORCE=1 to re-fetch)."
  exit 0
fi

# Resolved before the download, not after: a pin that cannot be checked is a mistake in the
# repository, and there is no reason to spend 180 MB discovering it.
expected=""
if [ "$VERSION" = "$PINNED_VERSION" ]; then
  if [ ! -f "$SHA_FILE" ]; then
    echo "error: $SHA_FILE is missing. It is the other half of the version pin;" >&2
    echo "       without it the pinned version cannot be verified. Restore it from git." >&2
    exit 1
  fi

  expected="$(awk -v want="$PACKAGE" '$2 == want { print $1 }' "$SHA_FILE")"
  if [ -z "$expected" ]; then
    echo "error: $SHA_FILE has no entry for $PACKAGE, which is the pinned version." >&2
    echo "       Add one, or the release build is compiling against an unverified download." >&2
    exit 1
  fi
else
  echo "note: ${VERSION} is not the pinned version (${PINNED_VERSION}); no checksum is recorded"
  echo "      for it, so this download is unverified. Fine for checking a new Emby release,"
  echo "      never for building something to publish."
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Downloading Emby Server ${VERSION} (~180 MB, reference assemblies only are kept)..."
curl -fSL --retry 3 --retry-delay 2 -o "$TMP/emby.deb" "$URL"

if [ -n "$expected" ]; then
  actual="$(sha256sum "$TMP/emby.deb" | cut -d' ' -f1)"
  if [ "$actual" != "$expected" ]; then
    echo "error: checksum mismatch for $PACKAGE" >&2
    echo "       expected $expected" >&2
    echo "       actual   $actual" >&2
    echo "       Refusing to extract. Nothing was written to $LIB_DIR." >&2
    exit 1
  fi
  echo "Checksum verified against build/emby-sha256.txt."
fi

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
