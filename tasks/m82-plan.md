# M8.2 — Windows Cleanup Coverage + Evidence Quality

## Baseline (verified, not assumed)

- `dotnet test native/ClearForge.sln`: 299/299 pass.
- `flutter analyze`: clean. `flutter test`: 50/50 pass.
- Worktree: no commits; all M7.2→M8.1.1 files preserved; no resets.

## Audit inventory (source-inspected)

| Category | Root / scope | Mechanism | Actionable |
|---|---|---|---|
| temporary-files | %TEMP% recursive ≤50k, ≥24h | catalog rule `user-temp-expired-files` v1 | yes |
| crash-dumps | %LOCALAPPDATA%\CrashDumps ≤10k, ≥24h | catalog rule `user-crash-dumps` v1 | yes |
| browser-cache | detected closed profiles; Cache/Code Cache/cache2 dirs only | `BrowserCacheScanner` + session CommitGuard (browser-not-running) | yes |
| duplicates | named scopes (downloads/documents/desktop) | SHA-256 groups; newest kept | yes |
| startup-items | registry/startup dirs (read-only) | inventory only | review-only |
| storage recs | scope inventory | `RecommendationEngine` + `RecommendationActionBridge` | review-only |

Sole mutation path: `QuarantineExecutor.ExecuteMove`/`Restore` (journaled,
same-volume, SHA-256 verified). Scanning is read-only, reparse-safe,
containment-checked per entry, bounded per rule.

## Selected new categories (evidence-ranked)

| Category | Roots | Evidence | Risk |
|---|---|---|---|
| thumbnail-cache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` filtered to `thumbcache_*.db`/`iconcache_*.db` | "Thumbnail Cache" handler observed; 392.8 MB measured; Class A | low |
| gpu-shader-cache | `D3DSCache`, `NVIDIA\DXCache`, `NVIDIA\GLCache`, `AMD\DxCache`, `AMD\GLCache` | "D3D Shader Cache" handler observed; Class A regenerable | low |
| package-manager-caches | `NuGet\v3-cache`, `NuGet\plugins-cache`, `npm-cache`, `Yarn\Cache`, `pip\Cache` | documented `nuget locals`/`npm cache`/`yarn cache`/`pip cache` clear commands; regenerable (needs network) | medium |
| windows-error-reports | `WER\ReportQueue`, `WER\ReportArchive` | `wer.dll` handler observed; SPEC Class A stale WER logs; user-approved (SPEC §10 ask-first boundary) | medium |
| browser-cache (extension) | Chromium profile `GPUCache`, `ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`, `DawnGraphiteCache`, `DawnWebGPUCache` | documented disposable GPU caches; same closed-browser CommitGuard | low |

Rejected: Recycle Bin (permanent-delete semantics), WinSxS / SoftwareDistribution /
CBS / Windows Temp / system dumps / prefetch (system scope — elevation/DISM only),
INetCache (legacy IE, uncertain), VS Code/Electron app caches (would require a new
process guard — deferred), Downloads/Documents/Desktop (user content), OneDrive
dehydration (sync semantics), Delivery Optimization (ProgramData, elevated),
credential/history/cookie/session stores, registry (permanently excluded).

## Design

### Rule schema: `IncludePatterns`
`CleanupRule` gains `IReadOnlyList<string>? IncludePatterns = null` — filename-only
Win32 globs matched via `FileSystemName.MatchesSimpleExpression` (OrdinalIgnoreCase).
Validation: ≤32 patterns, each 1–128 chars, no `\`/`/`/`:`/`..` (filename-only —
a pattern can never widen a rule outside its root). Only `thumbnail-cache` uses it;
every other new category scans a wholly-disposable root.

### Roots (`WindowsRoots`)
13 new existence-gated roots under `SpecialFolder.LocalApplicationData` — same
pattern as `user-crash-dumps`. Missing vendor dirs simply do not resolve.

### Rules (`DefaultRules.M1` + `m1-catalog.yaml` mirror)
13 new versioned rules, age ≥24h, `exclude_locked`, `follow_reparse_points=false`,
quarantine action. Same-category rules share one Smart Scan finding (grouped by
category) — bounded result size.

### Classification / evidence
`Classifier` appends the matched filename pattern to the candidate reason when a
rule carries `IncludePatterns`, so the provenance names the exact pattern.
`CatalogScanProducer` gains a per-category title + reason map (replaces the
generic "Expired {category}" for all catalog categories) carrying the disposable
-evidence statement; `RecommendationActionBridge.TitleFor` gains the new titles.

### Safety invariants (unchanged)
No new mutation path; coordinator stays read-only; preview→approval→commit per
session; per-item identity/containment/reparse/lock/capacity revalidation;
journaled move + SHA-256; restore never overwrites. Locked cache files (Explorer
holds thumbcache open; GPU caches held by drivers) report `skipped_locked` —
honest partial outcomes, never forced.

## UI
- Engine-side finding titles/reasons carry category evidence (above).
- Dart `categoryLabel()` map in `scan_models.dart` for human-readable category
  names in the Smart Scan group headers and the Cleaner item subtitle.

## M44 implications (isolation preserved)
`scan.start` auto-selects all low-risk candidates — new low-risk categories would
commit REAL user cache files in the test run. Fix: M44 narrows `selected` to the
fixture candidate ids before any commit, and the plan-review rejection becomes a
dynamic "reject every action that is not the temp-files fixture action" loop.
TEMP/TMP `cf-m44-*` guard, unique pipe, spawned-pid match, pid-specific teardown
unchanged.

## Tests (`M82Tests.cs`)
- Rule validation: patterns accepted/rejected (separators, `..`, count/length).
- Scanner: pattern filtering, containment, reparse, limits, read-only.
- Classifier: pattern evidence in reason; age boundary.
- Lifecycle: preview→commit→quarantine→restore round-trip per category class;
  locked → `skipped_locked` then retry-safe; changed → `skipped_changed`;
  restore conflict → `restore_conflict`.
- Smart Scan: per-category titles, one finding per category across multi-rule
  categories, dedup conservative, review-only unaffected, expired-session honesty.
- Catalog: every `DefaultRules` entry validates; expected ids/categories present.
- Performance: measured bounded scan of a representative fixture tree.

## Files
- new: `native/ClearForge.Engine.Tests/M82Tests.cs`, `tasks/m82-plan.md`
- edit: `Policies/CleanupRule.cs`, `Scanning/FileScanner.cs`,
  `Classification/Classifier.cs`, `Windows/WindowsRoots.cs`,
  `Windows/DefaultRules.cs`, `config/cleanup-rules/m1-catalog.yaml`,
  `Privacy/BrowserProfiles.cs`, `Privacy/BrowserCacheScan.cs`,
  `SmartScan/SmartScanProducers.cs`, `Storage/RecommendationActionBridge.cs`,
  `scan_models.dart`, `smart_scan_screen.dart`, `cleaner_screen.dart`,
  `integration_test/m44_verify_test.dart`, `docs/architecture.md`
