# Emby Proxy Router

A minimal Emby Server plugin with exactly one job: route the outbound HTTP(S) traffic that the
**Emby server core itself** initiates through a configurable proxy — HTTP, HTTPS or **SOCKS5** —
while always contacting private networks directly.

## What this plugin does

* Routes the Emby core's HTTP clients through a proxy: metadata providers (TMDB, TVDB, …),
  remote image providers, subtitle downloads.
* Supports **HTTP, HTTPS and SOCKS5 proxies**, selectable via dropdown or directly via URL scheme
  (`socks5://host:1080`).
* Checks the proxy when you open or save the settings page, and shows the result there. The check
  talks to the proxy and to nothing else.
* **No fallback to a direct connection.** A configured proxy is used; if it cannot be reached, the
  request fails. That is how curl, a browser and every other program handed a proxy address behave,
  and it is what makes the routing safe without the plugin having to track whether the proxy is up.
* Routes RFC1918 and link-local directly by default, with a switch to send those through the proxy
  too. Loopback is never proxied.

## What this plugin explicitly does NOT do

This is intentional — the point of the project is a single, auditable responsibility:

* **No** subtitle logic, no metadata enrichment, no image processing.
* **No** auto-update mechanism, no telemetry, no phone-home behaviour.
* **No** system-wide proxy configuration. Only Emby's own HTTP stack is redirected; `ffmpeg`, DLNA,
  client connections and everything else are untouched.
* **No** proxying of inbound connections. Reverse-proxy operation is a different problem.
* **No** circumvention of Emby's licence check. Licence traffic goes through the proxy like
  everything else; the plugin neither blocks it nor rewrites it.

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
given the proxy — those requests are blocked rather than leaking, but they
are not being routed either, and a server restart usually clears it. In the server log:

```
Harmony patch active on HttpMessageHandler ApplicationHost.CreateHttpClientHandler(HttpMessageHandlerOptions) (Emby.Server.Implementations 4.9.5.0).
Proxy Router: enabled - socks5://192.168.1.10:1080 (auth as user) | private networks bypassed
```

## Configuration

| Field | Meaning |
| --- | --- |
| **Enable proxy** | Off = Emby behaves as if the plugin were not installed. |
| **Proxy scheme** | `Http`, `Https` or `Socks5`. Only used when the address carries no scheme of its own. |
| **Proxy address** | `host:port` (e.g. `192.168.1.10:8080`) or a full URL (e.g. `socks5://192.168.1.10:1080`). A port is mandatory. |
| **Username / Password** | Optional. Take precedence over credentials embedded in the URL. Credentials *in* the address need the URL form (`http://user:password@host:port`); in the bare `host:port` form there is no scheme to attach them to and the address is rejected. |
| **Ignore certificate validation** | For HTTPS proxies using a self-signed certificate. |
| **Bypass proxy for private networks** | On (default) = RFC1918, link-local, ULA and `*.local` go directly. Off = they go through the proxy too, so a dead proxy also cuts the server off from its own LAN. Loopback is unaffected either way. |
| **Bypass list** | *Additional* entries, one per line: CIDR, single IP, hostname or `*.example.com`. Applies on top of the switch above, and still applies when it is off. |

### What happens when the proxy is down

Requests that would have used it fail. There is no fallback to a direct connection, and no setting
to enable one — the same behaviour `curl`, a browser or any other program gets when it is handed a
proxy address that stops answering. Nothing slips past the proxy unnoticed because nothing is ever
routed around it.

Metadata lookups will therefore fail for as long as the proxy is gone. That is the price, and it is
the point.

One case is different, because it is the one .NET cannot express: the proxy is switched **on** and
its address does not parse. There is no proxy to connect to and no failure to inherit, so the plugin
refuses the request itself and says why:

```
ERROR Request blocked, no usable proxy: https://api.themoviedb.org (proxy is enabled but misconfigured: Proxy address needs an explicit port.)
```

**Repeated messages are collapsed, never dropped.** A library scan against a misconfigured proxy
would otherwise write one identical line per lookup and bury the first one. Each destination is
logged immediately the first time, then at most once a minute, with the number left out stated on
the next line:

```
ERROR Request blocked, no usable proxy: https://api.themoviedb.org (...) [+2417 identical in the last 60 s]
```

This affects the log only. Every blocked request is still blocked, and still fails with its own
message.

### Checking the proxy

Opening or saving the settings page runs one check and shows the result. There is no timer and no
periodic check.

The check talks to the proxy and to nothing else. For SOCKS5 it completes the handshake and, if you
configured a username, the authentication — so it catches a wrong password, and it warns when the
proxy accepts you without the credentials you supplied, which otherwise leaves a configuration that
looks authenticated and is not. For an HTTPS proxy it completes the TLS handshake. For a plain HTTP
proxy it establishes that something accepts connections on that port, and claims no more.

It deliberately stops before the point where the proxy would connect somewhere on your behalf. That
is why there is no check URL to configure: fetching one would show a third party the proxy's egress
address every time, and make the verdict depend on that party's uptime. The trade-off is stated
under Known limitations — this check cannot prove the proxy actually forwards traffic.

### What is bypassed without any configuration

**Never proxied, not switchable:**

```
127.0.0.0/8   ::1   localhost
```

A proxy somewhere else has no route back to this machine, so a request to the server's own loopback
address sent through it cannot succeed however the plugin is configured. Making that switchable
would only offer a setting whose "on" position is always wrong. .NET's own `WebProxy` agrees —
it bypasses a loopback host before it consults its bypass settings at all.

**Bypassed by default, controlled by "Bypass proxy for private networks":**

```
10.0.0.0/8   172.16.0.0/12   192.168.0.0/16   169.254.0.0/16
fc00::/7     fe80::/10       *.local
```

A trailing dot is ignored throughout, so `emby.local.` is `emby.local`.

Sending LAN traffic through a remote proxy is rarely the intent, and an unreachable proxy would
otherwise cut the server off from its own network — which is why this is on by default.
Switch it off when the proxy genuinely is the machine's only route outward and you want everything,
without exception, to take it.

**Everything matched by name rather than by address range goes in the bypass list**, and that is
more than it sounds. Matching is **literal, with no DNS resolution** (see Known limitations), so a
LAN device addressed by a name is covered by none of the above — it is a name, not an IP:

* dotted names pointing at a LAN address — `emby.lan`, `nas.home.arpa`, `nas.fritz.box`, or your own
  domain on a 192.168 address
* short names with no dot at all — `nas`, `router`. These are **not** bypassed on their own: the
  compiled-in rules match allocated address ranges, never the shape of a name. Write `nas` in the
  list and it is matched exactly.

The list is *additional* — it starts empty, and it keeps working when the switch is off.

**Emby's own licensing and Connect hosts are not bypassed.** `mb3admin.com` and
`connect.emby.media` go through the proxy like every other destination. Bypassing them would leave a
server whose only route outward *is* the proxy unable to reach them at all, and it would buy little
privacy in exchange: a licence check carries the key identifying the installation either way. The
cost of routing them is stated under Known limitations.

Log messages deliberately contain only scheme, host and port — never the path. Paths and query
strings of metadata lookups carry title information and frequently API keys.

### Languages

The settings page follows the server's display language; there is deliberately **no language setting
in the plugin**, and a change takes effect without a restart. Translations are embedded in the DLL —
there are no loose files to deploy. A language the plugin does not ship shows English, and an
incomplete translation falls back to English per string rather than showing blank labels.

**The server log stays English regardless.** Only the dashboard is translated. A log line is usually
read by whoever is debugging the server rather than by whoever picked the language, and often away
from the machine — in an issue, or searched for a phrase from this README — so translating it would
only make it harder to search and harder to pass on.

Adding a language is a single file: copy `src/EmbyProxyRouter/Localization/en.json` to `<code>.json`
using the code Emby uses (`fr`, `zh-CN`, `pt-BR`, …), translate the values, leave the keys untouched
and rebuild. Leave out the keys prefixed `Log` — those are the log messages and are meant to stay
English. Pull requests with translations are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

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
* **The settings-page check cannot prove the proxy forwards traffic.** It establishes that the proxy
  answers, and for SOCKS5 that it accepts your credentials — everything that can be established
  without asking the proxy to connect somewhere on your behalf. A proxy that authenticates and then
  refuses every request still shows as working.
* **"Ignore certificate validation" is broad.** The option only takes effect while the proxy is
  enabled and its address is valid — with the plugin switched off, Emby's TLS behaviour is left
  exactly as it was found. But while it is in effect it covers *every* outbound connection the Emby
  core makes: the proxy itself, the destinations tunnelled through it, and the destinations that go
  out directly because they are on the bypass list. A certificate callback is handed the TLS
  handshake, not the request that triggered it, so the plugin cannot narrow this any further. Only
  enable it when the proxy uses a self-signed certificate.
* **Credentials are stored in plain text.** Emby persists plugin options as JSON under
  `/config/plugins/configurations/`. The password field is masked in the UI, not in the file.
* **Bound to an internal Emby method.** The patched method is not public API, so an Emby update can
  change it at any time. The plugin verifies the signature at startup and reports a mismatch loudly
  instead of silently doing nothing — but it cannot repair one.
* **Emby Premiere validation depends on the proxy.** Licence traffic goes through the proxy, so an
  unreachable proxy stops Premiere from validating along with everything else, and the proxy's
  egress address is what Emby's licensing servers see. If that is not what you want, put
  `mb3admin.com`, `*.mb3admin.com` and `connect.emby.media` in the bypass list.

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
