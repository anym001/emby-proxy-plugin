# Contributing

Thanks for your interest in improving the Emby Proxy Router plugin. Contributions are welcome —
especially around proxy handling, verification against new Emby versions, translations, and
documentation.

## Ways to contribute

- **Bug reports & feature requests:** open an issue using the provided templates.
- **Security issues:** please report them privately via
  [a security advisory](https://github.com/anym001/emby-proxy-plugin/security/advisories/new).
  Do not open a public issue.
- **Translations:** drop a `<code>.json` into `src/EmbyProxyRouter/Localization/` using the culture
  code Emby uses (`fr`, `zh-CN`, `pt-BR`, …). Nothing else changes — no csproj entry, no C# change.
  Use `en.json` as the reference; every key in it must exist in your file.
- **Pull requests:** see below.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each PR focused on a single topic.
3. Make sure it passes CI (`ci.yml` runs on every PR: actionlint, a Release build, the two
   verification scripts below, and the unit tests).
4. Open a PR and describe what you changed and why.

Before anything else, check the change against **"What this plugin explicitly does NOT do"** in the
[README](README.md). The narrow scope is the point of the project; features that would be useful in
a general-purpose plugin are out of scope by design, not by omission.

## Building and verifying

```bash
./build/fetch-emby-refs.sh                                       # once, populates lib/
dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
./build/verify-patch-target.sh                                   # needs ilspycmd
./build/verify-single-dll.sh
dotnet test -c Release tests/EmbyProxyRouter.Tests/EmbyProxyRouter.Tests.csproj
```

The tests cover the parts that are decidable without a server: address parsing, bypass matching, the
routing verdict, the gate, the proxy probe, the log throttle. Add cases there when you change any of
those — a bug in them is invisible to everything else on this list.

**A green test run is not evidence that the plugin works**, and neither is compiling. The tests say
nothing about whether the Harmony patch still applies, and the plugin references four Emby API
assemblies that rarely change while the method it patches is internal to the server and has changed
before. If you touch routing, parsing, bypass matching or localization, exercise the behaviour for
real as well.

`verify-patch-target.sh` is mandatory when changing `build/emby-version.txt`. A Harmony postfix whose
signature no longer matches never applies: the plugin installs cleanly, reports no error, and
silently routes nothing.

**A new bundled dependency is a new licence obligation.** Harmony is not deployed beside the plugin,
it is compiled into it, so `EmbyProxyRouter.dll` is itself a copy of an MIT-licensed library.
`THIRD-PARTY-NOTICES.md` carries that notice and the csproj embeds it in the DLL so it travels with a
binary handed out on its own. Anything else that ends up inside the output goes in that file too —
Emby's plugin Development Policy makes a licence violation grounds for removal from the catalog.
`verify-single-dll.sh` fails the build if the notice stops being embedded, so this is checked rather
than remembered.

**`docs/catalog/preview.png` shows the settings page, so changing that page dates it.** It is the
image Emby's catalog form asks for. If you change `PluginOptions` or the status lines, retake it —
`docs/catalog/README.md` says how it was produced.

**The pinned Emby version is two files, not one.** `build/emby-version.txt` and the matching entry in
`build/emby-sha256.txt` are changed together — the fetch script refuses to extract the pinned version
without a checksum that matches, so bumping one alone leaves a pin nobody can build. A version other
than the pinned one has no checksum by design; the script says so and continues, which is what a run
checking a *new* Emby release needs. Nothing built that way is ever published.

**The plugin's own version lives in the csproj, and the release tag has to match it — but you do not
bump it.** `release-please.yml` reads the Conventional Commits on `main` and maintains a standing
pull request that bumps `<Version>` in `src/EmbyProxyRouter/EmbyProxyRouter.csproj` and
`CHANGELOG.md`. Merging that PR is what creates the tag and the GitHub Release; `release.yml` then
builds against it as it always has. What determines the bump is the commit type: `fix:` is a patch,
`feat:` is a minor, and a `!` after the type (`feat!:`, `fix!:`, …) or a `BREAKING CHANGE:` footer is
a major. Get the type right and the version follows on its own.

`release.yml` still compares the tag against `<Version>` and refuses to publish when they disagree,
because Emby takes the plugin version out of the assembly: a tag saying one thing while the DLL says
another produces a release that is indistinguishable from the previous one once installed. That check
stays as the safety net even though release-please is what normally keeps the two in step.

## Conventions

- **Commit subjects are Conventional Commits**: `type(scope)?!: subject`, one of `feat`, `fix`,
  `refactor`, `docs`, `test`, `ci`, `build` or `chore` — the same set `release-please-config.json`
  has changelog sections for. `ci.yml` checks every commit in a pull request against this and fails
  if one doesn't match, because release-please reads exactly this prefix to decide the version bump
  and changelog section; a subject it can't parse is silently dropped from both rather than
  rejected, so the check is what stands between a bad subject and a release that's missing a change
  it should have listed.
- **English everywhere** — code, comments, YAML, documentation, commit messages, PR titles.
- **User-visible strings are localized**, never hardcoded. A new UI string goes into `en.json` *and*
  every other language file.
- **The Emby log stays English**, whatever the dashboard language is set to — a log line is usually
  read by someone other than the person who set that language, and often out of context in an issue.
  Log strings are prefixed `Log`, live in `en.json` only, and are resolved with
  `Localizer.GetInvariant` / `FormatInvariant`. A value shown on the page *and* written to the log
  is a `LocalizedText`, rendered `.Localized()` for one and `.Invariant()` for the other. Do not
  translate a `Log*` key; the tests reject it.
- **Log only scheme, host and port for request URLs.** Paths and query strings of Emby's metadata
  lookups carry title information and API keys.
- Never commit `lib/*.dll` — they are proprietary Emby binaries — and never commit build output or
  secrets.

[ARCHITECTURE.md](ARCHITECTURE.md) explains how the plugin hooks into Emby and why it is built that
way, with the evidence behind each decision. It is worth reading before a larger change.
