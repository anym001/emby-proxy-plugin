#!/usr/bin/env bash
#
# Prints the package entry for Emby's plugin catalog.
#
# Emby has no support for third-party plugin repositories: the catalog URL is
# compiled into Emby.Server.Implementations.Updates.InstallationManager
# (https://www.mb3admin.com/admin/service/EmbyPackages.json) and cannot be
# pointed anywhere else. Getting UI updates therefore means submitting this
# entry to Emby and having them host it. Nothing in this repository can do that
# on its own, which is why this is a generator rather than a committed file.
#
# It is a generator for a second reason: versionStr and checksum change with
# every release, and a checked-in copy of them would be wrong the moment it was
# written. Run this against the artifact that is actually being published.
#
# Usage:  ./build/catalog-entry.sh [tag]
# Default tag is v<version from the csproj>, which is what release.yml publishes.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/EmbyProxyRouter/EmbyProxyRouter.csproj"
DLL="$REPO_ROOT/src/EmbyProxyRouter/bin/Release/EmbyProxyRouter.dll"
REPO_URL="https://github.com/anym001/emby-proxy-plugin"

# Must match Plugin.Id. Emby matches an installed plugin to its catalog entry by
# name AND guid (InstallationManager.GetAvailablePluginUpdates), so a mismatch
# here does not fail loudly - it simply means no update is ever offered.
GUID="5f1c1b6e-9a3d-4d21-8f0a-2b7c6e4d91a3"
NAME="Proxy Router"
OWNER="anym001"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -1)"
[ -n "$VERSION" ] || { echo "error: no <Version> found in $CSPROJ" >&2; exit 1; }

# The assembly version is four-part because that is what Version defaults to,
# but the tags this repository publishes are three-part (git tag v1.0.0). Drop a
# trailing .0 so the generated sourceUrl points at a release that exists, and
# echo the result to stderr so a wrong guess is visible rather than silent.
case "$VERSION" in
  *.*.*.0) DEFAULT_TAG="v${VERSION%.0}" ;;
  *)       DEFAULT_TAG="v$VERSION" ;;
esac
TAG="${1:-$DEFAULT_TAG}"
echo "Generating the catalog entry for tag $TAG (plugin version $VERSION)." >&2

# The version this plugin is verified against becomes the minimum server version.
# Emby drops a package version whose requiredVersionStr is above the running
# server (InstallationManager.IsPackageVersionUpToDate), so an older server is
# never offered a build that was never tested against it.
REQUIRED="$(tr -d '[:space:]' < "$REPO_ROOT/build/emby-version.txt")"

if [ ! -f "$DLL" ]; then
  echo "error: $DLL not found." >&2
  echo "       Build it first: dotnet build -c Release $CSPROJ" >&2
  exit 1
fi

# Emby parses the checksum with new Guid(...) and compares it against the MD5 of
# the downloaded file, so it has to be the 32 hex digits and nothing else.
# BSD and GNU coreutils disagree on which tool provides that.
if command -v md5 >/dev/null 2>&1; then
  CHECKSUM="$(md5 -q "$DLL")"
elif command -v md5sum >/dev/null 2>&1; then
  CHECKSUM="$(md5sum "$DLL" | cut -d' ' -f1)"
else
  echo "error: neither 'md5' nor 'md5sum' is available." >&2
  exit 1
fi

cat <<EOF
{
  "name": "$NAME",
  "guid": "$GUID",
  "owner": "$OWNER",
  "category": "General",
  "type": "UserInstalled",
  "targetFilename": "EmbyProxyRouter.dll",
  "isPremium": false,
  "adult": false,
  "shortDescription": "Routes the Emby core's outbound HTTP(S) traffic through an HTTP, HTTPS or SOCKS5 proxy, and blocks it rather than leaking when the proxy is down.",
  "overview": "Routes outbound HTTP(S) traffic initiated by the Emby core - metadata providers, remote images, subtitle downloads - through an HTTP, HTTPS or SOCKS5 proxy. Fail-closed by default: if the proxy is unreachable, affected requests are aborted and logged instead of silently falling back to a direct connection. Private, loopback and link-local networks and Emby's own licensing servers are always contacted directly. Does not touch ffmpeg, DLNA, client connections or inbound traffic.",
  "richDescUrl": "$REPO_URL/blob/$TAG/README.md",
  "versions": [
    {
      "name": "$NAME",
      "guid": "$GUID",
      "versionStr": "$VERSION",
      "classification": "Release",
      "requiredVersionStr": "$REQUIRED",
      "targetFilename": "EmbyProxyRouter.dll",
      "sourceUrl": "$REPO_URL/releases/download/$TAG/EmbyProxyRouter.dll",
      "checksum": "$CHECKSUM",
      "infoUrl": "$REPO_URL/releases/tag/$TAG",
      "runtimes": "netcore",
      "description": "See the release notes at $REPO_URL/releases/tag/$TAG",
      "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    }
  ]
}
EOF
