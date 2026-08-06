## Summary

<!-- What does this PR change and why? -->

## Related issues

<!-- e.g. Closes #123 -->

## Checklist

- [ ] Changes are scoped to a single topic
- [ ] Still within scope — see "What this plugin explicitly does NOT do" in [README.md](../README.md)
- [ ] `dotnet build -c Release` is clean, and the output is still a single DLL (no `0Harmony.dll` beside it)
- [ ] `build/verify-patch-target.sh` passes — mandatory when `build/emby-version.txt` changes
- [ ] Behaviour was actually exercised, not just compiled (routing, parsing, bypass matching, localization)
- [ ] New user-visible strings exist in **every** `Localization/*.json`, not only `en.json`
- [ ] Request URLs are logged as scheme, host and port only — never paths or query strings
- [ ] No `lib/*.dll`, no build output, no secrets committed
- [ ] Documentation updated where relevant (README / CLAUDE.md)
