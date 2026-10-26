using System.Collections.Concurrent;
using ClearForge.Contracts;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Cleanup;

public sealed record QuarantineOptions(
    string QuarantineRoot,
    long MaxQuarantineBytes = 4L * 1024 * 1024 * 1024, // 4 GiB default ceiling
    TimeSpan? SessionTtl = null,
    TimeSpan? ApprovalTtl = null);

/// <summary>
/// Journaled quarantine executor. Moves eligible, session-bound candidates into
/// an application-owned quarantine on the same volume. Permanent deletion does
/// not exist in this build by design.
/// </summary>
public sealed class QuarantineExecutor
{
    private readonly ScanSessionStore _scans;
    private readonly CleanupJournal _journal;
    private readonly QuarantineOptions _opts;
    private readonly ITrustedRoots _roots;
    private readonly IContentHasher _hasher;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _candidateLocks = new();

    public QuarantineExecutor(ScanSessionStore scans, CleanupJournal journal,
        QuarantineOptions opts, ITrustedRoots roots, IContentHasher? hasher = null)
    {
        _scans = scans;
        _journal = journal;
        _opts = opts;
        _roots = roots;
        _hasher = hasher ?? Sha256ContentHasher.Instance;
    }

    public string QuarantineRoot => _opts.QuarantineRoot;

    /// <summary>Configured session lifetime, or null when sessions don't expire.</summary>
    public TimeSpan? SessionTtl => _opts.SessionTtl;

    /// <summary>How long a preview approval stays valid for commit.</summary>
    public TimeSpan? ApprovalTtl => _opts.ApprovalTtl;

    /// <summary>
    /// Execute quarantine moves for session-bound candidate IDs. Per-item
    /// outcomes; a failure on one item never aborts the batch. Cooperative
    /// cancellation between items.
    /// </summary>
    public IReadOnlyList<CleanupItemResult> Commit(
        string sessionId, IReadOnlyList<string> candidateIds, CancellationToken ct)
    {
        var results = new List<CleanupItemResult>(candidateIds.Count);
        foreach (var cid in candidateIds)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new CleanupItemResult(cid, Protocol.Errors.Cancelled, 0, Protocol.Errors.Cancelled));
                continue;
            }
            results.Add(CommitOne(sessionId, cid));
        }
        return results;
    }

    private CleanupItemResult CommitOne(string sessionId, string candidateId)
    {
        // Session validity gate.
        if (!_scans.TryGet(sessionId, out var session))
            return Fail(candidateId, Protocol.Errors.SessionNotFound);
        if (_opts.SessionTtl is { } ttl && DateTimeOffset.UtcNow - session.CreatedUtc > ttl)
            return Fail(candidateId, Protocol.Errors.SessionExpired);

        var resolved = _scans.ResolveCandidates(session, new[] { candidateId });
        if (resolved.Count == 0)
            return Fail(candidateId, Protocol.Errors.CandidateNotFound);
        var (file, pub) = resolved[0];

        var gate = _candidateLocks.GetOrAdd(candidateId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Idempotency: an already-quarantined candidate returns its prior result.
            var prior = _journal.TryGet(sessionId, candidateId);
            if (prior?.State == JournalState.Quarantined)
                return new CleanupItemResult(candidateId,
                    Protocol.Errors.ItemAlreadyQuarantined, prior.FileLength, null);
            if (prior?.State == JournalState.Quarantining)
                return Fail(candidateId, Protocol.Errors.RecoveryRequired);

            // Live per-item safety gate (e.g. browser-started-since-scan).
            // Evaluated inside the candidate lock, immediately before the move.
            if (session.CommitGuard?.Invoke() is { } guardError)
                return Fail(candidateId, guardError);

            return ExecuteMove(sessionId, candidateId, file, pub);
        }
        finally { gate.Release(); }
    }

    private CleanupItemResult ExecuteMove(
        string sessionId, string candidateId, ScannedFile file, ScanCandidate pub)
    {
        var journal = _journal.InsertOrGetOperationId(
            sessionId, candidateId, pub.Category, pub.DisplayName, file.CanonicalPath,
            file.LengthBytes, file.LastWriteUtc.UtcTicks,
            file.LastWriteUtc.UtcTicks, (long)file.Attributes);
        _ = journal;

        // Revalidation: canonical containment inside ANY registered root.
        var canonical = TrustedRoots.Canonicalize(file.CanonicalPath);
        var inside = _roots.RootIds
            .Select(id => _roots.Resolve(id))
            .Where(r => r is not null)
            .Any(r => TrustedRoots.IsContainedIn(r!, canonical));
        if (!inside)
            return Record(sessionId, candidateId, Protocol.Errors.RefusedPolicy, file);

        FileInfo current;
        try { current = new FileInfo(canonical); }
        catch { return Record(sessionId, candidateId, Protocol.Errors.FailedIo, file); }
        if (!current.Exists)
            return Record(sessionId, candidateId, Protocol.Errors.SkippedVanished, file);
        if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Record(sessionId, candidateId, Protocol.Errors.RefusedPolicy, file);
        // Identity re-check: size + timestamps + attributes must match the scan snapshot.
        if (current.Length != file.LengthBytes
            || current.LastWriteTimeUtc != file.LastWriteUtc.UtcDateTime)
            return Record(sessionId, candidateId, Protocol.Errors.SkippedChanged, file);

        // Same-volume requirement: quarantine must live on the source volume.
        var quarantineRoot = Path.GetFullPath(_opts.QuarantineRoot);
        if (!string.Equals(Path.GetPathRoot(canonical),
                Path.GetPathRoot(quarantineRoot), StringComparison.OrdinalIgnoreCase))
            return Record(sessionId, candidateId, Protocol.Errors.CrossVolumeRefused, file);

        // Capacity bound.
        var used = _journal.Query(JournalState.Quarantined.ToString())
            .Sum(e => e.FileLength);
        if (used + current.Length > _opts.MaxQuarantineBytes)
            return Record(sessionId, candidateId, Protocol.Errors.QuarantineFull, file);

        // Destination: session/candidate-scoped, never overwrite an existing entry.
        var destDir = Path.Combine(quarantineRoot, sessionId);
        var dest = Path.Combine(destDir, $"{candidateId.Replace(':', '_')}_{pub.DisplayName}");
        if (File.Exists(dest))
            return Record(sessionId, candidateId, Protocol.Errors.QuarantineCollision, file);

        // Intent before evidence: on retries this resets a prior Failed row so
        // the journal always reflects that a new attempt is in flight.
        _journal.SetState(sessionId, candidateId, JournalState.Prepared);

        // Evidence handle: read access shared with readers and delete (rename),
        // denied to writers. Holding it across hash + move means no new writer
        // can attach mid-operation; it doubles as the lock check — an open for
        // write held by anyone else makes this open fail exactly like the old
        // exclusive probe did.
        FileStream evidence;
        try
        {
            evidence = _hasher.OpenEvidenceRead(canonical);
        }
        catch (IOException)
        {
            return Record(sessionId, candidateId, Protocol.Errors.SkippedLocked, file);
        }
        catch (UnauthorizedAccessException)
        {
            return Record(sessionId, candidateId,
                Protocol.Errors.SkippedElevationRequired, file);
        }

        string expectedHash;
        using (evidence)
        {
            // Identity on the open handle: the file may have drifted between
            // the earlier stat and this open.
            var opened = new FileInfo(canonical);
            if (!opened.Exists || opened.Length != file.LengthBytes
                || opened.LastWriteTimeUtc != file.LastWriteUtc.UtcDateTime)
                return Record(sessionId, candidateId, Protocol.Errors.SkippedChanged, file);
            var identityLength = opened.Length;
            var identityWriteUtc = opened.LastWriteTimeUtc;
            var identityCreatedUtc = opened.CreationTimeUtc;

            try { expectedHash = _hasher.ComputeHash(evidence); }
            catch (Exception)
            {
                // Unreadable mid-hash → integrity was never established;
                // source is untouched and a later retry may succeed.
                _journal.SetState(sessionId, candidateId, JournalState.Failed,
                    Protocol.Errors.IntegrityIoError,
                    integrity: JournalIntegrity.Failed);
                return new CleanupItemResult(candidateId, "skipped", 0,
                    Protocol.Errors.IntegrityIoError);
            }

            // Post-hash identity re-check: denies that the bytes we hashed were
            // swapped out from under us mid-read (e.g. a delete-shared rename
            // plus a drop-in replacement at the same path). A mismatch means
            // the hash cannot be trusted as the source's content — fail closed
            // rather than quarantining unverifiable bytes.
            var after = new FileInfo(canonical);
            if (!after.Exists || after.Length != identityLength
                || after.LastWriteTimeUtc != identityWriteUtc
                || after.CreationTimeUtc != identityCreatedUtc)
            {
                _journal.SetState(sessionId, candidateId, JournalState.Failed,
                    Protocol.Errors.IntegrityUnverified);
                return new CleanupItemResult(candidateId, "skipped", 0,
                    Protocol.Errors.IntegrityUnverified);
            }

            try
            {
                Directory.CreateDirectory(destDir);
                // Journal BEFORE the move, atomically with the evidence: a
                // crash after this write leaves a Quarantining row carrying
                // the expected hash — recovery can verify or fail closed,
                // never guess.
                _journal.SetState(sessionId, candidateId, JournalState.Quarantining,
                    quarantinePath: dest, sha256: expectedHash,
                    integrity: JournalIntegrity.Captured);
                // MoveFile semantics: same volume = rename (atomic-ish,
                // preserves ACLs and metadata); cross-volume was refused
                // above. The evidence handle's delete-share permits the
                // rename while still open.
                File.Move(canonical, dest);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)
                || ex.HResult == unchecked((int)0x80070021))
            {
                _journal.SetState(sessionId, candidateId, JournalState.Failed,
                    Protocol.Errors.SkippedLocked);
                return new CleanupItemResult(candidateId, Protocol.Errors.SkippedLocked, 0, null);
            }
            catch (UnauthorizedAccessException)
            {
                _journal.SetState(sessionId, candidateId, JournalState.Failed,
                    Protocol.Errors.SkippedElevationRequired);
                return new CleanupItemResult(candidateId,
                    Protocol.Errors.SkippedElevationRequired, 0, null);
            }
            catch (Exception)
            {
                _journal.SetState(sessionId, candidateId, JournalState.Failed,
                    Protocol.Errors.FailedIo);
                return new CleanupItemResult(candidateId, Protocol.Errors.FailedIo, 0, null);
            }
        }

        // Verify the outcome: source gone, destination exists, and the moved
        // bytes hash back to the recorded digest. A mismatch or unreadable
        // destination is never reported as a verified quarantine — the only
        // copy stays put and the row becomes manual-review.
        if (!File.Exists(dest) || new FileInfo(dest).Length != file.LengthBytes)
        {
            _journal.SetState(sessionId, candidateId, JournalState.RecoveryRequired,
                "post_move_verify_failed");
            return new CleanupItemResult(candidateId,
                Protocol.Errors.RecoveryRequired, 0, "post_move_verify_failed");
        }
        string destHash;
        try { destHash = _hasher.ComputeHash(dest); }
        catch (Exception)
        {
            _journal.SetState(sessionId, candidateId, JournalState.RecoveryRequired,
                Protocol.Errors.IntegrityIoError, integrity: JournalIntegrity.Failed);
            return new CleanupItemResult(candidateId,
                Protocol.Errors.IntegrityRecoveryRequired, 0,
                Protocol.Errors.IntegrityIoError);
        }
        if (!string.Equals(destHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            _journal.SetState(sessionId, candidateId, JournalState.RecoveryRequired,
                Protocol.Errors.IntegrityMismatch, integrity: JournalIntegrity.Failed);
            return new CleanupItemResult(candidateId,
                Protocol.Errors.IntegrityRecoveryRequired, 0,
                Protocol.Errors.IntegrityMismatch);
        }
        _journal.SetState(sessionId, candidateId, JournalState.Quarantined,
            quarantinePath: dest, integrity: JournalIntegrity.Verified);
        return new CleanupItemResult(candidateId, Protocol.Errors.ItemCleaned,
            file.LengthBytes, null);
    }

    /// <summary>Restore a journaled, quarantined entry. Never overwrites an existing file.</summary>
    public RestoreItemResult Restore(string operationId)
    {
        var entry = _journal.TryGetByOperation(operationId);
        if (entry is null)
            return new RestoreItemResult(operationId, "error", Protocol.Errors.OperationNotFound);
        if (entry.State != JournalState.Quarantined)
            return new RestoreItemResult(operationId, "error",
                $"not_quarantined:{entry.State}");

        if (entry.QuarantinePath is null || !File.Exists(entry.QuarantinePath))
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.RestoreSourceMissing);

        // Identity check before move-back.
        var staged = new FileInfo(entry.QuarantinePath);
        if (staged.Length != entry.FileLength
            || staged.LastWriteTimeUtc.Ticks != entry.FileLastWriteUtcTicks)
        {
            _journal.SetState(entry.SessionId, entry.CandidateId,
                JournalState.RecoveryRequired, "quarantine_identity_mismatch");
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.RecoveryRequired);
        }

        // Integrity gate (schema v2). Three honest cases, never implied:
        //  - legacy row: no trustworthy hash exists — restore remains allowed
        //    (the file is the user's data) but is explicitly reported as
        //    unverified and never presented as integrity-checked.
        //  - malformed or missing hash on a non-legacy row: anomalous state →
        //    fail closed for manual review.
        //  - well-formed hash: staged bytes must match before anything moves.
        var hasHash = ContentIntegrity.IsWellFormedSha256(entry.ContentSha256);
        var isLegacy = entry.IntegrityState == JournalIntegrity.Legacy
                       && entry.ContentSha256 is null;
        if (entry.ContentSha256 is not null && !hasHash)
        {
            _journal.SetState(entry.SessionId, entry.CandidateId,
                JournalState.RecoveryRequired, Protocol.Errors.IntegrityUnverified,
                integrity: JournalIntegrity.Failed);
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.IntegrityUnverified);
        }
        if (!hasHash && !isLegacy)
        {
            _journal.SetState(entry.SessionId, entry.CandidateId,
                JournalState.RecoveryRequired, Protocol.Errors.IntegrityUnverified,
                integrity: JournalIntegrity.Failed);
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.IntegrityUnverified);
        }
        if (hasHash)
        {
            string stagedHash;
            try { stagedHash = _hasher.ComputeHash(entry.QuarantinePath); }
            catch (Exception)
            {
                // Unreadable artifact: keep the row Quarantined — a retry may
                // succeed; nothing was moved.
                return new RestoreItemResult(operationId, "error",
                    Protocol.Errors.IntegrityIoError);
            }
            if (!string.Equals(stagedHash, entry.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                _journal.SetState(entry.SessionId, entry.CandidateId,
                    JournalState.RecoveryRequired, Protocol.Errors.IntegrityMismatch,
                    integrity: JournalIntegrity.Failed);
                return new RestoreItemResult(operationId, "error",
                    Protocol.Errors.IntegrityMismatch);
            }
        }

        // Never overwrite whatever now lives at the original path.
        if (File.Exists(entry.SourcePath) || Directory.Exists(entry.SourcePath))
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.RestoreConflict);

        _journal.SetState(entry.SessionId, entry.CandidateId,
            JournalState.RestorePending);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.SourcePath)!);
            File.Move(entry.QuarantinePath, entry.SourcePath);
        }
        catch (Exception)
        {
            // The throw may still mean a racing/duplicated move actually
            // completed — reconcile before failing so a completed restore is
            // never contradicted by a Failed row.
            var landed = File.Exists(entry.SourcePath)
                && new FileInfo(entry.SourcePath).Length == entry.FileLength
                && !File.Exists(entry.QuarantinePath);
            if (!landed)
            {
                _journal.SetState(entry.SessionId, entry.CandidateId,
                    JournalState.Failed, Protocol.Errors.FailedIo);
                return new RestoreItemResult(operationId, "error",
                    Protocol.Errors.FailedIo);
            }
        }

        // Verify: destination exists with expected length; staging slot gone.
        var back = new FileInfo(entry.SourcePath);
        if (!back.Exists || back.Length != entry.FileLength
            || File.Exists(entry.QuarantinePath))
        {
            _journal.SetState(entry.SessionId, entry.CandidateId,
                JournalState.RecoveryRequired, "post_restore_verify_failed");
            return new RestoreItemResult(operationId, "error",
                Protocol.Errors.RecoveryRequired);
        }

        // Post-restore content check. For hashed rows the restored bytes must
        // equal the recorded digest; for legacy rows the digest observed NOW
        // is recorded as restore-time evidence — explicitly not proof of the
        // original quarantined content. A verification failure never deletes
        // the only surviving copy.
        if (hasHash)
        {
            string restoredHash;
            try { restoredHash = _hasher.ComputeHash(entry.SourcePath); }
            catch (Exception)
            {
                _journal.SetState(entry.SessionId, entry.CandidateId,
                    JournalState.RecoveryRequired,
                    Protocol.Errors.IntegrityRecoveryRequired,
                    integrity: JournalIntegrity.Failed);
                return new RestoreItemResult(operationId, "error",
                    Protocol.Errors.IntegrityRecoveryRequired);
            }
            if (!string.Equals(restoredHash, entry.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                _journal.SetState(entry.SessionId, entry.CandidateId,
                    JournalState.RecoveryRequired,
                    Protocol.Errors.IntegrityRecoveryRequired,
                    integrity: JournalIntegrity.Failed);
                return new RestoreItemResult(operationId, "error",
                    Protocol.Errors.IntegrityRecoveryRequired);
            }
            _journal.SetState(entry.SessionId, entry.CandidateId,
                JournalState.Restored, integrity: JournalIntegrity.Restored);
            return new RestoreItemResult(operationId, Protocol.Errors.RestoredOk, null);
        }

        // Legacy path: capture the observed digest as restore-time evidence.
        string? observed = null;
        try { observed = _hasher.ComputeHash(entry.SourcePath); }
        catch (Exception) { observed = null; } // unreadable — restore still stands
        _journal.SetState(entry.SessionId, entry.CandidateId,
            JournalState.Restored, sha256: observed,
            integrity: JournalIntegrity.LegacyRestored);
        return new RestoreItemResult(operationId, Protocol.Errors.RestoredOk,
            Protocol.Errors.IntegrityLegacyUnverified);
    }

    private CleanupItemResult Record(string sessionId, string candidateId,
        string errorCode, ScannedFile file)
    {
        _journal.InsertOrGetOperationId(sessionId, candidateId, null, null,
            file.CanonicalPath, file.LengthBytes, file.LastWriteUtc.UtcTicks,
            file.LastWriteUtc.UtcTicks, (long)file.Attributes);
        _journal.SetState(sessionId, candidateId, JournalState.Failed, errorCode);
        return new CleanupItemResult(candidateId, "skipped", 0, errorCode);
    }

    private static CleanupItemResult Fail(string candidateId, string code) =>
        new(candidateId, "rejected", 0, code);
}
