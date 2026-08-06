#!/usr/bin/env bash
#
# Asserts that a Release build still produces one self-contained plugin DLL.
#
# The plugin ships as a single file: Harmony and the translations are embedded
# resources, so deployment is one copy into /config/plugins and nothing else. If
# that ever stops being true, the plugin would be installed incomplete — Emby
# does not resolve a missing 0Harmony.dll for it — and the failure would look
# like the patch simply not applying.
#
# Both ci.yml and release.yml run this, so a release cannot ship an output shape
# that a pull request would have rejected.
#
# Usage:  ./build/verify-single-dll.sh [output-directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$REPO_ROOT/src/EmbyProxyRouter/bin/Release}"
DLL="$OUT/EmbyProxyRouter.dll"

if [ ! -f "$DLL" ]; then
  echo "::error::$DLL was not produced"
  exit 1
fi

if [ -f "$OUT/0Harmony.dll" ]; then
  echo "::error::0Harmony.dll was copied next to the plugin; it must stay embedded"
  exit 1
fi

SIZE="$(stat -c%s "$DLL")"
echo "EmbyProxyRouter.dll: $SIZE bytes"

# An unembedded Harmony would drop this well below a megabyte.
if [ "$SIZE" -lt 1000000 ]; then
  echo "::error::plugin DLL is suspiciously small - are the embedded resources missing?"
  exit 1
fi

echo "OK: single self-contained plugin DLL"
