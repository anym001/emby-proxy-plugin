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

CI is three workflows plus Dependabot, split on one principle: **verifying a change and shipping a
deliverable are separate events.** Do not merge them back together.

* `ci.yml` — pull requests against `main` plus manual dispatch with an optional `emby-version` input.
  Lints, compiles, runs both verify scripts, and runs the tests. `inputs.*` is empty on `pull_request`, which is why
  the version is resolved in a step rather than in `env:`. It uploads a DLL **only on
  `workflow_dispatch`** — a candidate against a new Emby version is worth having; a pull-request
  artifact is not. Do not add the upload back to pull requests.
* `release.yml` — tags matching `v*`, and nothing else. The only workflow that publishes. It repeats
  CI's verification rather than trusting a pull request ran it, because a tag can sit on any commit.
  It has **no version input on purpose**: a released DLL must be built against the version the
  repository claims to support. Needs `contents: write`; publishes with the preinstalled `gh` CLI so
  it pulls in no third-party action.
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
server: `ProxyEndpoint.TryParse`, `BypassRules`, `ProxyState.Decide`, `DynamicWebProxy`,
`ProxyGateHandler` against a stub inner handler, `LogThrottle`, and the `ProxySettings` clamps. It
references the plugin project and copies the Emby assemblies into its own output, since a test host
has no server to supply them. Add cases there when you touch any of those.

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
* **`ProxyGateHandler` exists because `IWebProxy` cannot block.** Returning `null` from `GetProxy`
  means "connect directly" — a leak. Fail-closed needs a `DelegatingHandler` that can refuse.
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
  table, fail-closed vs. fail-open, known limitations. No decompiled code, no CI internals, no
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
* Adding a string: add the key to `en.json` **and** every other language file, then add a static
  property to `Strings` only if an attribute needs it.
* Adding a language: drop in `<code>.json` using the culture code Emby uses (`fr`, `zh-CN`,
  `pt-BR`, …). Nothing else changes — no csproj entry, no C# change.
* Log message *prefixes* stay English so log lines remain greppable, but embedded detail strings are
  localized because they are also shown in the dashboard.

## Things that are easy to get wrong

* `async` methods cannot take `out` parameters — use a small result struct (see `ProbeResult`).
* Overriding `EditableObjectBase.Validate` requires `protected override`, not `protected internal`.
* `EditMultilineAttribute` takes a required line count: `[EditMultiline(12)]`.
* The health checker must build its **own** `SocketsHttpHandler`. If it used the patched pipeline, a
  fail-closed block would prevent the very request meant to lift the block.
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
