# Catalog assets

The images Emby's package form asks for, and what they are.

| File | Field on the form | Notes |
| --- | --- | --- |
| `preview.png` | Preview Image | Screenshot of the settings page with the plugin installed on a running server. |
| `../../src/EmbyProxyRouter/thumb.png` | Thumb Image | The form asks for a 16:9 tile. Not duplicated here — this is the file the csproj embeds and the dashboard shows, so the catalog and the dashboard cannot drift apart. |

No version is written down on this page. The Emby version behind the screenshot is the one pinned in
`build/emby-version.txt`, which is the only place that number lives, and that pin is also the rule:
capture against the pinned version, whatever it is at the time.

## How preview.png is made

It is a real screenshot rather than a mock-up, and reproducing it is the same procedure each time:

1. Start a server from the package `build/fetch-emby-refs.sh` pins — the same download, checksum
   verified against `build/emby-sha256.txt`.
2. Copy the Release DLL into its `plugins` folder and start the server.
3. Point a SOCKS5 stub at a port it can reach. The stub answers the greeting and the RFC1929
   sub-negotiation and nothing else, which is exactly as far as `ProxyProbe` goes — see the class
   remarks on why it stops before CONNECT.
4. Enter the settings through the dashboard and save, so the two status lines are the plugin's own
   output rather than a picture of a form.
5. Capture the page with both status lines and the top of the form visible.

Use `192.0.2.2` as the proxy address, from the range RFC 5737 reserves for documentation, so the image
shows a working configuration without publishing anyone's real proxy.

Because the status lines are rendered by the plugin, the image also stands as evidence that the
Harmony patch applies to a running server and not only to the decompiled method signature.

Retake it when the settings page changes — `PluginOptions` or the status lines — or when the Emby pin
moves, or the catalog shows a page the plugin no longer renders.
