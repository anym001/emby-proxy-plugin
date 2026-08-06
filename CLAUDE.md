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
```

Requires .NET SDK 8.0. `lib/*.dll` are proprietary Emby assemblies and are gitignored — never commit
them, and never commit build output.

**The pinned Emby version lives in `build/emby-version.txt` and nowhere else.** The fetch script and
both workflows read it. Do not reintroduce a literal default alongside it.

CI is two workflows plus Dependabot:

* `build.yml` — pull requests against `main` plus manual dispatch with an optional `emby-version`
  input. `inputs.*` is empty on `pull_request`, which is why the version is resolved in a step rather
  than in `env:`. Also asserts the output stays a single self-contained DLL.
* `release-check.yml` — finds Emby releases newer than the pinned version and dispatches `build.yml`
  against them. Emby publishes stable (`4.9.x`) and beta (`4.10.0.x`) in parallel, so selection is by
  the `prerelease` flag, never by version order.
* `.github/dependabot.yml` — grouped monthly updates for the workflow actions, and `Lib.Harmony`.
  Nothing else is a dependency: the Emby assemblies come from the .deb, not from NuGet.

**Actions are pinned to commit SHAs**, with the version as a trailing comment
(`actions/checkout@11d5960… # v4.4.0`). Never reintroduce a floating `@v4` — a tag can be moved,
a SHA cannot, and Dependabot is what keeps the pins current. `build.yml` lints the workflows with
actionlint before it builds; run it locally before pushing a workflow change.

**Do not name build artifacts after `github.sha`.** On `pull_request` that is the ephemeral merge
commit, which belongs to no branch and cannot be resolved after the run. Use
`github.event.pull_request.head.sha || github.sha`. The build itself keeps running against the merge.

`build/verify-patch-target.sh` is the check that gives a new-version build meaning: compiling only
exercises four rarely-changing API assemblies, while the patched method is internal to the server and
has changed before. A non-matching Harmony postfix fails **silently** — it never applies, and the
plugin looks installed while routing nothing.

There is no test project in the repository. Verification during development was done with throwaway
harnesses (a Python SOCKS5 server plus small console apps referencing the built DLL). If you change
routing, parsing, bypass matching, or localization, verify behaviour by actually exercising it —
compiling is not evidence that it works.

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

`README.md` documents each of these with the evidence behind it. Keep it in sync when behaviour
changes.

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
