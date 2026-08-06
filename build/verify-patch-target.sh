#!/usr/bin/env bash
#
# Verifies that the method this plugin patches still looks the way the patch
# expects it to.
#
# This is the check that matters when moving to a new Emby version. Compiling
# proves nothing here: the plugin references four API assemblies that rarely
# change, while the method it actually patches lives in
# Emby.Server.Implementations and is internal to the server. That method already
# changed once — it used to return HttpClientHandler and now returns
# HttpMessageHandler, which is why older patches fail with "Mod failed" on 4.9.x.
# A Harmony postfix whose __result parameter does not match simply never applies,
# and the plugin would install cleanly and silently route nothing.
#
# Usage:  ./build/verify-patch-target.sh [path-to-Emby.Server.Implementations.dll]
# Requires ilspycmd:  dotnet tool install -g ilspycmd --version 9.1.0.7988

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLL="${1:-$REPO_ROOT/lib/Emby.Server.Implementations.dll}"

TYPE="Emby.Server.Implementations.ApplicationHost"
METHOD="CreateHttpClientHandler"
# What Patch/HttpHandlerPatch.cs declares as `ref HttpMessageHandler __result`.
EXPECTED_RETURN="HttpMessageHandler"

if [ ! -f "$DLL" ]; then
  echo "error: $DLL not found. Run build/fetch-emby-refs.sh first." >&2
  exit 1
fi

if ! command -v ilspycmd >/dev/null 2>&1; then
  echo "error: ilspycmd is required." >&2
  echo "       dotnet tool install -g ilspycmd --version 9.1.0.7988" >&2
  exit 1
fi

echo "Checking $TYPE.$METHOD in $(basename "$DLL")..."

# ilspycmd writes a version-update notice to stdout, so its output is never
# empty — including when the type does not exist. Drop the chatter, then look
# for the declaration itself rather than treating "got some output" as success.
decompiled="$(ilspycmd -t "$TYPE" "$DLL" 2>/dev/null \
              | grep -vE '^(You are not using|Latest version is)' || true)"

SIMPLE_TYPE="${TYPE##*.}"
if ! grep -qE "(class|struct|interface) $SIMPLE_TYPE\b" <<<"$decompiled"; then
  echo "FAIL: $TYPE was not found in $(basename "$DLL")." >&2
  echo "      The type has been renamed, moved to another assembly, or removed;" >&2
  echo "      Patch/HttpHandlerPatch.cs resolves it by name and would find nothing." >&2
  exit 1
fi

# The declaration, not the call sites: match a line that has the method name
# followed by an opening parenthesis and a return type in front of it.
# The trailing '|| true' is load-bearing: under 'set -e' with pipefail, a grep
# that matches nothing would abort the script here, and the whole point of this
# check is to explain what changed rather than exit silently.
signature="$(grep -E "[A-Za-z0-9_.<>]+ $METHOD\(" <<<"$decompiled" | grep -v '=>' | head -1 | sed 's/^[[:space:]]*//' || true)"

if [ -z "$signature" ]; then
  echo "FAIL: $METHOD no longer exists on $TYPE." >&2
  echo "      The Harmony patch cannot apply; the plugin would route nothing." >&2
  exit 1
fi

echo "  found: $signature"

if ! grep -qE "(^|[[:space:]])$EXPECTED_RETURN $METHOD\(" <<<"$signature"; then
  echo "FAIL: $METHOD no longer returns $EXPECTED_RETURN." >&2
  echo "      Patch/HttpHandlerPatch.cs declares 'ref $EXPECTED_RETURN __result';" >&2
  echo "      a mismatched postfix never applies, and the plugin would install" >&2
  echo "      cleanly while silently routing nothing." >&2
  exit 1
fi

echo "OK: the patch target still returns $EXPECTED_RETURN."
