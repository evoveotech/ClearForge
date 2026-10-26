using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Scanning;

namespace ClearForge.Host;

public sealed record DispatchResult(object? Result, string? Error, string? Message)
{
    public static DispatchResult Ok(object result) => new(result, null, null);
    public static DispatchResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Routes validated requests to engine services. The surface is an explicit
/// allowlist — there is no path-accepting method.
/// </summary>
public sealed class RequestDispatcher
{
    private static readonly JsonSerializerOptions ParamsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ScanSessionStore _scans;
    private readonly ClearForge.Engine.Cleanup.QuarantineExecutor? _executor;
    private readonly ClearForge.Engine.Cleanup.CleanupJournal? _journal;
    private readonly ClearForge.Engine.Duplicates.DuplicateFinder? _dupes;
    private readonly ClearForge.Engine.Startup.IStartupSource? _startup;
    private readonly ClearForge.Engine.Apps.IAppInventorySource? _apps;
    private readonly ClearForge.Engine.Privacy.BrowserPrivacyScanner? _privacy;
    private readonly ClearForge.Engine.Privacy.BrowserCacheScanner? _cacheScan;
    private readonly ClearForge.Engine.Duplicates.ScopeRegistry? _scopes;

    public RequestDispatcher(ScanSessionStore scans,
        ClearForge.Engine.Cleanup.QuarantineExecutor? executor = null,
        ClearForge.Engine.Cleanup.CleanupJournal? journal = null,
        ClearForge.Engine.Duplicates.DuplicateFinder? dupes = null,
        ClearForge.Engine.Duplicates.ScopeRegistry? scopes = null,
        ClearForge.Engine.Startup.IStartupSource? startup = null,
        ClearForge.Engine.Apps.IAppInventorySource? apps = null,
        ClearForge.Engine.Privacy.BrowserPrivacyScanner? privacy = null,
        ClearForge.Engine.Privacy.BrowserCacheScanner? cacheScan = null,
        ClearForge.Engine.Storage.StorageInventory? inventory = null,
        ClearForge.Engine.Storage.RecommendationActionBridge? actionBridge = null,
        ClearForge.Models.ModelManager? models = null,
        ClearForge.Engine.SmartScan.SmartScanCoordinator? smartScan = null)
    {
        _scans = scans;
        _executor = executor;
        _journal = journal;
        _dupes = dupes;
        _scopes = scopes;
        _startup = startup;
        _apps = apps;
        _privacy = privacy;
        _cacheScan = cacheScan;
        _inventory = inventory;
        _actionBridge = actionBridge;
        _models = models;
        _smartScan = smartScan;
    }

    private readonly ClearForge.Engine.SmartScan.SmartScanCoordinator? _smartScan;

    public Task<DispatchResult> Dispatch(string method, JsonElement? p) => method switch
    {
        Protocol.Methods.HealthCheck => Task.FromResult(DispatchResult.Ok(
            new HealthResponse("ok", EngineVersion.Value, Protocol.Version,
                Environment.ProcessId))),

        Protocol.Methods.StartScan => Task.FromResult(StartScan(p)),
        Protocol.Methods.GetScanResults => Task.FromResult(GetResults(p)),
        Protocol.Methods.CancelScan => Task.FromResult(CancelScan(p)),
        Protocol.Methods.PreviewCleanup => Task.FromResult(Preview(p)),
        Protocol.Methods.CommitCleanup => Task.FromResult(Commit(p)),
        Protocol.Methods.GetOperationStatus => Task.FromResult(GetStatus(p)),
        Protocol.Methods.GetCleanupHistory => Task.FromResult(GetCleanupHistory(p)),
        Protocol.Methods.CleanupStatus => Task.FromResult(GetStatus(p)),
        Protocol.Methods.CleanupRestore => Task.FromResult(Restore(p)),
        Protocol.Methods.CleanupCancel => Task.FromResult(CancelScan(p)),
        Protocol.Methods.DuplicatesStart => Task.FromResult(DuplicatesStart(p)),
        Protocol.Methods.DuplicatesGroups => Task.FromResult(DuplicatesGroups(p)),
        Protocol.Methods.DuplicatesGroup => Task.FromResult(DuplicatesGroup(p)),
        Protocol.Methods.StartupList => Task.FromResult(StartupList()),
        Protocol.Methods.AppsList => Task.FromResult(AppsList()),
        Protocol.Methods.PrivacyDetect => Task.FromResult(PrivacyDetect()),
        Protocol.Methods.PrivacyScanCache => Task.FromResult(PrivacyScanCache(p)),
        Protocol.Methods.StorageInventory => Task.FromResult(StorageInventory(p)),
        Protocol.Methods.StorageRecommend => Task.FromResult(StorageRecommend(p)),
        Protocol.Methods.StoragePlan => Task.FromResult(StoragePlan(p)),
        Protocol.Methods.SmartScanStart => Task.FromResult(SmartScanStart(p)),
        Protocol.Methods.SmartScanStatus => Task.FromResult(SmartScanStatusResult(p)),
        Protocol.Methods.SmartScanResults => Task.FromResult(SmartScanResults(p)),
        Protocol.Methods.SmartScanCancel => Task.FromResult(SmartScanCancel(p)),

        // M7.2 curated model catalogue + install lifecycle (engine-owned,
        // outside the cleanup pipeline — the AI worker keeps no network or
        // write authority; it re-verifies digest and model-root at load).
        Protocol.Methods.ModelsCatalog => Task.FromResult(ModelsCatalog()),
        Protocol.Methods.ModelsStatus => Task.FromResult(ModelsStatus()),
        Protocol.Methods.ModelsInstall => Task.FromResult(ModelsInstall(p)),
        Protocol.Methods.ModelsCancel => Task.FromResult(ModelsCancel(p)),
        Protocol.Methods.ModelsRemove => Task.FromResult(ModelsRemove(p)),

        _ => Task.FromResult(DispatchResult.Fail(
            Protocol.Errors.UnknownMethod, $"method '{method}' is not supported")),
    };

    private DispatchResult StartScan(JsonElement? p)
    {
        StartScanRequest? req = null;
        if (p is { ValueKind: JsonValueKind.Object })
        {
            try { req = p.Value.Deserialize<StartScanRequest>(ParamsJson); }
            catch (JsonException) { return DispatchResult.Fail(Protocol.Errors.InvalidParams, "bad params"); }
        }

        try
        {
            var session = _scans.Start(req?.Categories);
            return DispatchResult.Ok(new ScanSessionInfo(
                session.Id, session.Status.ToString().ToLowerInvariant(),
                0, 0));
        }
        catch (SessionCapacityException ex)
        {
            return DispatchResult.Fail(Protocol.Errors.SessionCapacity, ex.Message);
        }
    }

    private DispatchResult GetResults(JsonElement? p)
    {
        var sessionId = ParamSessionId(p);
        if (sessionId is null) return DispatchResult.Fail(Protocol.Errors.InvalidParams, "sessionId required");
        if (!_scans.TryGet(sessionId, out var s)) return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");

        // Status and the item snapshot are read under one lock so the
        // response is always a consistent point-in-time view.
        List<(ScannedFile File, Contracts.ScanCandidate Public)> snapshot;
        SessionStatus status;
        lock (s.Items) { status = s.Status; snapshot = s.Items.ToList(); }

        var categories = snapshot.GroupBy(i => i.Public.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        return DispatchResult.Ok(new
        {
            sessionId = s.Id,
            status = status.ToString().ToLowerInvariant(),
            estimatedBytes = snapshot.Sum(i => i.Public.EstimatedBytes),
            categories,
            candidates = snapshot.Select(i => i.Public).ToArray(),
            rawEnumerated = s.RawEnumerated,
            // Rules whose enumeration hit the file cap — the listed candidate
            // set is a bounded subset, not complete coverage.
            truncatedRules = s.TruncatedRules.ToArray(),
            lastError = s.LastError,
        });
    }

    private DispatchResult CancelScan(JsonElement? p)
    {
        var sessionId = ParamSessionId(p);
        if (sessionId is null) return DispatchResult.Fail(Protocol.Errors.InvalidParams, "sessionId required");
        return _scans.Cancel(sessionId)
            ? DispatchResult.Ok(new { sessionId, status = "cancelling" })
            : DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");
    }

    private DispatchResult Preview(JsonElement? p)
    {
        var (req, err) = Parse<PreviewCleanupRequest>(p);
        if (err is not null) return err;
        if (!_scans.TryGet(req!.SessionId, out var s))
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");
        if (req.CandidateIds is null || req.CandidateIds.Count == 0)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "candidateIds required");

        // Preview-time revalidation gate (e.g. browser running). Commit also
        // re-checks per item — preview alone is not the safety boundary.
        if (s.CommitGuard?.Invoke() is { } guardErr)
            return DispatchResult.Fail(guardErr, "operation currently unsafe");

        // Session freshness: preview reports an expired session honestly so
        // the UI never presents a stale action as approvable. Commit still
        // enforces TTL per item — this is the same gate, earlier.
        if (_executor?.SessionTtl is { } ttl
            && DateTimeOffset.UtcNow - s.CreatedUtc > ttl)
            return DispatchResult.Fail(Protocol.Errors.SessionExpired,
                "session expired — rescan required");

        var resolved = _scans.ResolveCandidates(s, req.CandidateIds);
        if (resolved.Count != req.CandidateIds.Distinct().Count())
            return DispatchResult.Fail(Protocol.Errors.CandidateNotFound, "one or more candidateIds not in session");

        // M8.3: duplicate-group keeper/membership invariants — enforced here,
        // at the trust boundary, before any approval is recorded.
        if (ValidateDuplicateSelection(s, req.CandidateIds,
                req.DuplicateGroupId, req.KeeperId) is { } dupErr)
            return dupErr;

        // A successful preview records the engine-side approval a commit must
        // match — this is the authoritative approval boundary; the Flutter UI
        // is only a hint on top of it.
        lock (s.Items)
        {
            s.Approval = new ApprovalGrant(
                resolved.Select(i => i.Public.Id).ToHashSet(StringComparer.Ordinal),
                DateTimeOffset.UtcNow, req.DuplicateGroupId, req.KeeperId);
        }

        return DispatchResult.Ok(new
        {
            sessionId = s.Id,
            candidateCount = resolved.Count,
            estimatedBytes = resolved.Sum(i => i.Public.EstimatedBytes),
            req.DuplicateGroupId,
            req.KeeperId,
            candidates = resolved.Select(i => new
            {
                i.Public.Id, i.Public.DisplayName, i.Public.Category,
                i.Public.EstimatedBytes, i.Public.Risk,
                eligibility = ProbeEligibility(i.File),
            }).ToArray(),
        });
    }

    /// <summary>
    /// Read-only per-candidate eligibility probe at preview time, mirroring
    /// the executor's revalidation gates without mutating anything: lets the
    /// approval UI show which items would be skipped and why. Commit still
    /// re-validates — this is informational.
    /// </summary>
    private static string ProbeEligibility(ScannedFile file)
    {
        FileInfo current;
        try { current = new FileInfo(file.CanonicalPath); }
        catch { return "unreadable"; }
        if (!current.Exists) return "missing";
        if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "reparse_point";
        if (current.Length != file.LengthBytes
            || current.LastWriteTimeUtc != file.LastWriteUtc.UtcDateTime)
            return "changed";
        try
        {
            using var probe = new FileStream(file.CanonicalPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException) { return "locked"; }
        catch (UnauthorizedAccessException) { return "elevation_required"; }
        return "eligible";
    }

    private DispatchResult Commit(JsonElement? p)
    {
        var (req, err) = Parse<CommitCleanupRequest>(p);
        if (err is not null) return err;
        if (!_scans.TryGet(req!.SessionId, out var s))
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");
        if (_executor is null)
            return DispatchResult.Fail(Protocol.Errors.CleanupNotEnabled,
                "cleanup executor not configured");
        if (req.CandidateIds is null || req.CandidateIds.Count == 0)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "candidateIds required");

        // Session freshness, same gate as preview — a stale snapshot can never
        // be resurrected by holding a previously-granted approval.
        if (_executor.SessionTtl is { } ttl
            && DateTimeOffset.UtcNow - s.CreatedUtc > ttl)
            return DispatchResult.Fail(Protocol.Errors.SessionExpired,
                "session expired — rescan required");

        // Approval enforcement: commit may only cover candidates that passed
        // cleanup.preview in this session while the grant is still fresh.
        // This is the authoritative gate — never delegated to the client.
        ApprovalGrant? grant;
        lock (s.Items) grant = s.Approval;
        if (grant is null)
            return DispatchResult.Fail(Protocol.Errors.ApprovalRequired,
                "cleanup.preview approval required before commit");
        if (_executor.ApprovalTtl is { } attl
            && DateTimeOffset.UtcNow - grant.GrantedUtc > attl)
            return DispatchResult.Fail(Protocol.Errors.ApprovalStale,
                "approval is stale — run cleanup.preview again");
        var distinct = req.CandidateIds.Distinct().ToArray();
        if (distinct.Any(id => !grant.CandidateIds.Contains(id)))
            return DispatchResult.Fail(Protocol.Errors.ApprovalMismatch,
                "commit includes candidates outside the previewed approval");

        var resolved = _scans.ResolveCandidates(s, distinct);
        if (resolved.Count != distinct.Length)
            return DispatchResult.Fail(Protocol.Errors.CandidateNotFound, "one or more candidateIds not in session");

        // M8.3: the commit must echo the same group+keeper binding the
        // preview approved — the reviewed context is part of the approval.
        if (grant.DuplicateGroupId is not null || req.DuplicateGroupId is not null
            || grant.KeeperId is not null || req.KeeperId is not null)
        {
            if (!string.Equals(grant.DuplicateGroupId, req.DuplicateGroupId, StringComparison.Ordinal)
                || !string.Equals(grant.KeeperId, req.KeeperId, StringComparison.Ordinal))
                return DispatchResult.Fail(Protocol.Errors.ApprovalMismatch,
                    "commit does not match the previewed duplicate-group binding");
        }
        // Re-run membership + keeper-freshness gates: a keeper that changed
        // or vanished since review means the evidence is stale — re-review.
        if (ValidateDuplicateSelection(s, distinct,
                req.DuplicateGroupId, req.KeeperId) is { } dupErr)
            return dupErr;

        // Measured free-space delta on the quarantine volume: informational
        // only — same-volume quarantine moves are not expected to free space.
        long? before = FreeSpaceBytes(_executor.QuarantineRoot);
        var items = _executor.Commit(s.Id, distinct, s.Cancellation.Token);
        long? after = FreeSpaceBytes(_executor.QuarantineRoot);
        long? delta = before is null || after is null ? null : after - before;

        return DispatchResult.Ok(new CleanupCommitResponse(
            s.Id, distinct.Length, items.Count(i => i.Status != "rejected"),
            s.Cancellation.IsCancellationRequested,
            items, delta));
    }

    private static long? FreeSpaceBytes(string quarantineRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(quarantineRoot));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    private DispatchResult Restore(JsonElement? p)
    {
        if (_executor is null)
            return DispatchResult.Fail(Protocol.Errors.CleanupNotEnabled,
                "cleanup executor not configured");
        var (req, err) = Parse<RestoreRequest>(p);
        if (err is not null) return err;
        if (req!.OperationIds is null || req.OperationIds.Count == 0)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "operationIds required");

        var items = req.OperationIds.Select(id => _executor.Restore(id)).ToArray();
        return DispatchResult.Ok(new { items });
    }

    private DispatchResult GetCleanupHistory(JsonElement? p)
    {
        if (_journal is null) return DispatchResult.Ok(new HistoryResponse(Array.Empty<JournalEntryDto>()));
        // Bounded pagination for reconciliation workflows — read-only, capped
        // per page; the UI keeps using the defaults and sees no change.
        string? state = null, sessionId = null;
        int limit = 500, offset = 0;
        if (p is { ValueKind: JsonValueKind.Object } obj)
        {
            if (obj.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                state = st.GetString();
            if (obj.TryGetProperty("sessionId", out var si) && si.ValueKind == JsonValueKind.String)
                sessionId = si.GetString();
            if (obj.TryGetProperty("limit", out var l)) l.TryGetInt32(out limit);
            if (obj.TryGetProperty("offset", out var o)) o.TryGetInt32(out offset);
        }
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        var entries = _journal.Query(state, limit, offset, sessionId).Select(e => new JournalEntryDto(
            e.OperationId, e.SessionId, e.CandidateId, e.Category, e.FileLength,
            e.State.ToString().ToLowerInvariant(), e.ErrorCode, e.DisplayName,
            e.UpdatedUtc.ToString("O"), e.IntegrityState)).ToArray();
        return DispatchResult.Ok(new
        {
            entries,
            total = _journal.Count(state, sessionId),
        });
    }

    private DispatchResult GetStatus(JsonElement? p)
    {
        var sessionId = ParamSessionId(p);
        if (sessionId is null) return DispatchResult.Fail(Protocol.Errors.InvalidParams, "sessionId required");
        if (!_scans.TryGet(sessionId, out var s)) return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");
        int count; long bytes; SessionStatus status;
        lock (s.Items) { count = s.Items.Count; bytes = s.Items.Sum(i => i.File.LengthBytes); status = s.Status; }
        // Surface expiry so action UIs can drop stale references — commit would
        // reject them anyway, but the status check lets the UI fail earlier.
        var expired = _executor?.SessionTtl is { } ttl
                      && DateTimeOffset.UtcNow - s.CreatedUtc > ttl;
        return DispatchResult.Ok(new ScanSessionInfo(s.Id,
            expired ? "expired" : status.ToString().ToLowerInvariant(), count, bytes));
    }

    private static string? ParamSessionId(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object }) return null;
        return p.Value.TryGetProperty("sessionId", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
    }

    /// <summary>
    /// M8.3 duplicate-review invariants, enforced at the trust boundary —
    /// never delegated to the client. Whenever the selection intersects a
    /// duplicate group the request must name that group and the chosen
    /// keeper: the keeper must be a member of that group and must never be
    /// in the quarantine set; the set may contain only other members of the
    /// same group (selecting an entire group is therefore impossible);
    /// stale or forged group/keeper ids are rejected; and the keeper's file
    /// must still match its scan snapshot — a changed or vanished keeper
    /// means the reviewed evidence is stale and must be re-reviewed.
    /// </summary>
    private static DispatchResult? ValidateDuplicateSelection(
        ScanSession s, IReadOnlyList<string> rawIds,
        string? groupId, string? keeperId)
    {
        var groups = s.DuplicateReport?.Groups;
        var selected = new HashSet<string>(rawIds, StringComparer.Ordinal);

        // Keeperless request: allowed only when the selection touches no
        // duplicate group — otherwise an entire group could be quarantined
        // with no surviving copy.
        if (groupId is null && keeperId is null)
        {
            if (groups is not null && groups.Any(g =>
                    g.Candidates.Any(c => selected.Contains(c.Id))))
                return DispatchResult.Fail(Protocol.Errors.KeeperRequired,
                    "selection touches a duplicate group — supply duplicateGroupId and keeperId");
            return null;
        }
        if (groupId is null || keeperId is null)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams,
                "duplicateGroupId and keeperId must be supplied together");
        if (selected.Count != rawIds.Count)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams,
                "candidateIds must not contain duplicates");

        var group = groups?.FirstOrDefault(g => g.GroupId == groupId);
        if (group is null)
            return DispatchResult.Fail(Protocol.Errors.GroupNotFound,
                "unknown or stale duplicate group for this session");
        var members = group.Candidates.Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!members.Contains(keeperId))
            return DispatchResult.Fail(Protocol.Errors.KeeperInvalid,
                "keeperId is not a member of the named group");
        if (selected.Contains(keeperId))
            return DispatchResult.Fail(Protocol.Errors.KeeperInvalid,
                "the keeper must never be in the quarantine set");
        if (selected.Any(id => !members.Contains(id)))
            return DispatchResult.Fail(Protocol.Errors.GroupMismatch,
                "selection includes candidates outside the named group");

        // Keeper freshness: the reviewed choice only stands while the file
        // the user chose to keep still matches its scan snapshot. (Existence
        // + identity stat only — the keeper is never opened or moved.)
        ScannedFile? kf;
        lock (s.Items)
            kf = s.Items.FirstOrDefault(i => i.Public.Id == keeperId).File;
        if (kf is null)
            return DispatchResult.Fail(Protocol.Errors.KeeperInvalid,
                "keeper id does not resolve in this session");
        FileInfo current;
        try { current = new FileInfo(kf.CanonicalPath); }
        catch { return DispatchResult.Fail(Protocol.Errors.KeeperStale,
            "the keeper is unreadable — rescan the group"); }
        if (!current.Exists || current.Length != kf.LengthBytes
            || current.LastWriteTimeUtc != kf.LastWriteUtc.UtcDateTime)
            return DispatchResult.Fail(Protocol.Errors.KeeperStale,
                "the keeper changed or vanished since review — rescan the group");
        return null;
    }

    // --- M3 features ---

    private DispatchResult DuplicatesStart(JsonElement? p)
    {
        if (_dupes is null || _scopes is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "duplicates module not configured");
        var (req, err) = Parse<DuplicatesStartRequest>(p);
        if (err is not null) return err;
        if (req!.ScopeIds is null || req.ScopeIds.Count == 0)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "scopeIds required");
        var bad = req.ScopeIds.Where(id => _scopes.Resolve(id) is null).ToArray();
        if (bad.Length > 0)
            return DispatchResult.Fail(Protocol.Errors.ScopeNotAllowed,
                $"unknown or unavailable scopes: {string.Join(',', bad)}");

        ScanSession session;
        try { session = _scans.CreateExternal(); }
        catch (SessionCapacityException ex)
        {
            return DispatchResult.Fail(Protocol.Errors.SessionCapacity, ex.Message);
        }
        var sid = session.Id;
        Task.Run(() =>
        {
            try
            {
                // ScanIntoSession attaches the report to the session itself —
                // group evidence lives and dies with its session.
                _dupes.ScanIntoSession(session, req.ScopeIds, session.Cancellation.Token);
                session.Status = session.Cancellation.IsCancellationRequested
                    ? ClearForge.Engine.Scanning.SessionStatus.Cancelled
                    : ClearForge.Engine.Scanning.SessionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                session.Status = ClearForge.Engine.Scanning.SessionStatus.Cancelled;
            }
            catch (Exception ex)
            {
                session.LastError = ex.GetType().Name;
                session.Status = ClearForge.Engine.Scanning.SessionStatus.Failed;
            }
        });
        return DispatchResult.Ok(new ScanSessionInfo(sid, "scanning", 0, 0));
    }

    private DispatchResult DuplicatesGroups(JsonElement? p)
    {
        var sessionId = ParamSessionId(p);
        if (sessionId is null) return DispatchResult.Fail(Protocol.Errors.InvalidParams, "sessionId required");
        // Group evidence is session-owned (M8.3): a session without a
        // duplicate report — wrong session kind, still scanning, cancelled or
        // failed — has no groups; an evicted session no longer exists at all.
        if (!_scans.TryGet(sessionId, out var s) || s.DuplicateReport is not { } report)
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "no duplicate report for session");
        return DispatchResult.Ok(new DuplicatesGroupsResponse(
            report.SessionId,
            report.Groups.Select(g => new DuplicateGroupDto(
                g.GroupId, g.Sha256Short, g.FileBytes, g.Copies,
                g.Candidates.Select(c => c.Id).ToArray())).ToArray(),
            report.EstimatedRecoverableBytes));
    }

    /// <summary>
    /// M8.3 read-only group review: per-member path, size, mtime and a live
    /// eligibility probe so the user can make an informed keeper choice.
    /// Client-supplied ids are never trusted — membership resolves against
    /// the session's own evidence. No state is recorded; this is not part of
    /// the approval path.
    /// </summary>
    private DispatchResult DuplicatesGroup(JsonElement? p)
    {
        var (req, err) = Parse<DuplicatesGroupRequest>(p);
        if (err is not null) return err;
        if (req!.GroupId is not { Length: > 0 })
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "groupId required");
        if (!_scans.TryGet(req.SessionId, out var s))
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound, "unknown session");
        if (s.DuplicateReport is not { } report)
            return DispatchResult.Fail(Protocol.Errors.GroupNotFound,
                "session has no duplicate report");
        var group = report.Groups.FirstOrDefault(g => g.GroupId == req.GroupId);
        if (group is null)
            return DispatchResult.Fail(Protocol.Errors.GroupNotFound,
                "unknown or stale duplicate group for this session");

        (ScannedFile File, ScanCandidate Public)[] items;
        SessionStatus status;
        bool cancelled;
        lock (s.Items)
        {
            status = s.Status;
            cancelled = s.Cancellation.IsCancellationRequested;
            items = s.Items.ToArray();
        }
        var byId = items.ToDictionary(i => i.Public.Id, i => i, StringComparer.Ordinal);
        var members = group.Candidates.Select(c =>
            byId.TryGetValue(c.Id, out var m)
                ? new DuplicateMemberDto(c.Id, m.File.CanonicalPath,
                    m.Public.DisplayName, m.File.LengthBytes,
                    m.File.LastWriteUtc.ToString("O"), ProbeEligibility(m.File))
                // Membership/session drift is shown, never hidden.
                : new DuplicateMemberDto(c.Id, c.DisplayName, c.DisplayName,
                    group.FileBytes, "", "unreadable")).ToArray();

        // Engine suggestion only — newest write, ties break on ordinal path
        // (the old auto-keep rule, demoted to a labelled hint). A member whose
        // file vanished or is unreadable is never suggested.
        var suggested = members
            .Where(m => m.Eligibility is not ("missing" or "unreadable"))
            .Select(m => (m.CandidateId,
                LastWrite: DateTimeOffset.TryParse(m.LastWriteUtc, out var t)
                    ? t : DateTimeOffset.MinValue,
                m.Path))
            .OrderByDescending(m => m.LastWrite)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .Select(m => m.CandidateId)
            .FirstOrDefault();

        var state = GroupSessionState(s, status, cancelled);
        var actionable = state == "live"
            && members.Any(m => m.Eligibility == "eligible");
        return DispatchResult.Ok(new DuplicateGroupDetailResponse(
            s.Id, group.GroupId, group.Sha256, group.FileBytes, group.Copies,
            group.FileBytes * (group.Copies - 1), state, actionable,
            suggested, members));
    }

    private string GroupSessionState(ScanSession s, SessionStatus status, bool cancelled)
    {
        if (_executor?.SessionTtl is { } ttl
            && DateTimeOffset.UtcNow - s.CreatedUtc > ttl) return "expired";
        if (cancelled || status is SessionStatus.Failed or SessionStatus.Cancelled)
            return "cancelled";
        if (status == SessionStatus.Scanning) return "scanning";
        return "live";
    }

    private DispatchResult StartupList()
    {
        if (_startup is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "startup module not configured");
        var entries = _startup.Enumerate().Select(e => new StartupEntryDto(
            e.Name, e.Source, e.Target, e.Publisher, e.Status, e.Confidence,
            Modifiable: false)).ToArray();
        return DispatchResult.Ok(new StartupListResponse(entries,
            "Read-only inventory. Modify entries via Windows Settings > Apps > Startup."));
    }

    private DispatchResult AppsList()
    {
        if (_apps is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "apps module not configured");
        var apps = _apps.Enumerate().Select(a => new AppEntryDto(
            a.Name, a.Publisher, a.Version, a.InstallLocation, a.SizeBytes,
            a.InstallDate, a.Source, a.Scope, a.Kind, a.HasUninstaller,
            a.Confidence)).ToArray();
        return DispatchResult.Ok(new AppListResponse(apps,
            "Read-only inventory of registered applications. Classic desktop "
            + "installers only — Store/Appx packages are not enumerated yet. "
            + "Uninstall via Settings > Apps > Installed apps."));
    }

    private DispatchResult PrivacyScanCache(JsonElement? p)
    {
        if (_cacheScan is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "privacy cache scan not configured");
        ScanSession session;
        try { session = _scans.CreateExternal(); }
        catch (SessionCapacityException ex)
        {
            return DispatchResult.Fail(Protocol.Errors.SessionCapacity, ex.Message);
        }
        var sid = session.Id;
        Task.Run(() =>
        {
            try
            {
                _cacheScan.ScanIntoSession(session, session.Cancellation.Token);
                session.Status = session.Cancellation.IsCancellationRequested
                    ? SessionStatus.Cancelled : SessionStatus.Completed;
            }
            catch (OperationCanceledException) { session.Status = SessionStatus.Cancelled; }
            catch (Exception ex)
            {
                session.LastError = ex.GetType().Name;
                session.Status = SessionStatus.Failed;
            }
        });
        return DispatchResult.Ok(new ScanSessionInfo(sid, "scanning", 0, 0));
    }

    private DispatchResult PrivacyDetect()
    {
        if (_privacy is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "privacy module not configured");
        var profiles = _privacy.Detect().Select(pr => new BrowserProfileDto(
            pr.Browser, pr.ProfileName, pr.BrowserRunning, pr.CacheBytes,
            pr.Categories.ToDictionary(kv => kv.Key, kv => new PrivacyCategoryDto(
                kv.Value.Id, kv.Value.Available, kv.Value.Warning,
                kv.Value.UnavailableReason)))).ToArray();
        return DispatchResult.Ok(new PrivacyDetectResponse(profiles));
    }

    // --- M4 Smart Storage (read-only surface; executable recs are session-bound) ---

    private readonly ClearForge.Engine.Storage.StorageInventory? _inventory;
    private readonly ClearForge.Engine.Storage.RecommendationActionBridge? _actionBridge;
    private ClearForge.Engine.Storage.StorageInventoryReport? _lastInventory;
    private int _inventoryGeneration;
    private int _recommendationGeneration = -1;
    private IReadOnlyList<ClearForge.Engine.Storage.Recommendation>? _mergedRecommendations;
    private readonly object _recsLock = new();

    private ClearForge.Engine.Storage.StorageInventory RequireInventory()
        => _inventory ?? throw new InvalidOperationException("storage module not configured");

    /// <summary>
    /// The last observed inventory; a fresh scan bumps the generation so the
    /// next recommend rebuilds action sessions instead of reusing stale ones.
    /// </summary>
    private ClearForge.Engine.Storage.StorageInventoryReport CurrentInventory()
    {
        if (_lastInventory is null)
        {
            _lastInventory = RequireInventory().Scan(
                _scopes?.All.Keys.ToArray() ?? Array.Empty<string>(), CancellationToken.None);
            _inventoryGeneration++;
        }
        return _lastInventory;
    }

    /// <summary>
    /// Inventory-derived (review-only) plus session-bound executable
    /// recommendations, cached per inventory generation. One action session
    /// per category per generation — repeated recommend/plan calls never
    /// duplicate registrations.
    /// </summary>
    private IReadOnlyList<ClearForge.Engine.Storage.Recommendation> MergedRecommendations()
    {
        // Serialized: concurrent recommend calls must never double-register
        // action sessions for the same generation.
        lock (_recsLock)
        {
            if (_mergedRecommendations is not null && _recommendationGeneration == _inventoryGeneration)
                return _mergedRecommendations;
            var merged = ClearForge.Engine.Storage.RecommendationEngine.FromInventory(CurrentInventory())
                .Concat(_actionBridge?.Build() ?? Enumerable.Empty<ClearForge.Engine.Storage.Recommendation>())
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToArray();
            _mergedRecommendations = merged;
            _recommendationGeneration = _inventoryGeneration;
            return merged;
        }
    }

    private DispatchResult StorageInventory(JsonElement? p)
    {
        if (_inventory is null) return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "storage module not configured");
        var scopeIds = _scopes?.All.Keys.ToArray() ?? Array.Empty<string>();
        var r = _inventory.Scan(scopeIds, CancellationToken.None);
        lock (_recsLock)
        {
            _lastInventory = r;
            _inventoryGeneration++; // fresh evidence invalidates bound action sessions
        }
        return DispatchResult.Ok(new
        {
            totalLogicalBytes = r.TotalLogicalBytes,
            partial = r.Partial,
            scopes = r.Scopes.ToDictionary(kv => kv.Key, kv => kv.Value.Select(n => new
            {
                name = n.Name, kind = n.Kind,
                logicalBytes = n.LogicalBytes, categoryHint = n.CategoryHint,
            }).ToArray()),
        });
    }

    private DispatchResult StorageRecommend(JsonElement? p)
    {
        if (_inventory is null) return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "storage module not configured");
        var recs = MergedRecommendations();
        return DispatchResult.Ok(new { recommendations = recs.Select(r => new
        {
            r.Id, r.Category, r.Title, r.Explanation, r.Evidence,
            r.EstimatedLogicalBytes,
            confidence = r.Confidence.ToString().ToLowerInvariant(),
            risk = r.Risk.ToString().ToLowerInvariant(),
            reversibility = r.Reversibility.ToString().ToLowerInvariant(),
            r.Preconditions, r.SkipReasons, r.ActionRef,
            action = r.Action is null ? null : new
            {
                r.Action.Kind, r.Action.SessionId, r.Action.Status,
                r.Action.CandidateCount, r.Action.EligibleBytes,
            },
        }).ToArray() });
    }

    private DispatchResult StoragePlan(JsonElement? p)
    {
        long goal = 0;
        if (p is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("goalBytes", out var g))
            g.TryGetInt64(out goal);
        if (goal <= 0) return DispatchResult.Fail(Protocol.Errors.InvalidParams, "goalBytes must be > 0");
        if (_inventory is null) return DispatchResult.Fail(Protocol.Errors.PolicyRefused, "storage module not configured");
        var recs = MergedRecommendations();
        var constraints = ParsePlanConstraints(p);
        var plan = ClearForge.Engine.Storage.RecommendationEngine.Plan(goal, recs, constraints);
        return DispatchResult.Ok(new
        {
            goalBytes = plan.GoalBytes,
            estimatedTotalBytes = plan.EstimatedTotalBytes,
            likelyAchievable = plan.LikelyAchievable,
            selected = plan.Selected.Select(r => new
            {
                r.Id, r.Title, r.Explanation, r.Evidence, r.EstimatedLogicalBytes,
                confidence = r.Confidence.ToString().ToLowerInvariant(),
                risk = r.Risk.ToString().ToLowerInvariant(),
                reversibility = r.Reversibility.ToString().ToLowerInvariant(),
                r.ActionRef,
                action = r.Action is null ? null : new
                {
                    r.Action.Kind, r.Action.SessionId, r.Action.Status,
                    r.Action.CandidateCount, r.Action.EligibleBytes,
                },
            }).ToArray(),
            skipped = plan.Skipped.Select(s => new { s.Id, s.Title, s.Reason }).ToArray(),
            plan.Explanations, plan.MissingEvidence,
        });
    }

    /// <summary>
    /// Optional user constraints for planning: rejected recommendation IDs and
    /// excluded categories/scopes. All fields optional and additive — they
    /// only narrow what the deterministic planner may select.
    /// </summary>
    private static ClearForge.Engine.Storage.PlanConstraints? ParsePlanConstraints(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("constraints", out var c)
            || c.ValueKind != JsonValueKind.Object)
            return null;
        return new ClearForge.Engine.Storage.PlanConstraints(
            StringList(c, "excludeIds"),
            StringList(c, "excludeCategories"),
            StringList(c, "excludeScopes"));
    }

    private static IReadOnlyCollection<string>? StringList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!).ToArray();
    }

    private static (T? Req, DispatchResult? Err) Parse<T>(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object })
            return (default, DispatchResult.Fail(Protocol.Errors.InvalidParams, "params object required"));
        try { return (p.Value.Deserialize<T>(ParamsJson), null); }
        catch (JsonException) { return (default, DispatchResult.Fail(Protocol.Errors.InvalidParams, "bad params")); }
    }

    // --- M7.2 curated model catalogue + secure install lifecycle ---

    private readonly ClearForge.Models.ModelManager? _models;

    /// <summary>Stable wire shape for a model's live lifecycle state.</summary>
    private static object StateDto(ClearForge.Models.ModelManager.ModelState s) => new
    {
        modelId = s.ModelId,
        state = s.State,
        installedBytes = s.InstalledBytes,
        downloadedBytes = s.DownloadedBytes,
        totalBytes = s.TotalBytes,
        error = s.Error,
    };

    private DispatchResult ModelsCatalog()
    {
        if (_models is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "models module not configured");
        var states = _models.Status().ToDictionary(s => s.ModelId, StringComparer.Ordinal);
        return DispatchResult.Ok(new
        {
            catalogVersion = _models.Catalog.Version,
            models = _models.Catalog.Models.Select(m => new
            {
                m.Id, m.DisplayName, m.Publisher, m.Repository,
                m.UpstreamRepository, m.Revision,
                artifact = new
                {
                    m.Artifact.FileName, m.Artifact.SizeBytes,
                    m.Artifact.Format, m.Artifact.Sha256,
                },
                license = new { m.License.Id, m.License.Url },
                runtime = new
                {
                    m.Runtime.Backend, m.Runtime.Acceleration,
                    m.Runtime.Architectures,
                },
                requirements = new
                {
                    m.Requirements.MinRamBytesEstimate,
                    m.Requirements.MinFreeStorageBytesEstimate,
                    estimatesAreApproximate = true,
                },
                supportStatus = m.SupportStatus,
                qualityNotes = m.QualityNotes,
                state = states.TryGetValue(m.Id, out var s) ? StateDto(s) : null,
            }).ToArray(),
        });
    }

    private DispatchResult ModelsStatus()
    {
        if (_models is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "models module not configured");
        return DispatchResult.Ok(new
        {
            models = _models.Status().Select(StateDto).ToArray(),
        });
    }

    private static string? ParamModelId(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object }) return null;
        return p.Value.TryGetProperty("modelId", out var el)
            && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private DispatchResult ModelsInstall(JsonElement? p)
    {
        if (_models is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "models module not configured");
        var id = ParamModelId(p);
        if (id is null)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "modelId required");
        try { return DispatchResult.Ok(StateDto(_models.BeginInstall(id))); }
        catch (ClearForge.Models.ModelException ex)
        {
            return DispatchResult.Fail(ex.Code, ex.Message);
        }
    }

    private DispatchResult ModelsCancel(JsonElement? p)
    {
        if (_models is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "models module not configured");
        var id = ParamModelId(p);
        if (id is null)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "modelId required");
        return DispatchResult.Ok(new { modelId = id, cancelled = _models.Cancel(id) });
    }

    private DispatchResult ModelsRemove(JsonElement? p)
    {
        if (_models is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "models module not configured");
        var id = ParamModelId(p);
        if (id is null)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams, "modelId required");
        try { return DispatchResult.Ok(StateDto(_models.Remove(id))); }
        catch (ClearForge.Models.ModelException ex)
        {
            return DispatchResult.Fail(ex.Code, ex.Message);
        }
    }

    // --- M8.1 unified smart scan ---

    private static string? ParamSmartScanId(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object }) return null;
        return p.Value.TryGetProperty("smartScanId", out var el)
               && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>
    /// Live executability of a finding's backing session at ANSWER time —
    /// not a stored snapshot: "missing" (evicted), "expired" (past
    /// SessionTtl), "cancelled" (latched cancellation → commits would
    /// report cancelled per item), else "live". Null for findings with no
    /// binding. The engine still re-enforces every gate at preview/commit;
    /// this lets the UI stop offering actions that can only fail.
    /// </summary>
    private string? FindingSessionState(ClearForge.Engine.SmartScan.SmartFinding f)
    {
        if (f.SessionId is null) return null;
        if (!_scans.TryGet(f.SessionId, out var child)) return "missing";
        if (_executor?.SessionTtl is { } ttl
            && DateTimeOffset.UtcNow - child.CreatedUtc > ttl) return "expired";
        if (child.Status is SessionStatus.Failed or SessionStatus.Cancelled
            || child.Cancellation.IsCancellationRequested) return "cancelled";
        return "live";
    }

    private object SmartScanStatusDto(ClearForge.Engine.SmartScan.SmartScanSession s)
    {
        lock (s.Sync)
            return new
            {
                smartScanId = s.Id,
                status = s.Status.ToString().ToLowerInvariant(),
                userCancelled = s.UserCancelled,
                producers = s.Producers.Select(p => new
                {
                    producerId = p.ProducerId,
                    status = p.Status.ToString().ToLowerInvariant(),
                    findings = p.Findings,
                    diagnostic = p.Diagnostic,
                }).ToArray(),
                findingCount = s.Findings.Count,
                actionableCount = s.Findings.Count(f => f.Actionable),
                deduplicated = s.DeduplicatedCount,
                estimatedLogicalBytes = s.Findings.Where(f => f.Actionable)
                    .Sum(f => f.EstimatedLogicalBytes),
            };
    }

    private DispatchResult SmartScanStart(JsonElement? p)
    {
        if (_smartScan is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "smart scan not configured");
        // Optional explicit scope narrowing; default = every configured scope.
        IReadOnlyList<string> scopeIds = _scopes?.All.Keys.ToArray()
            ?? Array.Empty<string>();
        if (p is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("scopeIds", out var sids)
            && sids.ValueKind == JsonValueKind.Array)
        {
            var asked = sids.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToArray();
            var bad = asked.Where(id => _scopes?.Resolve(id) is null).ToArray();
            if (bad.Length > 0)
                return DispatchResult.Fail(Protocol.Errors.ScopeNotAllowed,
                    $"unknown or unavailable scopes: {string.Join(',', bad)}");
            scopeIds = asked;
        }
        try
        {
            var s = _smartScan.Start(scopeIds);
            return DispatchResult.Ok(new { smartScanId = s.Id, status = "running" });
        }
        catch (SessionCapacityException ex)
        {
            return DispatchResult.Fail(Protocol.Errors.SessionCapacity, ex.Message);
        }
    }

    private DispatchResult SmartScanStatusResult(JsonElement? p)
    {
        if (_smartScan is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "smart scan not configured");
        var id = ParamSmartScanId(p);
        if (id is null || !_smartScan.Store.TryGet(id, out var s))
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound,
                "unknown smart scan");
        return DispatchResult.Ok(SmartScanStatusDto(s));
    }

    private DispatchResult SmartScanResults(JsonElement? p)
    {
        if (_smartScan is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "smart scan not configured");
        var id = ParamSmartScanId(p);
        if (id is null || !_smartScan.Store.TryGet(id, out var s))
            return DispatchResult.Fail(Protocol.Errors.SessionNotFound,
                "unknown smart scan");
        lock (s.Sync)
            return DispatchResult.Ok(new
            {
                smartScanId = s.Id,
                status = s.Status.ToString().ToLowerInvariant(),
                userCancelled = s.UserCancelled,
                producers = s.Producers.Select(p => new
                {
                    producerId = p.ProducerId,
                    status = p.Status.ToString().ToLowerInvariant(),
                    findings = p.Findings,
                    diagnostic = p.Diagnostic,
                }).ToArray(),
                findings = s.Findings.Select(f => new
                {
                    f.Id, f.Producer, f.Category, f.Title, f.Reason,
                    f.Evidence, f.Confidence,
                    risk = f.Risk.ToString().ToLowerInvariant(),
                    f.EstimatedLogicalBytes, f.Reversibility,
                    f.Actionable, f.SkipReason, f.SessionId, f.CandidateIds,
                    f.DuplicateOf,
                    sessionState = FindingSessionState(f),
                }).ToArray(),
                deduplicated = s.DeduplicatedCount,
                // Logical estimate only — quarantined bytes are NOT freed space.
                estimatedLogicalBytes = s.Findings.Where(f => f.Actionable)
                    .Sum(f => f.EstimatedLogicalBytes),
            });
    }

    private DispatchResult SmartScanCancel(JsonElement? p)
    {
        if (_smartScan is null)
            return DispatchResult.Fail(Protocol.Errors.PolicyRefused,
                "smart scan not configured");
        var id = ParamSmartScanId(p);
        if (id is null)
            return DispatchResult.Fail(Protocol.Errors.InvalidParams,
                "smartScanId required");
        return _smartScan.Store.Cancel(id)
            ? DispatchResult.Ok(new { smartScanId = id, status = "cancelling" })
            : DispatchResult.Fail(Protocol.Errors.SessionNotFound,
                "unknown smart scan");
    }
}
