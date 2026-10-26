# ClearForge Living Roadmap

Mission: enterprise-quality Windows + macOS maintenance product competitive with
Mac Sai. Statuses: TODO / IN_PROGRESS / TESTING / BLOCKED / DONE.
Verification vocabulary: SCAFFOLDED / COMPILED / TESTED / VERIFIED /
RELEASE-READY / SHIPPED / BLOCKED — never interchange.

## Verified baseline (this session, 2026-10-26)

- `dotnet test native/ClearForge.sln` → **337/337 pass** (incl. 21 M8.3 tests).
- `flutter analyze` → clean. `flutter test` → **56/56 pass**.
- HEAD = `3bac212` on `main`, synced with `origin/main` (M8.3 committed+pushed
  mid-session by the continuing agent pipeline; verified independently here).
- Toolchain: .NET SDK 9.0.203 (per global.json, targets net8.0),
  Flutter 3.41.6 stable, Dart 3.11.4.
- Sole mutation path remains preview → approval grant → commit → per-item
  revalidation → journaled same-volume quarantine → SHA-256 integrity →
  restore. No permanent-delete path exists.

## Milestone ledger

| ID | Scope | Status | Evidence |
|---|---|---|---|
| M0–M8.1.1 | Engine host, IPC, scanner, quarantine journal, unified Smart Scan, acceptance hardening | DONE/VERIFIED | commits `42fcfb8`, `aac2645`; test files M3–M811 |
| M8.0.3 | Journal schema v2 + SHA-256 content integrity | DONE/VERIFIED | `M803Tests.cs`, `tasks/plan.md` |
| M8.2 | Windows cleanup coverage (15 catalog rules) + evidence quality | DONE/VERIFIED | `M82Tests.cs`, `tasks/m82-plan.md` |
| M8.3 | Duplicate review: keeper selection, group binding, fail-closed enforcement | DONE/VERIFIED | commit `3bac212`; 337+56 tests |
| M8.3r | Disk explorer: treemap (Space Lens), large/old files, hardlink consolidation | TODO | roadmap in gap doc §M8.3; none found in source |
| M8.4a | Application inventory (read-only) | DONE/VERIFIED | 355/355 + 61/61 tests; `apps.list` over HKLM64/32+HKCU |
| M8.4b | Leftover mapping + guided uninstall (deep-link only, never auto-run) | TODO | dep M8.4a |
| M8.5 | Startup completeness: HKLM, StartupApproved disabled-state, Task Scheduler/services read-only | TODO | gap doc §M8.5 |
| M9 | Extensions inventory (review-only), monitoring/tray, winget drift, browser history/cookies (lifecycle review gate), scheduling | TODO | gap doc §M9+ |
| REL-1 | Engine discovery Debug-path-only → packaged layout + resolution order | DONE/VERIFIED | `resolveEnginePath`: exePath → env → packaged → Release/Debug walk-up; 9 tests |
| REL-2 | Installer: MSIX/signed-installer ADR → build → upgrade/rollback/uninstall tests | TODO | `installer/` empty; `docs/release-checklist.md` |
| MAC-1 | macOS platform: runner, roots, fs identity, IPC transport, quarantine adapter | BLOCKED | no macOS hardware/toolchain on this machine; engineering + tests only, never claim release-ready |

## Platform capability matrix

| Capability | Windows | macOS |
|---|---|---|
| Engine host + IPC (NDJSON) | TESTED (named pipe, ACL owner-only) | MISSING — Unix socket adapter needed |
| Scan/session/containment | TESTED | MISSING — roots, symlink/TOCTOU model needed |
| Journaled quarantine + SHA-256 + restore | TESTED | MISSING — FS adapter (clonefile/copy semantics) |
| Smart Scan categories | TESTED (15 rules) | MISSING — macOS roots/categories undefined |
| Duplicates + keeper review | TESTED | MISSING |
| Storage recommendations | TESTED | MISSING |
| Startup inventory | PARTIAL (HKCU Run + folder) | MISSING (LaunchAgents/Daemons, login items) |
| App inventory | TESTED (M8.4a registry hives; Appx absent, disclosed) | MISSING |
| Browser cache guard | TESTED | MISSING |
| Local AI worker + model mgmt | TESTED | UNKNOWN (LLamaSharp osx-arm64/x64 viability) |
| Flutter UI | TESTED (debug build + tests) | MISSING (no macos/ runner) |
| Installer/signing | MISSING | BLOCKED (needs Mac + Apple ID creds) |

## Competitive matrix status

Canonical analysis: `docs/competitive/MACSAI_GAP_ANALYSIS.md` (source-backed,
M8.0 snapshot). Deliberate exclusions carried forward: permanent delete /
shredder (conflicts with no-permanent-delete invariant), signature AV
(false-confidence risk; Defender status read-only is the M9 option),
`.lproj`-class bundle mutation, unvalidated "performance boost" claims.

## Risk register

| Risk | Severity | Owner | Status |
|---|---|---|---|
| No installer → product cannot ship/run installed | High | REL-1/REL-2 | open |
| macOS entirely unbuilt and unverifiable on this host | High | MAC-1 | BLOCKED externally |
| Leftover name-matching false positives (M8.4b) | Med | M8.4b | mitigate: lineage model + never-flag list + review-only default |
| Session/eviction bounds | Closed | — | MaxSessions=128, 60min retention, scanning never evicted |
| Quarantine same-volume rule means a user with TEMP on a non-OS volume gets `cross_volume_refused` for the whole temp category (found via CI where workspace TEMP is on D:) | Med | — | open — per-volume quarantine roots are a design decision; documented, not silently changed |
| CI `flutter test` includes live-pipe tests needing a built engine | Closed | — | lanes build engine; tests resolve via `resolveEnginePath` |
| `config/*.yaml` drift vs compiled rules | Low | — | sync enforced by tests per DefaultRules header; audit during M8.4b |

## Next actions

1. M8.4b: leftover/lineage map (review-only), Appx enumeration decision,
   guided-uninstall deep-link evaluation.
2. M8.3r: treemap + large/old files; or REL-1 engine-discovery fix —
   reprioritize each checkpoint by: defects → blockers → end-to-end gaps →
   platform → competitive value.
