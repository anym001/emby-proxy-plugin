# Architecture

Why the plugin is built the way it is. The [README](README.md) covers installation and use; this
file is for anyone reading or changing the code.

Everything below was established against **Emby Server 4.9.5.0** (net8.0) by decompiling the
official `emby-server-deb_4.9.5.0_amd64.deb` and by runtime tests on .NET 8 — not from
documentation. Each point is load-bearing: re-verify before simplifying one away.

## The patch target

```csharp
// Emby.Server.Implementations.ApplicationHost
protected virtual HttpMessageHandler CreateHttpClientHandler(HttpMessageHandlerOptions options)
{
    SocketsHttpHandler socketsHttpHandler = new SocketsHttpHandler { ActivityHeadersPropagator = null };
    ...
    return socketsHttpHandler;
}
```

* The **return type is `HttpMessageHandler`**, not `HttpClientHandler`. Older patches against this
  method declared `ref HttpClientHandler __result` — that no longer matches, and is the likely cause
  of "Mod failed" reports on 4.9.x.
* The concrete host class `EmbyServer.CoreAppHost` is `sealed` and does **not** override the method;
  the name appears in no other assembly in the install. Patching the base declaration is therefore
  sufficient.
* Target framework per `EmbyServer.runtimeconfig.json`: **net8.0**, self-contained on .NET 8.0.25.

## SOCKS5 feasibility

`WebProxy`/`HttpClientHandler` cannot speak SOCKS at all. `SocketsHttpHandler` can, from .NET 6
onwards — and that is exactly what Emby 4.9.5.0 returns. Verified empirically against a real SOCKS5
server:

| Behaviour | Result |
| --- | --- |
| `socks5://` via a custom `IWebProxy` on `SocketsHttpHandler` | works |
| `GetProxy()` is called **per request** | yes — the basis for reconfiguration without a restart |
| Credentials via `IWebProxy.Credentials` | works |
| Credentials as `socks5://user:pass@host:port` | **ignored** — .NET then offers only "no authentication" |
| Hostname resolution | sent as ATYP=3 to the proxy (remote DNS, no DNS leak) |

This is why `ProxyEndpoint.TryParse` takes an entered URL apart and moves the credentials into
`IWebProxy.Credentials`. The URL form remains accepted as input syntax, but would otherwise produce
a configuration that looks authenticated and is not.

## Why a dynamic `IWebProxy` instead of a `WebProxy`

Two properties force it:

* `CoreHttpClientManager` caches one `HttpClient` and its handler per
  `host + compression + userinfo + timeout` in a `ConcurrentDictionary` — with **no eviction**.
* `SocketsHttpHandler` freezes its properties after the first request; assigning `Proxy` later
  throws `InvalidOperationException`.

A statically assigned proxy would therefore be frozen until the server restarts. Because .NET calls
`GetProxy()` per request, changes to the address, the bypass list and the on/off switch take effect
immediately instead — even on long-cached handlers.

## Why a `DelegatingHandler` for fail-closed

An `IWebProxy` can only *choose a proxy* or return `null` — and `null` means "connect directly",
which is precisely the leak fail-closed is meant to prevent. The plugin therefore additionally wraps
the handler in a `DelegatingHandler` that can actively refuse a request. This is safe because
`CoreHttpClientManager` only ever passes the result to `new HttpClient(handler)` and never casts it.

`ProxyState.Decide` is the single routing authority behind both: the proxy and the gate must never
reach different verdicts for the same destination.

## Localization

The settings page follows the server's display language, and the plugin has **no language setting of
its own**. Emby applies the configured display language process-wide, in
`Emby.Server.Implementations.ApplicationHost.SetDefaultThreadCulture`:

```csharp
string uICulture = ServerConfigurationManager.Configuration.UICulture;
...
CultureInfo.DefaultThreadCurrentUICulture = (CultureInfo.CurrentUICulture = cultureInfo);
```

That method is called from the host constructor **and** from `OnConfigurationUpdated`, and the server
installs no per-request localization middleware. So `CultureInfo.CurrentUICulture` is exactly the
dashboard language, and `Localizer` reading it at lookup time picks up a change without a restart. A
second, independent switch would let the plugin page disagree with the rest of the dashboard.

Translations live in `src/EmbyProxyRouter/Localization/*.json` and are embedded into the DLL at build
time. `en.json` is the reference: any key missing from another file falls back to English key by key,
so an incomplete translation degrades to mixed language rather than to blank labels. Culture names
are matched exactly first, then by their neutral part — `de-AT` uses `de.json`, while a `pt-BR.json`
added later would win over `pt.json` for Brazilian Portuguese.

Labels and descriptions are wired through Emby's own localization attributes
(`[DisplayNameL(nameof(Strings.X), typeof(Strings))]`) rather than literal strings. The attribute
resolves through `LocalizableString`, which requires a **public static string property with a
getter**; it caches the reflected `PropertyInfo` but re-invokes the getter on every read, which is
the other half of what makes a live language change work.

Log message *prefixes* stay English so log lines remain greppable across installations, but embedded
detail strings are localized, because the same text is shown in the dashboard.

## The default bypass list

RFC1918, loopback and link-local, plus Emby's own endpoints. The latter are not guesswork; they were
read out of the 4.9.5.0 assemblies:

* `mb3admin.com` — `PluginSecurityManager`: `/admin/service/registration/validate` and
  `/admin/service/appstore/register`; plus the plugin catalogue in `InstallationManager`
  (`www.mb3admin.com/admin/service/package/...`).
* `connect.emby.media` — `Emby.Server.Connect`: `https://connect.emby.media/service/`.

Sending licence traffic through a proxy under a fail-closed policy risks breaking Emby Premiere
activation, and obscuring licence identity is not what this plugin is for.

## Building

Requirements: **.NET SDK 8.0** plus `curl`, `ar` and `tar`.

```bash
./build/fetch-emby-refs.sh                                       # once, populates lib/
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/verify-single-dll.sh
```

Result: `src/EmbyProxyRouter/bin/Release/EmbyProxyRouter.dll` — a **single** file. Harmony is
included as an embedded resource and loaded at runtime, so no `0Harmony.dll` is copied alongside it.
`verify-single-dll.sh` asserts exactly that, and CI and the release workflow run the same script.

### Why the reference DLLs are not in the repository

They are proprietary Emby binaries; redistributing them is not ours to do. On top of that, there is
no matching NuGet package for 4.9.5.0 anyway: `MediaBrowser.Server.Core` stops at 4.9.1.90, and
`Emby.Web.GenericEdit` — required for the settings page — is not on NuGet at all. The script fetches
only what is needed from the official release: four assemblies the plugin compiles against, plus
`Emby.Server.Implementations.dll`, which is not referenced but is the assembly the plugin patches —
keeping it allows the patch target to be verified without a second 180 MB download.

The version is pinned in `build/emby-version.txt` — one file, read by both the fetch script and CI.
For a different Emby version:

```bash
FORCE=1 ./build/fetch-emby-refs.sh 4.9.6.0
```

### Verifying the patch target

```bash
dotnet tool install -g ilspycmd --version 9.1.0.7988
./build/verify-patch-target.sh
```

This is the check that matters when moving to a new Emby version, and compiling is not a substitute
for it. The four referenced API assemblies rarely change, while the patched method is internal to the
server and **has** changed before — it used to return `HttpClientHandler` and now returns
`HttpMessageHandler`. A Harmony postfix whose `__result` parameter no longer matches simply never
applies: the plugin would install cleanly, show no error, and silently route nothing.

The script reports which part changed — the type, the method, or its return type.

## Continuous integration

Three workflows plus Dependabot, split along one line: **verifying a change and shipping a
deliverable are separate events.** Pull requests are verified; only a tag ships.

### `ci.yml` — every pull request

There is no test project in this repository, so this workflow is what stands in for one. It is not
"the build" — it never publishes anything — it is the set of assertions that a change is sound:

* `actionlint` on the workflows. A typo in an expression or a step key is caught in seconds instead
  of by a run that took minutes to get there.
* Compiling against the real Emby assemblies. Not much of a test on its own, but the thing that
  catches a change which simply does not build.
* `build/verify-patch-target.sh` — the assertion that actually matters (see above).
* `build/verify-single-dll.sh` — the output is still one self-contained file: no `0Harmony.dll` next
  to it, not suspiciously small. That property is what makes deployment a one-file copy, and it
  would otherwise break silently.

The last two are scripts rather than inline steps because `release.yml` runs the same ones. A
release therefore cannot ship an output that a pull request would have rejected.

It also runs on manual dispatch with an Emby version as input; given none, it uses the pinned
`build/emby-version.txt`. The version is the cache key for the fetched assemblies, so only the first
run per version pays for the ~180 MB download.

**Only a manual run uploads a DLL**, named `EmbyProxyRouter-emby<version>-<commit>`. That is the
`release-check.yml` case: a candidate against a *new* Emby version, worth having in hand to try
against a real server. Pull requests skip the upload — they are asking whether the change is sound,
not producing something to install.

The commit in that name is the one that was pushed, not `github.sha`. On a pull request `github.sha`
is the throwaway merge commit GitHub creates for the run: it belongs to no branch and cannot be
resolved once the run is over. The checks still run against that merge, because testing the merge
result is the point; only the label points at a commit that exists.

### `release.yml` — tags matching `v*`, or a published release

The only workflow that produces something a user installs. It builds against the pinned Emby version
— deliberately with no override input, because the DLL is handed to users and the version it was
verified against has to be the one the repository claims to support — and attaches
`EmbyProxyRouter.dll` to a GitHub Release.

It repeats CI's verification instead of trusting that a pull request ran it. A tag can be placed on
any commit, including one that never went through a pull request, and shipping a plugin whose Harmony
patch no longer matches is the one failure this project cannot afford: it is silent. Publishing uses
the preinstalled `gh` CLI rather than a third-party action, so it adds no supply chain of its own.

Cutting a release is one command:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

The workflow also triggers on `release: published`, because publishing a release through the GitHub
UI (or the API) with a tag name that doesn't exist yet creates that tag through the Releases endpoint
rather than a real push — that path emits a `release` event but never a `push` event, so `push: tags`
alone never sees it and the tag is created with nothing built for it. On a `release` event
`GITHUB_SHA`/`GITHUB_REF_NAME` resolve to the default branch tip, not the tag, so the checkout step
and the tag-name resolution both read `github.event.release.tag_name` explicitly instead of trusting
the ambient ref. Publishing a pre-existing tag as a release this way triggers both events for the same
tag; the existing-release fallback below absorbs the duplicate run rather than failing.

If a release already exists for the tag — drafted in the UI beforehand, or the workflow re-run — the
DLL is uploaded to it instead of the run failing.

### `release-check.yml` — manual, weekly schedule prepared

Answers the question a pull request cannot: *does a newer Emby Server release break the plugin?* It
reads the Emby release list, compares the newest release against the pinned version, and — if a newer
one exists — dispatches `ci.yml` against it. That run fetches that version's assemblies, re-runs the
patch-target check, and leaves a candidate DLL as an artifact.

The two lines Emby publishes in parallel (stable `4.9.x` and beta `4.10.0.x`) are separated by the
`prerelease` flag rather than by version order, because taking the newest tag would silently track
betas. The flags found are printed to the job summary, so the decision is visible rather than assumed.
Betas can be checked deliberately with the `include-prerelease` input.

A green run means the version can be adopted by bumping `build/emby-version.txt`. A red one means the
plugin needs attention before it can claim to support that version — which is exactly the failure
that goes unnoticed otherwise, because a non-matching Harmony patch fails silently rather than loudly.

Both this and `ci.yml` are `workflow_dispatch`; GitHub only offers the *Run workflow* button for
workflows present on the default branch. `release-check.yml` has a weekly `schedule` prepared but
commented out.

### Pinning and Dependabot

Every action is pinned to a **commit SHA** with the version in a trailing comment, not to a floating
tag: a tag can be moved to point at different code, a SHA cannot. That is also what makes the grouped
Dependabot pull request worth having — it is the mechanism that keeps the pins current.

`.github/dependabot.yml` covers the two dependency surfaces this repository actually has. The
workflow actions are grouped into a single monthly pull request — they move together, and separate
pull requests would conflict on the same pinned-SHA lines. `Lib.Harmony`, the only NuGet dependency,
gets its own. Both go through `ci.yml` like any other change, which matters most for Harmony: it is
embedded into the plugin DLL and patches the CLR at runtime, so a bump is verified by the
patch-target check before it is taken rather than after.

## Distribution and the Emby plugin catalog

The deliverable is a DLL copied into `/config/plugins`. Emby *can* update a plugin from its dashboard,
but only through one channel, and it is worth knowing exactly how narrow that channel is before
relying on it.

`InstallationManager` reads the catalog from `https://www.mb3admin.com/admin/service/EmbyPackages.json`
— a **compiled-in constant**. Emby has no equivalent of Jellyfin's custom repository URLs: there is no
setting, no config file and no API for a second source. Appearing in the dashboard therefore means
being accepted into Emby's own catalog; there is no self-hosted alternative.

`PluginUpdateTask` (`Key = "PluginUpdates"`, hidden) runs on startup and every 24 h — every 3 h when
the server's update level is Beta. For each loaded plugin it looks the catalog up by **name and
`Plugin.Id`**, takes the highest version whose `requiredVersionStr` is at or below the running server,
and installs it if it exceeds the assembly version. Note that its own description claims it only
touches "plugins that are configured to update automatically"; in 4.9.5.0 there is no such filter in
the code path.

Installation downloads `sourceUrl`, verifies `checksum` as the MD5 of the file when one is given, and
writes it to `PluginsPath/targetFilename`. `sourceUrl` is an arbitrary URL, so Emby would host the
metadata while the DLL keeps coming from this repository's releases.

`build/catalog-entry.sh` prints the entry to submit:

```bash
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/catalog-entry.sh v1.0.0 > catalog-entry.json
```

It is a generator rather than a committed file because `versionStr`, `checksum` and `sourceUrl` change
with every release, and a stored copy would be stale the moment it was written. Three fields in its
output are Emby's to confirm on submission rather than facts read out of the assemblies: `id` is
assigned by them and is omitted, `type` is compared as a free-form string against whatever the
dashboard requests, and `classification` is emitted as `"Release"` because the client-side enum is
`Release | Beta | Dev`.

Two consequences worth weighing before pursuing this. The update task is unconditional, so accepting a
catalog entry means the plugin replaces its own binary on a schedule, driven by a record held by a
third party — for a plugin whose purpose is control over outbound traffic, that is a real trust
surface. And `mb3admin.com` is in `BypassRules.Always`, so the update check deliberately does *not*
go through the proxy and keeps working under fail-closed.

## Project layout

```
.github/workflows/ci.yml            actionlint + compile + the two verify scripts (pull requests)
.github/workflows/release.yml       Builds and publishes the DLL to a GitHub Release (tags v*)
.github/workflows/release-check.yml Finds newer Emby releases, dispatches a CI run against them
.github/dependabot.yml              Updates for the workflow actions and Lib.Harmony
.github/ISSUE_TEMPLATE/             Bug report, feature request, private security link
.github/PULL_REQUEST_TEMPLATE.md    Checklist covering the traps in CONTRIBUTING.md
ARCHITECTURE.md                     This file
CONTRIBUTING.md                     How to build, verify and submit a change
build/emby-version.txt              The pinned Emby version (single source of truth)
build/fetch-emby-refs.sh            Fetches the Emby assemblies
build/verify-patch-target.sh        Asserts the patched method still matches
build/verify-single-dll.sh          Asserts the output is still one self-contained file
build/catalog-entry.sh              Generates the package entry for Emby's plugin catalog
lib/                                Target folder for the assemblies (not committed)
src/EmbyProxyRouter/
  Plugin.cs                   Entry point, dashboard status, server entry point
  PluginOptions.cs            Settings page (Emby.Web.GenericEdit)
  Localization/en.json        Reference language (every key must exist here)
  Localization/de.json        German translation
  Localization/Localizer.cs   Language resolution and JSON lookup
  Localization/Strings.cs     Static properties consumed by Emby's localization attributes
  Patch/HarmonyLoader.cs      Loads the embedded Harmony assembly
  Patch/HttpHandlerPatch.cs   The postfix patch, including signature verification
  Proxy/ProxyEndpoint.cs      Address parsing, credential relocation
  Proxy/BypassRules.cs        CIDR and host matching
  Proxy/ProxySettings.cs      Immutable configuration snapshot
  Proxy/ProxyState.cs         Routing decision, in one place
  Proxy/DynamicWebProxy.cs    IWebProxy, consulted per request
  Proxy/ProxyGateHandler.cs   Fail-closed enforcement and logging
  Proxy/ProxyHealthChecker.cs Reachability checking
  Proxy/ProxyRuntime.cs       Holds the singletons together
```
