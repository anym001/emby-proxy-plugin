# Catalog assets

The images Emby's package form asks for, and what they are.

| File | Field on the form | Notes |
| --- | --- | --- |
| `preview.png` | Preview Image | Screenshot of the settings page with the plugin installed on a running server. |
| `../../src/EmbyProxyRouter/thumb.png` | Thumb Image | The form asks for a 16:9 tile. Not duplicated here — this is the file the csproj embeds and the dashboard shows, so the catalog and the dashboard cannot drift apart. |

No version is written down on this page. The Emby version the screenshot was taken against is the one
pinned in `build/emby-version.txt`, which is the only place that number lives; repeating it here would
just be a second copy to keep in step. That is also the rule for retaking it: capture against the
pinned version, whatever it is at the time.

`preview.png` is a real screenshot, not a mock-up. The server was started from the package
`build/fetch-emby-refs.sh` pins — the same download, checksum-verified against `build/emby-sha256.txt`
— the release DLL was copied into its `plugins` folder, and the settings were entered through the
dashboard. Both status lines are the plugin's own output, so the image doubles as evidence that the
Harmony patch applies to a real server rather than only to the decompiled method signature.

The proxy behind it is a stub that answers the SOCKS5 greeting and the RFC1929 sub-negotiation and
nothing else, which is exactly as far as `ProxyProbe` goes — see the class remarks on why it stops
before CONNECT. Its address is `192.0.2.2`, from the range RFC 5737 reserves for documentation, so the
image shows a working configuration without publishing anyone's real proxy.

Retake it when the settings page changes — `PluginOptions` or the status lines — or when the Emby pin
moves, or the catalog will show a page the plugin no longer renders.
