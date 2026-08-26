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

## The plugin shell

Two things about `Plugin.cs` are load-bearing rather than incidental.

**The patch is applied from the constructor, not from the server entry point.** Emby builds handlers
lazily and then caches them per host for the life of the process, so any handler built before the
patch lands would keep bypassing the proxy until the server restarts. The entry point runs late
enough for that to happen, and the failure is invisible: traffic on those handlers simply never
reaches the proxy while the dashboard reports the patch as active.

**The constructor must not throw.** A plugin whose constructor throws is removed from the dashboard
entirely, which leaves no way to see the error that caused it — the one failure mode with no route
back to the user. Everything the constructor does is therefore inside a `try`, including resolving
the log manager: that is the one step whose failure would otherwise throw before there is anything
left to report the throw with.

### Working against `Emby.Web.GenericEdit`

The settings page is an `EditableObjectBase`, and three of its requirements are compile-time traps
rather than anything the API documents:

* Overriding `Validate` requires `protected override`, not `protected internal`.
* `EditMultilineAttribute` takes a required line count — `[EditMultiline(6)]`, never `[EditMultiline]`.
* Attribute-resolved labels need a public static property on `Strings`; see Localization below.

One more is a C# constraint that shapes an API here: `async` methods cannot take `out` parameters, so
anything asynchronous that would naturally return two values returns a small result struct instead.
`ProbeResult` exists for exactly that reason.

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

## Why there is no reachability state

A configured proxy is used, and if it cannot be reached the request fails. .NET never falls back to
a direct connection on its own, so naming the proxy in `GetProxy` *is* the enforcement — nothing has
to decide whether the proxy is up.

That is worth stating because the alternative is seductive and expensive. Knowing in advance that
the proxy is down is only useful in order to *stop* using it, which this plugin never does. Wanting
that answer is what forces a poller, a check URL, a reachability state feeding routing, and a startup
window in which the verdict is not yet in — and it makes routing depend on whatever host the check
URL points at. Handing a proxy address to `curl` or to a browser behaves the same way: the proxy is
used because it was configured, and a request through one that is down fails rather than quietly
going out directly.

## Why a `DelegatingHandler` is still needed

An `IWebProxy` can only *choose a proxy* or return `null`, and `null` means "connect directly" — so
it cannot refuse. The plugin therefore wraps the handler in a `DelegatingHandler` that can. This is
safe because `CoreHttpClientManager` only ever passes the result to `new HttpClient(handler)` and
never casts it.

Two cases need it, and both are the same leak reached from opposite ends: the routing verdict cannot
be carried out, and handing the request on would send it in the clear.

* **The proxy is switched on and its address does not parse.** There is no URI to name, so the
  resolver has nothing to return but `null`. This is the case the gate was built for.
* **The inner handler never received the proxy.** Configuring it threw, or Emby returned a handler
  type nothing here can attach a proxy to. The verdict is `ViaProxy` and the address is perfectly
  good — but this particular handler would send the request straight out.

Neither is a judgement and neither is a mode. Both are facts settled before the request arrives: the
first when the configuration was parsed, the second when the handler was built. `ProxyState.Decide`
remains the only thing that decides *where* a request goes; the gate asks the narrower question of
whether the handler it wraps can carry that out, and `ProxyGateHandler.Refusal` is where the two
answers meet.

The handler also bounds `ConnectTimeout`, which .NET leaves infinite: once every request goes to a
single proxy, one that drops packets rather than refusing them hangs each of them for the full
`HttpClient.Timeout`.

`ProxyState.Decide` is the single routing authority behind both: the proxy and the gate must never
reach different verdicts for the same destination. Callers that need the verdict *and* the settings
it rests on pass one snapshot into `Decide` rather than reading `ProxyState.Settings` again around
the call — two reads can straddle a configuration change and apply one snapshot's verdict to
another's endpoint.

`Decide` hands back a `RouteReason` code, not a message. It runs up to twice per outbound request,
and the caller that only wants the verdict — the resolver's bypass check — would otherwise pay for a
culture lookup and a dictionary read it discards. `ProxyState.Explain` turns a reason into text at
the point a line is actually written, which also keeps the routing core independent of the
localization layer. The reason itself is not optional: a blocked request is entitled to say why,
and "the address does not parse" is the one failure that would otherwise surface as nothing at all.

### Why the warnings are throttled

The gate writes a line per blocked request, which is right for one request and wrong
for a library scan — a few thousand lookups against a dead proxy bury the first line, the one that
mattered, under a few thousand identical ones. `LogThrottle` collapses them to one line per
destination and reason per minute, shared across every gate instance because Emby caches a handler
per host and a per-instance throttle would see one destination each.

It is deliberately biased towards logging, because "never silently" is the property the plugin is
built around and it would be perverse to break it in the logging: the first sighting of a key is
never suppressed, suppressed occurrences are counted and reported on the next line the key produces,
and a full key map logs untracked rather than dropping. Throttling never applies to enforcement —
every blocked request still fails, and still carries its own explanation to its caller.

The gate is also why `Decorate` separates configuring the inner handler from wrapping it. Assigning
`Proxy` to a `SocketsHttpHandler` that has already served a request throws, and letting that failure
skip the wrap would hand Emby a bare handler with neither a proxy nor a gate, every request on it
going out in the clear — the one outcome the plugin exists to prevent. So the gate goes on either way and
the failure is surfaced as a third patch state on the settings page, between "Active" and "NOT
active".

`Decorate` also tells the gate *which* handler it wrapped, via the `proxyAttached` constructor
argument. Wrapping alone is not enough: a gate that only knew about the unparseable-address verdict
would pass these requests through to a handler with no proxy on it, and the degraded status on the
settings page would be claiming a protection that was not there. The flag has no default, because
getting it wrong is silent.

## Certificate validation

`SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback` is what the "ignore certificate
validation" option acts on, and two constraints shape how it is installed.

It must be **read per callback, not captured**. The handler's properties freeze after its first
request, so a value snapshotted at decoration time could never be revised and toggling the option
would need a server restart — the same reason `DynamicWebProxy` exists.

It must **not simply overwrite what is already there**. Emby may install a callback of its own;
replacing it outright would silently drop the server's own policy. The plugin therefore captures the
existing callback and delegates to it for everything it does not have an opinion about, falling back
to `errors == SslPolicyErrors.None` when there is none.

The relaxation itself is gated on the proxy being enabled *and* its address parsing, because the
option exists for proxies presenting a self-signed certificate: with no proxy in play there is
nothing for it to excuse, and a disabled plugin has to leave Emby's TLS behaviour untouched.

What this still cannot do is distinguish connections. The callback receives the handshake, not the
request that triggered it, so while the option is in effect it also covers destinations that go out
directly because they are on the bypass list. Narrowing that
would mean correlating a handshake back to a request through the connection pool, which is not a
mechanism worth introducing for an option that is off by default. It is documented in README.md
under "Known limitations" instead.

## The settings-page check

`ProxyProbe` answers one question — is this address really a working proxy? — on demand, from
`OnBeforeShowUI` and `OnOptionsSaved`, and never on a timer. Nothing in `ProxyState` consults it; a
failure here does not move a single request.

It speaks to a raw `TcpClient` rather than going through `HttpClient`, which is not an optimisation:
`HttpClient` here means the patched pipeline, so the probe would be checking the proxy by way of the
proxy — and would fail whenever the gate refuses, which is precisely the configuration it exists to
diagnose.

It talks to the proxy and to nobody else, stopping before the point where the proxy would open an
outbound connection:

| Scheme | What is established | How far it goes |
| --- | --- | --- |
| `socks5` | it speaks SOCKS5, and it accepted the credentials | greeting + RFC1929 sub-negotiation |
| `https` | it completes a TLS handshake | `SslStream.AuthenticateAsClientAsync` |
| `http` | something accepts connections on that port | TCP connect |

Both SOCKS5 methods are offered, the way .NET's own client does, so that the reply says which one
the proxy chose. A proxy answering "no authentication" to a configuration that carries a username
yields a setup that looks authenticated and is not — the same trap that makes `ProxyEndpoint` move
credentials out of the URI — and that is reported as a distinct warning verdict rather than as plain
success. Offering only the authenticated method would have hidden it behind a bare rejection.

The honest limit: none of this proves the proxy forwards traffic. A SOCKS5 server that authenticates
and then refuses every `CONNECT` still probes Ok. Establishing more means sending something through
to a destination, and any destination shows that party the proxy's egress address and makes the
verdict depend on its uptime. Reaching the proxy and being accepted by it is what can be established
locally, so that is what is claimed and no more.

## The default bypass list

Two constants, split by whether the user may switch them off.

`BypassRules.Always` is loopback only — `127.0.0.0/8`, `::1`, `localhost` — and is merged
unconditionally. A proxy elsewhere has no route back to this machine, so a request to the server's
own loopback address cannot succeed through it under any configuration; a switch for that would only
have a wrong position and a right one. .NET's own `WebProxy` draws the line in the same place:
`IsBypassed` returns true for a loopback host before it consults `BypassProxyOnLocal` or the bypass
list.

`BypassRules.PrivateNetworks` — RFC1918, `169.254.0.0/16`, `fc00::/7`, `fe80::/10`, `*.local` — is
merged only when `PluginOptions.BypassPrivateNetworks` is set, which by default it is not. The
default is that everything goes through the proxy, which is the answer needing no qualification for
a plugin whose job is to route outbound traffic. Switching it on is for a server that also talks to
its own LAN and does not want that traffic crossing a remote proxy, or dying with it.

The default has a cost worth naming: an unreachable proxy takes the LAN with it, so another Emby
server, a DLNA endpoint or a local metadata cache becomes unreachable along with the internet.

`Parse` takes the flag as a required parameter rather than an optional one, for the same reason the
fixed entries are merged inside it: a caller that can omit the question is a caller that will
eventually answer it by accident.

Both constants are address ranges plus mDNS, and deliberately nothing else. A rule for dotless
hostnames (`nas`, `router`) existed briefly and was removed: it was the only compiled-in rule
matching on the *shape* of a name rather than on an allocated range, and shape is a weaker
justification than it looks — a dotless name is unroutable on the public internet by convention,
where the ranges are unroutable by allocation. Mixing the two made the fixed set harder to state
than "these ranges, plus mDNS". Anyone reaching a NAS by short name writes `nas` in the bypass list,
where an exact host entry matches it, and the decision stays visible in the configuration.

A trailing dot is folded away before any matching, for the same reason the IPv4-mapped IPv6 form is:
a destination must not change route because of how it happened to be spelled.

So everything matched **by name** rather than by address range lives in the user's list, which
applies on top of both constants and regardless of the switch. That is more than it sounds, because
matching is literal and does no DNS: a LAN device addressed by a dotted name (`emby.lan`,
`nas.home.arpa`, an own domain pointing at a 192.168 address) is a name, not an IP, so the CIDR
rules never see it either.

### This switch is not .NET's `bypassOnLocal`

The two are easy to conflate — a "bypass local addresses" option is a familiar thing to find in a
proxy configuration — and their coverage is almost disjoint, which is worth stating. Measured
against .NET 8, `WebProxy(bypassOnLocal: true)` bypasses loopback,
dotless hostnames, **the machine's own interface addresses** (whatever range they are in — a public
address on the host's own NIC is bypassed) and hosts in the machine's own DNS suffix. It does *not*
bypass `192.168.1.50`, `10.11.12.13`, `169.254.0.0/16`, `fc00::/7` or `*.local`.

This plugin's switch governs exactly the set that one does not: RFC1918, link-local, ULA and mDNS.
Loopback is unconditional here rather than part of the switch, and dotless names are not covered at
all. That is why the switch and the user's bypass list both exist and neither is redundant: they
cover different things, and neither mechanism subsumes the other.

**Emby's own endpoints are deliberately not bypassed.** `mb3admin.com` (`PluginSecurityManager`:
`/admin/service/registration/validate`, `/admin/service/appstore/register`, and the plugin catalogue
in `InstallationManager`) and `connect.emby.media` (`Emby.Server.Connect`) were compiled-in entries
until they were removed on purpose. Two reasons: a server whose only route outward *is* the proxy
could not reach them at all, and the privacy argument for the bypass was thin, since a licence check
carries the key that identifies the installation either way.

The accepted cost is that a dead proxy also stops Premiere from validating, and
that Emby's licensing servers see the proxy's egress address. A user who wants the old behaviour can
put the hosts in the bypass list; the plugin no longer decides it for them.

## Localization

Strings live in `src/EmbyProxyRouter/Localization/*.json` and are embedded at build time, so there
are no loose files to deploy. `en.json` is the reference: every key must exist there, and other
languages fall back to it key by key rather than rendering blank.

**The plugin has no language setting and must not grow one.** The language follows Emby's own
display language, which the server applies process-wide in `ApplicationHost.SetDefaultThreadCulture`
— called from the host constructor *and* from `OnConfigurationUpdated`, with no per-request
localization middleware anywhere in the server. `Localizer` therefore reads
`CultureInfo.CurrentUICulture` on each lookup, which is what makes the page track the dashboard
without a restart. A switch of its own would only let the plugin page disagree with the rest of the
dashboard.

Settings-page labels and descriptions go through `[DisplayNameL(nameof(Strings.X), typeof(Strings))]`,
which resolves through Emby's `LocalizableString`. That requires `Strings` to expose a **public
static string property with a getter**: it caches the reflected `PropertyInfo` but re-invokes the
getter on every read, which is the other half of what makes a live language change work. Runtime
strings — status text, validation errors — call `Localizer.Get` / `Localizer.Format` directly.

Adding a language is one file: `<code>.json` using the culture code Emby uses (`fr`, `zh-CN`,
`pt-BR`, …). No csproj entry, no C# change.

### The log is English; only the dashboard is translated

A log line is read by whoever is debugging the server — often not the person whose display language
is set, and usually after the fact, pasted into an issue or grepped for a phrase out of this
documentation. Translating it would make that worse for everyone involved. The rule is mechanical so
it can be checked rather than remembered:

* Keys written to the log are prefixed `Log` and live in **`en.json` alone**, resolved with
  `Localizer.GetInvariant` / `FormatInvariant` — never `Get` / `Format`. `LogLanguageTests` fails the
  build if a `Log*` key appears in any other language file.
* A value reaching **both** audiences — a proxy-address parse error, the endpoint description — is
  carried as a `LocalizedText` rather than a rendered string, and each sink asks for what it needs:
  `.Invariant()` for the log, `.Localized()` for the page. Nested `LocalizedText` arguments follow
  their parent, so a rendered message is in one language throughout.
* Everything else is a UI string and belongs in every language file.

## Building

Requirements: **.NET SDK 8.0** plus `curl`, `ar` and `tar`.

```bash
./build/fetch-emby-refs.sh                                       # once, populates lib/
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/verify-single-dll.sh
dotnet test -c Release tests/EmbyProxyRouter.Tests/EmbyProxyRouter.Tests.csproj
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

#### The checksum pin

`build/emby-sha256.txt` holds the SHA-256 of the package the pinned version resolves to, and the
fetch script **refuses to extract** the pinned version unless it matches — nothing is written to
`lib/` on a mismatch. This download decides what ships: the release DLL is compiled against the
assemblies inside it, and `verify-patch-target.sh` reads its patch target out of the same file.

Be precise about what this does and does not buy. HTTPS authenticates the host; it says nothing
about the artefact still being the one this repository was verified against. The checksum closes
that second gap and nothing else — it is tamper-evidence for later fetches, not authentication of
the upstream release. The value is first recorded at the moment a version is adopted, and it lands
in a pull request a human merges, which is where trusting it is actually decided.

Only the pinned version can be checked, because only it has an entry. `ci.yml` dispatches the script
against *newer* Emby releases to see whether they still work, and those have no checksum by
definition; such a run says so and carries on, which is the case it exists for. It also never
publishes. `release.yml` takes no version input at all, so a release always goes through the
verified path.

**The two files are one pin.** Bumping the version without the checksum leaves a pin that cannot be
built — the script fails rather than extracting something unverified. The bump pull request `ci.yml`
opens writes both.

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

Four workflows plus Dependabot, split along one line: **verifying a change and shipping a
deliverable are separate events.** Pull requests are verified; only a tag ships.

### `ci.yml` — every pull request

Not "the build" — it never publishes anything — but the set of assertions that a change is sound:

* `actionlint` on the workflows. A typo in an expression or a step key is caught in seconds instead
  of by a run that took minutes to get there.
* Compiling against the real Emby assemblies. Not much of a test on its own, but the thing that
  catches a change which simply does not build.
* `build/verify-patch-target.sh` — the assertion that actually matters (see above).
* `build/verify-single-dll.sh` — the output is still one self-contained file: no `0Harmony.dll` next
  to it, not suspiciously small. That property is what makes deployment a one-file copy, and it
  would otherwise break silently.
* `dotnet test` on `tests/EmbyProxyRouter.Tests` — the plugin's own logic (see below).

Those checks answer two different questions and neither substitutes for the other. The tests decide
whether the logic is right; the rest decide whether the plugin still fits the server it plugs into.
A green test run says nothing about whether the Harmony patch applies.

The two verify scripts are scripts rather than inline steps because `release.yml` runs the same
ones, and the test step is repeated there for the same reason. A release cannot ship something a
pull request would have rejected.

### The test project

`tests/EmbyProxyRouter.Tests` (xUnit, `net8.0`) covers what is decidable without a server, which is
most of the logic that has actually been wrong: `ProxyEndpoint.TryParse`, `BypassRules`,
`DynamicWebProxy`, `ProxyGateHandler` against a stub inner handler and a recording logger,
`HttpHandlerPatch.Decorate` and `Configure` reached by reflection — the latter for the
"ignore certificate validation" option — `LogThrottle`, `ProxyProbe` against an in-process SOCKS5
server, `ProxySettings`, and the log-language rule (`LogLanguageTests` fails the build if a `Log*`
key turns up in a language file other than `en.json`). Apart from that server on a loopback port,
all of it is pure functions over their inputs — no Emby host, no Harmony.

`ProxyState.Decide` and `Explain` have no test class of their own. They are exercised through
`DynamicWebProxy` and `ProxyGateHandler`, which is deliberate: those two are the callers that must
never reach different verdicts for the same destination, so testing them is what pins the property
that matters.

It references the plugin project and, unlike the plugin, copies the Emby assemblies into its own
output with `Private=true`: a test host has no server to supply them. `lib/` still has to be
populated by `build/fetch-emby-refs.sh` first.

The classes that write `HttpHandlerPatch`'s statics share the `PatchStatics` xUnit collection.
xUnit runs separate classes in parallel, so a new class touching those fields has to join the
collection rather than race with the ones already in it.

Two things it deliberately does not cover. The **Harmony patch**, because whether
`CreateHttpClientHandler` still has the expected shape is a fact about a shipped Emby build rather
than about this code — `build/verify-patch-target.sh` answers that by decompiling the real assembly.
And **real proxy traffic**, because whether a SOCKS5 server actually negotiates authentication has
to be exercised against a server.

A regression case is worth having only once it has been seen to fail. When adding one, break the fix
on purpose and confirm it goes red before committing it.

It also runs on manual dispatch with an Emby version as input; given none, it uses the pinned
`build/emby-version.txt`. The version is the cache key for the fetched assemblies, so only the first
run per version pays for the ~180 MB download.

That resolution happens in a **step**, not in `env:`, and it has to. `inputs.*` is empty on any event
that carries no inputs — `pull_request` above all — so an `env:` block reading it would resolve to
the empty string on every pull request rather than falling back to the pinned file. The step reads
the input, falls back to `build/emby-version.txt` when it is blank, and everything downstream uses
what it resolved.

**Only a manual run uploads a DLL**, named `EmbyProxyRouter-emby<version>-<commit>`. That is the
`release-check.yml` case: a candidate against a *new* Emby version, worth having in hand to try
against a real server. Pull requests skip the upload — they are asking whether the change is sound,
not producing something to install.

The commit in that name is the one that was pushed, not `github.sha`. On a pull request `github.sha`
is the throwaway merge commit GitHub creates for the run: it belongs to no branch and cannot be
resolved once the run is over. The checks still run against that merge, because testing the merge
result is the point; only the label points at a commit that exists.

### `release-please.yml` — pushes to `main`

The only place the plugin's own `<Version>` changes. It reads the Conventional Commits since the last
release and maintains a standing pull request that bumps `<Version>` in the csproj and updates
`CHANGELOG.md`; merging that pull request is what creates the tag and the GitHub Release. `fix:`
bumps a patch, `feat:` a minor, and a `!` after the type or a `BREAKING CHANGE:` footer a major.

It builds and publishes nothing itself. The release it creates goes through the Releases API rather
than a tag push, which is exactly the case `release.yml` listens for with `release: published` — so
the deliverable is produced by that workflow unchanged, and the two need no coupling beyond the tag.

**It must not run on the default `GITHUB_TOKEN`**, and this is the one trap here that has already
cost a release. GitHub suppresses the events raised by actions taken with that token, so the tag and
release this workflow creates fired neither of `release.yml`'s triggers: v1.1.1 shipped as a GitHub
Release with no DLL attached and had to be repaired by hand. It therefore authenticates with
`RELEASE_PLEASE_TOKEN`, a PAT scoped to this repository alone (Contents + Pull requests, read and
write), which is not subject to that suppression.

This is also why `ci.yml` does not check whether the plugin's version is already released: the number
only ever moves here, and never to one that already has a tag, so a per-pull-request check would fail
every ordinary change sitting between two releases instead of catching a mistake.

### `release.yml` — tags matching `v*`, or a published release

The only workflow that produces something a user installs. It builds against the pinned Emby version
— deliberately with no override input, because the DLL is handed to users and the version it was
verified against has to be the one the repository claims to support — and attaches
`EmbyProxyRouter.dll` to a GitHub Release.

It repeats CI's verification instead of trusting that a pull request ran it. A tag can be placed on
any commit, including one that never went through a pull request, and shipping a plugin whose Harmony
patch no longer matches is the one failure this project cannot afford: it is silent. Publishing needs
`contents: write` and uses the preinstalled `gh` CLI rather than a third-party action, so it adds no
supply chain of its own.

It also asserts that the tag equals `v` + `<Version>` from the csproj and refuses to publish when the
two disagree. Emby reads the plugin version out of the assembly, so a tag claiming one number while
the DLL says another produces a release nobody can identify once it is installed. This is the only
place that assertion is made — see `ci.yml` above for why it is not repeated per pull request.

Cutting a release is one command:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

The workflow also triggers on `release: published`, because publishing a release through the GitHub
UI (or the API) for a tag that doesn't exist yet creates that tag through the Releases endpoint rather
than a real push, and `push: tags` alone has been observed to miss that path entirely — no run at all,
not even a failed one. Whether that path *also* fires a `push` event alongside `release` has proven
inconsistent in practice, so `release: published` is the trigger this workflow actually relies on;
`push: tags` stays for the plain `git push origin <tag>` path, which fires no `release` event at all.
On a `release` event `GITHUB_SHA`/`GITHUB_REF_NAME` resolve to the default branch tip, not the tag, so
the checkout step and the tag-name resolution both read `github.event.release.tag_name` explicitly
instead of trusting the ambient ref. Both triggers can fire for the same tag - drafted then published,
or published for an already-pushed tag; the existing-release fallback below absorbs the duplicate run
rather than failing.

If a release already exists for the tag — drafted in the UI beforehand, or the workflow re-run — the
DLL is uploaded to it instead of the run failing.

### `release-check.yml` — manual, weekly schedule prepared

Answers the question a pull request cannot: *does a newer Emby Server release break the plugin?* It
reads the Emby release list, compares the newest release against the pinned version, and — if a newer
one exists — dispatches `ci.yml` against it. That run fetches that version's assemblies, re-runs the
patch-target check, and leaves a candidate DLL as an artifact.

`ci.yml` reports that run's outcome itself, in a `report-candidate` job that only exists when the
workflow was dispatched with an `emby-version` input — a plain pull-request run never touches it. On a
pass it opens a pull request that bumps `build/emby-version.txt` to the candidate version; on a
failure it opens an issue. Neither creates a tag or a release — adopting a version stays the human
step described in `release.yml` above, this only turns "green means adoptable" into something that
doesn't require reading the Actions log to find out. Both checks skip quietly (already pinned to that
version, or a branch/issue for it already exists) so a check dispatched daily from a cron doesn't pile
up duplicates while a version stays unadopted or broken.

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

`.github/dependabot.yml` covers the three dependency surfaces this repository has, split by what a
bump can break rather than by ecosystem:

* **The workflow actions**, grouped into a single monthly pull request — they move together, and
  separate pull requests would conflict on the same pinned-SHA lines.
* **`Lib.Harmony`**, the plugin's only shipped NuGet dependency, ungrouped and on its own. It is
  embedded *into* the plugin DLL rather than shipped beside it, so a bump changes the artifact
  itself, and it patches the CLR at runtime — the part of this plugin with the least margin for a
  silent regression. The patch-target check is what has to pass before such a bump is taken.
* **The test project's xunit and VSTest packages**, grouped separately. They ship in nothing and
  cannot affect the artifact, so a bump to them is verified by the tests passing rather than by the
  patch-target check — which is why they are not folded in with Harmony.

All three go through `ci.yml` like any other change.

## Distribution and the Emby plugin catalog

The deliverable is a DLL copied into `/config/plugins`. Emby *can* update a plugin from its dashboard,
but only through one channel, and it is worth knowing exactly how narrow that channel is before
relying on it.

`InstallationManager` reads the catalog from `https://www.mb3admin.com/admin/service/EmbyPackages.json`
— a **compiled-in constant**. There is no setting, no config file and no API for pointing the server
at a second source. Appearing in the dashboard therefore means being accepted into Emby's own
catalog; there is no self-hosted alternative.

`PluginUpdateTask` (`Key = "PluginUpdates"`, hidden) runs on startup and every 24 h — every 3 h when
the server's update level is Beta. For each loaded plugin it looks the catalog up by **name and
`Plugin.Id`**, takes the highest version whose `requiredVersionStr` is at or below the running server,
and installs it if it exceeds the assembly version. Note that its own description claims it only
touches "plugins that are configured to update automatically"; in 4.9.5.0 there is no such filter in
the code path.

Installation downloads `sourceUrl`, verifies `checksum` as the MD5 of the file when one is given, and
writes it to `PluginsPath/targetFilename`.

### The submission is a web form, not a JSON entry

`sourceUrl` being an arbitrary URL once suggested that Emby would host only the metadata while the
DLL kept coming from this repository's releases. That is what the *schema* allows and not what the
*process* does: [Emby's own instructions][catalog-doc] end with "select the dll file from your local
computer and save. This will upload the dll and make it available in the plug-in catalog." **Emby
hosts the binary.** There is no JSON submission and no field into which a `sourceUrl` is pasted.

The steps, in order:

1. A beta thread in the community plugins forum with the DLL attached, and enough iterations of real
   testing behind it. This is a gate, not a formality — the standing answer to a request to be added
   is "start by providing a binary here in the community. Once it's been given a little testing then
   we can look at adding it to the catalog."
2. A developer id, by PM to `ebr` or a post in the developer forum.
3. The package form at <https://plugins.emby.tv/admin/packages.html>, which asks for name, guid,
   short description, overview, website, a 16x9 thumb, a preview image, target system, package type
   (`Userinstalled`), category, tile colour and target filename.
4. A version upload per release: version number matching the assembly, class (`Dev`/`Beta`/`Release`),
   a change description, the minimum server version, and the DLL itself.

The two images are in the repository: `src/EmbyProxyRouter/thumb.png` is the 16x9 tile (the same file
the csproj embeds, so the catalog and the dashboard cannot disagree), and `docs/catalog/preview.png`
is a screenshot of the settings page taken against a real 4.9.5.0 server — `docs/catalog/README.md`
records how, and when it has to be retaken.

Everything else on that form is Emby's to accept, so nothing here can pre-fill it. What this
repository *can* do is keep the fields that have to agree with the assembly in one place:

```bash
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/catalog-entry.sh v1.0.0
```

Read its output as a record of the facts, not as something to submit. `name`, `guid`, `versionStr`,
`requiredVersionStr` and `targetFilename` are read out of the build and get copied into the form;
`sourceUrl` and `checksum` describe this repository's releases and match no field on it; `category`
is a dropdown there; `id` is assigned by Emby; and `classification` is emitted as `"Release"` because
the client-side enum is `Release | Beta | Dev`. It stays a generator rather than a committed file
because `versionStr` and `checksum` change with every release, and a stored copy would be stale the
moment it was written.

The catalog *host* is worth re-checking before a submission: the constant compiled into
`InstallationManager` is `mb3admin.com`, while the admin front-end is served from `plugins.emby.tv`.
Both may front the same service, but nothing here has verified that.

[catalog-doc]: https://dev.emby.media/doc/plugins/dev/Getting-your-plug-in-in-the-catalog.html

### What the Development Policy requires of this plugin

[The policy][policy] bans, among other things, "including or otherwise distributing code or libraries
in a manner that violates the license terms of those particular libraries or the license terms of
Emby". This plugin embeds Harmony, so every copy of the DLL is a copy of an MIT-licensed library, and
MIT wants its notice carried along. `THIRD-PARTY-NOTICES.md` is that notice, and the csproj embeds it
in the DLL as well so it travels with a binary that has been separated from this repository.

[policy]: https://github.com/MediaBrowser/Emby/wiki/Development-Policy

Two consequences worth weighing before pursuing this. The update task is unconditional, so accepting a
catalog entry means the plugin replaces its own binary on a schedule, driven by a record held by a
third party — for a plugin whose purpose is control over outbound traffic, that is a real trust
surface. And `mb3admin.com` is no longer in `BypassRules.Always`, so the update check would go
through the proxy like any other request — and would fail whenever the proxy is down, which is one
more way for a self-updating plugin to behave unpredictably.

## Project layout

```
.github/workflows/ci.yml            actionlint + compile + the verify scripts + tests (pull requests)
.github/workflows/release-please.yml Maintains the version-bump PR that cuts a release (pushes to main)
.github/workflows/release.yml       Builds and publishes the DLL to a GitHub Release (tags v*)
.github/workflows/release-check.yml Finds newer Emby releases, dispatches a CI run against them
.github/dependabot.yml              Updates for the workflow actions, Lib.Harmony and the test tooling
.github/ISSUE_TEMPLATE/             Bug report, feature request, private security link
.github/PULL_REQUEST_TEMPLATE.md    Checklist covering the traps in CONTRIBUTING.md
ARCHITECTURE.md                     This file
CHANGELOG.md                        Maintained by release-please; not edited by hand
CONTRIBUTING.md                     How to build, verify and submit a change
THIRD-PARTY-NOTICES.md              The MIT notice for the embedded Harmony; embedded in the DLL too
docs/catalog/preview.png            Settings-page screenshot for the catalog's preview image
docs/catalog/README.md              What the catalog images are and how preview.png was produced
release-please-config.json          Changelog sections and the csproj the version is bumped in
.release-please-manifest.json       The last released version, as release-please tracks it
build/emby-version.txt              The pinned Emby version (single source of truth)
build/emby-sha256.txt               SHA-256 of the pinned version's package; the other half of the pin
build/fetch-emby-refs.sh            Fetches the Emby assemblies, verifying that checksum
build/verify-patch-target.sh        Asserts the patched method still matches
build/verify-single-dll.sh          Asserts the output is still one self-contained file
build/catalog-entry.sh              Generates the package entry for Emby's plugin catalog
lib/                                Target folder for the assemblies (not committed)
src/EmbyProxyRouter/
  Plugin.cs                   Entry point, dashboard status, server entry point
  PluginOptions.cs            Settings page (Emby.Web.GenericEdit)
  thumb.png                   Tile shown in the dashboard's plugin list (embedded)
  Localization/en.json        Reference language (every key must exist here)
  Localization/de.json        German translation
  Localization/Localizer.cs   Language resolution and JSON lookup (localized and English)
  Localization/LocalizedText.cs Deferred message, for values shown on the page AND logged
  Localization/Strings.cs     Static properties consumed by Emby's localization attributes
  Patch/HarmonyLoader.cs      Loads the embedded Harmony assembly
  Patch/HttpHandlerPatch.cs   The postfix patch, including signature verification
  Proxy/ProxyEndpoint.cs      Address parsing, credential relocation
  Proxy/ProxyScheme.cs        The Http / Https / Socks5 dropdown values
  Proxy/BypassRules.cs        CIDR and host matching
  Proxy/ProxySettings.cs      Immutable configuration snapshot
  Proxy/ProxyState.cs         Routing decision, in one place
  Proxy/DynamicWebProxy.cs    IWebProxy, consulted per request
  Proxy/ProxyGateHandler.cs   Refuses requests with no usable proxy, and logs them
  Proxy/LogThrottle.cs        Collapses a repeated warning to one line per key per window
  Proxy/ProxyProbe.cs         The settings-page check (no timer, no third party)
  Proxy/ProxyRuntime.cs       Holds the singletons together
tests/EmbyProxyRouter.Tests/
  Fakes.cs                    Recording logger and stub inner handler
  ProxyEndpointTests.cs       Address parsing, ports, credentials
  BypassRulesTests.cs         Compiled-in entries, wildcards, CIDR, IPv4-mapped IPv6
  DynamicWebProxyTests.cs     What the resolver answers for each verdict
  ProxyGateHandlerTests.cs    Both refusals, redaction, throttling
  HandlerDecorationTests.cs   What Decorate hands back when the proxy will not attach
  LogThrottleTests.cs         Windowing, suppressed counts, capacity behaviour
  ProxySettingsTests.cs       Snapshot construction from the options
  ProxyProbeTests.cs          The settings-page check against an in-process SOCKS5 server
  CertificatePolicyTests.cs   Scope of "ignore certificate validation"
  LogLanguageTests.cs         Enforces that the log is English and the page is not
```
