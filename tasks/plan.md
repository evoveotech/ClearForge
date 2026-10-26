# M8.0.3 — Enterprise Quarantine Integrity + Journal Schema v2

## Audit summary

- Sole filesystem mutation path for quarantine/restore: `QuarantineExecutor.ExecuteMove` / `.Restore` (`File.Move` x2). `Recovery.Reconcile` mutates the journal only. No other quarantine/restore routes exist; `RecommendationActionBridge`, duplicates, and browser-cache scans all funnel through the same executor.
- Journal: SQLite WAL + FULL + busy_timeout, `meta.schema_version='1'`, one row per `(session_id, candidate_id)`. States: Prepared, Quarantining, Quarantined, RestorePending, Restored, Failed, RecoveryRequired.
- Today verification is metadata-only (length + last-write). No content integrity.
- Protocol already names `integrity_mismatch` for model downloads — reuse naming style.

## Design

### Schema v2 (CleanupJournal.cs)

New columns on `journal`:
- `content_sha256 TEXT` — lowercase 64-hex expected hash, null for legacy rows.
- `integrity_state TEXT` — provenance, never implied:
  - `captured`   hash persisted pre-move, post-move verify pending
  - `verified`   quarantined artifact verified against expected hash
  - `restored`   restored destination verified against expected hash
  - `legacy`     schema-v1 row; NO trustworthy historical hash
  - `legacy_restored` legacy row restored; hash recorded is *observed at restore time*, not proof of original content
  - `failed`     last integrity check failed (paired with RecoveryRequired/Failed journal state)

Migration (`CleanupJournal` ctor → `MigrateIfNeeded`):
- version > 2 or malformed → throw (fail closed; engine refuses to serve an unknown journal)
- version 1 (or missing meta row with existing rows) → `BEGIN IMMEDIATE`: `ALTER TABLE journal ADD COLUMN` x2, `UPDATE journal SET integrity_state='legacy'`, `UPDATE meta SET v='2'` — atomic, rerunnable, no table recreation, no row loss
- version 2 → no-op; fresh DB → create v2 schema directly

`JournalEntry` + `JournalEntryDto` gain `integrity` (additive wire field). `SetState` gains optional `sha256`/`integrity` params that write only when non-null so error transitions never erase evidence.

### Hash lifecycle

`ContentIntegrity.cs` (new): streaming SHA-256 (128 KiB buffer, bounded memory); `OpenEvidenceRead(path)` → `FileAccess.Read`, `FileShare.Read | FileShare.Delete` — denies concurrent writers while held, still permits the rename needed by `File.Move`.

Quarantine order (all existing gates preserved, unchanged order before hashing):
1. session/approval/identity/containment/reparse/capacity/collision gates (existing)
2. open evidence handle (replaces probe): IOException→`skipped_locked`, Unauthorized→`skipped_elevation_required`
3. re-stat identity on open handle → `skipped_changed` if drifted
4. stream hash source
5. post-hash identity re-check (length+mtime+created) → `integrity_unverified` fail-closed, source untouched
6. one journal write: Quarantining + quarantine_path + sha256 + integrity='captured'
7. `File.Move` with handle still open (delete-share permits rename), close handle
8. post-move: length + full hash verify of dest; pass → Quarantined + 'verified'; fail → RecoveryRequired + `integrity_recovery_required`, artifact preserved, never deleted

Restore order:
1. existing lookup/state/staged-exists/identity checks
2. integrity gate:
   - `legacy` + null hash → legacy path (see below)
   - malformed hash → RecoveryRequired + `integrity_unverified`
   - hash missing on non-legacy row → RecoveryRequired + `integrity_unverified`
   - staged hash IO failure → return `integrity_io_error`, stay Quarantined (retry-safe)
   - staged hash mismatch → RecoveryRequired + `integrity_mismatch`, artifact preserved
3. destination-occupied refusal (existing), RestorePending, move
4. post-move hash verify of restored file: pass → Restored + 'restored'; fail → RecoveryRequired + `integrity_recovery_required` (never delete the only copy)
5. legacy path: restore proceeds; observed hash recorded + integrity='legacy_restored'; result = status `restored` + errorCode `integrity_legacy_unverified`

New error codes: `integrity_unverified`, `integrity_io_error`, `integrity_recovery_required`, `integrity_legacy_unverified` (`integrity_mismatch` reused).

### Recovery additions (same reconcile architecture)

- Quarantining + recorded hash: dest exists → hash-verify before promoting to Quarantined; mismatch → RecoveryRequired `integrity_mismatch`.
- RestorePending + recorded hash: dest exists → hash-verify before Restored; mismatch → RecoveryRequired `integrity_recovery_required`.
- Legacy rows keep length-based rules and their `legacy` marker.
- Bounded cost: hashing only runs on interrupted (non-terminal) rows.

### Test seam

`IContentHasher` (interface: `OpenEvidenceRead`, `ComputeHash(Stream)`, `ComputeHash(path)`) injected optionally into `QuarantineExecutor`; production default = real SHA-256. Tests inject wrappers that corrupt mid-hash, return wrong dest hashes, or throw IO.

## Crash window table

| Interruption | Journal | FS | Recovery outcome |
|---|---|---|---|
| Before hashing | Prepared | src intact | Failed(interrupted_before_move), retry safe |
| During hashing | Prepared | src intact (deny-write held) | Failed(interrupted_before_move) |
| Hash persisted, pre-move | Quarantining+hash | src intact, dest absent | Failed(interrupted_during_move) |
| During/after move | Quarantining+hash | dest present, src gone | hash-verified → Quarantined+verified |
| Post-move, pre-verify | Quarantining+hash | dest present | hash-verified → Quarantined or RecoveryRequired(mismatch) |
| Verified, pre-final write | Quarantining+hash | dest verified | → Quarantined+verified |
| During restore verify | Quarantined | staged intact | stays Quarantined; retry verifies again (idempotent) |
| Post restore-move, pre-final | RestorePending | dest present, staged gone | hash-verified → Restored+restored |
| During migration | v1 rows | n/a | transaction rolls back; rerun-safe |

## Legacy policy (decision to confirm)

v1 rows get `integrity_state='legacy'`, `content_sha256=NULL` — never backfilled (hashing today cannot prove original content). Restore remains possible (data preservation) but returns explicit `integrity_legacy_unverified` marker and journal records `legacy_restored` — cannot be mistaken for verified integrity. Rows stay in History unchanged.

## Files

- new: `native/ClearForge.Engine/Cleanup/ContentIntegrity.cs`, `native/ClearForge.Engine.Tests/M803Tests.cs`
- edit: `CleanupJournal.cs`, `QuarantineExecutor.cs`, `Recovery.cs`, `Contracts/Protocol.cs`, `Contracts/Models.cs`, `engine_controller.dart`, `history_screen.dart`, `docs/recovery.md`
