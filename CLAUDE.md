# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this project is

A single-purpose Emby Server plugin: it routes outbound HTTP(S) traffic initiated by the Emby core
through an HTTP, HTTPS or SOCKS5 proxy, and blocks that traffic rather than letting it out directly
when the proxy is unavailable.

The scope is deliberately narrow. Before adding anything, check it against the "What this plugin
explicitly does NOT do" list in `README.md`. Features that would be useful in a general-purpose
plugin (subtitle handling, metadata enrichment, auto-update, a scheduled task framework) are out of
scope by design, not by omission.

## Language policy

* **Repository content is English.** Code, comments, identifiers, documentation, commit messages,
  and the reference language file `Localization/en.json`.
* **User-visible UI strings are localized**, not hardcoded. See "Localization" below.
* Conversation with the repository owner is in German. That never changes what lands in the repo.

## Build and test

```bash
./build/fetch-emby-refs.sh                                       # once, populates lib/
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/verify-patch-target.sh                                   # needs ilspycmd
./build/verify-single-dll.sh
dotnet test -c Release tests/EmbyProxyRouter.Tests/EmbyProxyRouter.Tests.csproj
```

Requires .NET SDK 8.0. `lib/*.dll` are proprietary Emby assemblies and are gitignored — never commit
them, and never commit build output.

**The pinned Emby version lives in `build/emby-version.txt` and nowhere else.** The fetch script and
all workflows read it. Do not reintroduce a literal default alongside it.

**The version pin is two files.** `build/emby-sha256.txt` carries the SHA-256 of the package that
version resolves to, and `fetch-emby-refs.sh` refuses to extract the pinned version without a
matching entry — that download decides what ships, since the release DLL compiles against the
assemblies inside it and `verify-patch-target.sh` reads its target out of the same file. Change the
two together; bumping the version alone leaves a pin that cannot be built. A non-pinned version has
no entry on purpose (that is the new-Emby-release check), reports itself as unverified, and never
publishes. The bump pull request `ci.yml` opens writes both files.

CI is three workflows plus Dependabot, split on one principle: **verifying a change and shipping a
deliverable are separate events.** Do not merge them back together.

* `ci.yml` — pull requests against `main` plus manual dispatch with an optional `emby-version` input.
  Lints, compiles, runs both verify scripts, and runs the tests. `inputs.*` is empty on `pull_request`, which is why
  the version is resolved in a step rather than in `env:`. It uploads a DLL **only on
  `workflow_dispatch`** — a candidate against a new Emby version is worth having; a pull-request
  artifact is not. Do not add the upload back to pull requests. It also fails a branch whose
  `<Version>` is already a published tag: that and `release.yml`'s tag assertion are the same
  invariant approached from the two ends, one before the merge and one before the publish.
* `release.yml` — tags matching `v*`, and nothing else. The only workflow that publishes. It repeats
  CI's verification rather than trusting a pull request ran it, because a tag can sit on any commit.
  It has **no version input on purpose**: a released DLL must be built against the version the
  repository claims to support. It asserts that the tag equals `v` + `<Version>` from the csproj and
  refuses the release otherwise — for the same reason turned on the plugin's own version, since Emby
  reads that number out of the assembly and a tag disagreeing with it ships a release nobody can
  identify once installed. Needs `contents: write`; publishes with the preinstalled `gh` CLI so it
  pulls in no third-party action.
* `release-check.yml` — finds Emby releases newer than the pinned version and dispatches `ci.yml`
  against them. Emby publishes stable (`4.9.x`) and beta (`4.10.0.x`) in parallel, so selection is by
  the `prerelease` flag, never by version order.
* `.github/dependabot.yml` — grouped monthly updates for the workflow actions, `Lib.Harmony`, and the
  test project's xunit/VSTest packages as a separate group. The Emby assemblies come from the .deb,
  not from NuGet, and are not a Dependabot concern.

The two `build/verify-*.sh` scripts exist as scripts, not inline steps, because `ci.yml` and
`release.yml` both run them. Keep new assertions in a script for the same reason: a release must not
be able to ship an output that a pull request would have rejected.

**Actions are pinned to commit SHAs**, with the version as a trailing comment
(`actions/checkout@3d3c42e5… # v7.0.1`). Never reintroduce a floating major tag — it can be moved,
a SHA cannot, and Dependabot is what keeps the pins current. `ci.yml` lints the workflows with
actionlint before anything else; run it locally before pushing a workflow change.

**Do not name artifacts after `github.sha`.** On `pull_request` that is the ephemeral merge commit,
which belongs to no branch and cannot be resolved after the run. Use
`github.event.pull_request.head.sha || github.sha`. The run itself keeps going against the merge.

`build/verify-patch-target.sh` is the check that gives a new-version build meaning: compiling only
exercises four rarely-changing API assemblies, while the patched method is internal to the server and
has changed before. A non-matching Harmony postfix fails **silently** — it never applies, and the
plugin looks installed while routing nothing.

`tests/EmbyProxyRouter.Tests` is an xUnit project covering the parts that are decidable without a
server: `ProxyEndpoint.TryParse`, `BypassRules`, `DynamicWebProxy`, `ProxyGateHandler` against a
stub inner handler, `HttpHandlerPatch.Decorate` and `Configure` reached by reflection (the latter
for the certificate-validation option), `LogThrottle`, `ProxyProbe` against an in-process SOCKS5
server, `ProxySettings`, and the log-language rule below. `ProxyState.Decide` has no test class of
its own — it is exercised through `DynamicWebProxy` and the gate, which are the two callers that
have to agree with it.
It references the plugin project and copies the Emby assemblies into its own output, since a test
host has no server to supply them. Add cases there when you touch any of those.

The tests that write `HttpHandlerPatch`'s statics share the `PatchStatics` xUnit collection. xUnit
runs different classes in parallel, so a new class touching those fields has to join it rather than
race with the ones already there.

**A green test run is not evidence that the plugin works.** It says nothing about whether the Harmony
patch applies — that is `verify-patch-target.sh`'s job — and nothing about real proxy traffic. The
two kinds of check answer different questions and neither substitutes for the other. If you change
routing, parsing, bypass matching, or localization, still exercise the behaviour for real; earlier
verification used a Python SOCKS5 server plus small console apps against the built DLL.

A test that has never failed has not been shown to test anything. When adding a regression case,
break the fix on purpose once and confirm the case goes red.

## Architecture, and why it is shaped this way

The design is driven by verified facts about Emby 4.9.5.0 and .NET 8. Do not "simplify" these
without re-verifying, because each one is load-bearing:

* **Patch target:** `Emby.Server.Implementations.ApplicationHost.CreateHttpClientHandler`, which
  returns `HttpMessageHandler` (**not** `HttpClientHandler`) and yields a `SocketsHttpHandler`. The
  Harmony postfix parameter must be `ref HttpMessageHandler __result` or the patch will not apply.
* **`DynamicWebProxy` must stay dynamic.** `SocketsHttpHandler` freezes its properties after the
  first request, and `CoreHttpClientManager` caches handlers per host with no eviction. A static
  `WebProxy` would require a server restart for every settings change.
* **There is no reachability state, and adding one is a regression.** A configured proxy is used; if
  it cannot be reached the request fails, because .NET never falls back to a direct connection. That
  is the whole enforcement. Knowing in advance that the proxy is down is only useful in order to stop
  using it, which this plugin never does — wanting that answer is what drags in a poller, a check URL
  and a routing input that depends on a stranger's uptime.
* **`ProxyGateHandler` exists for what `IWebProxy` cannot express.** `null` from `GetProxy` means
  "connect directly", so a resolver can never refuse. Two cases need that and no others: the proxy
  is switched on and its address does not parse (no URI to name), and the inner handler never
  received the proxy (`Decorate` passes `proxyAttached: false`, so a `ViaProxy` verdict it cannot
  carry out is refused instead of sent in the clear). The admissible shape is narrow — a fact
  settled before the request arrives, never a judgement made during one. Anything that has to *find
  out* something to answer, above all whether the proxy is reachable, does not belong here.
* **`ProxyProbe` is a diagnostic, never a routing input.** It runs from the settings page only, talks
  to the proxy and to nobody else, and stops before the proxy would connect anywhere. Nothing in
  `ProxyState` may consult it.
* **SOCKS5 credentials must live in `IWebProxy.Credentials`.** .NET ignores userinfo in a SOCKS
  proxy URI and will silently negotiate "no authentication". `ProxyEndpoint.TryParse` strips
  userinfo out of the URI on purpose — do not "restore" it.
* **`ProxyState.Decide` is the single routing authority.** The proxy and the gate must never reach
  different verdicts for the same destination. Add routing logic there, not in either caller.

`ARCHITECTURE.md` documents each of these with the evidence behind it. Keep it in sync when behaviour
changes.

## Where documentation goes

Three files, and the split is deliberate — do not let technical detail drift back into the README:

* **`README.md` is for end users**: what the plugin does and does not do, installing it, the settings
  table, what happens when the proxy is down, known limitations. No decompiled code, no CI internals, no
  reasoning about .NET handler lifetimes.
* **`ARCHITECTURE.md` is for anyone reading or changing the code**: the patch target, the verified
  SOCKS5 behaviour, why the proxy is dynamic and why the gate is a `DelegatingHandler`, localization
  internals, building from source, and CI.
* **`CONTRIBUTING.md` is the contributor-facing subset of this file** — the workflow and the traps,
  not the rationale.

## Localization

User-visible strings live in `src/EmbyProxyRouter/Localization/*.json` and are embedded at build
time. `en.json` is the reference: every key must exist there, and other languages fall back to it
key by key.

* **The plugin has no language setting, and must not grow one.** The language follows Emby's own
  display language, which the server applies process-wide in
  `ApplicationHost.SetDefaultThreadCulture` (from the host constructor *and* from
  `OnConfigurationUpdated`, with no per-request localization middleware anywhere). `Localizer` reads
  `CultureInfo.CurrentUICulture` on each lookup, so the page tracks the dashboard and needs no
  restart. A separate switch would let the plugin page disagree with the rest of the dashboard.
* Settings-page labels and descriptions use `[DisplayNameL(nameof(Strings.X), typeof(Strings))]`.
  This resolves through Emby's `LocalizableString`, which requires `Strings` to expose a **public
  static string property with a getter**. It caches the reflected `PropertyInfo` but re-invokes the
  getter each read, which is the other half of what makes a live language change work.
* Runtime strings (status text, validation errors, health-check detail) call `Localizer.Get` /
  `Localizer.Format` directly.
* Adding a string: decide first which audience it has. A **UI** string goes into `en.json` **and**
  every other language file, and gets a static property on `Strings` only if an attribute needs it.
  A **log** string gets a `Log` prefix and goes into `en.json` alone.
* Adding a language: drop in `<code>.json` using the culture code Emby uses (`fr`, `zh-CN`,
  `pt-BR`, …). Nothing else changes — no csproj entry, no C# change.
* **The Emby log is English. Only the dashboard follows the display language.** A log line is read
  by whoever is debugging the server — often not the person whose language is set, and usually after
  the fact, pasted into an issue or grepped for a phrase out of the documentation. The rule is
  mechanical so it can be checked rather than remembered:
  * Keys written to the log are prefixed `Log` and live in **`en.json` only**. Resolve them with
    `Localizer.GetInvariant` / `FormatInvariant`, never `Get` / `Format`. Do not translate them —
    `LogLanguageTests` fails the build if a `Log*` key turns up in another language file.
  * A value that reaches **both** audiences — the reachability detail, a proxy-address parse error,
    the endpoint description — is carried as a `LocalizedText` rather than a rendered string, and
    each sink asks for what it needs: `.Invariant()` for the log, `.Localized()` for the page.
    Nested `LocalizedText` arguments follow their parent, so a rendered message is in one language
    throughout.
  * Everything else is a UI string and belongs in every language file, as before.

## Things that are easy to get wrong

* `async` methods cannot take `out` parameters — use a small result struct (see `ProbeResult`).
* Overriding `EditableObjectBase.Validate` requires `protected override`, not `protected internal`.
* `EditMultilineAttribute` takes a required line count: `[EditMultiline(12)]`.
* `ProxyProbe` talks to a raw `TcpClient`, not through `HttpClient`. Sending it through the patched
  pipeline would have it check the proxy by way of the proxy.
* Log only scheme, host and port for request URLs. Paths and query strings of metadata lookups carry
  title information and API keys.
* The plugin constructor must not throw. A throwing constructor removes the plugin from the
  dashboard, leaving no way to see the error.

## Git

Work on the branch specified for the task. Do not open a pull request unless explicitly asked.

The repository is **`main`-only** — no `dev` branch. A `dev` branch makes sense when it publishes an
artifact a staging instance pulls, but the deliverable here is a DLL copied into `/config/plugins`, so
a `dev` branch would carry no artifact and gate nothing. Do not add one, and do not point Dependabot at
a `target-branch`.

When a convention here changes and it affects someone sending a pull request, change it in
`CONTRIBUTING.md` too.
