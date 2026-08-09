# Emby Proxy Router

A minimal Emby Server plugin with exactly one job: route the outbound HTTP(S) traffic that the
**Emby server core itself** initiates through a configurable proxy — HTTP, HTTPS or **SOCKS5**. All
of that traffic goes through it; opting part of it out is something you configure. Media streaming
and playback are a different kind of traffic entirely and are not covered — see below.

## What this plugin does

* Routes the Emby core's HTTP clients through a proxy: metadata providers (TMDB, TVDB, …),
  remote image providers, subtitle downloads.
* Supports **HTTP, HTTPS and SOCKS5 proxies**, selectable via dropdown or directly via URL scheme
  (`socks5://host:1080`).
* Checks the proxy when you open or save the settings page, and shows the result there. The check
  talks to the proxy and to nothing else.
* **No fallback to a direct connection.** A configured proxy is used; if it cannot be reached, the
  request fails.

## What this plugin explicitly does NOT do

This is intentional — the point of the project is a single, auditable responsibility:

* **No** subtitle logic, no metadata enrichment, no image processing.
* **No** auto-update mechanism, no telemetry, no phone-home behaviour.
* **No** system-wide proxy configuration. Only Emby's own HTTP stack is redirected; `ffmpeg`, DLNA,
  client connections and everything else are untouched.
* **No** proxying of streaming or playback traffic. Video and audio delivered to clients — direct
  play, direct stream, or transcoding via `ffmpeg` — never passes through the patched HTTP stack and
  is untouched regardless of whether the proxy is enabled. If the goal is tunnelling IPTV/Live TV
  playback or watched media through the proxy, this plugin does not do that; only the server's own
  API-style traffic (metadata, images, subtitles, licence checks) is redirected.
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
| **Bypass proxy for private networks** | Off (default) = everything goes through the proxy, private networks included, so a dead proxy also cuts the server off from its own LAN. On = RFC1918, link-local, ULA and `*.local` go directly instead. Loopback is unaffected either way. |
| **Bypass list** | *Additional* entries, one per line: CIDR, single IP, hostname or `*.example.com`. Applies on top of the switch above, and still applies when it is off. |

### What happens when the proxy is down

Requests that would have used it fail. There is no fallback to a direct connection, and no setting
to enable one. Metadata lookups will fail for as long as the proxy is gone.

One case is different: the proxy is switched **on** and its address does not parse. The plugin
refuses those requests itself and says why:

```
ERROR Request blocked, no usable proxy: https://api.themoviedb.org (proxy is enabled but misconfigured: Proxy address needs an explicit port.)
```

**Repeated messages are collapsed, never dropped.** Each destination is logged immediately the first
time, then at most once a minute, with the number left out stated on the next line:

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

There is no check URL to configure: the check stops before the point where the proxy would connect
somewhere on your behalf. It therefore cannot prove the proxy forwards traffic — see Known
limitations.

### What bypasses the proxy

**Never proxied, not switchable:**

```
127.0.0.0/8   ::1   localhost
```

A proxy elsewhere has no route back to this machine, so these cannot succeed through one.

**Bypassed only when "Bypass proxy for private networks" is switched on — it is off by default:**

```
10.0.0.0/8   172.16.0.0/12   192.168.0.0/16   169.254.0.0/16
fc00::/7     fe80::/10       *.local
```

A trailing dot is ignored throughout, so `emby.local.` is `emby.local`.

Switch it on when the server also talks to its own LAN — another Emby server, a DLNA endpoint, a
local metadata cache — and you do not want that traffic crossing a remote proxy, or dying with it.
Left off, an unreachable proxy costs you those alongside the internet.

**Everything matched by name rather than by address range goes in the bypass list.** Matching is
**literal, with no DNS resolution** (see Known limitations), so a LAN device addressed by a name is
covered by none of the above — it is a name, not an IP:

* dotted names pointing at a LAN address — `emby.lan`, `nas.home.arpa`, `nas.fritz.box`, or your own
  domain on a 192.168 address
* short names with no dot at all — `nas`, `router`. These are **not** bypassed on their own: the
  compiled-in rules match allocated address ranges, never the shape of a name. Write `nas` in the
  list and it is matched exactly.

The list is *additional* — it starts empty, and it keeps working when the switch is off.

**Emby's own licensing and Connect hosts are not bypassed.** `mb3admin.com` and
`connect.emby.media` go through the proxy like every other destination — see Known limitations.

Log messages deliberately contain only scheme, host and port — never the path. Paths and query
strings of metadata lookups carry title information and frequently API keys.

### Languages

The settings page follows the server's display language; there is deliberately **no language setting
in the plugin**, and a change takes effect without a restart. Translations are embedded in the DLL —
there are no loose files to deploy. A language the plugin does not ship shows English, and an
incomplete translation falls back to English per string rather than showing blank labels.

**The server log stays English regardless.** Only the dashboard is translated.

Adding a language is a single file: copy `src/EmbyProxyRouter/Localization/en.json` to `<code>.json`
using the code Emby uses (`fr`, `zh-CN`, `pt-BR`, …), translate the values, leave the keys untouched
and rebuild. Leave out the keys prefixed `Log` — those are the log messages and are meant to stay
English. Pull requests with translations are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Known limitations

* **Live TV is only partially covered, and never for the stream itself.** Some of its lookup traffic
  (EPG, channel data) may use HTTP handlers the plugin reaches; the actual video stream from a tuner
  or IPTV source is delivered via `ffmpeg`/a direct connection and never goes through this plugin —
  see "What this plugin explicitly does NOT do" above.
* **The bypass list performs no DNS resolution.** Hostnames are matched literally, and IP rules only
  apply to IP literals.
* **HTTP(S) proxy authentication is reactive.** .NET sends credentials only after the proxy answers
  `407`. A proxy that rejects outright without issuing a challenge will not work.
* **The settings-page check cannot prove the proxy forwards traffic.** It establishes that the proxy
  answers, and for SOCKS5 that it accepts your credentials. One that authenticates and then refuses
  every request still shows as working.
* **"Ignore certificate validation" is broad.** While in effect it covers *every* outbound connection
  the Emby core makes — not only the proxy, but the destinations tunnelled through it and those on
  the bypass list. Only enable it when the proxy uses a self-signed certificate.
* **Credentials are stored in plain text.** Emby persists plugin options as JSON under
  `/config/plugins/configurations/`. The password field is masked in the UI, not in the file.
* **An installed plugin decides the route even when it is switched off.** Emby's handlers are built
  once and cached for the life of the process, so the plugin has to attach itself to every one of
  them whether or not the proxy is enabled. With it disabled that means a direct connection — and a
  proxy configured in the environment (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`), which .NET would
  otherwise honour, is not used. If you want the environment's proxy back, remove the plugin rather
  than disabling it.
* **Bound to an internal Emby method.** The patched method is not public API, so an Emby update can
  change it. The plugin verifies it at startup and reports a mismatch, but cannot repair one.
* **Emby Premiere validation depends on the proxy.** Licence traffic goes through it, so an
  unreachable proxy stops Premiere validating, and Emby's licensing servers see the proxy's egress
  address. Put `mb3admin.com`, `*.mb3admin.com` and `connect.emby.media` in the bypass list if that
  is not what you want.

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
