# Spec: ClearForge — Independent Premium Windows PC Cleaner

Status: DRAFT v0.1 (SPECIFY phase) — awaiting human review
Stack: Flutter desktop UI + C# (.NET 8) native Windows engine
Platform: Windows 10 22H2+ / Windows 11 x64
Authoring rule: every behaviour below is either (a) observed on the reference machine, (b) documented public behaviour of comparable products, or (c) marked as an assumption. No proprietary implementation details are used or claimed.

---

## 1. Objective

Build an independently implemented Windows cleaner whose observable feature set is comparable to CCleaner Free/Professional while exceeding native Windows cleanup (Storage Sense, Disk Cleanup, DISM) in transparency, recoverability, and user control.

User: Windows power users and IT-adjacent users who want a clear audit trail of what was removed and the ability to undo it — the differentiators native tools lack.

Success criteria (testable):
- A full scan produces a classified, per-item preview with byte totals and a plain-language "why safe" for every item, in under 60 s on a mid-range SSD machine.
- Every delete/move is journaled; 100% of quarantined items are restorable byte-identical (SHA-256 verified).
- The tool performs zero writes before explicit user consent, and zero writes ever to Risk Tier 3 targets.
- All cleanup runs unelevated except categories that physically require elevation, which trigger exactly one UAC prompt per session, disclosed in advance.

---

## 2. Evidence base

### 2.1 Observed on reference machine (Windows 11 Home, build 26300, 2026-10-24)

**Registered native cleanup handlers** — `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches` contained 30 handlers. These are the categories Windows' own Disk Cleanup / Storage settings expose; they define the parity floor:

| Handler key | Notes |
|---|---|
| Active Setup Temp Folders, Temporary Setup Files, Setup Log Files | Setup residue |
| Content Indexer Cleaner | Search index temp data |
| D3D Shader Cache | Regenerable GPU cache |
| Delivery Optimization Files | `domgmt.dll` handler |
| Device Driver Packages | `pnpclean.dll` — driver store superseded packages |
| Diagnostic Data Viewer database files, Feedback Hub Archive log files | `utcutil.dll` |
| Downloaded Program Files, Offline Pages Files, Internet Cache Files | Legacy IE-era handlers |
| DownloadsFolder | **Personal-adjacent — handler exists but opt-in only** |
| Language Pack | Unused language packs |
| Old ChkDsk Files | Found fragments |
| Previous Installations, Windows ESD installation files, Upgrade Discarded Files, Windows Upgrade Log Files, Windows Reset Log Files | `setupcln.dll` — **recovery-adjacent, Tier 2** |
| Recycle Bin | Opt-in |
| RetailDemo Offline Content | n/a on retail machines |
| System error memory dump files, System error minidump files | `werfault.exe` — admin scope |
| Temporary Files, Temporary Sync Files | Core temp |
| Thumbnail Cache | Regenerable |
| Update Cleanup | `scavengeui.dll` → DISM component cleanup |
| User file versions | File History versions — personal-adjacent |
| Windows Defender | Definition-update leftovers |
| Windows Error Reporting Files | `wer.dll` |

**Storage Sense policy** — `HKCU\...\StorageSense\Parameters\StoragePolicy` observed: enabled (`01`=1), recycle-bin age gate (`256`=30 days), temp-file toggle (`32`=0 off), Downloads cleanup (`512`=0 off), OneDrive cloud-dehydration (`128`=30 days). Confirms Microsoft's own model: age-gated, opt-in, and *never* touches Downloads by default.

**Measured reclaimable inventory** (reference machine, user scope):
- `%TEMP%` 286.6 MB; `C:\Windows\Temp` 14.7 MB; `SoftwareDistribution\Download` 504.9 MB; `C:\Windows\Logs\CBS` 183.6 MB; Explorer thumbnail/icon cache 392.8 MB; `CrashDumps` 18.1 MB; `Prefetch` ~0; WER ~1 MB.

**Unverified on this machine**: `dism /Online /Cleanup-Image /AnalyzeComponentStore` requires elevation — flagged in §10.

### 2.2 Documented observable behaviour — CCleaner Free / Professional (v6.x UI)

Catalogued from the product's public interface and documentation (no binary analysis):

- **Health Check (Free)**: one-click scan grouping findings into Privacy (trackers/browser data), Space (junk files), Speed (startup items), Security (out-of-date apps → upsell).
- **Custom Clean → Windows tab**: Internet Explorer/Edge Legacy data, Windows Explorer MRUs (recent docs, Run box, jump lists, thumbnail cache), system logs, memory/minidumps, chkdsk fragments, Windows error reporting, DNS cache, prefetch (advanced), and user-drawer include list.
- **Custom Clean → Applications tab**: per-application checkboxes — browsers (cache, history, cookies, session, download history, saved form data, saved passwords as *separate* toggles), plus caches for common third-party apps.
- **Registry Cleaner**: scans "integrity" categories (missing shared DLLs, unused file extensions, obsolete software entries, MUI cache, app paths, installer refs). **Excluded from this product — see §6 R3 and §9.**
- **Tools**: Uninstall, Startup manager, Browser plugins, Disk Analyzer (space visualization), Duplicate Finder, System Restore manager, Drive Wiper (free-space overwrite).
- **Options**: cookie keep-list, include/exclude rules, secure deletion (1/3/7/35 passes), "only delete temp files older than 24h", shutdown/close after cleaning, per-user scope.
- **Professional adds**: scheduled cleaning, Smart Cleaning (auto-clean on browser close, junk-threshold alerts), multi-user cleaning, Software/Driver Updater, priority support.

### 2.3 Documented native capabilities

- **Storage Sense**: scheduled/on-low-disk cleanup of temp files, Recycle Bin (age-gated), Downloads (off by default), OneDrive on-demand rehydration. No preview ledger, no per-item restore.
- **Disk Cleanup (cleanmgr.exe)**: present on the reference machine; handler-driven, system-scope after elevation.
- **DISM**: `/StartComponentCleanup`, `/ResetBase`, `/SPSuperseded` — the only supported path for WinSxS reduction.
- **Task Manager / Settings > Apps > Startup**: startup management parity.
- **cipher.exe /w**: native free-space wipe parity for Drive Wiper.
- **Optimize Drives (defrag/TRIM)**: drive maintenance parity.

---

## 3. Feature and behaviour matrix

Legend: ✅ supported · ⚠️ partial/different · ❌ absent · 🚫 deliberately excluded

| Capability | CCleaner Free | CCleaner Pro | Native Windows | ClearForge MVP | ClearForge Pro |
|---|---|---|---|---|---|
| Temp/cache scan + clean | ✅ | ✅ | ✅ Storage Sense/handlers | ✅ + per-item preview | ✅ |
| Recycle Bin | ✅ | ✅ | ✅ age-gated | ✅ opt-in checkbox | ✅ |
| Windows Update / WinSxS cleanup | ⚠️ via handler | ⚠️ | ✅ DISM | ✅ DISM wrapper (elevated, disclosed) | ✅ |
| Previous installs / ESD / upgrade logs | ✅ | ✅ | ✅ | ⚠️ report-only MVP (Tier 2, needs informed-consent flow) | ✅ |
| Memory/minidumps | ✅ | ✅ | ✅ handler | ✅ admin scope, keep-latest option | ✅ |
| Browser cache | ✅ | ✅ | ⚠️ per-browser UI | ✅ Chromium + Firefox | ✅ |
| Browser cookies/history | ✅ | ✅ | ⚠️ | ❌ MVP | ⚠️ opt-in, per-site keep-list, closed-browser check |
| Saved passwords / sessions | ✅ toggle | ✅ | ⚠️ | 🚫 never | 🚫 never |
| Third-party app cache modules | ✅ large list | ✅ | ❌ | ⚠️ top-20 curated | ✅ expandable catalog |
| Registry cleaner | ✅ | ✅ | ❌ | 🚫 excluded | 🚫 excluded |
| Startup manager | ✅ | ✅ | ✅ | ✅ | ✅ |
| Uninstaller | ✅ | ✅ | ✅ | ✅ | ✅ |
| Duplicate finder | ✅ | ✅ | ❌ | ❌ | ✅ |
| Disk analyzer | ✅ | ✅ | ⚠️ Storage view | ❌ | ✅ |
| Drive/free-space wiper | ✅ | ✅ | ⚠️ cipher /w | ❌ | ✅ documented-method overwrite |
| Software updater | ❌ | ✅ | ⚠️ winget | ❌ | ✅ via `winget upgrade` (documented API) |
| Scheduled cleaning | ❌ | ✅ | ✅ Storage Sense | ❌ | ✅ Task Scheduler |
| Smart/automatic cleaning | ❌ | ✅ | ✅ | ❌ | ✅ opt-in, whitelisted categories only |
| Multi-user profiles | ❌ | ✅ | ⚠️ | ❌ | ✅ elevated helper |
| Per-item preview + provenance | ❌ category-level | ❌ | ❌ | ✅ differentiator | ✅ |
| Quarantine + restore | ❌ (backup prompt for registry only) | ❌ | ❌ | ✅ | ✅ |
| Secure deletion | ✅ | ✅ | ⚠️ | ❌ | ✅ opt-in, SSD TRIM caveat disclosed |
| Scan journal / audit report | ❌ | ❌ | ❌ | ✅ | ✅ |

---

## 4. Proposed architecture and module boundaries

### 4.1 Process topology

```
┌─────────────────────────────┐   named pipe (JSON-RPC 2.0)   ┌──────────────────────────┐
│  ClearForge UI (Flutter)    │◄────────────────────────────►│  clearforge-engine.exe   │
│  unelevated, user session   │                              │  C# .NET 8, unelevated   │
└─────────────────────────────┘                              └───────────┬──────────────┘
                                                                         │ on-demand, UAC-consented
                                                              ┌──────────▼──────────────┐
                                                              │ clearforge-helper.exe   │
                                                              │ elevated, per-session   │
                                                              └─────────────────────────┘
```

- **UI owns nothing destructive.** It renders plans and collects consent; the engine re-validates consent tokens server-side before executing. UI cannot inject arbitrary paths — all requests reference scan-result item IDs, not raw paths.
- **Engine** (unelevated default): all user-scope scanning/cleaning. 
- **Helper** (elevated): spawned only when the plan contains Tier-2 admin items; lives for the session; killed on exit. One disclosed UAC prompt. No service, no persistence, no auto-start in MVP.
- **Premium scheduler**: registers a standard Task Scheduler task running the engine with `-profile scheduled`; no driver, no watchdog, no always-on process.

### 4.2 Engine module boundaries (dependencies point inward)

```
domain/        classification model, risk tiers, policy rules, Result<T> errors
catalog/       category definitions + location providers (data-driven, versioned)
scanner/       read-only enumeration, hashing, provenance capture
planner/       applies exclusions/policy → Plan { items[], tiers, consentRequired }
executor/      journaled per-item operations: verify → quarantine-move/delete → commit
quarantine/    staging store, manifest, retention, restore
reporter/      scan/clean reports, Evidence-style manifest (json + human summary)
host/          JSON-RPC server, auth token, cancellation
```

Contracts: `IScanner → ScanResult`, `IPlanner → Plan`, `IExecutor → OperationJournal`, `IQuarantine → RestoreHandle`. Engine never executes a `Plan` that lacks per-category `ConsentToken`s minted by the UI consent step.

### 4.3 Key APIs (all documented)

`FindFirstFileEx`/`NtQueryDirectoryFile` enumeration · `MoveFileEx` (quarantine = move, preserves ACLs) · `DeleteFile`/`RemoveDirectory` · Recycle Bin via `IFileOperation`+`FOF_ALLOWUNDO` where undo semantics wanted · DISM API/`Dism.exe` for component cleanup · Task Scheduler COM API · `winget` COM/CLI for updates · WRL/`IUnknown`-free WinRT consumption for Storage settings parity checks.

---

## 5. File classification and risk model

Two axes: **Class** (what it is) and **Risk Tier** (what cleaning may do).

| Class | Definition | Examples |
|---|---|---|
| A — Transient cache | Rebuilt automatically; app tolerates absence | `%TEMP%`, Windows Temp, thumbnail/icon/shader cache, DO cache, INetCache, stale WER/CBS logs |
| B — Regenerable app state | App rebuilds on next launch; minor UX cost | Browser cache, app log/crash telemetry, prefetch |
| C — Personal/user data | User-created or identity-bearing | Downloads, Documents, Recycle Bin contents, browser history/cookies/passwords/sessions |
| D — System & recovery | OS integrity, rollback, security | WinSxS, Windows.old/ESD, restore points, system dumps, registry hives, SoftwareDistribution (live), defender definitions |

| Risk tier | Rule | Allowed action |
|---|---|---|
| R0 — Safe | Class A/B in known catalog locations, owned by current user | Scan → preview → clean |
| R1 — Caution | Class A/B with user-impact edge (Recycle Bin, crash dumps, prefetch); or class C adjacent to temp | Same + explicit per-category opt-in, default **unchecked** |
| R2 — Restricted | Class D or requires elevation | Only via documented API (DISM, IFileOperation) + informed consent screen + elevated helper; never raw deletion |
| R3 — Forbidden | Credentials, browser profile stores, live system files, registry hives, security tooling, anything outside catalog | Hard refuse at planner; cannot be enabled by config |

**Default-deny**: any path not matched by the versioned catalog is unclassified → shown as informational, never actionable. Junction/reparse-point escape, UNC paths, and `System32`/`WinSxS`/`Program Files` roots are unconditionally excluded unless the catalog entry explicitly documents them.

---

## 6. Cleanup lifecycle and error handling

### 6.1 Lifecycle (state machine per item)

`DISCOVERED → CLASSIFIED → PLANNED → CONSENTED → EXECUTING → {COMMITTED | SKIPPED | FAILED} → REPORTED`

Plan-level flow: **SCAN** (read-only snapshot: path, size, hash-optional, provenance) → **CLASSIFY** → **PLAN** (exclusions, tiering, consent map) → **PREVIEW** (UI) → **CONSENT** (tokens minted) → **EXECUTE** (journaled; per item: re-verify path+signature still matches snapshot → `MoveFileEx` to quarantine or delete → record) → **VERIFY** (post-scan delta) → **REPORT** (manifest persisted to `%LOCALAPPDATA%\ClearForge\reports`).

### 6.2 Error algebra (tagged, never silent)

| Result | Cause | Behaviour |
|---|---|---|
| `Cleaned` | success | journal commit |
| `Skipped.Locked` | `ERROR_SHARING_VIOLATION`/access-denied-by-lock | skip, report; **never** force-close handles or kill processes |
| `Skipped.ElevationRequired` | access denied, R2 category | mark for helper or report as such |
| `Skipped.Vanished` | file removed between scan and execute | benign, reported |
| `Skipped.Changed` | size/hash/mtime differs from snapshot | do not touch — target changed since preview |
| `Refused.Policy` | R3 / unclassified / boundary escape | hard refuse, logged, surfaced in UI as "protected" |
| `Failed.QuarantineFull` | staging capacity exceeded | pause plan, prompt: expand / proceed-without-undo / abort |
| `Failed.IO` | other IO errors | item fails, plan continues; counted in report |
| `Aborted.UserCancel` | cancel | finish in-flight item, commit partial journal |
| `Recovered.Crash` | journal shows uncommitted op on startup | resume-or-complete quarantine move; report as recovered |

### 6.3 Quarantine / recovery

- Default action = **move to quarantine**, not delete: staging under `%LOCALAPPDATA%\ClearForge\quarantine` on the same volume (move = rename, fast, ACL-preserving) + manifest `{itemId, originalPath, sha256, size, timestamp, category}`.
- Retention: 7 days default, user-configurable; purge is the only hard delete and itself journaled.
- Restore: verify hash → move back → restore timestamps; conflicts prompt rename/overwrite choice.
- Opt-out: "Don't quarantine Class A items" setting; secure-delete mode bypasses quarantine with a dedicated warning screen.

---

## 7. UI screens and interaction spec (Flutter)

| # | Screen | Key elements | Exit states |
|---|---|---|---|
| 1 | First-run disclosure | What we clean, what we never touch, no-telemetry note, default safety settings → "Get started" | dashboard |
| 2 | Dashboard | Health Check-style card (Space/Speed/Privacy grouping), "Analyze" CTA, last-clean summary, quarantine size | → scan |
| 3 | Scan progress | Live counters, cancellable, per-category spinner | → results |
| 4 | Results / Preview | Tree: category → items; per-row size, age, class badge (A–D), "Why safe?" tooltip citing catalog rule; tier badges; select-all only checks R0/R1-allowed; R1 rows unchecked by default | → consent |
| 5 | Consent | Summary by tier; R2 categories show elevation explanation; explicit "Clean N items, ~X MB" button | → executing |
| 6 | Executing | Per-item progress, live skip reasons, cancel | → report |
| 7 | Report | Freed bytes, cleaned/skipped/failed counts with reasons, quarantined count, "Undo this clean" button, export JSON/PDF | — |
| 8 | Quarantine | Item list, search, restore/delete-forever, retention setting | — |
| 9 | Exclusions | Path/glob/pattern rules, category disables, browser keep-list | — |
| 10 | Settings | quarantine on/off + retention, secure-delete method (1/3/7 pass), age threshold (24h default), language, logs | — |
| 11 | Scheduler (Pro) | frequency, categories allowed (R0/R1 only), "clean only when idle", run-as rules | — |
| 12 | Startup manager (Pro) | enable/disable/delete startup entries, impact labels | — |
| 13 | Tools (Pro) | uninstall, disk analyzer, duplicates, free-space wipe, software updates via winget | — |

Non-negotiables: no dark patterns (no pre-checked risky rows, no fake "errors found" counts, results show *files* not scare-metrics); every destructive affordance labelled with byte count and count of personal-adjacent items.

---

## 8. Test cases, failure scenarios, recovery

Environment: disposable Windows VM + generated fixture trees only. No destructive test ever targets a real profile.

**Unit**: classifier table tests (each catalog rule → expected class/tier); planner exclusion math; journal serialization; consent-token binding (item IDs, expiry, single-use).

**Integration (fixture filesystem)**:
- Locked file (hold `CreateFile` share-none) → `Skipped.Locked`, plan continues.
- ACL-denied path → `Skipped.ElevationRequired`.
- File mutated mid-flow (resize after scan) → `Skipped.Changed`.
- Reparse point pointing outside catalog root → `Refused.Policy`, no traversal.
- UNC path, >MAX_PATH path, ADS names, trailing-dot names → handled or skipped, never crash.
- Cancel at 50% → partial journal committed, report accurate.
- Kill engine mid-execute → next launch shows `Recovered.Crash`, quarantine consistent.
- Quarantine-full → pause/prompt path.
- Round-trip: quarantine → restore → SHA-256 equality for every item.

**Elevated matrix**: every R2 category run both unelevated (expect `ElevationRequired`) and elevated (expect success), asserting exactly one UAC prompt and helper teardown.

**Golden comparison**: run Storage Sense equivalent categories on the VM, then ClearForge — assert ClearForge's R0 set ⊇ native set and produces equal-or-better byte totals with a superset audit trail.

**Adversarial**: catalog defined per versioned manifest signed at build; fuzz planner with hostile path names; verify UI cannot submit raw paths (only item IDs).

---

## 9. Feature prioritisation

**MVP (v1.0)** — safe-clean core: scan/preview/consent/clean for Class A + Recycle Bin opt-in; exclusions; quarantine+restore; reports; startup-manager view-only; DISM component cleanup (elevated, disclosed). Everything above is implementable with documented APIs and normal permissions.

**Premium (v1.x)** — scheduler + smart cleaning (whitelisted categories only); browser cache modules; cookie keep-list (still never passwords/sessions); multi-user via helper; uninstaller; disk analyzer; duplicate finder; free-space wipe; winget-based software updates; startup enable/disable.

**Excluded permanently**: registry cleaning/sweeping (no supported API, measurable-harm class — Microsoft's own stance and our R3), credential/cookie bulk export, driver "updaters" outside winget, any auto-delete of personal files, kernel/driver components, security-tool interference, silent elevation.

---

## 10. Boundaries

- **Always**: journal every mutation; classify before action; default-deny unclassified paths; keep quarantine by default; document the Windows API used per category; run destructive tests only on VM fixtures.
- **Ask first**: adding a catalog location touching user profile outside temp/cache dirs; any R2 elevation use; changing retention defaults; adding a scheduled task; adding dependencies.
- **Never**: registry sweeping; deleting browser credential/session stores; force-killing processes or closing handles; disabling security tools; bypassing UAC; deleting restore points or recovery partitions; cleaning outside the versioned catalog; telemetry of file paths/names.

## 11. Assumptions

1. CCleaner catalogue in §2.2 reflects current public UI; exact per-version drift is possible — entries marked documented, not byte-verified.
2. DISM component-store cleanup produces equivalent results to the "Update Cleanup" handler; `AnalyzeComponentStore` output unverified on this machine (needs elevation).
3. Flutter ↔ engine via named-pipe JSON-RPC assumed acceptable; alternatives (FFI embedding, gRPC) noted as an open decision.
4. Target audience tolerates one UAC prompt for system-scope cleaning; zero-elevation mode is the default.
5. Quarantine-on-same-volume assumption holds for system-volume items (staging redirected there when source is on `C:`).

## 12. Unresolved questions

1. Confirm spec location and whether `HardiksTool\ClearForgeData` is the intended project root for future code.
2. Engine transport final choice: JSON-RPC named pipe (proposed) vs FFI vs gRPC?
3. Should the catalog be data-driven (JSON manifest, updateable) or compiled? Affects update channel design.
4. Browser coverage for MVP: Chromium-family + Firefox only, or include WebKitGTK-based apps?
5. Quarantine encryption for sensitive Class-B items — in scope or not?
6. Is a Microsoft Store / MSIX distribution target required (affects capability declarations and helper elevation model)?
7. Localization scope for v1.0?

---

## Verification (per spec-driven-development gate)

- [x] Six core areas covered (objective, commands n/a yet, structure §4, style pending code phase, testing §8, boundaries §10)
- [ ] Human review — **pending your sign-off**
- [x] Success criteria testable (§1)
- [x] Always/Ask/Never boundaries defined
- [x] Spec saved to repo: `HardiksTool/ClearForgeData/SPEC.md`
