using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.SmartScan;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.1.1 acceptance hardening: overall scan deadline, cancellation reaching
/// in-flight child sessions, dead-session findings never published,
/// session-state honesty in the DTOs, and structured dedup provenance.
/// </summary>
public class M811Tests : IDisposable
{
    private readonly FixtureDir _fx = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly ITrustedRoots _roots;
    private readonly CleanupRule _rule;

    public M811Tests()
    {
        _roots = new TrustedRoots().Add("t", _fx.Path);
        _rule = new CleanupRule("r1", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 1_000_000, true, false);
        _scans = new ScanSessionStore(new[] { _rule }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
    }

    public void Dispose() { _journal.Dispose(); _fx.Dispose(); _store.Dispose(); }

    private sealed class FakeProducer : ISmartScanProducer
    {
        private readonly string _id;
        private readonly int _priority;
        private readonly Func<SmartScanContext, CancellationToken, ProducerOutcome> _run;
        public string Id => _id;
        public int Priority => _priority;
        public FakeProducer(string id, int priority,
            Func<SmartScanContext, CancellationToken, ProducerOutcome> run)
        { _id = id; _priority = priority; _run = run; }
        public ProducerOutcome Run(SmartScanContext ctx, CancellationToken ct) =>
            _run(ctx, ct);
    }

    private static SmartFinding ActionFinding(string producer, string category,
        string sessionId, params string[] candidateIds) =>
        new("", producer, category, "t", "r", Array.Empty<string>(), "high",
            RiskLevel.Low, 100, "quarantinable", true, null, sessionId,
            candidateIds);

    private static SmartFinding ReviewFinding(string producer, string category) =>
        new("", producer, category, "t", "r", Array.Empty<string>(), "low",
            RiskLevel.Medium, 0, "manualonly", false, "review-only", null, null);

    private static SmartScanSession WaitDone(SmartScanSession s, int ms = 15_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            lock (s.Sync)
                if (s.Status != SmartScanStatus.Running) return s;
            Thread.Sleep(10);
        }
        lock (s.Sync) return s;
    }

    // --------------------- overall deadline (Audit C) ----------------------

    [Fact]
    public void Overall_deadline_stops_run_and_is_distinct_from_per_producer_timeout()
    {
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("slow", 10, (ctx, ct) =>
                {
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                    ct.ThrowIfCancellationRequested();
                    return new ProducerOutcome(Array.Empty<SmartFinding>());
                }),
                new FakeProducer("next", 20, (_, _) => new(
                    new[] { ReviewFinding("next", "x") })),
            },
            perProducerTimeout: TimeSpan.FromSeconds(30),
            overallTimeout: TimeSpan.FromMilliseconds(300));

        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Failed, s.Status);
        Assert.Equal(ProducerStatus.Failed, s.Producers[0].Status);
        Assert.Contains("overall scan deadline", s.Producers[0].Diagnostic);
        Assert.Equal(ProducerStatus.Failed, s.Producers[1].Status);
        Assert.Equal("overall scan deadline — never started",
            s.Producers[1].Diagnostic);
        // The later producer's findings were never published.
        Assert.Empty(s.Findings);
    }

    [Fact]
    public void User_cancel_takes_precedence_over_deadline_diagnostic()
    {
        var release = new ManualResetEventSlim(false);
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("slow", 10, (ctx, ct) =>
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                    ct.ThrowIfCancellationRequested();
                    return new ProducerOutcome(Array.Empty<SmartFinding>());
                }),
                new FakeProducer("next", 20, (_, _) => new(
                    new[] { ReviewFinding("next", "x") })),
            },
            perProducerTimeout: TimeSpan.FromSeconds(30),
            overallTimeout: TimeSpan.FromSeconds(30));

        var s = c.Start(Array.Empty<string>());
        Thread.Sleep(150);
        c.Store.Cancel(s.Id);
        release.Set();
        var done = WaitDone(s);
        Assert.Equal(ProducerStatus.Cancelled, done.Producers[0].Status);
        Assert.Equal("cancelled by user", done.Producers[0].Diagnostic);
        Assert.True(done.UserCancelled);
    }

    // ------------- cancellation reaches child sessions (Audit C) ------------

    [Fact]
    public void Catalog_producer_propagates_cancellation_into_child_session()
    {
        // Pre-cancelled token: the producer's ct→session bridge must fire so
        // the child session's own cancellation source is latched — this is
        // the mechanism that makes the 120s timeout and user cancel reach
        // in-flight filesystem enumeration (it previously did neither).
        var p = new CatalogScanProducer(_scans);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = p.Run(new SmartScanContext(Array.Empty<string>()), cts.Token);
        Assert.NotNull(outcome.Session);
        Assert.True(outcome.Session!.Cancellation.IsCancellationRequested);
        Assert.Equal(SessionStatus.Cancelled, outcome.Session.Status);
    }

    [Fact]
    public void Cancel_mid_run_latches_in_flight_child_session_cancellation()
    {
        // Mirrors the real producers' bridge: a cancelled run must reach the
        // child session WHILE it is scanning, not only after Run returns.
        var scanned = new ManualResetEventSlim(false);
        ScanSession? child = null;
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("p", 10, (ctx, ct) =>
                {
                    child = _scans.CreateExternal();
                    using var reg = ct.Register(
                        () => child.Cancellation.Cancel());
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                    scanned.Set();
                    // Producer honoured cancellation: mark the child like the
                    // real producers do and return it for tracking.
                    child.Status = SessionStatus.Cancelled;
                    return new ProducerOutcome(
                        new[] { ActionFinding("p", "cat", child.Id, "c1") },
                        child, "cancelled");
                }),
            },
            perProducerTimeout: TimeSpan.FromSeconds(30));

        var s = c.Start(Array.Empty<string>());
        while (child is null) Thread.Sleep(10);
        c.Store.Cancel(s.Id);
        Assert.True(scanned.Wait(TimeSpan.FromSeconds(5)),
            "cancellation did not reach the producer's token");
        Assert.True(child!.Cancellation.IsCancellationRequested,
            "child session cancellation was not latched mid-scan");
        var done = WaitDone(s);
        Assert.Equal(ProducerStatus.Cancelled, done.Producers[0].Status);
        // Findings bound to a cancelled session are dropped — they could
        // never execute (every commit item would report cancelled).
        Assert.Empty(done.Findings);
    }

    [Fact]
    public void Failed_child_session_marks_producer_failed_not_completed()
    {
        var sess = _scans.CreateExternal();
        sess.Status = SessionStatus.Failed;
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("p", 10, (_, _) => new(
                    new[] { ActionFinding("p", "cat", sess.Id, "c1") },
                    sess, "scan failed")),
            }, TimeSpan.FromSeconds(30));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(ProducerStatus.Failed, s.Producers[0].Status);
        // Actionable bindings to a failed session are never published.
        Assert.Empty(s.Findings);
        Assert.Equal(SmartScanStatus.Failed, s.Status);
    }

    // ------------- session-state honesty in DTOs (Audit B) -----------------

    [Fact]
    public async Task Results_report_expired_missing_and_live_session_states()
    {
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMinutes(30)),
            _roots);

        // Expired: completed but older than the executor SessionTtl (and
        // younger than store retention so it is not evicted).
        var aged = new ScanSession
        {
            Id = "aged-session",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-45),
        };
        aged.Status = SessionStatus.Completed;
        _scans.Register(aged);

        var live = _scans.CreateExternal();
        live.Status = SessionStatus.Completed;

        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("p", 10, (_, _) => new(new[]
                {
                    ActionFinding("p", "cat", aged.Id, "c1"),
                    ActionFinding("p", "cat", "ghost-session", "c2"),
                    ActionFinding("p", "cat", live.Id, "c3"),
                }, live)),
            }, TimeSpan.FromSeconds(30));
        var s = WaitDone(c.Start(Array.Empty<string>()));

        var d = new RequestDispatcher(_scans, executor, _journal, smartScan: c);
        var res = await d.Dispatch("smartscan.results",
            JsonDocument.Parse($"{{\"smartScanId\":\"{s.Id}\"}}").RootElement);
        var json = JsonSerializer.Serialize(res.Result,
            new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var states = doc.RootElement.GetProperty("findings").EnumerateArray()
            .ToDictionary(f => f.GetProperty("sessionId").GetString()!,
                f => f.GetProperty("sessionState").GetString());
        Assert.Equal("expired", states[aged.Id]);
        Assert.Equal("missing", states["ghost-session"]);
        Assert.Equal("live", states[live.Id]);
        Assert.False(doc.RootElement.GetProperty("userCancelled").GetBoolean());
    }

    [Fact]
    public async Task Expired_child_session_fails_closed_at_preview_and_commit()
    {
        _fx.Write("stale.tmp", "data");
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMinutes(30)),
            _roots);
        var aged = _scans.Register(new ScanSession
        {
            Id = "aged",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-45),
        });
        _scans.Populate(aged);
        Assert.Equal(SessionStatus.Completed, aged.Status);
        Assert.NotEmpty(aged.Items);
        var ids = aged.Items.Select(i => i.Public.Id).ToArray();

        var d = new RequestDispatcher(_scans, executor, _journal);
        var prev = await d.Dispatch("cleanup.preview",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = aged.Id, candidateIds = ids })).RootElement);
        Assert.Equal(Protocol.Errors.SessionExpired, prev.Error);
        var commit = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = aged.Id, candidateIds = ids })).RootElement);
        Assert.Equal(Protocol.Errors.SessionExpired, commit.Error);
        // Nothing moved.
        Assert.True(File.Exists(Path.Combine(_fx.Path, "stale.tmp")));
    }

    [Fact]
    public async Task User_cancelled_flag_is_visible_over_ipc()
    {
        var release = new ManualResetEventSlim(false);
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("slow", 10, (ctx, ct) =>
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                    ct.ThrowIfCancellationRequested();
                    return new ProducerOutcome(Array.Empty<SmartFinding>());
                }),
            }, TimeSpan.FromSeconds(30));
        var s = c.Start(Array.Empty<string>());
        var d = new RequestDispatcher(_scans, null, _journal, smartScan: c);
        await d.Dispatch("smartscan.cancel",
            JsonDocument.Parse($"{{\"smartScanId\":\"{s.Id}\"}}").RootElement);
        release.Set();
        WaitDone(s);
        var res = await d.Dispatch("smartscan.results",
            JsonDocument.Parse($"{{\"smartScanId\":\"{s.Id}\"}}").RootElement);
        var json = JsonSerializer.Serialize(res.Result,
            new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("userCancelled").GetBoolean());
    }

    [Fact]
    public void Cancel_on_terminal_run_does_not_latch_child_sessions()
    {
        // A finished run's bindings must stay executable — cancelling an
        // already-terminal run is an idempotent ack, not a state change.
        var child = _scans.CreateExternal();
        child.Status = SessionStatus.Completed;
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("p", 10, (_, _) => new(
                    new[] { ActionFinding("p", "cat", child.Id, "c1") }, child)),
            }, TimeSpan.FromSeconds(30));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Completed, s.Status);
        Assert.True(c.Store.Cancel(s.Id));
        Assert.False(child.Cancellation.IsCancellationRequested);
        Assert.False(s.UserCancelled);
    }

    [Fact]
    public async Task Session_expiring_between_preview_and_commit_fails_closed()
    {
        _fx.Write("race.tmp", "data");
        // Tiny executor TTL: preview inside the window, commit outside it.
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMilliseconds(200),
                ApprovalTtl: TimeSpan.FromMinutes(10)),
            _roots);
        var s = _scans.CreateExternal();
        _scans.Populate(s);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();

        var d = new RequestDispatcher(_scans, executor, _journal);
        var prev = await d.Dispatch("cleanup.preview",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = s.Id, candidateIds = ids })).RootElement);
        Assert.Null(prev.Error); // preview granted while session was live

        await Task.Delay(400); // cross the session TTL
        var commit = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = s.Id, candidateIds = ids })).RootElement);
        Assert.Equal(Protocol.Errors.SessionExpired, commit.Error);
        Assert.True(File.Exists(Path.Combine(_fx.Path, "race.tmp")));
    }

    [Fact]
    public void Expired_session_rejects_every_commit_item_at_executor_level()
    {
        _fx.Write("e1.tmp", "a");
        _fx.Write("e2.tmp", "b");
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMinutes(30)),
            _roots);
        var s = _scans.Register(new ScanSession
        {
            Id = "old", CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-45),
        });
        _scans.Populate(s);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        var items = executor.Commit(s.Id, ids, default);
        Assert.All(items,
            i => Assert.Equal(Protocol.Errors.SessionExpired, i.ErrorCode));
        Assert.True(File.Exists(Path.Combine(_fx.Path, "e1.tmp")));
        Assert.True(File.Exists(Path.Combine(_fx.Path, "e2.tmp")));
    }

    [Fact]
    public async Task Commit_outside_the_previewed_approval_set_is_refused()
    {
        _fx.Write("a.tmp", "a");
        _fx.Write("b.tmp", "b");
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMinutes(30)),
            _roots);
        var s = _scans.CreateExternal();
        _scans.Populate(s);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        var d = new RequestDispatcher(_scans, executor, _journal);

        // Grouped findings preview per session; the grant covers exactly
        // the ids previewed — committing a superset must fail.
        var prev = await d.Dispatch("cleanup.preview",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = s.Id, candidateIds = new[] { ids[0] } })).RootElement);
        Assert.Null(prev.Error);
        var widened = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = s.Id, candidateIds = ids })).RootElement);
        Assert.Equal(Protocol.Errors.ApprovalMismatch, widened.Error);
        Assert.True(File.Exists(Path.Combine(_fx.Path, "a.tmp")));
        Assert.True(File.Exists(Path.Combine(_fx.Path, "b.tmp")));
    }

    [Fact]
    public void Smart_scan_store_evicts_terminal_runs_past_retention()
    {
        var store = new SmartScanStore(maxSessions: 8,
            retention: TimeSpan.FromMilliseconds(50));
        var first = store.Create(new[] { "p" });
        first.Status = SmartScanStatus.Completed;
        Thread.Sleep(100);
        var second = store.Create(new[] { "p" });
        Assert.False(store.TryGet(first.Id, out _));
        Assert.True(store.TryGet(second.Id, out _));
        // A still-running run is never evicted by time alone.
        second.Status = SmartScanStatus.Running;
        Thread.Sleep(100);
        var third = store.Create(new[] { "p" });
        Assert.True(store.TryGet(second.Id, out _));
        Assert.True(store.TryGet(third.Id, out _));
    }

    // ------------------------- dedup provenance (Audit D) -------------------

    [Fact]
    public void Fully_covered_finding_records_winner_in_duplicate_of()
    {
        _fx.Write("shared.tmp", "dup-content");
        var s1 = _scans.CreateExternal(); _scans.Populate(s1);
        var s2 = _scans.CreateExternal(); _scans.Populate(s2);
        var cid1 = s1.Items.Single().Public.Id;
        var cid2 = s2.Items.Single().Public.Id;

        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("first", 10, (_, _) => new(
                    new[] { ActionFinding("first", "temp", s1.Id, cid1) }, s1)),
                new FakeProducer("second", 20, (_, _) => new(
                    new[] { ActionFinding("second", "temp", s2.Id, cid2) }, s2)),
            }, TimeSpan.FromSeconds(30));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        var loser = s.Findings.Single(f => !f.Actionable);
        var winner = s.Findings.Single(f => f.Actionable);
        Assert.Equal(winner.Id, loser.DuplicateOf);
    }

    // ---------------------------- bounded stores ---------------------------

    [Fact]
    public void Smart_scan_store_refuses_beyond_capacity()
    {
        var store = new SmartScanStore(maxSessions: 1);
        store.Create(new[] { "p" });
        Assert.Throws<SessionCapacityException>(() => store.Create(new[] { "p" }));
    }
}
