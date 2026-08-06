# Emby Proxy Router

A minimal Emby Server plugin with exactly one job: route the outbound HTTP(S) traffic that the
**Emby server core itself** initiates through a configurable proxy — HTTP, HTTPS or **SOCKS5** —
while always contacting private networks and Emby's licensing servers directly.

Developed and verified against **Emby Server 4.9.5.0** (net8.0).

---

## What this plugin does

* Routes the Emby core's HTTP clients through a proxy: metadata providers (TMDB, TVDB, …),
  remote image providers, subtitle downloads.
* Supports **HTTP, HTTPS and SOCKS5 proxies**, selectable via dropdown or directly via URL scheme
  (`socks5://host:1080`).
* Checks proxy reachability (TCP connect plus an HTTP check through the proxy) and shows the result
  in the dashboard.
* **Fail-closed by default:** if the proxy is unreachable, affected requests are aborted and logged
  rather than silently falling back to a direct connection. Fail-open is available as a deliberate
  opt-in checkbox.
* Always routes RFC1918, loopback, link-local and Emby's licensing/Connect servers directly.

## What this plugin explicitly does NOT do

This is intentional — the point of the project is a single, auditable responsibility:

* **No** subtitle logic, no metadata enrichment, no image processing.
* **No** auto-update mechanism, no telemetry, no phone-home behaviour.
* **No** system-wide proxy configuration. Only Emby's own HTTP stack is redirected; `ffmpeg`, DLNA,
  client connections and everything else are untouched.
* **No** proxying of inbound connections. Reverse-proxy operation is a different problem.
* **No** circumvention of Emby's licence check. The licensing servers are on the bypass list on
  purpose.

---

## Verified groundwork (Emby 4.9.5.0)

The points below are not taken from documentation. They were established by decompiling the
official `emby-server-deb_4.9.5.0_amd64.deb` and by runtime tests on .NET 8. They explain why the
code looks the way it does.

### The patch target

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

### SOCKS5 feasibility

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

This is why the plugin takes an entered URL apart and moves the credentials into
`IWebProxy.Credentials`. The URL form remains accepted as input syntax, but would otherwise produce
a configuration that looks authenticated and is not.

### Why a dynamic `IWebProxy` instead of a `WebProxy`

Two properties force it:

* `CoreHttpClientManager` caches one `HttpClient` and its handler per
  `host + compression + userinfo + timeout` in a `ConcurrentDictionary` — with **no eviction**.
* `SocketsHttpHandler` freezes its properties after the first request; assigning `Proxy` later
  throws `InvalidOperationException`.

A statically assigned proxy would therefore be frozen until the server restarts. Because .NET calls
`GetProxy()` per request, changes to the address, the bypass list and the on/off switch take effect
immediately instead — even on long-cached handlers.

### Why a `DelegatingHandler` for fail-closed

An `IWebProxy` can only *choose a proxy* or return `null` — and `null` means "connect directly",
which is precisely the leak fail-closed is meant to prevent. The plugin therefore additionally wraps
the handler in a `DelegatingHandler` that can actively refuse a request. This is safe because
`CoreHttpClientManager` only ever passes the result to `new HttpClient(handler)` and never casts it.

---

## Building

Requirements: **.NET SDK 8.0** plus `curl`, `ar` and `tar`.

```bash
git clone <this-repo> emby-proxy-plugin
cd emby-proxy-plugin

# Fetches the Emby assemblies (~180 MB download, only 5 DLLs are kept).
./build/fetch-emby-refs.sh

dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
```

Result: `src/EmbyProxyRouter/bin/Release/EmbyProxyRouter.dll` — a **single** file. Harmony is
included as an embedded resource and loaded at runtime, so no `0Harmony.dll` needs to be copied
alongside it.

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

### Continuous integration

Two workflows, with different jobs.

**`build.yml`** does the same steps as a local build on a runner, then verifies the patch target and
that the output is still a single self-contained DLL — no `0Harmony.dll` next to it, not suspiciously
small — because that property is what makes deployment a one-file copy and would otherwise break
silently. The DLL is uploaded as a build artifact, so a plugin binary can be produced without a local
.NET SDK.

It runs on every pull request against `main`, and can be started by hand with an Emby version as
input. Given none, it uses the pinned `build/emby-version.txt`. The version is also the cache key for
the fetched assemblies, so only the first run per version pays for the ~180 MB download.

**`release-check.yml`** answers the question the pull-request build cannot: *does a newer Emby Server
release break the plugin?* It reads the Emby release list, compares the newest release against the
pinned version, and — if a newer one exists — dispatches `build.yml` against it. That build fetches
that version's assemblies and re-runs the patch-target check.

The two lines Emby publishes in parallel (stable `4.9.x` and beta `4.10.0.x`) are separated by the
`prerelease` flag rather than by version order, because taking the newest tag would silently track
betas. The flags found are printed to the job summary, so the decision is visible rather than assumed.
Betas can be checked deliberately with the `include-prerelease` input.

A green run means the version can be adopted by bumping `build/emby-version.txt`. A red one means the
plugin needs attention before it can claim to support that version — which is exactly the failure
that goes unnoticed otherwise, because a non-matching Harmony patch fails silently rather than loudly.

Both workflows are `workflow_dispatch`; GitHub only offers the *Run workflow* button for workflows
present on the default branch, so manual runs work once they are on `main`. `release-check.yml` has a
weekly `schedule` prepared but commented out.

---

## Installation (Unraid / Docker)

The Emby container usually maps a host directory to `/config`. The plugin DLL belongs in its
`plugins` subfolder.

```bash
# On the Unraid host; adjust the path if needed:
PLUGINS=/mnt/user/appdata/emby/plugins

cp EmbyProxyRouter.dll "$PLUGINS/"
chown 99:100 "$PLUGINS/EmbyProxyRouter.dll"
chmod 644   "$PLUGINS/EmbyProxyRouter.dll"

docker restart emby
```

`99:100` is `nobody:users` on Unraid — the same UID/GID the Emby container runs as. If the ownership
is wrong, Emby ignores the file without comment.

Then configure it in the dashboard under **Plugins → Proxy Router**.

### Confirming the patch took effect

The settings page shows a **Patch status** line at the top. If it says anything other than "Active",
**no** traffic is being redirected, and the reason is stated right next to it. In the server log:

```
Harmony patch active on HttpMessageHandler ApplicationHost.CreateHttpClientHandler(HttpMessageHandlerOptions) (Emby.Server.Implementations 4.9.5.0).
Proxy Router: enabled - socks5://192.168.1.10:1080 (auth as user) | fail-closed | check interval 60 s
Proxy status: REACHABLE - HTTP check via socks5://192.168.1.10:1080 (auth as user) succeeded (...)
```

---

## Configuration

| Field | Meaning |
| --- | --- |
| **Enable proxy** | Off = Emby behaves as if the plugin were not installed. |
| **Proxy scheme** | `Http`, `Https` or `Socks5`. Only used when the address carries no scheme of its own. |
| **Proxy address** | `host:port` (e.g. `192.168.1.10:8080`) or a full URL (e.g. `socks5://192.168.1.10:1080`). A port is mandatory. |
| **Username / Password** | Optional. Take precedence over credentials embedded in the URL. |
| **Ignore certificate validation** | For HTTPS proxies using a self-signed certificate. |
| **Connect directly when the proxy is unavailable** | Off (default) = fail-closed. On = fail-open. |
| **Bypass list** | One entry per line: CIDR, single IP, hostname or `*.example.com`. |
| **Check URLs** | Fetched through the proxy; the first HTTP 2xx response counts as reachable. Empty = TCP check only. |
| **Check interval** | Seconds between reachability checks, minimum 10. |

### Languages

The settings page follows the server's display language. **There is no language setting in the
plugin** — that is deliberate: a second, independent language switch is not how Emby plugins behave,
and it would let the plugin page disagree with the rest of the dashboard.

Emby applies the configured display language process-wide, in
`Emby.Server.Implementations.ApplicationHost.SetDefaultThreadCulture`:

```csharp
string uICulture = ServerConfigurationManager.Configuration.UICulture;
...
CultureInfo.DefaultThreadCurrentUICulture = (CultureInfo.CurrentUICulture = cultureInfo);
```

That method is called from the host constructor **and** from `OnConfigurationUpdated`, and the server
installs no per-request localization middleware. So `CultureInfo.CurrentUICulture` is exactly the
dashboard language, and reading it at lookup time picks up a change without a server restart.

Translations live in `src/EmbyProxyRouter/Localization/*.json` and are embedded into the DLL at build
time — there are no loose files to deploy. `en.json` is the reference language: any key missing from
another file falls back to English key by key, so an incomplete translation degrades to mixed
language rather than to blank labels. A language Emby offers but the plugin does not ship shows
English.

Culture names are matched exactly first, then by their neutral part — `de-AT` uses `de.json`, while a
`pt-BR.json` added later would win over `pt.json` for Brazilian Portuguese.

Labels and descriptions are wired through Emby's own localization attributes
(`[DisplayNameL(nameof(Strings.X), typeof(Strings))]`) rather than literal strings. The attribute
caches the reflected `PropertyInfo` but re-invokes the getter on every read, which is what makes the
page follow the language live.

**Adding a language:**

1. Copy `en.json` to `<code>.json` in `src/EmbyProxyRouter/Localization/` and translate the values.
   Leave the keys untouched. Use the code Emby uses for that language (`fr`, `zh-CN`, `pt-BR`, …).
2. Rebuild. The file is embedded and picked up automatically; neither the `.csproj` nor any C# code
   needs to change.

Log messages keep their English prefixes so log lines stay greppable and comparable across
installations, but detail text embedded in them is localized, because the same text is shown in the
dashboard.

### Fail-closed vs. fail-open

**Fail-closed (default).** If the proxy is unreachable — or not yet checked, or misconfigured —
affected requests fail. Every case is logged as a warning:

```
WARN  Proxy unreachable - request blocked: https://api.themoviedb.org (proxy is unreachable). Fail-closed is active; ...
```

Metadata lookups will fail for as long as the proxy is gone. That is the price of guaranteeing that
nothing slips past the proxy unnoticed.

**Fail-open (opt-in).** Requests go out directly without the proxy — but **never silently**:

```
WARN  Fail-open active - request is going out DIRECTLY, without the proxy: https://api.themoviedb.org (proxy is unreachable)
```

The active policy is shown as its own status line at the top of the settings page.

Log messages deliberately contain only scheme, host and port — never the path. Paths and query
strings of metadata lookups carry title information and frequently API keys.

### Default bypass list

RFC1918, loopback and link-local, plus Emby's own endpoints. The latter are not guesswork; they were
read out of the 4.9.5.0 assemblies:

* `mb3admin.com` — `PluginSecurityManager`: `/admin/service/registration/validate` and
  `/admin/service/appstore/register`; plus the plugin catalogue in `InstallationManager`
  (`www.mb3admin.com/admin/service/package/...`).
* `connect.emby.media` — `Emby.Server.Connect`: `https://connect.emby.media/service/`.

Sending licence traffic through a proxy under a fail-closed policy risks breaking Emby Premiere
activation, and obscuring licence identity is not what this plugin is for. Remove the lines if you
disagree.

---

## Known limitations

* **Live TV is only partially covered.** `Emby.LiveTV.dll` uses both the central `IHttpClient`
  (which is redirected) and its own `HttpClientHandler` instances (which are **not**). If you use
  Live TV, do not assume that traffic goes through the proxy in full. Special handling for it is
  deliberately not built in.
* **The bypass list performs no DNS resolution.** Hostnames are matched literally and IP rules only
  apply to IP literals. Resolving would emit a DNS lookup for every request — exactly the visibility
  the plugin exists to avoid.
* **HTTP(S) proxy authentication is reactive.** .NET sends credentials only after the proxy answers
  `407`, not pre-emptively. Proxies that reject outright without issuing a challenge will not work.
* **"Ignore certificate validation" is broad.** The option disables TLS validation both for the
  connection to the proxy *and* for the destination connections tunnelled through it. Only enable it
  when the proxy uses a self-signed certificate.
* **Credentials are stored in plain text.** Emby persists plugin options as JSON under
  `/config/plugins/configurations/`. The password field is masked in the UI, not in the file.
* **Bound to an internal Emby method.** `CreateHttpClientHandler` is not public API. An Emby update
  can change it at any time. The plugin verifies the signature at startup and reports a mismatch
  loudly instead of silently doing nothing — but it cannot repair one.
* **Requests before the first health check.** Under fail-closed, requests are blocked until the
  first check completes. That is intended: unconfirmed proxy availability is not a reason to let
  traffic through.

---

## Project layout

```
.github/workflows/build.yml         Build + patch-target check (pull requests, manual)
.github/workflows/release-check.yml Finds newer Emby releases, dispatches a build
build/emby-version.txt              The pinned Emby version (single source of truth)
build/fetch-emby-refs.sh            Fetches the Emby assemblies
build/verify-patch-target.sh        Asserts the patched method still matches
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

## Licence

GPL-3.0 — see [LICENSE](LICENSE).

The operating principle (a Harmony postfix on Emby's internal handler factory) is inspired by
[StrmAssistant](https://github.com/sjtuross/StrmAssistant) (GPL-3.0). The code here was written
independently; the licence is adopted out of respect for that origin.
