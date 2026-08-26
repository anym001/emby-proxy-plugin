#!/usr/bin/env bash
#
# Asserts that a Release build still produces one self-contained plugin DLL,
# and that the notice which has to travel inside it is actually in there.
#
# The plugin ships as a single file: Harmony and the translations are embedded
# resources, so deployment is one copy into /config/plugins and nothing else. If
# that ever stops being true, the plugin would be installed incomplete — Emby
# does not resolve a missing 0Harmony.dll for it — and the failure would look
# like the patch simply not applying.
#
# Bundling Harmony that way makes every copy of the plugin a copy of an
# MIT-licensed library, so THIRD-PARTY-NOTICES.md is embedded alongside it. That
# is a licence obligation and Emby's plugin Development Policy makes breaking one
# grounds for removal from the catalog — but the only thing holding it together
# is one EmbeddedResource line in the csproj. Delete it, or mistype its
# LogicalName, and the build still succeeds and ships a DLL with no notice in it.
# That is the same shape of silent failure as the single-file check above, which
# is why it is asserted in the same place rather than left to review.
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

# The notice has to be inside the DLL, not merely present in the repository: a
# binary downloaded from a release, or handed out by Emby's catalog, arrives on
# its own.
#
# Two separate things are checked, because either can break without the other.
# The resource NAME is written out here rather than read back from the csproj on
# purpose - a check that derives what it expects from the thing it is checking
# would happily accept a typo. The resource CONTENT is taken from the notice
# file, because that assertion is a relationship ("what the file says is in the
# DLL") rather than a constant, and a copy of the licence text here would be one
# more thing to keep in step.
NOTICE="$REPO_ROOT/THIRD-PARTY-NOTICES.md"
NOTICE_RESOURCE="EmbyProxyRouter.THIRD-PARTY-NOTICES.md"

if [ ! -f "$NOTICE" ]; then
  echo "::error::$NOTICE is missing; it is the MIT notice for the embedded Harmony"
  exit 1
fi

# Whatever else the notice grows, the copyright line is the part MIT actually
# requires to be carried along, so it is both the thing worth proving shipped
# and a stable needle to prove it with.
COPYRIGHT="$(grep -m1 '^Copyright (c)' "$NOTICE" || true)"
if [ -z "$COPYRIGHT" ]; then
  echo "::error::THIRD-PARTY-NOTICES.md has no 'Copyright (c)' line left in it."
  echo "         That line is what MIT requires to be distributed with the library."
  exit 1
fi

# Matched against the metadata entry, not just "these bytes appear somewhere".
# Manifest resource names sit NUL-terminated in the assembly's string heap, so
# turning NULs into newlines puts each one on a line of its own and -x pins the
# match to a whole line. A plain substring search is not good enough here and
# quietly was not: the notice's own text mentions its resource name, that text is
# embedded, and so the correct name is findable inside the DLL even when the
# csproj declares a mistyped one - which is precisely the case this exists to
# catch.
#
# Not `grep -q`: it exits on the first match, tr takes SIGPIPE, and `set -o
# pipefail` then reports the whole pipeline as failed - turning a found notice
# into an error. Letting grep drain its input costs nothing on a 2 MB file.
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
