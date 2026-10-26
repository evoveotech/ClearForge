using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Storage;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M5 deterministic planner: goal + constraint handling, ordering, evidence
/// honesty, development-directory protection, review-only gating, and the
/// execution-vs-restore boundary of SessionTtl.
/// </summary>
public class M5Tests
{
    private static Recommendation Exec(string id, long bytes,
        Confidence conf = Confidence.High, RiskLevel risk = RiskLevel.Low) =>
        new(id, "temporary-files", $"Exec {id}", "x",
            new[] { "session=s" }, bytes, conf, risk, Reversibility.Quarantinable,
            Array.Empty<string>(), Array.Empty<string>(),
            new RecommendationAction("quarantine", "s", "completed", 1, bytes));

    private static Recommendation ReviewOnly(string id, long bytes,
        string category = "unclassified", string scope = "downloads") =>
        new(id, category, $"Review {id}", "x",
            new[] { $"scope={scope}" }, bytes, Confidence.Low, RiskLevel.Medium,
            Reversibility.ManualOnly, new[] { "user review required" },
            Array.Empty<string>(), null);

    private static Recommendation DevHint(long bytes, string scope = "documents") =>
        // Mirrors FromInventory's marker-verified build-output rec: quarantinable
        // reversibility but NO action binding — must stay non-executable.
        new("dev-x", "build-output", "Regenerable build output 'x'", "x",
            new[] { $"scope={scope}" }, bytes, Confidence.Medium, RiskLevel.Medium,
            Reversibility.Quarantinable, Array.Empty<string>(),
            Array.Empty<string>(), null);

    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Plan_is_deterministic_and_orders_by_score()
    {
        var ranked = new[] { Exec("a", 1 * GB), Exec("b", 3 * GB), Exec("c", 2 * GB) }
            .OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var p1 = RecommendationEngine.Plan(4 * GB, ranked);
        var p2 = RecommendationEngine.Plan(4 * GB, ranked);
        Assert.Equal(p1.Selected.Select(r => r.Id), p2.Selected.Select(r => r.Id));
        Assert.Equal(new[] { "b", "c" }, p1.Selected.Select(r => r.Id).ToArray());
        Assert.True(p1.LikelyAchievable);
    }

    [Fact]
    public void Rejected_ids_are_never_selected()
    {
        var ranked = new[] { Exec("a", 3 * GB), Exec("b", 3 * GB) };
        var p = RecommendationEngine.Plan(3 * GB, ranked,
            new PlanConstraints(ExcludeIds: new[] { "a" }));
        Assert.Equal(new[] { "b" }, p.Selected.Select(r => r.Id).ToArray());
        Assert.Contains(p.Skipped, s => s.Id == "a" && s.Reason.Contains("user"));
        // Approved scope can never silently re-include a rejected action.
        var p2 = RecommendationEngine.Plan(6 * GB, ranked,
            new PlanConstraints(ExcludeIds: new[] { "a", "b" }));
        Assert.Empty(p2.Selected);
        Assert.False(p2.LikelyAchievable);
        Assert.NotEmpty(p2.MissingEvidence);
    }

    [Fact]
    public void Excluded_categories_and_scopes_are_respected()
    {
        var ranked = new Recommendation[]
        {
            Exec("t", 3 * GB),
            ReviewOnly("u", 3 * GB, "unclassified", "downloads"),
            ReviewOnly("v", 3 * GB, "unclassified", "documents"),
        };
        var byCat = RecommendationEngine.Plan(3 * GB, ranked,
            new PlanConstraints(ExcludeCategories: new[] { "temporary-files" }));
        Assert.Empty(byCat.Selected);
        Assert.Contains(byCat.Skipped, s => s.Id == "t");

        var byScope = RecommendationEngine.Plan(3 * GB, ranked,
            new PlanConstraints(ExcludeScopes: new[] { "downloads" }));
        Assert.Contains(byScope.Skipped, s => s.Id == "u" && s.Reason.Contains("scope"));
        Assert.DoesNotContain(byScope.Skipped, s => s.Id == "v" && s.Reason.Contains("scope"));
    }

    [Fact]
    public void Review_only_and_dev_directories_are_never_selected()
    {
        var ranked = new Recommendation[]
        {
            Exec("ok", 1 * GB),
            ReviewOnly("unknown-dir", 9 * GB),
            DevHint(9 * GB),
        };
        var p = RecommendationEngine.Plan(5 * GB, ranked);
        Assert.Equal(new[] { "ok" }, p.Selected.Select(r => r.Id).ToArray());
        Assert.Contains(p.Skipped, s => s.Id == "dev-x");
        Assert.Contains(p.Skipped, s => s.Id == "unknown-dir");
        Assert.All(p.Selected, r => Assert.NotNull(r.Action));
        // A development directory is protected even when it dominates the
        // evidence — size alone never makes it disposable.
        Assert.False(p.LikelyAchievable);
    }

    [Fact]
    public void Insufficient_evidence_reports_missing_not_promised()
    {
        var p = RecommendationEngine.Plan(10 * GB, new[] { Exec("a", 1 * GB) });
        Assert.False(p.LikelyAchievable);
        Assert.Contains(p.MissingEvidence, m => m.Contains("not promised"));
        Assert.True(p.EstimatedTotalBytes < 10 * GB);
    }

    [Fact]
    public void Review_only_from_inventory_carries_no_action()
    {
        var inv = new StorageInventoryReport(10, false,
            new Dictionary<string, IReadOnlyList<StorageNode>>
            {
                ["documents"] = new[]
                {
                    new StorageNode("proj", "dir", 5 * GB, "build-output"),
                    new StorageNode("mystery", "dir", 5 * GB, null),
                },
            });
        var recs = RecommendationEngine.FromInventory(inv);
        Assert.All(recs, r => Assert.Null(r.Action));
        Assert.All(recs, r => Assert.Null(r.ActionRef));
        var p = RecommendationEngine.Plan(1 * GB, recs);
        Assert.Empty(p.Selected);
    }
}

/// <summary>SessionTtl: gates execution only; journaled restore is unaffected.</summary>
public class M5SessionTtlTests : IDisposable
{
    private readonly FixtureDir _temp = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly ITrustedRoots _roots;

    public M5SessionTtlTests()
    {
        _roots = new TrustedRoots().Add("t", _temp.Path);
        var rule = new CleanupRule("r", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 100, true, false);
        _scans = new ScanSessionStore(new[] { rule }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
    }

    public void Dispose() { _journal.Dispose(); _temp.Dispose(); _store.Dispose(); }

    private QuarantineExecutor Exec(TimeSpan? ttl) => new(_scans, _journal,
        new QuarantineOptions(Path.Combine(_store.Path, "q"), SessionTtl: ttl), _roots);

    private string ScanOne(out ScanSession session)
    {
        _temp.Write("old.tmp", "abc", DateTimeOffset.UtcNow.AddDays(-2));
        var s = _scans.CreateExternal();
        _scans.Populate(s);
        session = s;
        return s.Items.Select(i => i.Public.Id).Single();
    }

    [Fact]
    public void Expired_session_rejects_at_commit()
    {
        var cid = ScanOne(out var s);
        Thread.Sleep(2);
        var items = Exec(TimeSpan.FromMilliseconds(1)).Commit(s.Id, new[] { cid }, default);
        Assert.Equal("rejected", items[0].Status);
        Assert.Equal(Protocol.Errors.SessionExpired, items[0].ErrorCode);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "old.tmp")));
    }

    [Fact]
    public void Session_fresh_enough_executes()
    {
        var cid = ScanOne(out var s);
        var items = Exec(TimeSpan.FromMinutes(30)).Commit(s.Id, new[] { cid }, default);
        Assert.Equal("cleaned", items[0].Status);
    }

    [Fact]
    public void Expired_session_still_restores_prior_journal_entry()
    {
        // Quarantine with no TTL, then a second executor with a 1ms TTL —
        // the journaled quarantined entry must still restore: expiry limits
        // execution, never recovery.
        var cid = ScanOne(out var s);
        var items = Exec(null).Commit(s.Id, new[] { cid }, default);
        Assert.Equal("cleaned", items[0].Status);
        var op = _journal.Query().Single(e => e.State == JournalState.Quarantined);

        var restore = Exec(TimeSpan.FromMilliseconds(1)).Restore(op.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, restore.Status);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "old.tmp")));
    }
}
