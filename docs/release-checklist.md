# Release checklist (M4 gate — not yet applicable)

- [ ] All destructive-test scenarios pass on disposable VM snapshots
- [ ] `dotnet test` green on x64 and ARM64 CI lanes
- [ ] `flutter test` + `flutter analyze` clean; `flutter build windows --release` produces signed binaries
- [ ] Installer: MSIX or signed installer decision recorded in an ADR
- [ ] Code-signed engine + UI binaries; signature verification test
- [ ] Upgrade / uninstall / rollback tests pass; no orphaned privileged components
- [ ] Privacy policy + vulnerability-disclosure channel published
- [ ] No "performance boost" / "100% safe" claims in any UI or marketing copy
- [ ] Trademark/domain check for "ClearForge" completed
- [ ] Telemetry: off by default; any opt-in path carries no file paths
