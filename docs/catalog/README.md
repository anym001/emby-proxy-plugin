# Catalog assets

The images Emby's package form asks for, and what they are.

| File | Field on the form | Notes |
| --- | --- | --- |
| `preview.png` | Preview Image | 2720x1880. Screenshot of the settings page on a real Emby Server 4.9.5.0 with the plugin installed. |
| `../../src/EmbyProxyRouter/thumb.png` | Thumb Image | 640x360 (16:9). Not duplicated here — it is embedded in the DLL and shown in the dashboard's plugin list, so it lives with the source that embeds it. |

`preview.png` is a real screenshot, not a mock-up: the server was started from the pinned
`emby-server-deb_4.9.5.0_amd64.deb`, the release DLL was copied into its `plugins` folder, and the
settings were entered through the dashboard. Both status lines are the plugin's own output. The
proxy behind it is a stub that answers the SOCKS5 greeting and the RFC1929 sub-negotiation and
nothing else, which is exactly as far as `ProxyProbe` goes — see the class remarks on why it stops
before CONNECT.

The address is `192.0.2.2`, from the range RFC 5737 reserves for documentation, so the image shows a
working configuration without publishing anyone's real proxy.

Regenerate it after any change to `PluginOptions` or to the status lines, or the catalog will show a
page the plugin no longer renders.
