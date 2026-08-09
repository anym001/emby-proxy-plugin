# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this project is

A single-purpose Emby Server plugin that routes the Emby core's own outbound HTTP(S) traffic through
an HTTP, HTTPS or SOCKS5 proxy, and blocks it rather than letting it out directly when there is no
usable proxy. `README.md` states the scope precisely.

**The scope is narrow on purpose.** Before adding anything, check it against "What this plugin
explicitly does NOT do" in `README.md`. Features that would be useful in a general-purpose plugin
(subtitle handling, metadata enrichment, auto-update, a scheduled task framework) are out of scope by
design, not by omission.

## Language policy

* **Repository content is English.** Code, comments, identifiers, documentation, commit messages,
  and the reference language file `Localization/en.json`.
* **User-visible UI strings are localized**, not hardcoded. See "Localization rules" below.
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

## Where documentation goes, and why this file repeats none of it

Each fact lives in exactly one place. When two files carried the same explanation they drifted —
this file once said CI was four workflows while `ARCHITECTURE.md` said three — so the rule is now
mechanical: **do not restate here what one of the three documents already says.** Read the document
instead, and if a fact is wrong or missing, fix it there rather than adding a second copy here.

* **`README.md` is the general description for end users**: what the plugin does and does not do,
  installing it, the settings table, what happens when the proxy is down, known limitations. No
  decompiled code, no CI internals, no reasoning about .NET handler lifetimes.
* **`ARCHITECTURE.md` is the detailed description for anyone reading or changing the code**: the
  patch target, the verified SOCKS5 behaviour, why the proxy is dynamic and why the gate is a
  `DelegatingHandler`, the bypass rules, localization internals, building from source, the test
  project, and CI. **Keep it in sync when behaviour changes** — it is the only place the evidence
  behind a decision is written down.
* **`CONTRIBUTING.md` is the contributor-facing subset** — the workflow and the traps, not the
  rationale.

What follows is only what is *not* documentation: rules about how to work in this repository.

## Invariants that must not be "simplified" away

Each is load-bearing and was established by decompiling Emby 4.9.5.0 or by runtime tests on .NET 8.
`ARCHITECTURE.md` carries the evidence for every one — re-verify there before changing any of them.

* The Harmony postfix parameter is `ref HttpMessageHandler __result`. Anything else silently fails
  to apply.
* `DynamicWebProxy` stays dynamic. A static `WebProxy` would need a server restart per settings
  change.
* **There is no reachability state, and adding one is a regression.** Nothing may consult the proxy's
  availability in order to decide where a request goes.
* `ProxyGateHandler` refuses only on facts settled before the request arrives. Anything that has to
  *find out* something to answer — above all whether the proxy is reachable — does not belong there.
* `ProxyProbe` is a diagnostic, never a routing input. Nothing in `ProxyState` may consult it.
* SOCKS5 credentials live in `IWebProxy.Credentials`. `ProxyEndpoint.TryParse` strips userinfo out
  of the URI on purpose — do not "restore" it.
* `ProxyState.Decide` is the single routing authority. Add routing logic there, not in either caller.

## CI rules

`ARCHITECTURE.md` describes what the four workflows do. These are the rules for changing them:

* **Verifying a change and shipping a deliverable are separate events.** Do not merge the workflows
  back together, and do not add the pull-request DLL upload back to `ci.yml`.
* **Actions are pinned to commit SHAs**, version as a trailing comment
  (`actions/checkout@3d3c42e5… # v7.0.1`). Never reintroduce a floating major tag. Run actionlint
  locally before pushing a workflow change.
* **Do not name artifacts after `github.sha`.** On `pull_request` that is the ephemeral merge commit,
  which belongs to no branch. Use `github.event.pull_request.head.sha || github.sha`.
* Keep new assertions in `build/verify-*.sh` rather than inline steps, so a release cannot ship an
  output a pull request would have rejected.
* The pinned Emby version lives in `build/emby-version.txt` and nowhere else — do not reintroduce a
  literal default alongside it. It and `build/emby-sha256.txt` are **one pin**: change them together.

## Testing rules

* **A green test run is not evidence that the plugin works.** It says nothing about whether the
  Harmony patch applies — that is `verify-patch-target.sh`'s job — and nothing about real proxy
  traffic. If you change routing, parsing, bypass matching or localization, exercise the behaviour
  for real as well; earlier verification used a Python SOCKS5 server plus small console apps against
  the built DLL.
* **A test that has never failed has not been shown to test anything.** When adding a regression
  case, break the fix on purpose once and confirm the case goes red.
* Add cases to `tests/EmbyProxyRouter.Tests` when you touch anything it covers.

## Localization rules

* **The plugin has no language setting, and must not grow one.**
* Adding a string: decide first which audience it has. A **UI** string goes into `en.json` **and**
  every other language file, and gets a static property on `Strings` only if an attribute needs it.
  A **log** string gets a `Log` prefix, goes into `en.json` alone, and is resolved with
  `Localizer.GetInvariant` / `FormatInvariant`. Never translate a `Log*` key — `LogLanguageTests`
  fails the build if one appears in another language file.
* A value reaching both the page and the log is a `LocalizedText`, never a rendered string.

## Things that are easy to get wrong

The traps that cost a build or a silent regression. `ARCHITECTURE.md` explains each — "The plugin
shell" for the first three, "The settings-page check" for the fourth:

* The plugin constructor must not throw, and the patch is applied from it rather than from the entry
  point. Do not move it.
* `EditableObjectBase.Validate` is `protected override`; `EditMultilineAttribute` needs a line count.
* `async` methods cannot take `out` parameters — return a small struct (see `ProbeResult`).
* `ProxyProbe` uses a raw `TcpClient`, never `HttpClient`. Do not route it through the pipeline.
* Log only scheme, host and port for request URLs. Paths and query strings of metadata lookups carry
  title information and API keys.

## Git

Work on the branch specified for the task. Do not open a pull request unless explicitly asked.

The repository is **`main`-only** — no `dev` branch. A `dev` branch makes sense when it publishes an
artifact a staging instance pulls, but the deliverable here is a DLL copied into `/config/plugins`, so
a `dev` branch would carry no artifact and gate nothing. Do not add one, and do not point Dependabot at
a `target-branch`.

When a convention here changes and it affects someone sending a pull request, change it in
`CONTRIBUTING.md` too.
