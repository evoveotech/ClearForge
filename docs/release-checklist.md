# Release checklist

Status legend: `[x]` verified with evidence · `[ ]` open · `~` partial/in progress.
Last updated: 2026-10-27 (post-M8.4b, HEAD ~`ff85847`).

## Windows — current status: not yet RELEASE-READY

- [x] Engine + unit/integration suites green (`dotnet test` 384/384 local; CI green)
- [x] `flutter test` (70) + `flutter analyze` clean
- [x] `flutter build windows --release` produces a working binary (verified: app launches, spawns bundled engine)
- [x] Packaged layout verified: `engine\ClearForge.Host.exe` beside app resolves and spawns
- [x] Portable ZIP package reproducible (`packaging\build-package.ps1` → 28 MB, clean-extract launch verified)
- [x] M4.4 GUI workflow passes end-to-end on CI (scan→cancel→commit→byte-identical restore→plan)
- [ ] Destructive-test scenarios on disposable VM snapshots (only local user-dir verified)
- [ ] ARM64 CI lane + ARM64 build/test (x64 only today)
- [ ] Installer: Inno Setup script exists (`packaging/inno/clearforge.iss`); built artifact not yet produced or tested
- [ ] Self-contained engine decision (zip currently requires .NET 8 Desktop Runtime on the machine)
- [ ] Code-signed engine + UI binaries; signature verification test — **BLOCKED on signing identity**
- [ ] Upgrade / uninstall / rollback validation on a clean machine
- [ ] Clean-machine install test (dependency-free target verified end-to-end)
- [ ] Privacy policy + vulnerability-disclosure channel published
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
