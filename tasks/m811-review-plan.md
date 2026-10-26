# M8.1.1 — Smart Scan acceptance hardening: review plan

## Baseline (verified, not assumed)

- `dotnet test ClearForge.sln`: 284/284 pass.
- `flutter test`: 45/45 pass; `flutter analyze`: clean.
- Worktree has no commits; all M7.2/M8.0.x/M8.1 files preserved; no resets.

## Confirmed defects (code inspection)

### Audit C — cancellation/deadlines

- **D1 (critical)**: `CatalogScanProducer.Run` ignores `ct` entirely —
  `Populate` watches only `session.Cancellation`, and the child session is
  added to `SmartScanSession.ChildSessions` only AFTER `producer.Run`
  returns. Neither the 120s per-producer timeout nor `smartscan.cancel` can
  interrupt an in-flight catalog scan. Browser-cache/duplicates/storage
  producers DO honour `ct`.
- **D2**: findings from a producer that completes after user cancel are still
  published; top-level run status then reports `Partial`, hiding the abort.
  Chosen fix: keep per-producer honesty (completed work stays reported) but
  add a `userCancelled` flag to status/results DTOs and never auto-select on
  a user-cancelled run — additive, no existing semantic weakened.
- **D3**: no overall scan deadline; 5 x 120s sequential = unbounded-feeling
  ~10min. Fix: documented `overallTimeout` (default = per-producer budget x
  producer count = 10min wall clock) enforced via a run-level linked token;
  producers killed by it report Failed("overall deadline"), distinct from
  per-producer timeout and user cancel.
- **D4**: an exception escaping `Run()` outside the producer loop leaves
  `Status=Running` forever (UI polls indefinitely). Wrap `Run` in a
  catch-all → Failed.

### Audit B — child-session lifecycle

- **D5**: `SessionTtl=30min` gates execution; SmartScanStore retains runs
  30min; ScanSessionStore retains sessions 60min → findings stay selectable
  while their backing session is expired/evicted; commit fails closed but
  the UI has no proactive expired state. Fix: per-finding `sessionState`
  (`live|expired|missing`) computed at results-fetch time in the DTO; UI
  disables selection, shows "expired — rescan required", offers rescan.
  Engine gates stay unchanged (fail-closed already correct).

### Audit A — scope transparency / consent

- **D6**: no way to see WHICH files a category finding covers before
  approving; confirm dialog only says "N finding(s)". Fix: read-only
  per-finding "Review files" dialog (scan.getResults filtered to the
  finding's candidateIds — no preview, no side effects) + a confirm dialog
  listing each finding's title, file count and estimated bytes.
- **D7**: `_smartPoll` auto-selects low-risk findings on ANY terminal state
  including user-cancelled. Fix: auto-select only when not userCancelled.
- **D8**: after commit, findings remain selectable and give no "already
  quarantined" signal. Fix: mark findings whose candidates all cleaned/
  already-quarantined as committed; unselectable; shown as done.

### Audit D — dedup

- **D9**: `SmartFinding.DuplicateOf` exists on the wire + Dart model but is
  never populated. Set it to the winner finding id for fully-covered
  findings (structured "covered by" reference, evidence prose kept).
- Dedup itself verified conservative: exact canonical path only,
  OrdinalIgnoreCase (Windows-consistent), losers review-only, evidence
  folded into winners, bytes not double-counted. Known limits documented:
  8.3-name/junction aliases of the same file are NOT merged (commit
  revalidation makes the second attempt skipped_vanished — fail-closed);
  case-only-differing paths on case-sensitive volumes would merge — on
  default Windows NTFS they cannot coexist.

### Audit E — partial results / honesty

- **D10**: producer whose child session ends `Failed` is still reported
  `Completed` (catalog path). Fix: map `outcome.Session.Status` Failed →
  producer Failed, Cancelled → Cancelled.
- **D11**: session-level preview/commit errors surface as opaque
  `CleanupItemOutcome(sessionId,'error')` rows. Fix: UI maps session →
  finding titles; per-item rows mapped to their finding.
- **D12**: selected actionable finding with a null binding is silently
  skipped in commitSmartScan — approved scope silently dropped. Fix:
  explicit `no_executable_binding` outcome row.

## Non-defects verified

- Engine approval boundary is authoritative: commit without matching
  preview fails `approval_required`; commit outside grant fails
  `approval_mismatch`; stale grant fails `approval_stale`; expired session
  fails `session_expired`; executor revalidates identity before every move.
- Findings with different child sessions each go through preview→commit
  per session; the exact set previewed is the set committed.
- Read-only scan contract holds (no mutation anywhere in coordinator or
  producers); FileScanner never follows reparse points; startup/storage
  producers are review-only.
- Stores are bounded (smart runs: 8 × 30min; scan sessions: 128 × 60min;
  candidates bounded by ScanLimits 50k/rule; diagnostics carry exception
  type names only, never paths).

## Implementation order

1. `SmartScanCoordinator` + producers (D1–D4, D9, D10).
2. `RequestDispatcher` DTO fields (D5 `sessionState`, D2 `userCancelled`).
3. `M811Tests.cs` .NET regressions.
4. Dart: `scan_models.dart`, `engine_controller.dart`,
   `smart_scan_screen.dart` (D5–D8, D11, D12).
5. `smart_scan_test.dart` additions.
6. `docs/architecture.md` M8.1 section.
7. Full validation: dotnet test, flutter analyze, flutter test, Windows
   debug build, M44 isolated integration test.
