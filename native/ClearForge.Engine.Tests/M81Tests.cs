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
/// M8.1 unified Smart Scan: coordinator orchestration, conservative path-level
/// dedup, failure/cancellation semantics, and the rule that only actionable
/// findings carry executable bindings through the existing approval pipeline.
/// </summary>
public class M81Tests : IDisposable
{
    private readonly FixtureDir _fx = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ITrustedRoots _roots;
    private readonly CleanupRule _rule;

    public M81Tests()
    {
        _roots = new TrustedRoots().Add("t", _fx.Path);
        _rule = new CleanupRule("r1", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 1_000_000, true, false);
        _scans = new ScanSessionStore(new[] { _rule }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        _executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
    }

    public void Dispose() { _journal.Dispose(); _fx.Dispose(); _store.Dispose(); }

    // --- fakes ---------------------------------------------------------------

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
            Thread.Sleep(25);
        }
        lock (s.Sync) return s;
    }

    private static SmartScanCoordinator Coordinator(
        params ISmartScanProducer[] producers) =>
        new(new SmartScanStore(), producers, TimeSpan.FromSeconds(30));

    // --------------------------- coordinator --------------------------------

    [Fact]
    public void All_producers_complete_yields_completed_status()
    {
        var c = Coordinator(
            new FakeProducer("a", 10, (_, _) => new(
                new[] { ReviewFinding("a", "x") })),
            new FakeProducer("b", 20, (_, _) => new(
                new[] { ReviewFinding("b", "y") })));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Completed, s.Status);
        Assert.Equal(2, s.Findings.Count);
        Assert.All(s.Producers, p => Assert.Equal(ProducerStatus.Completed, p.Status));
    }

    [Fact]
    public void Failing_producer_yields_partial_run_with_valid_results()
    {
        var c = Coordinator(
            new FakeProducer("good", 10, (_, _) => new(
                new[] { ReviewFinding("good", "x") })),
            new FakeProducer("bad", 20, (_, _) =>
                throw new InvalidOperationException("producer exploded")));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Partial, s.Status);
        Assert.Single(s.Findings);
        Assert.Equal(ProducerStatus.Completed, s.Producers[0].Status);
        Assert.Equal(ProducerStatus.Failed, s.Producers[1].Status);
        Assert.Equal("InvalidOperationException", s.Producers[1].Diagnostic);
    }

    [Fact]
    public void Cancellation_stops_future_producers_and_reports_honestly()
    {
        var release = new ManualResetEventSlim(false);
        var c = Coordinator(
            new FakeProducer("first", 10, (ctx, ct) =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                ct.ThrowIfCancellationRequested();
                return new ProducerOutcome(Array.Empty<SmartFinding>());
            }),
            new FakeProducer("second", 20, (_, _) => new(
                new[] { ReviewFinding("second", "x") })));
        var s = c.Start(Array.Empty<string>());
        Thread.Sleep(200); // first producer is running now
        c.Store.Cancel(s.Id);
        release.Set();
        var done = WaitDone(s);
        // In-flight producer honoured cancellation; pending producer never ran.
        Assert.Equal(ProducerStatus.Cancelled, done.Producers[0].Status);
        Assert.Equal(ProducerStatus.Cancelled, done.Producers[1].Status);
        Assert.Equal(SmartScanStatus.Cancelled, done.Status);
        Assert.Empty(done.Findings);
    }

    [Fact]
    public void Cancel_after_partial_progress_reports_partial_not_cancelled()
    {
        var release = new ManualResetEventSlim(false);
        var c = Coordinator(
            new FakeProducer("first", 10, (_, _) => new(
                new[] { ReviewFinding("first", "x") })),
            new FakeProducer("second", 20, (ctx, ct) =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                ct.ThrowIfCancellationRequested();
                return new ProducerOutcome(Array.Empty<SmartFinding>());
            }));
        var s = c.Start(Array.Empty<string>());
        while (s.Producers[0].Status != ProducerStatus.Completed) Thread.Sleep(10);
        c.Store.Cancel(s.Id);
        release.Set();
        var done = WaitDone(s);
        Assert.Equal(ProducerStatus.Completed, done.Producers[0].Status);
        Assert.Equal(ProducerStatus.Cancelled, done.Producers[1].Status);
        // Completed work is honestly reported — never claimed as cancelled.
        Assert.Equal(SmartScanStatus.Partial, done.Status);
        Assert.Single(done.Findings);
    }

    [Fact]
    public void Producer_timeout_marks_it_failed_not_fatal()
    {
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[]
            {
                new FakeProducer("slow", 10, (ctx, ct) =>
                {
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(60));
                    ct.ThrowIfCancellationRequested();
                    return new ProducerOutcome(Array.Empty<SmartFinding>());
                }),
                new FakeProducer("fast", 20, (_, _) => new(
                    new[] { ReviewFinding("fast", "x") })),
            }, TimeSpan.FromMilliseconds(300));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Partial, s.Status);
        Assert.Equal(ProducerStatus.Failed, s.Producers[0].Status);
        Assert.Contains("timeout", s.Producers[0].Diagnostic);
        Assert.Single(s.Findings);
    }

    [Fact]
    public void Empty_producer_results_are_valid_not_failed()
    {
        var c = Coordinator(
            new FakeProducer("empty", 10, (_, _) =>
                new ProducerOutcome(Array.Empty<SmartFinding>(),
                    Diagnostic: "nothing found")));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Completed, s.Status);
        Assert.Empty(s.Findings);
        Assert.Equal("nothing found", s.Producers[0].Diagnostic);
    }

    [Fact]
    public void Aggregation_ordering_is_deterministic()
    {
        var a = WaitDone(Coordinator(
            new FakeProducer("p", 10, (_, _) => new(new[]
            {
                ReviewFinding("p", "b-cat"), ReviewFinding("p", "a-cat"),
            }))).Start(Array.Empty<string>()));
        var b = WaitDone(Coordinator(
            new FakeProducer("p", 10, (_, _) => new(new[]
            {
                ReviewFinding("p", "b-cat"), ReviewFinding("p", "a-cat"),
            }))).Start(Array.Empty<string>()));
        Assert.Equal(a.Findings.Select(f => f.Id),
            b.Findings.Select(f => f.Id));
    }

    // ------------------------------ dedup ------------------------------------

    [Fact]
    public void Same_canonical_path_dedupes_without_double_counting()
    {
        // Two producers both flag the same file — real sessions so the
        // coordinator can resolve canonical paths for each candidate.
        var p1 = _fx.Write("shared.tmp", "dup-content");
        var s1 = _scans.CreateExternal(); _scans.Populate(s1);
        var s2 = _scans.CreateExternal(); _scans.Populate(s2);
        var cid1 = s1.Items.Single().Public.Id;
        var cid2 = s2.Items.Single().Public.Id;

        var c = Coordinator(
            new FakeProducer("first", 10, (_, _) => new(
                new[] { ActionFinding("first", "temp", s1.Id, cid1) }, s1)),
            new FakeProducer("second", 20, (_, _) => new(
                new[] { ActionFinding("second", "temp", s2.Id, cid2) }, s2)));
        var s = WaitDone(c.Start(Array.Empty<string>()));

        var actionable = s.Findings.Where(f => f.Actionable).ToArray();
        var reviewOnly = s.Findings.Where(f => !f.Actionable).ToArray();
        Assert.Single(actionable);           // only one binding survives
        Assert.Single(reviewOnly);           // loser kept as evidence, not hidden
        Assert.Equal("covered by an earlier finding — not double-counted",
            reviewOnly[0].SkipReason);
        Assert.Equal(1, s.DeduplicatedCount);
        // Estimated bytes counted once.
        Assert.Equal(actionable[0].EstimatedLogicalBytes,
            s.Findings.Where(f => f.Actionable).Sum(f => f.EstimatedLogicalBytes));
        // Winner's evidence records the merge.
        Assert.Contains(actionable[0].Evidence, e => e.Contains("second"));
    }

    [Fact]
    public void Distinct_files_are_never_merged()
    {
        _fx.Write("one.tmp", "a");
        _fx.Write("two.tmp", "b");
        var s1 = _scans.CreateExternal(); _scans.Populate(s1);
        var ids = s1.Items.Select(i => i.Public.Id).ToArray();
        Assert.Equal(2, ids.Length);

        var c = Coordinator(new FakeProducer("p", 10, (_, _) => new(
            new[] { ActionFinding("p", "temp", s1.Id, ids) }, s1)));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Single(s.Findings);
        Assert.Equal(2, s.Findings[0].CandidateIds!.Count); // both kept
        Assert.Equal(0, s.DeduplicatedCount);
    }

    // ------------------------------ safety -----------------------------------

    [Fact]
    public void Scan_is_read_only()
    {
        var p = _fx.Write("scanme.tmp", "payload");
        var before = File.ReadAllBytes(p);
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new CatalogScanProducer(_scans) });
        var s = WaitDone(c.Start(Array.Empty<string>()));
        Assert.Equal(SmartScanStatus.Completed, s.Status);
        Assert.True(File.Exists(p));
        Assert.Equal(before, File.ReadAllBytes(p));
    }

    [Fact]
    public void Review_only_findings_carry_no_executable_binding()
    {
        var c = Coordinator(new FakeProducer("p", 10, (_, _) => new(
            new[] { ReviewFinding("p", "startup-items") })));
        var s = WaitDone(c.Start(Array.Empty<string>()));
        var f = s.Findings.Single();
        Assert.False(f.Actionable);
        Assert.Null(f.SessionId);
        Assert.Null(f.CandidateIds);
        Assert.NotNull(f.SkipReason);
    }

    [Fact]
    public void Real_catalog_producer_binds_candidates_to_real_session()
    {
        _fx.Write("real.tmp", "data");
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new CatalogScanProducer(_scans) });
        var s = WaitDone(c.Start(Array.Empty<string>()));
        var f = s.Findings.Single();
        Assert.True(f.Actionable);
        Assert.NotNull(f.SessionId);
        Assert.NotEmpty(f.CandidateIds!);

        // The binding executes only through the existing preview→commit path.
        var res = _executor.Commit(f.SessionId!, f.CandidateIds!, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, res.Status);
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();
        Assert.NotNull(e.ContentSha256); // M8.0.3 integrity intact
    }

    [Fact]
    public async Task Commit_still_requires_preview_at_the_dispatch_boundary()
    {
        _fx.Write("a.tmp", "x");
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new CatalogScanProducer(_scans) });
        var s = WaitDone(c.Start(Array.Empty<string>()));
        var f = s.Findings.Single();
        var d = new RequestDispatcher(_scans, _executor, _journal, smartScan: c);

        var commit = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(new
            { sessionId = f.SessionId, candidateIds = f.CandidateIds })).RootElement);
        Assert.Equal(Protocol.Errors.ApprovalRequired, commit.Error);
    }

    // ------------------------------- IPC --------------------------------------

    [Fact]
    public async Task Smartscan_methods_flow_through_dispatch()
    {
        _fx.Write("ipc.tmp", "data");
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new CatalogScanProducer(_scans) });
        var d = new RequestDispatcher(_scans, _executor, _journal, smartScan: c);

        var start = await d.Dispatch("smartscan.start", null);
        var sid = start.Result!.GetType().GetProperty("smartScanId")!
            .GetValue(start.Result) as string;
        Assert.NotNull(sid);

        SmartScanSession? done = null;
        for (var i = 0; i < 400 && done is null; i++)
        {
            if (c.Store.TryGet(sid!, out var live) && live.Status != SmartScanStatus.Running)
                done = live;
            else Thread.Sleep(25);
        }
        Assert.NotNull(done);

        var results = await d.Dispatch("smartscan.results",
            JsonDocument.Parse($"{{\"smartScanId\":\"{sid}\"}}").RootElement);
        var json = JsonSerializer.Serialize(results.Result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var findings = doc.RootElement.GetProperty("findings");
        Assert.True(findings.GetArrayLength() >= 1);
        Assert.Equal("catalog",
            findings[0].GetProperty("producer").GetString());
        Assert.True(findings[0].GetProperty("actionable").GetBoolean());

        var cancel = await d.Dispatch("smartscan.cancel",
            JsonDocument.Parse($"{{\"smartScanId\":\"{sid}\"}}").RootElement);
        Assert.Null(cancel.Error);

        var missing = await d.Dispatch("smartscan.status",
            JsonDocument.Parse("{\"smartScanId\":\"nope\"}").RootElement);
        Assert.Equal(Protocol.Errors.SessionNotFound, missing.Error);
    }
}
