# ClearForge M8.0 — Mac Sai Competitive Gap Analysis

Status: research and architecture-planning milestone. No implementation in this document.
Method: direct source inspection of both repositories. Every claim carries a file:line
citation; README/doc claims that could not be verified in source are marked
**UNVERIFIED**. MacSai tests were inventoried but not executed (no Swift toolchain on
Windows); ClearForge test counts are from file inspection, not a test run.

Repositories audited:

- **MacSai** — `github.com/iliyami/MacSai`, shallow clone of `main`, app version 1.26.0
  (`Sources/MacCleanKit/Constants.swift:235`). SwiftPM, macOS 14+, three targets:
  `MacClean` (GUI), `MacCleanMenu` (menu-bar extra), `MacCleanKit` (pure-logic library,
  zero deps). One external dependency: GRDB.swift.
- **ClearForge** — `C:\Users\hardi\IdeaProjects\HardiksTool\ClearForgeData`, working
  tree (staged, no commits yet). Flutter Windows UI + .NET 8 engine + LLamaSharp AI
  worker over two ACL'd NDJSON named pipes.

Verdict up front: MacSai leads decisively on **feature breadth** (~20 junk categories,
uninstaller, extensions, malware, maintenance, monitoring vs. ClearForge's 2 cleanup
rules). ClearForge already leads on **execution safety architecture** (journaled
same-volume quarantine with a state machine and startup recovery, session-bound
candidate IDs, fail-closed browser guards) and is the only one of the two with **any
local-AI surface** (MacSai has none). The gap to close is coverage and UX
completeness — not safety plumbing. "Five steps ahead" is not a defensible claim
today; "defensibly safer per action, with a credible path to breadth" is.

---

## 1. Mac Sai feature inventory (source-backed)

### 1.1 Scan/clean core

| Feature | Actual behavior | Evidence | Safety boundary | Limits |
|---|---|---|---|---|
| Smart Scan | 13 registered `ScanModule`s; 9 run in Smart Scan **sequentially**; progress is per-module index only; cooperative `Task.isCancelled` per module boundary | `Core/Scanner/ScanCoordinator.swift:50-131`, registration `App/AppState.swift:45-63` | `CleanFilter.isActionable` (writability + user exclusions) filters every result before display; SafetyGuard re-validates at delete time | No cross-module parallelism; per-module progress only |
| System Junk | 20 categories: user/system caches, user/system logs, `.lproj` language files, document versions, broken/incomplete downloads, iOS backups, old updates, DMGs, Xcode junk, package-manager caches (npm/cargo/gradle/Homebrew/pip), IDE caches (Cursor/Antigravity only), AI-tool caches (claude/codex), broken preferences, broken login items, app leftovers, universal binaries, deleted users | `Modules/SystemJunk/SystemJunkModule.swift:17-38`; `MacCleanKit/Categories/*.swift` | Provably-corrupt plists only; Apple domains never flagged; app-leftover scan returns empty if app enumeration empty (fail-safe); deleted-users never flags current user | macOS paths only; `.lproj` auto-selected despite mutating app bundles (`ScanCategory.swift:190`) |
| Universal binaries | Fat Mach-Os in `/Applications` thinned via `lipo -thin`; staged output, `lsof` in-use check, **codesign-verify rollback** | `Cleaner/ThinAppBundleOperation.swift:66-141`, `ThinBinaryOperation.swift:88-176` | Skips `/System`, `com.apple.*`, Utilities, App Store apps, `arm64e` bundles; `autoSelect=false` | Mutates binaries in place; restore = reinstall, not undo |
| Project Junk | User-picked root; marker-verified `node_modules`, `.next`, `dist`, `target`, `build` (Gradle), `.venv` (requires `pyvenv.cfg`), `Pods`; `dist`/`Pods` only when gitignored; outermost-only | `Modules/ProjectJunk/ProjectJunkScanner.swift:10-16,87-122`; `MacCleanKit/ProjectJunkFinder.swift:30-150` | Root allowlist refuses `/`, `/System`, `/Library`, `/Applications`, `~/Library`; `defaultSelection = []` | maxDepth 6; simplified gitignore matching, no negation |
| Disk Hogs | Docker (`docker system df` JSON) + Xcode simulators (`simctl list`) | `MacCleanKit/DiskHogs.swift:70-310`; `Modules/DiskHogs/DiskHogsClient.swift` | Prune = `image|container|builder prune -f` dangling-only, **volumes never pruned**; only *unavailable* simulators offered; UUID-only identifiers | Docker sizes self-reported; process I/O untested |
| Large & old files | `~` depth≤5; large = ≥50 MB; old = mtime >180 days | `Modules/LargeOldFiles/LargeOldFilesModule.swift:26-88` | `autoSelect=false`, `filteringUncleanable` | No type/extension filters; excluded from Smart Scan (`:14`) |
| Space Lens | Squarified treemap over `FileTreeScanner.topLevelSizes` (allocated size, iCloud-dataless aware); tap-to-drill, top-50 children | `MacCleanKit/SquarifiedTreemap.swift:108-232`; `Core/Scanner/FileTreeScanner.swift:114-183`; `Views/Files/SpaceLens*.swift` | Visualization only — no delete path | — |
| Duplicates | 4-stage: size group → partial SHA-256 (first 4KB) → full SHA-256 → (dev,inode) hardlink dedup; keeper = non-backup path → shallowest → oldest; **consolidate = APFS clonefile** w/ re-hash TOCTOU guard + verify | `MacCleanKit/DuplicateDetection.swift:17-130`; `FileConsolidator.swift:58-144` | `extractDeletableDuplicates` always keeps one copy; `autoSelect=false`; clone verifies SHA-256 before atomic swap | Files >500 MB skipped; hidden dirs + Library excluded |
| History & restore | `trashItem` per item; JSON journal (`clean-history.json`, mode 0600, atomic write); restore validates path-inside-Trash + **inode match** + no-overwrite; caps 20 sessions/5k items/30d | `Core/Cleaner/CleaningEngine.swift:238-249`; `Services/CleanHistoryRecorder.swift:66-135`; `CleanHistory.swift:91-147` | Restore only while item is in Trash; permanent ops not restorable | JSON not SQLite; no content hash on restore (inode only) |
| TrashBins | `~/.Trash` + all volume `.Trashes`; **empty = permanent `removeItem` — the only permanent path** | `Modules/TrashBins/TrashBinsModule.swift:32-50`; `CleanActions.swift:51-52,83` | EPERM probe distinguishes "empty" vs "no FDA" | Permanent; not restorable |
| Mail Attachments | Mail Downloads + Mail subtree (attachment exts) + Outlook/Spark caches | `Modules/MailAttachments/MailAttachmentsModule.swift:14-56` | Standard trash path | No dedicated tests |
| Shredder | Drag-in files; modes trash / permanent / secure (1-pass zero + remove); refuses dirs+symlinks in secure mode | `Modules/Shredder/ShredderModule.swift`, `SecureEraser` L19-151 | Failed overwrite → file NOT deleted; honestly documents SSD/TRIM caveat | — |
| Safety plumbing | `SafetyGuard` batch cap 10k/op, 500k total; symlink+firmlink canonicalization; protected prefixes; `PathExclusion` bidirectional boundary matching; `CleanFilter` access(W_OK) pre-filter; `BoundedProcess` (dual-drain, timeout) + `Timeout` race | `MacCleanKit/SafetyGuard.swift:46-152`; `CleanFilter.swift:27-105`; `PathExclusion.swift:36-79`; `BoundedProcess.swift:17-80`; `Timeout.swift:24-85` | Centralized — every delete funnels through `CleaningEngine` | Cooperative cancel only; extension removal bypasses SafetyGuard (see 1.3) |

### 1.2 Applications / protection / monitoring

| Feature | Actual behavior | Evidence | Safety boundary | Limits |
|---|---|---|---|---|
| App inventory | Recursive `.app` walk `/Applications`+`~/Applications` depth 4; Info.plist name/version; atime for last-opened | `Modules/Uninstaller/UninstallerModule.swift:21-123` | Read-only | Only 2 roots; atime unreliable (they use Spotlight for "unused") |
| Leftover detection | 10-level match corpus (bundleID → display/dir/normalized name → helper-stripped → version-stripped; company/team levels excluded) against 17 Library subdirs + system LaunchAgents/Daemons; word-boundary-aware matching | `MacCleanKit/AppMatching.swift:52-163`; `UninstallerModule.swift:127-212` | ≥3-char patterns, generic-token blacklist, Apple-protected app refusal | Substring FP risk on generic names; system plists listed but fail to trash without admin |
| Uninstall | Confirm → trash bundle + selected leftovers via CleaningEngine | `Views/Applications/AppUninstaller.swift:19-38` | SafetyGuard per path; ~29 Apple bundle IDs refused | User must quit app first; no running-process check |
| App Reset | Trashes only `AppResetPolicy` classes (Caches/Prefs/SavedState/Logs/Cookies/HTTPStorages/WebKit); never bundle or user data | `MacCleanKit/AppResetPolicy.swift:25-69` | keepBundle/keepUserData/keepSystemIntegration hard rules | — |
| Orphaned leftovers | Bundle-ID-shaped dir names in 5 safe roots; dotted-prefix lineage; never-flag prefixes (Apple/Chromium/Keystone/Qt/Electron); empty app list → empty result (fail-safe) | `MacCleanKit/OrphanedAppFiles.swift:40-95`; `AppLeftoversScanner.swift:36-101` | No name-based matching at all (deliberate, citing Pearcleaner FPs) | — |
| Unused apps | Spotlight `kMDItemLastUsedDate`, 3/6/12-month horizons; `usageDataUnavailable` when index broken | `Modules/UnusedApps/UnusedAppsClient.swift:17-56`; `MacCleanKit/UnusedApps.swift:80-112` | Excludes Apple, running apps | Spotlight-dependent |
| Startup/login items | Unified list: login items (osascript System Events — only reliable API on macOS 15+), user+system LaunchAgents, LaunchDaemons; toggle = plist `Disabled` rewrite or AppleScript login-item add/remove | `Modules/Optimization/OptimizationModule.swift:56-334`; `MacCleanKit/LoginItemsOutput.swift` | System items read-only; only `Disabled` key written | Toggle needs logout to take effect; no launchctl |
| Maintenance | 10 tasks (purge, tmutil thin, periodic, Spotlight reindex, diskutil verify, Mail reindex, lsregister, dscl flush, docker prune); admin via **in-process `do shell script … with administrator privileges`** — no persistent helper; hard-coded commands, sequential, per-task severity gating | `MacCleanKit/MaintenanceTask.swift:6-225`; `Modules/Maintenance/PrivilegedShellRunner.swift:33-60` | No user input into commands; `.advanced` tasks need per-task confirm | README's XPC `MacCleanHelper` **does not exist** (UNVERIFIED claim); `periodic` removed in macOS 26 |
| Privacy | Whole-file trash: Safari cache+`history.db`+`history.plist`, Chrome/Firefox caches, sharedfilelist recents; TimeFilter by age | `Modules/Privacy/PrivacyModule.swift:38-136` | File-level only — no per-record DB edits | Only 3 browsers for cleaning |
| Browser extensions | 6 browsers (Safari/Chrome/Brave/Edge/Arc/Firefox); Chromium manifest + `__MSG_` localization; origin classified user/policy/component; removal = trash ext folder iff browser closed AND profile not locked (SingletonLock lstat / `.parentlock` fcntl, TOCTOU re-check); policy/component → guided to settings | `MacCleanKit/BrowserExtensions.swift:167-204`; `BrowserProfileLock.swift`; `Modules/BrowserExtensions/BrowserExtensionsClient.swift:19-239` | `isSafeDeletionPath` validates ext-path shape | **Bypasses CleaningEngine/SafetyGuard** (direct `trashItem`, `BrowserExtensionsClient.swift:65`); "suspicious" ID denylist is empty |
| Managed extensions | PreferencePanes/Internet Plug-Ins/Safari `.appex`; trash only under `~/Library` allowlist; `/Library` → reveal-only | `MacCleanKit/ManagedExtensions.swift:98-369` | Symlink-resolved allowlist | — |
| Malware | Reads **on-disk Apple XProtect** (both bundle locations, highest version): SHA-256 `hash.sha256()` rules from XProtect.yara (own mini-parser, only filesize+filekind guards), SHA-1 plist entries, Safari ext blocklist (bundleID+teamID); + 17 curated name patterns + suspicious-launch-agent heuristics; depth tiers quick/balanced/deep | `MacCleanKit/XProtectDatabase.swift:21-460`; `Modules/Malware/MalwareModule.swift:59-454`; `MalwareHashIndex.swift:143-275` | Hash-only; confirmed hits → `.malware` auto-select, blocklist → review-only; non-writable hits → `lockedDetections` reveal-guidance | No YARA strings/heuristics; tiny name list; **zero network = sigs never update beyond OS ships**; `RealTimeMalwareMonitor` is **dead code** (never instantiated — grep confirmed) |
| Monitoring | Separate `MacCleanMenu` LSUIElement app; 3s poll: CPU tick-delta, GPU IOAccelerator, memory (Activity-Monitor def), swap sysctl, `statfs` disk, battery, network if_data; HealthMonitor throttled notifications (disk<5GB, mem≥0.85); **battery-health/cycle rules can never fire — inputs always nil** | `MacCleanMenu/SystemStats/*`; `MenuStatsModel.swift:43-101` | Read-only | Collector actors untested |
| Tips | **Only 2 tips**: trash>1GB → deep-link; user caches>2GB → deep-link; 30-day dismiss | `MacCleanMenu/SystemStats/TipsEngine.swift:15-130` | — | — |
| Resource hogs | Per-app CPU via `proc_pidinfo` delta, RSS via `proc_pid_rusage`; quit via `forceTerminate`; canQuit blocklist (self, finder, dock, WindowServer…) | `Modules/Optimization/ProcessMonitorAdvanced.swift:19-97`; `MacCleanKit/ResourceHogsPolicy.swift:41-90` | Blocklisted system processes | — |
| File handlers | `LSHandlers` plist read; **delete-only** reset-to-default with timestamped backup (max 20) + atomic restore | `Modules/Optimization/LaunchServicesService.swift:34-225` | Backup-before-mutate | Can't set a new default; may not refresh `lsd` |
| App permissions | Read-only TCC.db inventory (needs FDA); action = open System Settings pane | `Modules/AppPermissions/AppPermissionsClient.swift:33-128` | Cannot revoke | — |
| WiFi | Preferred-network list/forget via `networksetup` as root (single batched admin prompt, escaped args, device allowlist) | `Modules/WiFi/PreferredWiFiClient.swift:25-76` | Device-name allowlist `^[a-z][a-z0-9]{0,15}$` | — |
| App updater | Per-app `SUFeedURL` → HTTPS appcast parse → open download page / MAS page; no in-app install, no signature validation | `Modules/Updater/UpdaterModule.swift:19-157`; `MacCleanKit/AppcastParser.swift` | HTTPS-only; delta enclosures skipped | — |
| Architecture | Pure-logic `MacCleanKit` (no FileManager in most files) + actor `CleaningEngine` sole deletion path; `SharedAppState` UserDefaults suite for menu app; deep links; **no XPC**; GRDB cache DB + FSEvents monitor are **dead code** (referenced only by tests) | `Package.swift:18-51`; `App/AppState.swift:4-63`; `Core/Cache/Database.swift:5-273`; `Core/FSMonitor/FSEventMonitor.swift` | Single funnel for all deletes | ~half the `ScanModule`s return `[]` — they're interactive tools, not scanners |

### 1.3 MacSai limitations that matter for positioning

- Permanent deletion exists (TrashBins empty, Shredder) — ClearForge deliberately has none.
- Extension removal bypasses the centralized SafetyGuard pipeline.
- SQLite cache DB + FSEvents + real-time malware monitor = built but unwired.
- Maintenance runs via per-session `do shell script … with administrator privileges`;
  README's privileged-helper claim is stale.
- `.lproj` language-file removal is auto-selected despite mutating app bundles.
- Restore is inode-based, only while the file sits in Trash; no content-hash verify;
  sessions capped 20/5k-items/30d.
- ~144 test files but several safety-critical shells (Docker/simctl/dscl/lipo) are
  pure-logic-tested only.

---

## 2. ClearForge actual implementation (source-backed)

| Area | Status | Evidence | Notes |
|---|---|---|---|
| IPC protocol | **Implemented & tested** | `native/ClearForge.Contracts/Protocol.cs:13-38` (24 allowlisted methods), `Host/PipeServer.cs:44-149`, `RequestDispatcher.cs` | NDJSON, 1 MiB frame cap, ACL = creating-user FullControl, ~40 stable error codes, unknown→`unknown_method`. No method takes a raw path |
| Scanning | **Implemented, narrow** | `Engine/Scanning/FileScanner.cs:41-104`, `ScanSessionStore.cs` | Rule-driven under trusted roots; reparse/system skip, 50k-file budget, depth 32; opaque candidate IDs `{session}:{index}`; `CreateExternal` reuses pipeline for non-catalog producers |
| Policy/path safety | **Implemented, implicit exclusions** | `Policies/CleanupRule.cs`, `TrustedRoots.cs:34-51`, `Windows/WindowsRoots.cs:15-28`, `DefaultRules.cs:12-40` | Only **2 production rules** (user-temp ≥24h, crash-dumps ≥24h) and 2 roots. `config/*.yaml` **not consumed** — exclusions list is decorative; "kept in sync by tests" claim (`DefaultRules.cs:7-8`) has no backing test |
| Quarantine engine | **Implemented & strongly tested** | `Cleanup/QuarantineExecutor.cs`, `CleanupJournal.cs`, `Recovery.cs` | SQLite WAL+FULL journal, `Prepared→…→Restored|Failed|RecoveryRequired` state machine, journal-before-mutation, same-volume only, 4 GiB ceiling, lock probe, post-move verify, startup reconcile, **no delete path exists** (`CleanupTests.cs:275`) |
| Restore verification | **Partial** | `QuarantineExecutor.cs:224-226` | Identity = length+mtime, **not SHA-256** despite SPEC §1/§6.3 claims |
| Preview/revalidation | **Implemented** | `RequestDispatcher.cs:147-240`, `ScanSessionStore.cs:40` (CommitGuard), `QuarantineExecutor.cs:61-94` | Eligibility probe per item; commit re-resolves session IDs + revalidates identity/lock/containment under lock; session TTL 30 min |
| Consent tokens / server-side batch gates | **Missing** | — | SPEC §4.2 consent token absent; `requiresExplicitConfirmation` is a UI hint flag; no server-side batch-size gate (client `plan_guard.dart:26-30` only) |
| Smart Storage | **Implemented & best-tested** | `Storage/StorageInventory.cs`, `Recommendations.cs:79-181`, `RecommendationActionBridge.cs` | Marker-corroborated categorization (node_modules+bin/obj+.dart_tool+target), review-only for unclassified, deterministic greedy `storage.plan`, session-bound actions → same preview→commit pipeline (no second execution path). Scopes: downloads/documents/desktop only |
| Duplicates | **Implemented, minimal UX** | `Duplicates/DuplicateFinder.cs:106-170`, `M3Tests.cs` | size→SHA-256 groups; every member a medium-risk candidate; `EstimatedRecoverable = size×(copies−1)`. **No keep-one selection, no consolidation, hardcoded scopes** |
| Startup | **Read-only by design** | `Startup/StartupInventory.cs`, `Windows/WindowsStartupInventory.cs` | HKCU Run + user Startup folder only; publisher via FileVersionInfo; `Modifiable:false` hardcoded (`RequestDispatcher.cs:357-361`); no HKLM, Task Scheduler, services, StartupApproved |
| Privacy | **Cache-only** | `Privacy/BrowserProfiles.cs`, `BrowserCacheScan.cs` | Edge/Chrome/Firefox profiles; cache dirs only; history/cookies hard-coded `not_implemented_pending_lifecycle_review` (`BrowserProfiles.cs:93-99`); `CommitGuard` fails `browser_running` per-item inside commit lock — closes the started-after-scan race |
| Local AI | **Implemented, most-hardened area** | `ClearForge.Ai/*` (4-method pipe surface), `IntentTask.cs:92-317`, `GoalQuantities.cs`, `ClearForge.Models/*` | LLamaSharp CPU; grammar-constrained output (GBNF) + independent schema validation + deterministic quantity canonical-over-model; curated catalog w/ pinned SHA-256, HTTPS+host-allowlist, GGUF magic, atomic stage-in. Model-dependent tests **fail-not-skip** when artifact absent (`AiTests.cs:184`, `IntentBenchmarkTests.cs:62-65`) |
| Flutter app | **Functional, 7 screens** | `apps/desktop_flutter/lib/` | Cleaner / Storage / Duplicates / Startup / Privacy / AI Models / History. Storage surface is most complete (plan review, per-action reject→replan, `approvedSetIntact` guard, batch confirm) |
| Unified scan | **Missing** | — | Three independent session producers (`scan.start`, `privacy.scanCache`, `duplicates.start`) with different result paths; no composed scan |
| Installer/release | **Missing** | `docs/release-checklist.md` ("M4 gate — not yet applicable") | `installer/` dir referenced by docs doesn't exist; engine discovery is Debug-path-only (`engine_pipe_client.dart:216`); no CI |

### 2.1 ClearForge defects/doc-drift found during audit (fix regardless of roadmap)

1. `README.md:5,34` claims "M0+M1 complete… no deletion implemented" — stale by ~6 milestones.
2. `architecture.md:53` retains the superseded `cleanup_not_enabled` claim.
3. Milestone numbering drift: docs define M0–M4; code/tests reference M4.2–M7.2 with no doc defining those gates.
4. Duplicates screen commits quarantine **on one tap with no confirmation** (`duplicates_screen.dart:65-71`) — reversible but inconsistent with explicit-approval posture.
5. Cleaner screen commits without calling `cleanup.preview` first; `cleanup.status`/`cleanup.cancel` exist server-side but are never used — no mid-execution cancel.
6. `config/exclusions/default-exclusions.yaml` + `m1-catalog.yaml` are dead config; sync claim unenforced.
7. Session store never evicts completed sessions — unbounded growth in a long-lived host.
8. Engine + AI worker discovery assume Debug build layout; `engine_pipe_live_test.dart:12` hardcodes a user-specific path.

---

## 3. Competitive feature matrix

Ratings: **✓** implemented+tested · **◐** partial · **◇** architecture exists, feature incomplete · **✗** missing · **N/A** not appropriate for Windows.

| MacSai capability | ClearForge today | Gap | Windows approach | User value | Risk | Complexity | Test strategy | Milestone |
|---|---|---|---|---|---|---|---|---|
| Unified Smart Scan (9 modules, sequential, cancellable) | ✗ — 3 disjoint producers | No composed scan; results split across screens | Compose existing rules + `CreateExternal` producers into ONE session; per-category progress; single cancel token | High — one action, one review | Low (read-only) | **M** | Dedup across overlapping rules; cancel mid-category; session TTL; commit parity vs today | **M8.1** |
| System Junk (20 categories) | ◐ — 2 rules only | ~18 category gap | New `CleanupRule`s + trusted roots: `%TEMP%`, WER, thumbnail/icon cache DBs, Delivery Optimization, `SoftwareDistribution\Download` (post-update), `Logs\CBS`, `.NET/Edge` shader caches, DirectX shader cache | High | Med — wrong paths = app breakage; keep current-user scope; machine scope stays absent | **L** (per category S) | Golden fixture trees per category; containment/age-gate matrix; never-clean list | **M8.1→M8.2** |
| Project Junk (marker-verified dev dirs) | ◐ — `StorageInventory.Categorize` markers exist; recs review-only (bulk quarantine disabled) | No actionable dev-junk path; no root picker | Extend marker corpus (`.next`, `dist`, `build`, `.venv`+`pyvenv.cfg`, `Pods`, `node_modules`) + repo-root detection via `.git`/`.sln`; keep review-first then enable quarantine per verified kind | High for devs | Med — deleting `node_modules` is regenerable but disruptive | **M** | Marker-fixture matrix; nested-junk outermost-only; gitignore-respect | **M8.2** |
| Disk Hogs (dev-tool storage) | ◐ — inventory scopes only | No Docker/WSL/package-cache measurement | `docker system df --format '{{json .}}'` (works on Windows Docker Desktop); WSL2 vhdx inventory (`%LOCALAPPDATA%\Packages\*\LocalState\ext4.vhdx` + `wsl -l -v`); npm `_cacache`, NuGet `global-packages`/`http-cache`, pip, cargo, gradle, maven caches as rules | High for devs | Low-Med — `docker *-prune` dangling-only, never `-a`, never volumes | **M** | Parser goldens (df/simctl-equivalent outputs); prune-command allowlist; absent-tool degradation | **M8.2** |
| Large & old files | ✗ | — | New rule category over existing FileScanner: size≥N + age≥M over named scopes; `autoSelect=false` equivalent = `requiresExplicitConfirmation` | Med | Low | **S** | Boundary sizes/ages; dedup vs duplicates findings | **M8.3** |
| Space Lens treemap | ✗ — inventory is dir-level `Kind:"dir"` only | No file-level or visual breakdown | Extend `storage.inventory` with per-scope drill-down (top-N children, file-level); Flutter squarified treemap CustomPaint | Med | Low (read-only) | **M** | Layout goldens; bounded-enumeration partial flag honesty | **M8.3** |
| Duplicates (hash + consolidate) | ◐ — SHA-256 groups exist | No keep-one selection, no consolidation, fixed scopes, no confirm dialog | Keep-one picker (newest/oldest/shallowest heuristics like `chooseOriginal`); consolidation via **hardlinks** (NTFS `CreateHardLink`) after re-hash verify — same volume only; ReFS copy-on-write clone later | Med-High | Med — wrong "original" choice; hardlink same-volume constraint | **M** | Re-hash-before-link TOCTOU; link-only-same-volume; group math | **M8.3** |
| History & restore | **✓ — journaled quarantine, ahead of MacSai** | Add purge/retention + integrity MAC | Extend journal: content hash (SHA-256) column (schema v2 migration), retention purge (journaled), integrity MAC per threat-model open item | Med | Low | **M** | Migration v1→v2; hash-verify restore; purge reconcile | **M8.3** |
| App inventory + uninstall + leftovers | ✗ | — | Read registry `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall` (64+32) + HKCU + Store (`Get-AppxPackage`); `InstallLocation`/`Publisher`/size; leftover dirs in `%APPDATA%`/`%LOCALAPPDATA%`/`%PROGRAMDATA%` by publisher/name lineage (port `OrphanedAppFiles` lineage model); uninstall = deep-link `ms-settings:appsfeatures` or invoke registered `UninstallString` only on explicit confirm | High | Med-High — name-matching FP risk; never auto-run uninstallers | **L** | Registry hive fixtures; lineage/never-flag prefixes; empty-enum fail-safe | **M8.4** |
| App reset | ✗ | — | Same leftover map, reset-eligible classes only (Cache/LocalState), never app binaries/user data | Med | Med | S after M8.4 | Policy classification tests | **M8.4+** |
| Unused apps | ✗ | — | `DateAccessed`/Start Menu usage unreliable on Windows; use Uninstall `InstallDate` + optional Jump List MRU presence as weak evidence; mark `usageDataUnavailable` honestly | Low-Med | Low | S | Fixture-based; honesty flags | **M8.4+** |
| Startup items | ◐ — HKCU Run + Startup folder, read-only | No HKLM, Task Scheduler, services, disabled-state, toggle | Add HKLM Run/RunOnce, `StartupApproved\Run` (disabled detection), Task Scheduler `\Microsoft\Windows` excluded read-only, services read-only; enable/disable via `StartupApproved` later (reversible, journaled) | High | Low (read-only) / Med (toggle) | **M** | Registry fixtures; per-source failure isolation; toggle roundtrip | **M8.5** |
| Maintenance tasks | ✗ | — | DISM `/AnalyzeComponentStore` (read-only report), `cleanmgr` handler inventory parity (SPEC §2.1 already catalogued 30 handlers), Recycle Bin opt-in quarantine, Storage Sense policy surfacing | Med | Med — DISM needs elevation; defer elevated writes | **M-L** | Command allowlist; elevated ops deferred behind explicit consent design | **M8.5+/M9** |
| Browser privacy beyond cache | ◐ — cache only | History/cookies/downloads pending lifecycle review | Chromium `History`/`Cookies`/`Web Data` SQLite — whole-file quarantine unsafe while running; reuse existing `browser_running` CommitGuard; per-record deletion out of scope (same as MacSai) | Med | High if sloppy — session/login loss | **M** | Locked-DB race tests; per-category consent matrix | **M8.5+** |
| Extension management | ✗ | — | Chromium `Secure Preferences` JSON parse (extensions map, `disable_reasons`); Firefox `extensions.json`; review-only inventory first; removal deferred (MacSai's SafetyGuard bypass there is a cautionary precedent) | Med | Med | **M** | Fixture prefs; origin classification | **M9** |
| Malware scanning | ✗ | N/A for signature-built approach | **Do not build signature AV.** Windows has Defender — options: read-only Defender status (`Get-MpComputerStatus`), optional AMSI scan API, or integrate `MpCmdRun -Scan -File` as advisory. Re-evaluate | Med | High if half-done — false confidence is worse than none | **L** | n/a until scoped | **M9+ only if justified** |
| System monitoring/menu-bar | ✗ | — | Tray app or in-app widget: `GetDiskFreeSpaceEx`, performance counters, `GlobalMemoryStatusEx`; trend history in local SQLite | Med | Low | **M** | Counter goldens; trend-store migration | **M9** |
| Resource hogs | ✗ | — | `Process` CPU delta sampling + working-set; quit = `Process.Kill` behind blocklist (csrss, winlogon, System…) | Low-Med | Med | S-M | Blocklist tests | **M9** |
| File-handler/default-app mgmt | ✗ | — | `UserChoice` registry read-only report (hash-protected — cannot write) | Low | Low | S | — | Later |
| WiFi preferred networks | ✗ | — | `netsh wlan show profiles` + `wlan delete profile` user-scope | Low | Low | S | — | Later |
| App updater | ✗ | — | winget `list`/`upgrade` read-only inventory; action = open winget/settings | Med | Low | S-M | winget output parsing goldens | **M9** |
| AI intent interpretation | **✓ — unique to ClearForge** (MacSai has none) | Extend contract coverage | Keep advisory; bounded goals→validated constraints → deterministic `storage.plan` | Differentiator | Contained | — | Extend `intent-benchmark.json` adversarial cases | Continuous |

---

## 4. Five defensible differentiators

### D1 — Maintenance Dependency Graph (beyond MacSai's flat categories)

MacSai detects `node_modules` by marker (`ProjectJunkFinder.swift:30-150`) but cannot
answer "what breaks if I remove this?" ClearForge already corroborates dirs by marker
(`StorageInventory.cs:96-115`). Extend to a **typed dependency edge set**:

- `node_modules` ← regenerates via `npm ci`/`npm install` (needs `package-lock.json`);
  disruption = cold install time, offline fails.
- `bin`/`obj` ← `dotnet build` (needs `.csproj`); `target` ← `cargo build`;
  `.dart_tool` ← `flutter pub get`; `dist`/`.next` ← build scripts; `.venv` ←
  `pyvenv.cfg` + requirements.
- Browser cache ↔ running browser (already modeled by `CommitGuard`).
- Windows-specific: `SoftwareDistribution\Download` ↔ `wuauserv`; thumbnail DBs ↔
  Explorer regeneration.

Each inventory item gains `{regeneratesVia, requiresTool, requiresNetwork, estimatedRebuildCost, confidence, evidence[]}` — e.g. evidence = "marker `package-lock.json` present; lockfile age 3d". Confidence tiers: `verified-marker` / `inferred` / `unknown` — unknown never becomes a recommendation.

**Minimal impl:** extend `Categorize` to return a `ProjectArtifact` record; surface in
`storage.recommend` reasons. **Proof:** fixture repo matrix — each artifact kind
asserts correct regen command + disruption note. **Failure:** missing marker →
review-only (existing behavior). **Why better:** every competitor reports "X is big";
nobody reports "X is big AND regenerates in ~4 min offline" — that's the information
that makes deletion decisions safe.

### D2 — Reversible Maintenance Transactions

Already ahead: journaled quarantine + state machine + startup reconcile beats
MacSai's trash+inode restore (no content hash, 30d/20-session caps, restore dies if
Trash is emptied). To make it *defensible*: add **content-hash verification**
(SHA-256 at quarantine time, verify on restore — closes SPEC §1's unmet claim), a
**transaction manifest** per commit (session → items → outcomes → free-space delta),
**integrity MAC** (threat-model open item), and **verified-outcome reporting**
(commit already returns measured `freeSpaceDeltaBytes`).

**Minimal impl:** journal schema v2 + hash on stage-in + hash-check in restore path.
**Proof:** tampered staged file → restore refuses `integrity_mismatch` (new test).
**Failure:** ambiguous state → `RecoveryRequired`, never guess (existing).
**Why better:** MacSai can restore a *wrong* file if inode aliases; ClearForge cannot
return corrupted state silently — restore is cryptographically verified or refused.

### D3 — Evidence-Based Recovery Forecasting

Four distinct byte classes, already partially modeled: `logicalBytes` (what it
occupies), `eligibleBytes` (what passed revalidation), `bytesMoved` (journaled),
`freeSpaceDeltaBytes` (measured). MacSai reports only "found" and loosely "cleaned"
bytes — quarantine-move honesty (~0 delta same-volume) is *more* rigorous in
ClearForge already (`Models.cs:72-76` comment acknowledges this). Extend: forecast
per action = `eligible` with confidence; report post-commit = measured delta with
`same_volume_move` explanation; reconcile "expected vs actual" per transaction.

**Minimal impl:** extend commit result schema + History screen rendering.
**Proof:** unit test asserting same-volume delta ~0 labeled correctly; cross-category
forecast aggregation in `storage.plan`. **Failure:** unmeasurable → say so.
**Why better:** no "we freed 2 GB" marketing math — the product that shows its work
earns the user trust cleaners have burned for a decade.

### D4 — Bounded Local AI (already unique — keep it defensible)

MacSai has zero AI. ClearForge's surface is already the right shape: grammar-bound
output + independent schema gate + deterministic quantity canonical-over-model +
closed constraint keys + path/command rejection (`IntentTask.cs:92-157`). Extend in
breadth, not power: intent → constraint sets for *more* workflows (exclusion
management, category scoping, schedule preferences) — each still compiled into the
same deterministic planner. Never: free-form actions, raw paths, direct commit.

**Proof:** benchmark suite gates (`intent-benchmark.json` + hard deterministic
cases); add injection/exfiltration cases. **Failure:** `clarification_required`,
never best-guess. **Why better:** AI that can only emit *validated planning
constraints* is a categorically different risk class than AI that "helps clean" —
and it's verifiable, which competitors can't claim.

### D5 — Continuous Privacy-First Maintenance

MacSai: 3s-poll menu app + **2 tips** + throttled notifications. ClearForge counter:
journal + inventory history → **local trend analysis** (free-space trajectory,
category growth rates, days-until-threshold estimate), **explainable
recommendations** ("Downloads grew 3.2 GB/30d, 60% node_modules"), **user-controlled
scheduling** (Windows Task Scheduler, off by default, scan-only — execution still
requires in-app approval). Zero telemetry, zero cloud — the privacy claim is
*structural*, not a policy checkbox.

**Minimal impl:** snapshot inventory into journal DB weekly (opt-in) + trend query +
recommendation text generation from existing `RecommendationEngine` reasons.
**Proof:** synthetic history → correct trend math; scheduling creates only
notification, never mutation. **Failure:** missing history → no fabricated trends.

### Additional Windows-specific differentiators (evidence-supported)

- **DISM component-store intelligence**: `dism /Online /Cleanup-Image
  /AnalyzeComponentStore` read-only + `StartComponentCleanup` as the *only*
  delegated WinSxS path (per cleanup-policy native-delegation rule). Nobody in the
  consumer-cleaner space reports WinSxS reclaimable honestly.
- **Storage Sense transparency**: read `StoragePolicy` registry, surface what
  Windows already does, avoid duplicate work — SPEC §2.1 already catalogued this.
- **WSL2 vhdx + Docker Desktop** inventory (see matrix).
- **OneDrive dehydrated-file honesty**: `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` —
  report cloud-only vs local bytes (Space Lens correctness).
- **Recycle Bin as opt-in quarantine category** (SPEC MVP item, still unbuilt).
- **winget version-drift** read-only inventory.

---

## 5. Prioritized roadmap

Dependency spine: M8.1 (unified session) is the force multiplier — M8.2/M8.3 slot
new categories into it. M8.4/M8.5 are independent read-only inventories that gain
unified-scan participation later.

### M8.1 — Unified Smart Scan *(recommended first)*

- **Outcome:** one button → one session → categorized, deduplicated findings →
  existing preview→commit. Cancel anywhere.
- **Scope:** compose existing rules + privacy-cache producer (+ optionally
  duplicates results mapped into candidates) into a single session; per-category
  progress; unified results UI. **Exclusions:** no new categories, no delete.
- **Contracts/files:** `Protocol.cs` (extend `scan.start` params `{categories?}` or
  `scan.startUnified`), `RequestDispatcher`, `ScanSessionStore` (multi-producer
  population + path-level dedup — port `RecommendationActionBridge` claimed-paths
  dedup up into session build), `cleaner_screen.dart`/`engine_controller.dart`
  (category-grouped results, progress per category).
- **Threat model:** a single bigger session widens the TTL race window → keep
  per-item revalidation (unchanged); dedup must not alias two rules' findings.
- **Tests:** unit (dedup, category filtering, cancel mid-category); integration
  (scan→preview→commit parity with today, forged IDs); adversarial (overlapping
  rule scopes, double-counting).
- **Acceptance:** one `scan.getResults` returns all categories; identical commit
  outcomes vs per-producer flow; cancel leaves zero partial state; dedup verified
  byte-exact.
- **Deps:** none. **Risk:** session-merger complexity — mitigate by keeping
  producers unchanged and merging at session-population.

### M8.2 — Project/developer storage intelligence

- **Outcome:** dev folders + package caches appear as evidence-backed,
  regenerable-or-not findings (D1 light).
- **Scope:** marker-corpus extension; package-manager cache rules (npm/NuGet/pip/
  cargo/gradle — user scope only); Docker `system df` read-only + WSL vhdx
  inventory; `ProjectArtifact` metadata (regen command, disruption note).
- **Exclusions:** no bulk-dir quarantine enablement without separate gate; no
  `docker -a` prune.
- **Files:** `StorageInventory.cs`, `DefaultRules.cs`/`WindowsRoots.cs` (new roots),
  new `Storage/DevToolInventory.cs`, rules YAML parity (or remove dead YAML).
- **Tests:** marker fixtures, outermost-only nesting, absent-tool degradation,
  df-output parser goldens.
- **Deps:** M8.1 preferred (participates in unified scan).

### M8.3 — Disk explorer, large/old, duplicate intelligence

- **Outcome:** treemap explorer + large/old findings + usable duplicates
  (keep-one select, confirmation, configurable scopes).
- **Scope:** `storage.inventory` drill-down (file-level, top-N children);
  large/old rules; duplicates keep-one picker + scope picker; **fix the one-tap
  quarantine** (`duplicates_screen.dart:65-71`); hardlink consolidation behind
  re-hash verification; journal v2 content-hash (D2).
- **Files:** `StorageInventory`, `DuplicateFinder`, new IPC params, Flutter
  treemap + screens.
- **Tests:** treemap layout goldens; hardlink same-volume + TOCTOU; hash-verify
  restore.
- **Deps:** M8.1.

### M8.4 — Application inventory + safe uninstall/leftover mapping

- **Outcome:** installed-app inventory with publisher/size; leftover map for a
  chosen app; action = guided uninstall (deep-link) + quarantine leftovers.
- **Scope:** Uninstall-hive enumeration (HKLM 32/64 + HKCU + Appx); leftover
  matching via lineage model ported from `OrphanedAppFiles` semantics (publisher
  prefix, never-flag list, empty-enum fail-safe). **No auto-run of uninstallers
  in M8.4.**
- **Tests:** registry fixtures; FP guardrails; empty-enumeration fail-safe.
- **Deps:** none hard; M8.1 for scan participation.

### M8.5 — Startup & system-resource insights

- **Outcome:** complete startup picture (HKLM, StartupApproved disabled-state,
  Task Scheduler, services — all read-only first) + resource snapshot.
- **Scope:** new `IStartupSource`s; publisher/confidence already modeled.
  Toggle (write `StartupApproved`) is a later, journaled, reversible action —
  separate gate.
- **Tests:** registry fixtures, per-source failure isolation.

### M9+ (deferred, evidence-gated)

Security scanning (Defender status read-only first — **do not** ship a home-grown
signature engine), browser extensions inventory, continuous-maintenance scheduler
(D5), tray monitoring, winget drift, browser history/cookies (needs the lifecycle
review `BrowserProfiles.cs:96-99` is waiting for).

---

## 6. Recommended first implementation: M8.1 unified read-only scan

The architecture supports it **without a second execution path**: producers already
converge on `ScanSessionStore` (`CreateExternal`, `ScanSessionStore.cs:133-138`),
candidates already flow through `preview→commit`, and dedup logic exists to port
(`RecommendationActionBridge.cs:44,91-97`). The slice:

1. **Engine:** `scan.start` accepts optional category set (default = all enabled);
   session populated by catalog rules + browser-cache producer; per-path dedup at
   population; per-category progress in `scan.getResults`.
2. **UI:** Cleaner screen renders grouped categories with live per-category
   progress + cancel; low-risk auto-preselect preserved; commit path unchanged
   but **add the missing `cleanup.preview` call** (parity fix) and wire
   `cleanup.cancel`/`cleanup.status` for mid-commit control.
3. **Verify:** `npm run`-equivalent `dotnet test` on engine + `flutter test`;
   extend `m44_verify_test.dart` for unified flow.

Acceptance: a full scan over a fixture profile returns deduplicated categorized
findings with evidence strings, cancels cleanly mid-category, and an approved
commit produces identical journal rows to today's per-producer path.

---

## 7. License & attribution assessment

MacSai is **BSD-3-Clause**, "Copyright (c) 2026, Mac Sai Contributors" — verified
against `LICENSE` (clauses 1–3 verbatim standard text). Implications:

- **Concepts, feature ideas, and category lists are not copyrightable** — auditing
  and reimplementing semantics in C# is permitted.
- **Copying any source text or non-trivial logic structure** requires retaining the
  copyright notice + license text in source (clause 1) and in binary documentation
  (clause 2). Recommended: implement from this report's behavioral descriptions,
  not by transliterating Swift.
- **No endorsement**: "Mac Sai" name/contributors may not be used to promote
  ClearForge (clause 3) — no "better than Mac Sai" naming/artwork reuse.
- If any ported algorithm is judged a derived work (e.g., squarified treemap is a
  standard published algorithm — fine; `OrphanedAppFiles` lineage matching is
  arguably expressive — implement from spec instead), add `NOTICE`-style
  attribution in `docs/` and installer docs to be safe.

## 8. Risks, unknowns, unverified claims

- **MacSai test suite never executed** (read-only agent, no Swift toolchain on
  Windows) — coverage claims are filename-level.
- **ClearForge build/tests not run in this milestone** — all "implemented" claims
  are source-level, matching the anti-overclaim policy's `SCAFFOLDED→VERIFIED`
  vocabulary: engine features cited are `COMPILED`-claimed by existing test files
  but were **not executed here**.
- Model-dependent ClearForge tests fail-not-skip without the ~1 GB artifact —
  CI viability unknown (no CI config exists).
- MacSai `RealTimeMalwareMonitor`, GRDB cache DB, `FSEventMonitor`, XPC helper
  claim = dead/stale — competitive read should discount them.
- ClearForge dead config (`config/*.yaml`) and unbounded session growth are real
  defects regardless of roadmap.
- Windows feasibility unknowns to de-risk in milestones: DISM analysis output
  format/elevation; `Secure Preferences` signing semantics for extension
  inventories; StartupApproved binary-blob format (documented third-party, not
  officially); winget output stability.
- The duplicates one-tap quarantine and missing cleaner-side `cleanup.preview`
  are small but real posture inconsistencies worth folding into M8.1/M8.3.

## 9. What "ahead" honestly means

Ahead today: reversible-execution architecture, evidence strings per finding,
measured byte accounting, local-AI boundary, fail-closed browser guard. Behind:
category breadth (~2 vs ~20), app inventory, startup completeness, UX polish
(screens 7 vs 13+), monitoring, and shipping infrastructure (no installer, no CI).
The roadmap above closes breadth in dependency order while keeping the safety
lead — that is a defensible position to claim, and only after it is built and
verified should any "ahead" language ship in marketing.
