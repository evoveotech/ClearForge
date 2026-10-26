# Release checklist

Status legend: `[x]` verified with evidence · `[ ]` open · `~` partial/in progress.
Last updated: 2026-10-27 (post-M8.4b + REL-2, HEAD ~`ea2af7d`).

## Windows — current status: not yet RELEASE-READY

- [x] Engine + unit/integration suites green (`dotnet test` 384/384 local; CI green)
- [x] `flutter test` (71) + `flutter analyze` clean
- [x] `flutter build windows --release` produces a working binary (verified: app launches, spawns bundled engine)
- [x] Packaged layout verified: `engine\ClearForge.Host.exe` beside app resolves and spawns
- [x] Portable ZIP package reproducible (`packaging\build-package.ps1` → 47 MB, clean-extract launch verified)
- [x] Inno Setup installer builds (`packaging\inno\clearforge.iss` + Inno 6.7.3 → `clearforge-setup-windows-x64-0.1.0-m1.exe`)
- [x] Silent install verified: `/VERYSILENT` → 213 files under `%LOCALAPPDATA%\Programs\ClearForge`, HKCU uninstall key, Start Menu entry
- [x] Installed app launches and spawns bundled engine
- [x] Silent uninstall verified: program files removed, user data (journal/quarantine/models) preserved
- [x] M4.4 GUI workflow passes end-to-end on CI (scan→cancel→commit→byte-identical restore→plan)
- [ ] Destructive-test scenarios on disposable VM snapshots (only local user-dir verified)
- [ ] ARM64 CI lane + ARM64 build/test (x64 only today)
- [x] Self-contained engine (`win-x64`) — zero .NET-runtime prerequisite
- [x] Upgrade validation: install-over-install verified (idempotent, 213 files); rollback = reinstall older package (no versioned rollback machinery — documented limitation)
- [ ] Clean-machine install test (installer verified on dev machine only; a machine without the dev toolchain is still unverified)
- [ ] Code-signed engine + UI binaries; signature verification test — **BLOCKED on signing identity**
- [x] Privacy policy + vulnerability-disclosure channel published (`docs/privacy.md` + GitHub Security Advisories)
- [x] No "performance boost" / "100% safe" claims in UI or docs (audited during copy review)
- [ ] Trademark/domain check for "ClearForge"
- [x] Telemetry: none exists; AI is local-only; any future opt-in must carry no file paths

## macOS — status: BLOCKED

- [ ] macOS build environment (no toolchain access on dev host)
- [ ] Platform adapter implementation + verification
- [ ] Signing + notarization

## Publication gates — all open

- [ ] `RELEASE-READY` requires every `[x]` above + clean-machine + installer validation
- [ ] `SHIPPED` requires an authorized identity to sign and publish
- [x] No platform is labelled `SHIPPED` or `RELEASE-READY` prematurely — status labels used in `tasks/roadmap.md`
