using System.Collections.Concurrent;
using ClearForge.Contracts;
using ClearForge.Engine.Classification;
using ClearForge.Engine.Policies;

namespace ClearForge.Engine.Scanning;

public enum SessionStatus { Scanning, Completed, Cancelled, Failed }

/// <summary>
/// Engine-side record that a candidate set passed cleanup.preview for this
/// session — the approval evidence a commit must match. Written only by the
/// request boundary; never trusted from the wire.
/// </summary>
public sealed record ApprovalGrant(
    IReadOnlySet<string> CandidateIds,
    DateTimeOffset GrantedUtc,
    // M8.3: when the previewed set is a duplicate-group quarantine, the
    // reviewed group and the member the user chose to keep. Commit must echo
    // the same binding — a mismatch is approval_mismatch.
    string? DuplicateGroupId = null,
    string? KeeperId = null);

/// <summary>Bounds for in-memory scan-session retention.</summary>
public sealed record ScanSessionOptions(
    int MaxSessions = 128,
    TimeSpan? RetentionPeriod = null);

/// <summary>Raised when a new scan session would exceed MaxSessions.</summary>
public sealed class SessionCapacityException : Exception
{
    public SessionCapacityException(int max)
        : base($"scan session capacity ({max}) reached — retry after sessions expire")
        => MaxSessions = max;
    public int MaxSessions { get; }
}

/// <summary>
/// One scan session: internal records carry canonical paths; the public
/// projection (ScanCandidate) carries only opaque IDs.
/// </summary>
public sealed class ScanSession
{
    public required string Id { get; init; }

    // Status and Items are always read/written under lock(Items) so that a
    // "completed" observation can never race ahead of its item snapshot.
    private SessionStatus _status = SessionStatus.Scanning;
    public SessionStatus Status
    {
        get { lock (Items) return _status; }
        set { lock (Items) _status = value; }
    }
    /// <summary>Count of raw files enumerated before classification.</summary>
    public int RawEnumerated { get; set; }
    /// <summary>
    /// M8.2: rule ids whose enumeration ended at the file cap rather than by
    /// exhaustion — the candidate set for those rules is a bounded subset,
    /// and findings must say so. Mutated only under lock(Items).
    /// </summary>
    public HashSet<string> TruncatedRules { get; } = new(StringComparer.Ordinal);
    /// <summary>Internal diagnostic message; never contains a filesystem path.</summary>
    public string? LastError { get; set; }
    public List<(ScannedFile File, ScanCandidate Public)> Items { get; } = new();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// Optional live revalidation gate, evaluated per item immediately before
    /// each quarantine move (and at preview). Returns a structured error code
    /// when the operation must be refused — e.g. a contributing browser
    /// process started since the scan. Null means no extra gate.
    /// </summary>
    public Func<string?>? CommitGuard { get; set; }

    /// <summary>
    /// The preview approval recorded for this session; commit may only cover
    /// ids it contains. Read/write under lock(Items).
    /// </summary>
    public ApprovalGrant? Approval { get; set; }

    /// <summary>
    /// M8.3: duplicate-group evidence produced by DuplicateFinder for this
    /// session — set for both `duplicates.start` sessions and Smart Scan
    /// child sessions so keeper/group invariants can be enforced at the
    /// preview/commit boundary regardless of entry point. Null for
    /// non-duplicate scans. Owned by the session: it dies with eviction and
    /// needs no separate pruning.
    /// </summary>
    public Duplicates.DuplicateReport? DuplicateReport { get; set; }
}

/// <summary>
/// Coordinates scans against the versioned rule catalog and trusted roots.
/// All scan work is read-only.
/// </summary>
public sealed class ScanSessionStore
{
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly FileScanner _scanner;
    private readonly ConcurrentDictionary<string, ScanSession> _sessions = new();
    private readonly int _maxSessions;
    private readonly TimeSpan _retention;

    public ScanSessionStore(IReadOnlyList<CleanupRule> rules, FileScanner scanner,
        ScanSessionOptions? options = null)
    {
        _rules = rules;
        _scanner = scanner;
        _maxSessions = Math.Max(1, options?.MaxSessions ?? 128);
        _retention = options?.RetentionPeriod ?? TimeSpan.FromMinutes(60);
    }

    /// <summary>Categories backed by at least one rule whose action is
    /// "quarantine" — the only ones eligible for executable recommendations.</summary>
    public IReadOnlyCollection<string> QuarantinableCategories =>
        _rules.Where(r => r.Action == "quarantine")
              .Select(r => r.Category)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    public ScanSession Start(IReadOnlyList<string>? categories = null)
    {
        var session = RegisterNew(new ScanSession { Id = Guid.NewGuid().ToString("N") });
        Task.Run(() => Populate(session, categories));
        return session;
    }

    /// <summary>
    /// Synchronously enumerate + classify catalog rules into a session.
    /// Used by Start() asynchronously and by the recommendation bridge inline
    /// — one code path, one semantics.
    /// </summary>
    public void Populate(ScanSession session, IReadOnlyList<string>? categories = null)
    {
        var wanted = categories is { Count: > 0 }
            ? new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase)
            : null;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var index = 0;
            string IdFor(int _) => $"{session.Id}:{index++}";

            foreach (var rule in _rules)
            {
                if (session.Cancellation.IsCancellationRequested) break;
                if (wanted is not null && !wanted.Contains(rule.Category)) continue;
                if (rule.Validate() is { } invalidRule)
                {
                    session.LastError = $"rule {rule.Id}: {invalidRule}";
                    continue;
                }

                foreach (var file in _scanner.Enumerate(rule, session.Id, IdFor,
                    session.Cancellation.Token,
                    onLimitReached: () =>
                    {
                        lock (session.Items) session.TruncatedRules.Add(rule.Id);
                    }))
                {
                    session.RawEnumerated++;
                    var pub = Classifier.TryClassify(file, rule, now);
                    if (pub is not null)
                        lock (session.Items) { session.Items.Add((file, pub)); }
                }
            }
            session.Status = session.Cancellation.IsCancellationRequested
                ? SessionStatus.Cancelled : SessionStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            session.Status = SessionStatus.Cancelled;
        }
        catch (Exception ex)
        {
            session.LastError = ex.GetType().Name + ": " + ex.Message;
            session.Status = SessionStatus.Failed;
        }
    }

    /// <summary>Register an externally-produced session (e.g. duplicate scan)
    /// so its candidates flow through the same results/commit pipeline.</summary>
    public ScanSession Register(ScanSession session) => RegisterNew(session);

    /// <summary>Create an empty registered session for non-catalog scans.</summary>
    public ScanSession CreateExternal() =>
        RegisterNew(new ScanSession { Id = Guid.NewGuid().ToString("N") });

    /// <summary>
    /// Bounded registration: sweep stale terminal sessions first, then refuse
    /// when capacity is still reached. Scanning sessions are never evicted;
    /// a session carrying a still-fresh approval grant is approval-relevant
    /// and kept until the grant ages past retention. Eviction only removes
    /// the in-memory session — journal rows and restore eligibility are
    /// engine-owned and unaffected.
    /// </summary>
    private ScanSession RegisterNew(ScanSession session)
    {
        Evict();
        if (_sessions.Count >= _maxSessions)
            throw new SessionCapacityException(_maxSessions);
        _sessions[session.Id] = session;
        return session;
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (s.Status == SessionStatus.Scanning) continue;
            ApprovalGrant? grant;
            lock (s.Items) grant = s.Approval;
            var approvalFresh = grant is not null && now - grant.GrantedUtc <= _retention;
            if (approvalFresh || now - s.CreatedUtc <= _retention) continue;
            if (_sessions.TryRemove(kv.Key, out var evicted))
                evicted.Cancellation.Cancel(); // release any waiting reader
        }
    }

    public bool TryGet(string id, out ScanSession session) =>
        _sessions.TryGetValue(id, out session!);

    public bool Cancel(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return false;
        s.Cancellation.Cancel();
        return true;
    }

    /// <summary>
    /// Look up internal records for opaque candidate IDs. Only IDs minted by
    /// this session resolve — the client can never inject a path.
    /// </summary>
    public IReadOnlyList<(ScannedFile File, ScanCandidate Public)> ResolveCandidates(
        ScanSession session, IEnumerable<string> candidateIds)
    {
        var wanted = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        lock (session.Items)
            return session.Items.Where(i => wanted.Contains(i.Public.Id)).ToArray();
    }
}
