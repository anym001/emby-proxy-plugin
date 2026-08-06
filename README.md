# Emby Proxy Router

A minimal Emby Server plugin with exactly one job: route the outbound HTTP(S) traffic that the
**Emby server core itself** initiates through a configurable proxy — HTTP, HTTPS or **SOCKS5** —
while always contacting private networks and Emby's licensing servers directly.

Developed and verified against **Emby Server 4.9.5.0** (net8.0).

## What this plugin does

* Routes the Emby core's HTTP clients through a proxy: metadata providers (TMDB, TVDB, …),
  remote image providers, subtitle downloads.
* Supports **HTTP, HTTPS and SOCKS5 proxies**, selectable via dropdown or directly via URL scheme
  (`socks5://host:1080`).
* Checks proxy reachability and shows the result in the dashboard.
* **Fail-closed by default:** if the proxy is unreachable, affected requests are aborted and logged
  rather than silently falling back to a direct connection. Fail-open is available as a deliberate
  opt-in.
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

A ready-built DLL is attached to every [release](../../releases); building it yourself is described
in [ARCHITECTURE.md](ARCHITECTURE.md#building).

### Confirming the patch took effect

The settings page shows a **Patch status** line at the top. If it says anything other than "Active",
**no** traffic is being redirected, and the reason is stated right next to it. In the server log:

```
Harmony patch active on HttpMessageHandler ApplicationHost.CreateHttpClientHandler(HttpMessageHandlerOptions) (Emby.Server.Implementations 4.9.5.0).
Proxy Router: enabled - socks5://192.168.1.10:1080 (auth as user) | fail-closed | check interval 60 s
Proxy status: REACHABLE - HTTP check via socks5://192.168.1.10:1080 (auth as user) succeeded (...)
```

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

### Fail-closed vs. fail-open

**Fail-closed (default).** If the proxy is unreachable — or not yet checked, or misconfigured —
affected requests fail, and every case is logged as a warning:

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

### Languages

The settings page follows the server's display language; there is deliberately **no language setting
in the plugin**, and a change takes effect without a restart. Translations are embedded in the DLL —
there are no loose files to deploy. A language the plugin does not ship shows English, and an
incomplete translation falls back to English per string rather than showing blank labels.

Adding a language is a single file: copy `src/EmbyProxyRouter/Localization/en.json` to `<code>.json`
using the code Emby uses (`fr`, `zh-CN`, `pt-BR`, …), translate the values, leave the keys untouched
and rebuild. Pull requests with translations are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Known limitations

* **Live TV is only partially covered.** `Emby.LiveTV.dll` uses both the central `IHttpClient`
  (which is redirected) and its own handler instances (which are **not**). If you use Live TV, do not
  assume that traffic goes through the proxy in full. Special handling for it is deliberately not
  built in.
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
* **Bound to an internal Emby method.** The patched method is not public API, so an Emby update can
  change it at any time. The plugin verifies the signature at startup and reports a mismatch loudly
  instead of silently doing nothing — but it cannot repair one.
* **Requests before the first health check.** Under fail-closed, requests are blocked until the
  first check completes. That is intended: unconfirmed proxy availability is not a reason to let
  traffic through.

## Further reading

* [ARCHITECTURE.md](ARCHITECTURE.md) — how the plugin hooks into Emby, why it is built this way,
  building from source, and CI.
* [CONTRIBUTING.md](CONTRIBUTING.md) — reporting bugs, translations, pull requests.

## Licence

GPL-3.0 — see [LICENSE](LICENSE).

The operating principle (a Harmony postfix on Emby's internal handler factory) is inspired by
[StrmAssistant](https://github.com/sjtuross/StrmAssistant) (GPL-3.0).

---

Built with [Claude Code](https://claude.ai/code)
