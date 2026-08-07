# Emby Proxy Router

A minimal Emby Server plugin with exactly one job: route the outbound HTTP(S) traffic that the
**Emby server core itself** initiates through a configurable proxy — HTTP, HTTPS or **SOCKS5** —
while always contacting private networks and Emby's licensing servers directly.

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

## Installation

The Emby container usually maps a host directory to `/config`. Copy the plugin DLL into its
`plugins` subfolder and restart the container.

Then configure it in the dashboard under **Plugins → Proxy Router**.

A ready-built DLL is attached to every [release](../../releases); building it yourself is described
in [ARCHITECTURE.md](ARCHITECTURE.md#building).

### Confirming the patch took effect

The settings page shows a **Patch status** line at the top. "Active" is the state you want. "NOT
active" means **no** traffic is being redirected, and the reason is stated right next to it. A third
state, "Active, but …", means the patch is running while at least one HTTP handler could not be
given the proxy — under fail-closed those requests are still blocked rather than leaking, but they
are not being routed either, and a server restart usually clears it. In the server log:

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
| **Bypass list** | *Additional* entries, one per line: CIDR, single IP, hostname or `*.example.com`. Private and link-local networks are compiled in and always bypassed — see below. |
| **Check URL (HTTP)** | Tests whether the proxy forwards plain HTTP. Must answer 2xx; redirects are not followed and therefore fail. Empty = skip. |
| **Check URL (HTTPS)** | Tests whether the proxy opens a CONNECT tunnel — the path almost all Emby traffic takes. Both checks must succeed. Both fields empty = TCP check only. |
| **Check interval** | Seconds between reachability checks. Between 10 and 3600; values outside that range are clamped, including ones edited straight into the options file. |

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

**Repeated warnings are collapsed, never dropped.** A library scan against a dead proxy would
otherwise write one identical line per lookup and bury the first one. Each destination and reason is
logged immediately the first time, then at most once a minute, with the number of occurrences left
out stated on the next line:

```
WARN  Proxy unreachable - request blocked: https://api.themoviedb.org (proxy is unreachable). Fail-closed is active. [+2417 identical in the last 60 s]
```

This affects the log only. Every blocked request is still blocked, and still fails with its own
message.

### What is always bypassed

These are compiled into the plugin and applied on top of whatever the bypass list contains. They
cannot be removed from the settings page:

```
10.0.0.0/8   172.16.0.0/12   192.168.0.0/16   127.0.0.0/8   169.254.0.0/16
::1          fc00::/7        fe80::/10        localhost     *.local
mb3admin.com   *.mb3admin.com   connect.emby.media
```

Sending LAN traffic through a remote proxy is never the intent, and under fail-closed an unreachable
proxy would otherwise cut the server off from its own network. The Emby hosts are fixed for a
different reason: routing licence traffic through a proxy risks breaking Emby Premiere activation,
and that is not a consequence anyone should run into by editing a text box. The bypass list on the
settings page is therefore purely *additional* — it starts empty.

The trade-off is accepted deliberately: on a server whose only route outward is the proxy, these
hosts become unreachable rather than proxied.

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
* **"Ignore certificate validation" is broad.** The option only takes effect while the proxy is
  enabled and its address is valid — with the plugin switched off, Emby's TLS behaviour is left
  exactly as it was found. But while it is in effect it covers *every* outbound connection the Emby
  core makes: the proxy itself, the destinations tunnelled through it, and the destinations that go
  out directly because they are on the bypass list — Emby's licensing hosts among them. A
  certificate callback is handed the TLS handshake, not the request that triggered it, so the plugin
  cannot narrow this any further. Only enable it when the proxy uses a self-signed certificate.
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

## License

Emby Proxy Router is released under the **GNU General Public License v3.0** (GPL-3.0). You may use,
redistribute, and modify the software — but if you pass on a (modified) version, in source or as a
compiled DLL, you must make the complete corresponding source available under the same license
(GPL §6). The full text is in [`LICENSE`](LICENSE).

Copyright (C) 2026 anym001

---

Built with [Claude Code](https://claude.ai/code)
