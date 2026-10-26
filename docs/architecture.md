# ClearForge Architecture (M0–M2 as built)

**M8.2 additions (cleanup coverage):** the compiled catalog grew from 2 to 15
versioned rules across 6 quarantinable categories — existing
`temporary-files`/`crash-dumps` plus `thumbnail-cache` (Explorer
`thumbcache_*.db`/`iconcache_*.db`), `gpu-shader-cache` (D3D + NVIDIA + AMD
roots), `package-manager-caches` (NuGet/npm/Yarn/pip) and
`windows-error-reports` (WER ReportQueue/ReportArchive). Category selection
followed the evidence table in SPEC.md §2.1 (observed native cleanup
handlers + measured reclaimable inventory); each category is a bounded,
existence-gated `SpecialFolder.LocalApplicationData` root — no elevation,
no machine-scope or user-content paths. `CleanupRule` gained an optional
`IncludePatterns` field: filename-only Win32 globs that can only *narrow* a
rule to named files inside an already-contained root (used by
`thumbnail-cache`, the only root with mixed content). The classifier cites
the matched pattern in the candidate reason. Chromium profile GPU/shader
cache dirs (`GPUCache`, `ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`,
`DawnGraphiteCache`, `DawnWebGPUCache`) joined the browser-cache allowlist —
same closed-browser CommitGuard. `CatalogScanProducer` emits per-category
titles + disposable-evidence reasons; the Flutter screens show
human-readable category names. No new IPC methods, no new mutation path —
everything executes through the existing preview → approval → journaled
quarantine → SHA-256 pipeline. Tests: `M82Tests.cs`; the M44 integration
test narrows its commit set to fixture candidates so real cache files in
the new low-risk categories are never mutated.

**M8.1 additions (unified Smart Scan):** `SmartScan/SmartScanCoordinator`
orchestrates the existing read-only producers in deterministic priority
order — catalog rules (`ScanSessionStore.Populate`), browser cache
(`BrowserCacheScanner`, CommitGuard intact), duplicates (`DuplicateFinder`),
startup inventory (`IStartupSource`, review-only) and storage review
(`StorageInventory` + `RecommendationEngine`, review-only). Each run is a
`SmartScanSession` in a bounded `SmartScanStore` (8 sessions, 30 min
retention); producers run sequentially with a per-producer timeout, so one
failure or cancellation yields an honest `partial`/`cancelled`/`failed`
aggregate — never a fake "complete". Dedup is conservative: only an exact
canonical-path match proves two findings name the same file; the earlier
producer keeps the executable binding, losers become review-only evidence
with `DuplicateOf` pointing at the winner, and estimated logical bytes are
never double-counted. `SmartFinding` carries
producer, category, evidence, reason, confidence, risk, reversibility and
skip reasons. Actionable findings bind to child scan sessions; execution is
unchanged — `cleanup.preview → approval → cleanup.commit → journaled
quarantine` with M8.0.3 SHA-256 integrity. There is no second approval path
and no mutation in the coordinator. IPC additions: `smartscan.start|status|
results|cancel`. Tests: `M81Tests.cs`, `smart_scan_test.dart`.

**M8.1.1 lifecycle hardening:** deadlines and cancellation share one
cooperative token — a 2 min per-producer budget AND a documented overall
run deadline (default = per-producer budget x producer count = 10 min wall
clock). Session-producing producers bridge the coordinator token into their
child session's own cancellation source (`ct.Register`), so a timeout,
overall deadline, or user cancel interrupts in-flight filesystem
enumeration rather than only being observed on return. Cancellation is
cooperative: a producer mid-OS-call cannot be preempted, and is reported
honestly rather than claimed cancelled. Actionable findings bound to a
session that ended Failed/Cancelled — or whose cancellation latched as a
deadline raced its completion — are dropped, never published as selectable;
review-only findings keep their evidence. A terminal run ignores late
cancel requests so finished bindings stay executable. `smartscan.results`
adds per-finding `sessionState` (`live|expired|missing|cancelled`,
computed at answer time) and a `userCancelled` flag; the UI disables
stale findings, shows an explicit rescan path, never auto-selects on a
user-cancelled run, and exposes a read-only per-finding file review. Tests:
`M811Tests.cs`.

**M8.0.3 additions (integrity):** journal schema v2 — `content_sha256` +
`integrity_state` columns via a single-transaction migration (v1 rows marked
`legacy`, never backfilled). Quarantine hashes the source through a
`FileShare.Read|Delete` evidence handle (no writer can coexist), journals
the digest atomically with `Quarantining`, and verifies the destination
content before `Quarantined`. Restore verifies the staged artifact and the
restored destination; legacy rows restore through an explicit
`integrity_legacy_unverified` path. New codes: `integrity_unverified|
integrity_mismatch|integrity_io_error|integrity_recovery_required|
integrity_legacy_unverified`. See `docs/recovery.md`. Tests: `M803Tests.cs`.

**M8.0.1 additions (safety hardening):** `cleanup.commit` now requires a
fresh, matching preview approval recorded engine-side by `cleanup.preview`
(`ApprovalGrant` on the session): missing grant → `approval_required`, stale
grant (beyond `QuarantineOptions.ApprovalTtl`) → `approval_stale`, ids outside
the approved set → `approval_mismatch`; session expiry is checked before
approval (`session_expired`). Empty `candidateIds` → `invalid_params`.
`ScanSessionStore` is bounded (`ScanSessionOptions`: MaxSessions default 128,
retention default 60 min) — terminal sessions older than retention are evicted
on registration, scanning sessions and fresh-approval sessions are never
evicted, capacity overflow → `session_capacity`. Journal rows and restore
eligibility are unaffected by eviction. `PipeServer` dispatches frames
concurrently per connection (responses are id-correlated; writes serialized)
so `cleanup.cancel` is honored while a commit is in-flight; `CleanupJournal`
serializes its single SQLite connection. The duplicates screen requires an
explicit keep choice + explicit copy selection + confirmation, and all client
commit paths call `cleanup.preview` first. New error codes:
`approval_required|approval_stale|approval_mismatch|session_capacity`.
Tests: `M801Tests.cs`, `M801IpcTests.cs`.

**M7.2 additions:** `ClearForge.Models` (library, hosted by `ClearForge.Host`)
owns the curated model catalogue and the install lifecycle. The catalogue is a
versioned embedded document — application-owned, strictly validated, never
executable content. Downloads are explicit `models.install` calls: HTTPS-only,
manual redirect handling against a per-model host allowlist, pinned size +
SHA-256, streamed to `tmp/<id>.part` under the model root, then an atomic
same-volume rename. Resume only on a validated 206 range. `ClearForge.Ai.exe`
keeps no network or write authority — it re-verifies the digest and the model
root at load regardless. IPC additions: `models.catalog|status|install|cancel|
remove`.

**M4.2 additions:** `Storage/RecommendationActionBridge` binds Smart Storage
recommendations to real session-bound candidates produced by the existing
scanners (catalog rules via `ScanSessionStore.Populate`, browser cache via
`BrowserCacheScanner` with its CommitGuard intact). Executable recs carry an
`action = {kind:"quarantine", sessionId, status, candidateCount, eligibleBytes}`
binding; execution reuses `cleanup.preview → cleanup.commit` — no second
mutation path. Recs are cached per inventory generation; a fresh
`storage.inventory` rebinds to new sessions. Dev-build-output and unclassified
recommendations stay review-only (bulk directory quarantine is out of scope).

**M2 additions:** `ClearForge.Engine/Cleanup/` — `CleanupJournal` (versioned
SQLite, WAL+FULL sync), `QuarantineExecutor` (journaled same-volume moves,
per-candidate locks, idempotent commits), `Recovery` (startup reconcile,
ambiguous → RecoveryRequired). See `docs/recovery.md` for the state machine.
IPC additions: `cleanup.preview|commit|status|restore|cancel`, `history.get`.

## Processes

```
Flutter UI (apps/desktop_flutter)          unelevated
   │  named pipe \\.\pipe\clearforge.engine.v1
   │  newline-delimited JSON-RPC, 1 MiB frame cap
   ▼
ClearForge.Host (console, unelevated)
   ├── RequestDispatcher — allowlisted methods, strict schema validation
   ├── PipeServerHost — owner-only PipeSecurity ACL, peer-identity log
   └── ScanSessionStore — per-session cancellation + item store
ClearForge.Engine (library, no I/O deps beyond filesystem)
   ├── Policies: CleanupRule validation, TrustedRoots registry, containment
   ├── Scanning: FileScanner (bounded, no reparse traversal), sessions
   └── Classification: deterministic rule → RiskLevel + reason
ClearForge.Windows — production roots (WindowsRoots) + compiled rule catalog
ClearForge.Contracts — protocol constants, DTOs, error codes (shared vocab)
```

## Trust boundaries

- UI holds **no canonical paths** — only opaque `sessionId:index` candidate IDs.
- `cleanup.commit` exists end-to-end but returns `cleanup_not_enabled` (M1 gate).
- No elevated component exists yet. Elevation, when needed (P1+), is a separate
  helper spawned per session after a disclosed UAC prompt — never a service.

## Consistency invariant

`ScanSession.Status` and `Items` are read/written under one lock: a response can
never report `completed` with a partial item list (fixed after observed race).

## Status vocabulary

`scanning → completed | cancelled | failed` (session);
request results: `ok` or structured `RpcError{code,message}`.

## Data that never leaves the machine

Canonical paths, file contents, session items. Reports/telemetry (post-MVP) may
carry counts and categories only — never paths.
