#!/usr/bin/env bash
#
# Asserts that a Release build still produces one self-contained plugin DLL, and
# that Harmony's MIT notice is embedded in it.
#
# The plugin ships as a single file: Harmony and the translations are embedded
# resources, so deployment is one copy into /config/plugins and nothing else. If
# that ever stops being true, the plugin would be installed incomplete — Emby
# does not resolve a missing 0Harmony.dll for it — and the failure would look
# like the patch simply not applying.
#
# Harmony is compiled into that DLL, so THIRD-PARTY-NOTICES.md has to be in there
# too - a licence obligation, and Emby's Development Policy makes breaking one
# grounds for removal from the catalog. One EmbeddedResource line in the csproj
# holds it together: delete it or mistype its LogicalName and the build still
# succeeds, shipping a DLL with no notice. Same silent failure as the single-file
# check, so it is asserted in the same place.
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

# Three checks, each able to break without the others. The resource name is
# written out here rather than read from the csproj: a check that takes its
# expectation from the thing under test accepts a typo. The content needle comes
# from the notice file, so the licence text is not copied a second time.
NOTICE="$REPO_ROOT/THIRD-PARTY-NOTICES.md"
NOTICE_RESOURCE="EmbyProxyRouter.THIRD-PARTY-NOTICES.md"

if [ ! -f "$NOTICE" ]; then
  echo "::error::$NOTICE is missing; it is the MIT notice for the embedded Harmony"
  exit 1
fi

# The copyright line is the part MIT requires to be carried along, and a stable
# needle for proving it shipped.
COPYRIGHT="$(grep -m1 '^Copyright (c)' "$NOTICE" || true)"
if [ -z "$COPYRIGHT" ]; then
  echo "::error::THIRD-PARTY-NOTICES.md has no 'Copyright (c)' line left in it."
  echo "         That line is what MIT requires to be distributed with the library."
  exit 1
fi

# Match the metadata entry, not any occurrence. Manifest resource names sit
# NUL-terminated in the assembly's string heap, so turning NULs into newlines and
# pinning with -x isolates them. A substring search instead matches the notice's
# own text, which names its resource, and so passes on a DLL whose csproj
# declares a mistyped one.
#
# Not `grep -q`: it exits on the first match, tr takes SIGPIPE, and pipefail then
# turns a found notice into an error.
if ! tr '\0' '\n' < "$DLL" | grep -axF "$NOTICE_RESOURCE" >/dev/null; then
  echo "::error::the plugin DLL has no embedded resource named $NOTICE_RESOURCE"
  echo "         Harmony is compiled into this DLL, so its MIT notice has to be too."
  echo "         Check the EmbeddedResource for THIRD-PARTY-NOTICES.md in the csproj."
  exit 1
fi

if ! grep -aqF "$COPYRIGHT" "$DLL"; then
  echo "::error::the embedded notice does not carry the line: $COPYRIGHT"
  echo "         The resource exists but its content is not what THIRD-PARTY-NOTICES.md says."
  exit 1
fi

echo "Embedded notice: $NOTICE_RESOURCE ($COPYRIGHT)"

SIZE="$(stat -c%s "$DLL")"
echo "EmbyProxyRouter.dll: $SIZE bytes"

# An unembedded Harmony would drop this well below a megabyte.
if [ "$SIZE" -lt 1000000 ]; then
  echo "::error::plugin DLL is suspiciously small - are the embedded resources missing?"
  exit 1
fi

echo "OK: single self-contained plugin DLL"
