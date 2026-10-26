using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Storage;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M4.2 recommendation→action bridge: executable recommendations bind to real
/// session-bound candidates that flow through the existing journaled
/// quarantine pipeline. Review-only items can never carry an action.
/// </summary>
public class M42Tests : IDisposable
{
    // Mirrors PipeServerHost wire serialization (camelCase).
    private static readonly JsonSerializerOptions Camel = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FixtureDir _temp = new();   // cleanup root (rule-backed)
    private readonly FixtureDir _scope = new();  // inventory scope (separate tree)
    private readonly FixtureDir _store = new();  // journal + quarantine
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ITrustedRoots _roots;
    private readonly RecommendationActionBridge _bridge;

    private static CleanupRule TempRule(string rootId, long minAgeHours = 0, string category = "temporary-files") =>
        new($"rule-{category}", 1, category, "current-user", rootId, minAgeHours,
            "low", true, "quarantine", 100, true, false);

    public M42Tests()
    {
        _roots = new TrustedRoots().Add("t", _temp.Path);
        _scans = new ScanSessionStore(new[] { TempRule("t") }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        _executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
        _bridge = new RecommendationActionBridge(_scans);
    }

    public void Dispose() { _journal.Dispose(); _temp.Dispose(); _scope.Dispose(); _store.Dispose(); }

    private ScanSession SessionFor(Recommendation rec)
    {
        Assert.NotNull(rec.Action);
        Assert.True(_scans.TryGet(rec.Action!.SessionId, out var s));
        return s;
    }

    private RequestDispatcher Dispatcher()
    {
        var scopes = new ScopeRegistry().Add("s", _scope.Path);
        return new RequestDispatcher(_scans, _executor, _journal,
            scopes: scopes, inventory: new StorageInventory(scopes), actionBridge: _bridge);
    }

    private static async Task<JsonElement> Call(RequestDispatcher d, string method, string? paramsJson = null)
    {
        var res = await d.Dispatch(method,
            paramsJson is null ? null : JsonDocument.Parse(paramsJson).RootElement);
        Assert.Null(res.Error);
        return JsonSerializer.SerializeToElement(res.Result, Camel);
    }

    [Fact]
    public void Executable_recommendation_binds_real_session_bound_candidates()
    {
        _temp.Write("old.tmp", "payload");
        var rec = Assert.Single(_bridge.Build());
        Assert.Equal("temporary-files", rec.Category);
        Assert.NotNull(rec.Action);
        Assert.Equal("quarantine", rec.Action!.Kind);
        Assert.Equal($"session:{rec.Action.SessionId}", rec.ActionRef);

        var s = SessionFor(rec);
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal("completed", rec.Action.Status);
        Assert.Equal(1, rec.Action.CandidateCount);
        Assert.Equal(rec.Action.EligibleBytes, rec.EstimatedLogicalBytes);

        // Candidates resolve only inside the owning session.
        var resolved = _scans.ResolveCandidates(s, new[] { s.Items.Single().Public.Id });
        Assert.Single(resolved);
        Assert.Empty(_scans.ResolveCandidates(s, new[] { "forged:0" }));
    }

    [Fact]
    public void No_eligible_files_yields_no_recommendation()
    {
        Assert.Empty(_bridge.Build());
    }

    [Fact]
    public void Files_younger_than_threshold_are_not_actionable()
    {
        var scans = new ScanSessionStore(new[] { TempRule("t", minAgeHours: 24) },
            new FileScanner(_roots));
        var bridge = new RecommendationActionBridge(scans);
        var p = _temp.Write("fresh.tmp", "new");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
        Assert.Empty(bridge.Build());
    }

    [Fact]
    public async Task Review_only_recommendation_has_no_action_surface()
    {
        _scope.Write("mystery/big.bin", new string('z', 2048)); // unclassified dir
        _temp.Write("old.tmp", "payload");
        var d = Dispatcher();
        var json = await Call(d, "storage.recommend");
        var recs = json.GetProperty("recommendations").EnumerateArray().ToList();
        var review = recs.Single(r => r.GetProperty("category").GetString() == "unclassified");
        Assert.Equal(JsonValueKind.Null, review.GetProperty("actionRef").ValueKind);
        Assert.Equal(JsonValueKind.Null, review.GetProperty("action").ValueKind);
        var exec = recs.Single(r => r.GetProperty("category").GetString() == "temporary-files");
        Assert.Equal("quarantine", exec.GetProperty("action").GetProperty("kind").GetString());
    }

    [Fact]
    public void Committed_action_quarantines_and_restores_through_existing_pipeline()
    {
        var p = _temp.Write("victim.tmp", "payload-42");
        var rec = _bridge.Build().Single();
        var s = SessionFor(rec);
        var cid = s.Items.Single().Public.Id;

        var res = _executor.Commit(rec.Action!.SessionId, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, res.Status);
        Assert.False(File.Exists(p));

        var entry = _journal.Query(JournalState.Quarantined.ToString()).Single();
        var restored = _executor.Restore(entry.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, restored.Status);
        Assert.Equal("payload-42", File.ReadAllText(p));
    }

    [Fact]
    public void Forged_session_and_candidate_references_are_rejected()
    {
        _temp.Write("a.tmp");
        var rec = _bridge.Build().Single();
        Assert.Equal(Protocol.Errors.SessionNotFound,
            _executor.Commit("ghost", new[] { "ghost:0" }, default).Single().ErrorCode);
        Assert.Equal(Protocol.Errors.CandidateNotFound,
            _executor.Commit(rec.Action!.SessionId, new[] { "../../etc/passwd" }, default)
                .Single().ErrorCode);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    [Fact]
    public void Expired_action_session_is_rejected_at_commit()
    {
        _temp.Write("stale.tmp");
        var scans = new ScanSessionStore(new[] { TempRule("t") }, new FileScanner(_roots));
        var rec = new RecommendationActionBridge(scans).Build().Single();
        var exec = new QuarantineExecutor(scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMilliseconds(1)), _roots);
        Assert.True(scans.TryGet(rec.Action!.SessionId, out var s));
        var cid = s.Items.Single().Public.Id;
        Thread.Sleep(50);
        Assert.Equal(Protocol.Errors.SessionExpired,
            exec.Commit(rec.Action.SessionId, new[] { cid }, default).Single().ErrorCode);
    }

    [Fact]
    public async Task Expired_action_session_reports_expired_status()
    {
        _temp.Write("stale.tmp");
        var scans = new ScanSessionStore(new[] { TempRule("t") }, new FileScanner(_roots));
        var rec = new RecommendationActionBridge(scans).Build().Single();
        var exec = new QuarantineExecutor(scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMilliseconds(1)), _roots);
        var d = new RequestDispatcher(scans, exec, _journal,
            scopes: new ScopeRegistry().Add("s", _scope.Path),
            inventory: new StorageInventory(new ScopeRegistry().Add("s", _scope.Path)));
        Thread.Sleep(50);
        var st = await Call(d, "operation.getStatus",
            $"{{\"sessionId\":\"{rec.Action!.SessionId}\"}}");
        Assert.Equal("expired", st.GetProperty("status").GetString());
    }

    [Fact]
    public void Candidate_changed_since_binding_is_skipped_not_moved()
    {
        var p = _temp.Write("mutable.tmp", "original");
        var rec = _bridge.Build().Single();
        var s = SessionFor(rec);
        var cid = s.Items.Single().Public.Id;
        File.WriteAllText(p, "replaced-with-longer-content");
        var res = _executor.Commit(rec.Action!.SessionId, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.SkippedChanged, res.ErrorCode);
        Assert.True(File.Exists(p));
    }

    [Fact]
    public async Task Repeated_and_concurrent_commits_move_once()
    {
        _temp.Write("race.tmp", "data");
        var rec = _bridge.Build().Single();
        var s = SessionFor(rec);
        var cid = s.Items.Single().Public.Id;
        var results = await Task.WhenAll(
            Task.Run(() => _executor.Commit(rec.Action!.SessionId, new[] { cid }, default).Single()),
            Task.Run(() => _executor.Commit(rec.Action!.SessionId, new[] { cid }, default).Single()));
        Assert.Contains(results, r => r.Status == Protocol.Errors.ItemCleaned);
        Assert.Contains(results, r => r.Status == Protocol.Errors.ItemAlreadyQuarantined);
        var again = _executor.Commit(rec.Action!.SessionId, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemAlreadyQuarantined, again.Status);
        Assert.Single(Directory.GetFiles(Path.Combine(_store.Path, "q"), "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Partial_failure_reports_accurate_per_item_outcomes()
    {
        var locked = _temp.Write("locked.tmp", "a");
        _temp.Write("free.tmp", "b");
        var rec = _bridge.Build().Single();
        var s = SessionFor(rec);
        Assert.Equal(2, rec.Action!.CandidateCount);

        var lockedId = s.Items.Single(i => i.File.CanonicalPath == locked).Public.Id;
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var results = _executor.Commit(rec.Action.SessionId, ids, default);
        Assert.Equal(2, results.Count);
        Assert.Equal(Protocol.Errors.SkippedLocked,
            results.Single(r => r.CandidateId == lockedId).ErrorCode);
        Assert.Single(results, r => r.Status == Protocol.Errors.ItemCleaned);
    }

    [Fact]
    public async Task Cancelled_action_session_cannot_execute()
    {
        _temp.Write("gone.tmp");
        var d = Dispatcher();
        var rec = (await Call(d, "storage.recommend")).GetProperty("recommendations")[0];
        var sid = rec.GetProperty("action").GetProperty("sessionId").GetString()!;
        var results = await Call(d, "scan.getResults", $"{{\"sessionId\":\"{sid}\"}}");
        var cid = results.GetProperty("candidates")[0].GetProperty("id").GetString()!;

        await Call(d, "cleanup.preview",
            $"{{\"sessionId\":\"{sid}\",\"candidateIds\":[\"{cid}\"]}}");
        await d.Dispatch("cleanup.cancel",
            JsonDocument.Parse($"{{\"sessionId\":\"{sid}\"}}").RootElement);
        var commit = await Call(d, "cleanup.commit",
            $"{{\"sessionId\":\"{sid}\",\"candidateIds\":[\"{cid}\"]}}");
        Assert.Equal(Protocol.Errors.Cancelled,
            commit.GetProperty("items")[0].GetProperty("errorCode").GetString());
        Assert.True(File.Exists(Path.Combine(_temp.Path, "gone.tmp")));
    }

    [Fact]
    public void Overlapping_category_roots_do_not_double_count()
    {
        // Two rules, same root, different categories — pathological config.
        var rules = new[]
        {
            TempRule("t", category: "temporary-files"),
            TempRule("t", category: "crash-dumps"),
        };
        var scans = new ScanSessionStore(rules, new FileScanner(_roots));
        var bridge = new RecommendationActionBridge(scans);
        _temp.Write("shared.tmp", "only-once");

        var recs = bridge.Build();
        var rec = Assert.Single(recs); // second category's session deduped to empty
        Assert.Equal(new FileInfo(Path.Combine(_temp.Path, "shared.tmp")).Length,
            rec.Action!.EligibleBytes);
    }

    [Fact]
    public async Task Recommendations_are_stable_within_an_inventory_generation()
    {
        _temp.Write("a.tmp");
        var d = Dispatcher();
        var first = (await Call(d, "storage.recommend")).GetProperty("recommendations").GetRawText();
        var second = (await Call(d, "storage.recommend")).GetProperty("recommendations").GetRawText();
        Assert.Equal(first, second); // cached: same rec ids, same session ids
    }

    [Fact]
    public async Task Fresh_inventory_rebinds_to_fresh_sessions()
    {
        _temp.Write("a.tmp");
        var d = Dispatcher();
        var r1 = (await Call(d, "storage.recommend")).GetProperty("recommendations")[0];
        var sid1 = r1.GetProperty("action").GetProperty("sessionId").GetString();

        await Call(d, "storage.inventory"); // rescan → new generation
        var r2 = (await Call(d, "storage.recommend")).GetProperty("recommendations")[0];
        var sid2 = r2.GetProperty("action").GetProperty("sessionId").GetString();
        Assert.NotEqual(sid1, sid2);
    }

    [Fact]
    public async Task Committed_files_do_not_reappear_as_actionable()
    {
        _temp.Write("once.tmp");
        var d = Dispatcher();
        var rec = (await Call(d, "storage.recommend")).GetProperty("recommendations")[0];
        var sid = rec.GetProperty("action").GetProperty("sessionId").GetString()!;
        var results = await Call(d, "scan.getResults", $"{{\"sessionId\":\"{sid}\"}}");
        var cid = results.GetProperty("candidates")[0].GetProperty("id").GetString()!;
        await Call(d, "cleanup.preview",
            $"{{\"sessionId\":\"{sid}\",\"candidateIds\":[\"{cid}\"]}}");
        var commit = await Call(d, "cleanup.commit",
            $"{{\"sessionId\":\"{sid}\",\"candidateIds\":[\"{cid}\"]}}");
        Assert.Equal("cleaned",
            commit.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Number,
            commit.GetProperty("freeSpaceDeltaBytes").ValueKind); // measured, ~0 expected

        await Call(d, "storage.inventory"); // rescan → new generation
        var recs = (await Call(d, "storage.recommend")).GetProperty("recommendations");
        Assert.DoesNotContain(recs.EnumerateArray(),
            r => r.GetProperty("category").GetString() == "temporary-files");
    }

    [Fact]
    public async Task Concurrent_recommend_calls_share_one_action_session()
    {
        _temp.Write("a.tmp");
        var d = Dispatcher();
        var results = await Task.WhenAll(
            Task.Run(() => Call(d, "storage.recommend")),
            Task.Run(() => Call(d, "storage.recommend")));
        var sids = results
            .Select(r => r.GetProperty("recommendations")[0]
                .GetProperty("action").GetProperty("sessionId").GetString())
            .ToArray();
        Assert.Equal(sids[0], sids[1]); // one session, no duplicate registration
    }

    [Fact]
    public async Task Plan_includes_executable_recommendations()
    {
        _temp.Write("planned.tmp", new string('x', 512));
        var d = Dispatcher();
        var plan = await Call(d, "storage.plan", "{\"goalBytes\":128}");
        var selected = plan.GetProperty("selected").EnumerateArray().ToList();
        Assert.Contains(selected, s => s.GetProperty("id").GetString() == "act-temporary-files");
    }

    [Fact]
    public void Inventory_recommendation_ids_are_process_stable()
    {
        _scope.Write("mystery/big.bin", "data");
        var inv = new StorageInventory(new ScopeRegistry().Add("s", _scope.Path));
        var r1 = RecommendationEngine.FromInventory(inv.Scan(new[] { "s" }, default)).Single().Id;
        var r2 = RecommendationEngine.FromInventory(inv.Scan(new[] { "s" }, default)).Single().Id;
        Assert.Equal(r1, r2); // deterministic, not GetHashCode-randomized
    }
}

/// <summary>Browser-cache category bridging via the existing cache scanner.</summary>
public class M42BrowserCacheBridgeTests : IDisposable
{
    private readonly FixtureDir _profileRoot = new();
    private readonly FixtureDir _store = new();
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly TrustedRoots _roots = new();
    private bool _browserRunning;

    public M42BrowserCacheBridgeTests()
    {
        _scans = new ScanSessionStore(Array.Empty<CleanupRule>(), new FileScanner(_roots));
        _executor = new QuarantineExecutor(_scans,
            new CleanupJournal(Path.Combine(_store.Path, "j.db")),
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
    }

    public void Dispose() { _profileRoot.Dispose(); _store.Dispose(); }

    private RecommendationActionBridge Bridge()
    {
        var profiles = new BrowserPrivacyScanner(
            new[] { new BrowserPrivacyScanner.BrowserSpec("edge", _profileRoot.Path,
                new[] { "fixture-proc" }, "*", new[] { "Cache" }) },
            _ => _browserRunning);
        return new RecommendationActionBridge(_scans, new BrowserCacheScanner(profiles));
    }

    private string MakeProfile()
    {
        var cache = Path.Combine(_profileRoot.Path, "Default", "Cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "c1.dat"), new string('x', 64));
        _roots.Add("browser-profile", Path.Combine(_profileRoot.Path, "Default"));
        return cache;
    }

    [Fact]
    public void Browser_cache_recommendation_executes_via_existing_pipeline()
    {
        var cache = MakeProfile();
        var rec = Bridge().Build().Single(r => r.Category == "browser-cache");
        Assert.NotNull(rec.Action);

        Assert.True(_scans.TryGet(rec.Action!.SessionId, out var s));
        var cid = s.Items.Single().Public.Id;
        var res = _executor.Commit(rec.Action.SessionId, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, res.Status);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void Running_browser_produces_no_executable_recommendation()
    {
        _browserRunning = true;
        MakeProfile();
        Assert.DoesNotContain(Bridge().Build(), r => r.Category == "browser-cache");
    }

    [Fact]
    public void Browser_started_after_binding_fails_closed()
    {
        MakeProfile();
        var rec = Bridge().Build().Single(r => r.Category == "browser-cache");
        Assert.True(_scans.TryGet(rec.Action!.SessionId, out var s));
        var cid = s.Items.Single().Public.Id;
        _browserRunning = true; // CommitGuard must refuse
        var res = _executor.Commit(rec.Action.SessionId, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.BrowserRunning, res.ErrorCode);
    }
}
