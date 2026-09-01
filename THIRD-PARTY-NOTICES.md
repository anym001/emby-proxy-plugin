# Third-party notices

`EmbyProxyRouter.dll` is a single self-contained file: the third-party code it uses is inside the
deliverable, not beside it. This file is the notice MIT requires to travel with those copies, and it
is embedded in the DLL so a binary separated from this repository still carries it.

## Harmony (Lib.Harmony)

Harmony is the runtime patching library the plugin uses to attach itself to Emby's HTTP handler
factory. It is compiled into `EmbyProxyRouter.dll` as an embedded `0Harmony.dll` and loaded from
there at runtime (`Patch/HarmonyLoader.cs`), so every copy of the plugin is a copy of Harmony.

* Author: Andreas Pardeike
* Project: <https://github.com/pardeike/Harmony>
* License: MIT

The version lives in the `Lib.Harmony` `PackageReference` in
`src/EmbyProxyRouter/EmbyProxyRouter.csproj` and is not repeated here: Dependabot bumps it on a
schedule, and a second copy would go stale in a pull request nobody opens a licence file for. The
notice does not depend on it — the copyright holder and the text below hold for every version.

```
MIT License

Copyright (c) 2017 Andreas Pardeike

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## What is *not* redistributed

The Emby assemblies (`MediaBrowser.Common`, `MediaBrowser.Controller`, `MediaBrowser.Model`,
`Emby.Web.GenericEdit`, and `Emby.Server.Implementations` for the patch-target check) are proprietary
Emby binaries. They are referenced at compile time only — `<Private>false</Private>` in the csproj
keeps them out of the build output, and `build/fetch-emby-refs.sh` pulls them from Emby's own release
package into a gitignored `lib/`. They are neither committed to this repository nor shipped inside
the plugin.

The test project's dependencies (xunit, the VSTest tooling) build nothing that ships and are not
part of the deliverable.
