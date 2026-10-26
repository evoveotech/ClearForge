# M8.3 — Duplicate review, keeper selection & safe quarantine

## Baseline (verified, not assumed)

- `dotnet test native/ClearForge.sln`: 316/316 pass.
- `flutter analyze`: clean. `flutter test`: 50/50 pass.
- Worktree: clean on `main@aac2645` — the previous report's "zero commits /
  staged work" no longer applies; nothing pre-existing to preserve.

## Audit summary (source-inspected)

- Sole mutation path remains `QuarantineExecutor.ExecuteMove`/`Restore`
  (journaled, same-volume, SHA-256 verified). Scanning is read-only.
- `DuplicatesProducer` **silently keeps the newest member** and binds only the
  redundant copies — the user can never choose the keeper (G1).
- `duplicates.groups` returns only `groupId/sha256Short/fileBytes/copies/
  candidateIds`. The Flutter screen renders raw opaque candidate ids — no
  path, size, mtime, or eligibility (G2) — and silently defaults keeper to
  index 0 (`_keepIndex` → `_keep[key] ?? 0`) (G3).
- `cleanup.preview`/`cleanup.commit` have no duplicate-group semantics: a
  client can submit EVERY member of a group (zero copies kept) or a cross-
  group mix, and no keeper is ever recorded in the `ApprovalGrant` (G4).
- `DuplicateReport` lives only in dispatcher `_dupeReports` for
  `duplicates.start` sessions; smart-scan child sessions discard group
  structure after findings are built, so group membership cannot be
  revalidated for Smart Scan bindings (G5).

## Design

### Group evidence ownership
`ScanSession` gains `DuplicateReport` (nullable), set inside
`DuplicateFinder.ScanIntoSession` before it returns. One owner (the session),
dies with session eviction — replaces the dispatcher's `_dupeReports` map and
its manual pruning. `duplicates.groups` reads `session.DuplicateReport`.
`DuplicateGroup` gains the full `Sha256` (short prefix kept for list display).

### New IPC: `duplicates.group`
`{sessionId, groupId}` → read-only per-member review detail:
`{sessionId, groupId, sha256 (full), fileBytes, copies,
estimatedReclaimableLogicalBytes, sessionState, actionable,
suggestedKeeperId, members: [{candidateId, path, fileName, bytes,
lastWriteUtc, eligibility}]}`. Paths are exposed here intentionally — keeper
choice is meaningless without distinguishing locations; the pipe is
owner-only local IPC and Smart Scan evidence already carried member paths.
`eligibility` reuses the preview probe vocabulary
(eligible/missing/changed/locked/reparse_point/elevation_required/unreadable).
`suggestedKeeperId` = newest write (ties → ordinal path) — a labelled
suggestion, never a decision. `actionable` = session live AND at least one
member currently eligible. Missing report → `group_not_found`.

### Keeper invariants at the trust boundary (cleanup.preview/cleanup.commit)
Optional `duplicateGroupId` + `keeperId` params (both required together).
Enforced engine-side whenever the resolved selection intersects a duplicate
group — never delegated to the client:

- group must exist in `session.DuplicateReport` → else `group_not_found`
- `keeperId` must be a member of that group and not in the selection →
  `keeper_invalid`
- every selected candidate must be a member of the same group →
  `group_mismatch` (also rejects cross-group selections)
- any selection touching group members without group/keeper →
  `keeper_required` — selecting an entire group is therefore impossible
- keeper file must still exist and match its scan snapshot (size+mtime) at
  preview AND commit → else `keeper_stale` (re-review required)

`ApprovalGrant` records `DuplicateGroupId`/`KeeperId`; commit requires a
matching echo → `approval_mismatch`. Selection ⊆ grant unchanged.
Empty selection → `invalid_params` (unchanged). No second approval path —
the flow is still preview → grant → commit → per-item revalidation →
journaled quarantine → integrity verify → restore.

### Smart Scan
`SmartFinding` gains `groupId`. `DuplicatesProducer` binds ALL members as
candidates (no auto-keeper), marks `groupId`, neutral title/evidence, bytes
stay `(copies-1)*size` — max reclaimable if every non-keeper is chosen;
dedup path-claim semantics unchanged. UI treats `groupId != null` findings
as review-required: not bulk-selectable; a "Review group" affordance opens
the keeper-review dialog bound to `{sessionId, groupId}` — the same
`duplicates.group` + preview/commit path as the Duplicates screen.
Successful commit marks the finding committed.

### Flutter
- `scan_models.dart`: `SmartFinding.groupId`/`requiresKeeperReview`.
- `engine_controller.dart`: `duplicateGroupDetail`, `previewDuplicateGroup`,
  `commitDuplicateGroup` (echoes group/keeper through the chunked
  preview→commit helper); keeper-review findings excluded from bulk select.
- New shared `duplicate_group_review.dart` dialog: per-member full path
  (selectable/copyable), name, size, mtime, eligibility + reason, member
  count, full SHA-256, estimated reclaimable **logical** bytes (labelled
  not-guaranteed), labelled keeper suggestion, explicit keeper radio,
  explicit copy checkboxes, confirmation listing KEEP vs QUARANTINE vs
  left-in-place, per-item outcome display. Empty/loading/stale-session/
  error/success states; no success claim before engine confirmation.
- `duplicates_screen.dart` rebuilt around the review dialog;
  `smart_scan_screen.dart` duplicate findings route to it.

### New error codes
`group_not_found`, `keeper_required`, `keeper_invalid`, `keeper_stale`,
`group_mismatch`.

## Tests (`M83Tests.cs`, `duplicate_review_test.dart`)

Engine/dispatch: 3+ member group; each member as keeper; keeper-in-selection
rejected; candidate from another group; malformed/duplicate ids; stale
group id; expired session; changed file; missing file; locked file;
no-eligible-candidates group; keeper vanished post-preview; commit echo
mismatch; restore conflict after quarantine; dedup'd duplicate finding
review-only; smart-scan finding carries groupId + all members; cancel/
partial semantics intact. Flutter: model decode, keeper selection gating,
selection excludes keeper, outcome states.

## Files

- edit: `Contracts/Models.cs`, `Contracts/Protocol.cs`,
  `Scanning/ScanSessionStore.cs`, `Duplicates/DuplicateFinder.cs`,
  `SmartScan/SmartScanProducers.cs` (DuplicatesProducer),
  `Host/RequestDispatcher.cs`,
  `lib/core/models/scan_models.dart`, `lib/core/state/engine_controller.dart`,
  `lib/features/duplicates/duplicates_screen.dart`,
  `lib/features/smartscan/smart_scan_screen.dart`, `docs/architecture.md`
- new: `lib/features/duplicates/duplicate_group_review.dart`,
  `native/ClearForge.Engine.Tests/M83Tests.cs`,
  `apps/desktop_flutter/test/duplicate_review_test.dart`,
  `tasks/m83-plan.md`

## Out of scope

M8.4, installer/release work, permanent deletion, hard-link consolidation,
overwrite merging, elevation, new approval systems, journal schema changes
(none needed — candidate-level journaling already fits).
