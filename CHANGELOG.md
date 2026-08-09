# Changelog

## [1.1.1](https://github.com/anym001/emby-proxy-plugin/compare/v1.1.0...v1.1.1) (2026-08-09)


### Bug Fixes

* anchor release-please's manifest to the tag that actually exists ([41c1f8d](https://github.com/anym001/emby-proxy-plugin/commit/41c1f8dcb77cc512e9a99b38ac959fa755c62400))
* rename the private authority-parsing helper, it collided with Authority ([8926031](https://github.com/anym001/emby-proxy-plugin/commit/8926031dc92e123a465845f1487192ab94896640))

## [1.1.0](https://github.com/anym001/emby-proxy-plugin/compare/v1.0.1...v1.1.0) (2026-08-08)


### ⚠ BREAKING CHANGES

* drop fail-open, and with it every reason to poll anything
* default the private-networks bypass to off
* ship the version the repository claims, and check it from both ends

### Features

* bypass single-label hostnames, route Emby's own hosts through the proxy ([ca84065](https://github.com/anym001/emby-proxy-plugin/commit/ca84065a65c0ab10b977e04d82ea63b4fb819677))
* make the private-network bypass a setting, keep loopback unconditional ([5e2e6eb](https://github.com/anym001/emby-proxy-plugin/commit/5e2e6ebe1269dbe9f9cd0304437be8048eaac3ec))
* default the private-networks bypass to off ([a38b098](https://github.com/anym001/emby-proxy-plugin/commit/a38b09841e5d6754679b2e4d84136db36cc3bcf1))


### Bug Fixes

* keep credentials out of parse errors and harden the version pin ([e90257a](https://github.com/anym001/emby-proxy-plugin/commit/e90257a28431d4da27173b04bc987e810a9dac95))
* refuse requests on a handler the proxy could not be attached to ([e05a01e](https://github.com/anym001/emby-proxy-plugin/commit/e05a01e04f9600847464c57834d2f706556243dc))
* stop echoing the failing port text, and checksum the actionlint download ([e1c6c97](https://github.com/anym001/emby-proxy-plugin/commit/e1c6c978e7b0f1b7bd8ad8ec9fe8946ae28c3ee9))
* ship the version the repository claims, and check it from both ends ([1463fdc](https://github.com/anym001/emby-proxy-plugin/commit/1463fdc9abd5e9a17dac82d58d72d68d78864716))
* say why a SOCKS5 check failed when the port never answers ([70ad5f2](https://github.com/anym001/emby-proxy-plugin/commit/70ad5f263eb919ad763d4f078100aebf49a21529))


### Code Refactoring

* drop fail-open, and with it every reason to poll anything ([8545fb6](https://github.com/anym001/emby-proxy-plugin/commit/8545fb6d546841467c740a7dd0184a5d05d688f6))

## [1.0.1](https://github.com/anym001/emby-proxy-plugin/compare/v1.0.0...v1.0.1) (2026-08-07)


### Features

* show a tile for the plugin in the dashboard's plugin list ([8509055](https://github.com/anym001/emby-proxy-plugin/commit/850905508315849f0b3004b3b090be8d42d6eea2))


### Bug Fixes

* scope the certificate override, harden parsing and state handling ([061f65a](https://github.com/anym001/emby-proxy-plugin/commit/061f65a8b8a90192462be3cecbb41ccb9d419248))

## 1.0.0 (2026-08-07)

Initial release: routes outbound HTTP(S) traffic initiated by the Emby core through an HTTP, HTTPS
or SOCKS5 proxy, and blocks it when the proxy is unavailable rather than letting it out directly.
Most of this predates the Conventional Commits convention this changelog otherwise relies on, so
only what already carried a recognized prefix is listed below.

### Features

* make the bypass guarantees real and require every check URL ([3639503](https://github.com/anym001/emby-proxy-plugin/commit/36395036c157b4135eb6bcd37dd7c4a7a122c15b))
