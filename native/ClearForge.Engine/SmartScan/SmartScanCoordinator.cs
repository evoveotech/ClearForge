using ClearForge.Contracts;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Startup;
using ClearForge.Engine.Storage;

namespace ClearForge.Engine.SmartScan;

/// <summary>Lifecycle of one unified scan run.</summary>
public enum SmartScanStatus { Running, Completed, Partial, Cancelled, Failed }
public enum ProducerStatus { Pending, Running, Completed, Failed, Cancelled }

/// <summary>
/// One deduplicated finding. Actionable findings bind to session-owned
/// candidate ids — execution only ever flows through cleanup.preview →
/// explicit approval → cleanup.commit on those sessions. Review-only
/// findings carry evidence and a skip reason, never a binding.
/// </summary>
public sealed record SmartFinding(
    string Id,
    string Producer,
    string Category,
    string Title,
    string Reason,
    IReadOnlyList<string> Evidence,
    string Confidence,        // "high" | "medium" | "low"
    RiskLevel Risk,
    long EstimatedLogicalBytes, // logical bytes only — never promised free space
    string Reversibility,
    bool Actionable,
    string? SkipReason,
    string? SessionId,
    IReadOnlyList<string>? CandidateIds,
    string? DuplicateOf = null);

public sealed record ProducerReport(
    string ProducerId, ProducerStatus Status, string? Diagnostic, int Findings);

/// <summary>
/// One producer's contribution: its findings plus the scan session (when
/// any) holding its candidates for later commit. Read-only by contract —
/// producers never mutate the filesystem.
/// </summary>
public sealed record ProducerOutcome(
    IReadOnlyList<SmartFinding> Findings,
    ScanSession? Session = null,
    string? Diagnostic = null);

public interface ISmartScanProducer
{
    string Id { get; }
    /// <summary>Priority controls dedup precedence — lower wins.</summary>
    int Priority { get; }
    ProducerOutcome Run(SmartScanContext context, CancellationToken ct);
}

public sealed record SmartScanContext(IReadOnlyList<string> ScopeIds);

/// <summary>
/// A running or finished unified scan. Findings and producer reports are
/// replaced atomically as producers complete; readers always see a
/// consistent point-in-time view under lock(Sync).
/// </summary>
public sealed class SmartScanSession
{
    public required string Id { get; init; }
    public object Sync { get; } = new();
    public SmartScanStatus Status { get; set; } = SmartScanStatus.Running;
    public List<ProducerReport> Producers { get; } = new();
    public List<SmartFinding> Findings { get; set; } = new();
    public List<ScanSession> ChildSessions { get; } = new();
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();
    public int DeduplicatedCount { get; set; }

    /// <summary>
    /// True once the user explicitly cancelled this run. Kept separate from
    /// Status because a cancelled run may still carry honestly-completed
    /// producer work — Status reports the work outcome, this reports the
    /// abort. Clients must never present a user-cancelled run's findings as
    /// a fresh, pre-selected result set.
    /// </summary>
    public bool UserCancelled => Cancellation.IsCancellationRequested;
}

/// <summary>
/// Bounded retention for unified scan runs — mirrors the scan-session
/// lifecycle: terminal sessions past retention are evicted; their child
/// scan sessions age out under ScanSessionStore's own rules, after which
/// commits on stale bindings fail session_not_found honestly.
/// </summary>
public sealed class SmartScanStore
{
    private readonly Dictionary<string, SmartScanSession> _sessions = new();
    private readonly int _maxSessions;
    private readonly TimeSpan _retention;
    private readonly object _sync = new();

    public SmartScanStore(int maxSessions = 8, TimeSpan? retention = null)
    {
        _maxSessions = Math.Max(1, maxSessions);
        _retention = retention ?? TimeSpan.FromMinutes(30);
    }

    public SmartScanSession Create(IReadOnlyList<string> producerIds)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _sessions
                         .Where(kv => kv.Value.Status != SmartScanStatus.Running
                                      && now - kv.Value.CreatedUtc > _retention)
                         .ToArray())
            {
                _sessions.Remove(kv.Key);
                kv.Value.Cancellation.Cancel();
            }
            if (_sessions.Count >= _maxSessions)
                throw new SessionCapacityException(_maxSessions);
            var s = new SmartScanSession
            {
                Id = Guid.NewGuid().ToString("N"),
            };
            foreach (var id in producerIds)
                s.Producers.Add(new ProducerReport(id, ProducerStatus.Pending, null, 0));
            _sessions[s.Id] = s;
            return s;
        }
    }

    public bool TryGet(string id, out SmartScanSession session)
    {
        lock (_sync) return _sessions.TryGetValue(id, out session!);
    }

    public bool Cancel(string id)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out var s)) return false;
            lock (s.Sync)
            {
                // A terminal run has nothing to abort: latching its (or its
                // children's) cancellation would only make still-valid
                // session bindings report "cancelled" at commit — the exact
                // stale-state trap the UI must not create.
                if (s.Status != SmartScanStatus.Running) return true;
            }
            s.Cancellation.Cancel();
            foreach (var child in s.ChildSessions) child.Cancellation.Cancel();
            return true;
        }
    }
}

/// <summary>
/// Runs the registered read-only producers in deterministic priority order,
/// deduplicates actionable findings by canonical path, and reports honest
/// per-producer and aggregate status. Contains no mutation path.
/// </summary>
public sealed class SmartScanCoordinator
{
    private readonly SmartScanStore _store;
    private readonly IReadOnlyList<ISmartScanProducer> _producers;
    private readonly TimeSpan _perProducerTimeout;
    private readonly TimeSpan _overallTimeout;

    /// <summary>
    /// <paramref name="perProducerTimeout"/> bounds each producer's share of
    /// the run (default 2 min). <paramref name="overallTimeout"/> bounds the
    /// whole run's wall clock — defaults to the sum of per-producer budgets
    /// (2 min x producer count), so a run can never legitimately outlive it.
    /// Both are enforced through the SAME cooperative token the producers
    /// receive: a producer that cannot observe cancellation keeps running
    /// and is reported as still-running work, never as cancelled.
    /// </summary>
    public SmartScanCoordinator(SmartScanStore store,
        IEnumerable<ISmartScanProducer> producers,
        TimeSpan? perProducerTimeout = null, TimeSpan? overallTimeout = null)
    {
        _store = store;
        _producers = producers.OrderBy(p => p.Priority).ToArray();
        _perProducerTimeout = perProducerTimeout ?? TimeSpan.FromMinutes(2);
        _overallTimeout = overallTimeout ?? TimeSpan.FromTicks(
            _perProducerTimeout.Ticks * Math.Max(1, _producers.Count));
    }

    public SmartScanStore Store => _store;

    /// <summary>Start a run; returns the session immediately, work proceeds in background.</summary>
    public SmartScanSession Start(IReadOnlyList<string> scopeIds)
    {
        var session = _store.Create(_producers.Select(p => p.Id).ToArray());
        var context = new SmartScanContext(scopeIds);
        Task.Run(() =>
        {
            // A coordinator-level fault must never leave a run stuck in
            // "running" — the client would poll forever.
            try { Run(session, context); }
            catch (Exception ex)
            {
                lock (session.Sync)
                {
                    for (var i = 0; i < session.Producers.Count; i++)
                        if (session.Producers[i].Status
                            is ProducerStatus.Pending or ProducerStatus.Running)
                            session.Producers[i] = new ProducerReport(
                                session.Producers[i].ProducerId,
                                ProducerStatus.Failed,
                                $"coordinator fault: {ex.GetType().Name}", 0);
                    session.Status = SmartScanStatus.Failed;
                }
            }
        });
        return session;
    }

    private void Run(SmartScanSession session, SmartScanContext context)
    {
        var dedup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // canonical path → finding id
        var merged = new Dictionary<string, List<string>>(); // finding id → extra evidence

        // Overall deadline: linked to the user-cancel token so an explicit
        // cancel still wins. Fires at most once per run.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(
            session.Cancellation.Token);
        overall.CancelAfter(_overallTimeout);

        foreach (var producer in _producers)
        {
            var idx = session.Producers.FindIndex(p => p.ProducerId == producer.Id);
            if (session.Cancellation.IsCancellationRequested)
            {
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Cancelled, "cancelled before start", 0);
                continue;
            }
            if (overall.IsCancellationRequested)
            {
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Failed,
                        "overall scan deadline — never started", 0);
                continue;
            }

            lock (session.Sync)
                session.Producers[idx] = new ProducerReport(
                    producer.Id, ProducerStatus.Running, null, 0);

            ProducerOutcome outcome;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    session.Cancellation.Token, overall.Token);
                linked.CancelAfter(_perProducerTimeout);
                outcome = producer.Run(context, linked.Token);
            }
            catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
            {
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Cancelled, "cancelled by user", 0);
                continue;
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Failed,
                        $"overall scan deadline ({_overallTimeout.TotalSeconds:F0}s)", 0);
                continue;
            }
            catch (OperationCanceledException)
            {
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Failed,
                        $"timeout after {_perProducerTimeout.TotalSeconds:F0}s", 0);
                continue;
            }
            catch (Exception ex)
            {
                // One producer's failure must never take the run or other
                // producers down — diagnostic only, never file contents.
                lock (session.Sync)
                    session.Producers[idx] = new ProducerReport(
                        producer.Id, ProducerStatus.Failed, ex.GetType().Name, 0);
                continue;
            }

            if (outcome.Session is not null)
                lock (session.Sync) session.ChildSessions.Add(outcome.Session);

            // A session that did not complete — or whose cancellation is
            // latched because a deadline/abort raced its completion — can
            // never commit: every item would report cancelled at execution.
            // Actionable findings bound to it are dropped rather than
            // presented as selectable; review-only findings still carry
            // their evidence. The producer report below keeps this honest.
            var usable = outcome.Session is null
                || (outcome.Session.Status == SessionStatus.Completed
                    && !outcome.Session.Cancellation.IsCancellationRequested);
            var outcomeForDedup = usable
                ? outcome
                : outcome with
                {
                    Findings = outcome.Findings.Where(f => !f.Actionable).ToArray(),
                };
            var accepted = ApplyDedup(producer, outcomeForDedup, dedup, merged, session);
            lock (session.Sync)
            {
                session.Findings.AddRange(accepted);
                // Honest producer status: a child session that ended Failed
                // or Cancelled is reported as such even though the producer
                // itself returned without throwing.
                var status = outcome.Session?.Status switch
                {
                    SessionStatus.Failed => ProducerStatus.Failed,
                    SessionStatus.Cancelled => ProducerStatus.Cancelled,
                    _ => ProducerStatus.Completed,
                };
                session.Producers[idx] = new ProducerReport(
                    producer.Id, status, outcome.Diagnostic, accepted.Count);
            }
        }

        lock (session.Sync)
        {
            // Deterministic presentation order: actionable first, then by
            // estimated bytes, then stable id — reproducible for unchanged fs.
            session.Findings = session.Findings
                .OrderByDescending(f => f.Actionable)
                .ThenByDescending(f => f.EstimatedLogicalBytes)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
            var statuses = session.Producers.Select(p => p.Status).ToArray();
            session.Status =
                session.Cancellation.IsCancellationRequested
                && statuses.All(s => s is ProducerStatus.Cancelled or ProducerStatus.Pending)
                    ? SmartScanStatus.Cancelled
                : statuses.All(s => s == ProducerStatus.Completed)
                    ? SmartScanStatus.Completed
                : statuses.All(s => s is ProducerStatus.Failed or ProducerStatus.Cancelled)
                    ? SmartScanStatus.Failed
                : SmartScanStatus.Partial;
        }
    }

    /// <summary>
    /// Conservative dedup: ONLY the exact canonical path proves two findings
    /// name the same file. Earlier (higher-priority) producers keep the
    /// actionable binding; losers become review-only with the winner's id,
    /// and their evidence is folded into the winner — never silently dropped.
    /// Distinct paths are never merged and bytes are never double-counted.
    /// </summary>
    private List<SmartFinding> ApplyDedup(
        ISmartScanProducer producer, ProducerOutcome outcome,
        Dictionary<string, string> claimed,
        Dictionary<string, List<string>> mergedEvidence,
        SmartScanSession session)
    {
        var pathOf = outcome.Session is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : outcome.Session.Items.ToDictionary(
                i => i.Public.Id, i => i.File.CanonicalPath,
                StringComparer.OrdinalIgnoreCase);

        var accepted = new List<SmartFinding>();
        var seq = 0;
        foreach (var f in outcome.Findings)
        {
            var id = $"{producer.Id}-{seq++}";
            if (!f.Actionable || f.CandidateIds is null)
            {
                accepted.Add(f with { Id = id });
                continue;
            }

            var kept = new List<string>();
            var covered = new List<string>();
            foreach (var cid in f.CandidateIds)
            {
                var path = pathOf.TryGetValue(cid, out var p) ? p : null;
                if (path is not null && claimed.TryGetValue(path, out var winner))
                {
                    covered.Add(cid);
                    if (!mergedEvidence.TryGetValue(winner, out var ev))
                        mergedEvidence[winner] = ev = new();
                    ev.Add($"also flagged by {producer.Id}");
                }
                else
                {
                    if (path is not null) claimed[path] = id;
                    kept.Add(cid);
                }
            }

            if (kept.Count == 0)
            {
                session.DeduplicatedCount += f.CandidateIds.Count;
                // Structured reference to the winning finding — the UI can
                // point at exactly which finding covers this one instead of
                // relying on prose alone.
                var winnerId = f.CandidateIds
                    .Select(cid => pathOf.TryGetValue(cid, out var p) ? p : null)
                    .Where(p => p is not null)
                    .Select(p => claimed.TryGetValue(p!, out var w) ? w : null)
                    .FirstOrDefault(w => w is not null);
                accepted.Add(f with
                {
                    Id = id,
                    Actionable = false,
                    CandidateIds = null,
                    DuplicateOf = winnerId,
                    SkipReason = "covered by an earlier finding — not double-counted",
                    Evidence = f.Evidence.Append("deduplicated against higher-priority producer")
                        .ToArray(),
                });
                continue;
            }
            session.DeduplicatedCount += covered.Count;
            var evidence = f.Evidence;
            if (covered.Count > 0)
                evidence = evidence
                    .Append($"{covered.Count} member(s) covered by earlier findings")
                    .ToArray();
            var bytes = f.CandidateIds.Count == kept.Count
                ? f.EstimatedLogicalBytes
                : RecomputeBytes(f, kept.Count);
            accepted.Add(f with
            {
                Id = id,
                CandidateIds = kept,
                EstimatedLogicalBytes = bytes,
                Evidence = mergedEvidence.TryGetValue(id, out var extra)
                    ? evidence.Concat(extra).ToArray()
                    : evidence,
            });
        }

        // Fold deferred "also flagged by" evidence into already-accepted winners.
        foreach (var (winnerId, extra) in mergedEvidence)
        {
            var i = accepted.FindIndex(x => x.Id == winnerId);
            if (i >= 0)
                accepted[i] = accepted[i] with
                { Evidence = accepted[i].Evidence.Concat(extra.Distinct()).ToArray() };
            else
            {
                // Winner came from an earlier producer — patch it in place.
                lock (session.Sync)
                {
                    var j = session.Findings.FindIndex(x => x.Id == winnerId);
                    if (j >= 0)
                        session.Findings[j] = session.Findings[j] with
                        {
                            Evidence = session.Findings[j].Evidence
                                .Concat(extra.Distinct()).ToArray()
                        };
                }
            }
            mergedEvidence.Remove(winnerId);
        }
        return accepted;
    }

    /// <summary>When dedup removes member candidates, recoverable bytes shrink
    /// proportionally — logical estimate only, never a freed-space promise.</summary>
    private static long RecomputeBytes(SmartFinding f, int keptCount) =>
        f.CandidateIds!.Count == 0 ? 0
        : f.EstimatedLogicalBytes * keptCount / f.CandidateIds.Count;
}
