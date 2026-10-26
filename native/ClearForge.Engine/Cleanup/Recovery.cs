namespace ClearForge.Engine.Cleanup;

/// <summary>
/// Startup reconciliation: aligns journal state with actual filesystem state
/// after a crash or kill. Never guesses — ambiguity becomes RecoveryRequired.
/// </summary>
public static class Recovery
{
    /// <summary>Reconcile all non-terminal journal rows. Returns rows needing attention.</summary>
    public static IReadOnlyList<JournalEntry> Reconcile(CleanupJournal journal)
    {
        var attention = new List<JournalEntry>();

        foreach (var e in journal.Query())
        {
            switch (e.State)
            {
                case JournalState.Prepared:
                    // Intent recorded but move never started — or journal write
                    // raced the move. If a verified quarantined file exists at a
                    // recorded quarantine path we would have moved on; a Prepared
                    // row with the source still present is a clean "never moved".
                    // If the source is gone but no destination recorded, we cannot
                    // prove what happened → RecoveryRequired.
                    if (!File.Exists(e.SourcePath))
                    {
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.RecoveryRequired, "crash_before_move_source_missing");
                        attention.Add(e);
                    }
                    else
                    {
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.Failed, "interrupted_before_move");
                    }
                    break;

                case JournalState.Quarantining:
                    // Crash window around File.Move. Decide from filesystem;
                    // when an expected hash was captured before the move the
                    // destination is content-verified before promotion, and a
                    // mismatch fails closed instead of trusting the metadata.
                    var destOk = e.QuarantinePath is not null
                                 && File.Exists(e.QuarantinePath)
                                 && new FileInfo(e.QuarantinePath).Length == e.FileLength;
                    var srcExists = File.Exists(e.SourcePath);

                    if (destOk && !srcExists)
                    {
                        if (VerifyHash(e.QuarantinePath!, e.ContentSha256,
                                out var vErr))
                        {
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.Quarantined,
                                integrity: JournalIntegrity.Verified);
                        }
                        else if (vErr is null)
                        {
                            // No hash recorded (legacy) — metadata promotion
                            // only; the legacy marker is preserved.
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.Quarantined);
                        }
                        else
                        {
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.RecoveryRequired, vErr,
                                integrity: JournalIntegrity.Failed);
                            attention.Add(e);
                        }
                    }
                    else if (!destOk && srcExists)
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.Failed, "interrupted_during_move");
                    else
                    {
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.RecoveryRequired, "ambiguous_move_state");
                        attention.Add(e);
                    }
                    break;

                case JournalState.RestorePending:
                    var backOk = File.Exists(e.SourcePath)
                                 && new FileInfo(e.SourcePath).Length == e.FileLength;
                    var stagedExists = e.QuarantinePath is not null
                                       && File.Exists(e.QuarantinePath);

                    if (backOk && !stagedExists)
                    {
                        if (VerifyHash(e.SourcePath, e.ContentSha256, out var vErr))
                        {
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.Restored,
                                integrity: JournalIntegrity.Restored);
                        }
                        else if (vErr is null)
                        {
                            // Legacy row — no hash to check against.
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.Restored);
                        }
                        else
                        {
                            journal.SetState(e.SessionId, e.CandidateId,
                                JournalState.RecoveryRequired, vErr,
                                integrity: JournalIntegrity.Failed);
                            attention.Add(e);
                        }
                    }
                    else if (!backOk && stagedExists)
                    {
                        // Safe to finish: original path free, staged file intact.
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.Quarantined); // resume path: restore again
                    }
                    else
                    {
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.RecoveryRequired, "ambiguous_restore_state");
                        attention.Add(e);
                    }
                    break;

                // Terminal states need no work.
                case JournalState.Quarantined:
                    // Staged file missing while journal claims quarantine —
                    // surfaced, never silently reconciled.
                    if (e.QuarantinePath is null || !File.Exists(e.QuarantinePath))
                    {
                        journal.SetState(e.SessionId, e.CandidateId,
                            JournalState.RecoveryRequired, "quarantined_file_missing");
                        attention.Add(e);
                    }
                    break;

                case JournalState.Restored:
                case JournalState.Failed:
                case JournalState.RecoveryRequired:
                    if (e.State == JournalState.RecoveryRequired) attention.Add(e);
                    break;
            }
        }
        return attention;
    }

    /// <summary>
    /// Content check used only for interrupted (non-terminal) rows: hashes a
    /// file and compares it to the journal's expected digest. Returns true on
    /// match. errorCode is null when no trustworthy hash was recorded (legacy
    /// rows keep their metadata-only rules); otherwise it is the structured
    /// integrity_* failure to journal.
    /// </summary>
    private static bool VerifyHash(string path, string? expected, out string? errorCode)
    {
        if (expected is null) { errorCode = null; return false; }
        if (!ContentIntegrity.IsWellFormedSha256(expected))
        {
            errorCode = ClearForge.Contracts.Protocol.Errors.IntegrityUnverified;
            return false;
        }
        string actual;
        try { actual = Sha256ContentHasher.Instance.ComputeHash(path); }
        catch (Exception)
        {
            errorCode = ClearForge.Contracts.Protocol.Errors.IntegrityIoError;
            return false;
        }
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = null;
            return true;
        }
        errorCode = ClearForge.Contracts.Protocol.Errors.IntegrityMismatch;
        return false;
    }
}
