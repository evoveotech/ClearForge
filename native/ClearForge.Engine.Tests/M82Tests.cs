using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.SmartScan;
using ClearForge.Host;
using ClearForge.Windows;
using Xunit;
using Xunit.Abstractions;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.2 coverage expansion: filename-pattern rules, the new disposable-cache
/// catalog, per-category finding text, and the unchanged safety pipeline end
/// to end (preview → approval → journaled quarantine → verified restore).
/// </summary>
public class M82Tests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly FixtureDir _fx = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ITrustedRoots _roots;

    public M82Tests(ITestOutputHelper output)
    {
        _out = output;
        _roots = new TrustedRoots()
            .Add("thumb", Path.Combine(_fx.Path, "Explorer"))
            .Add("gpu", Path.Combine(_fx.Path, "D3DSCache"))
            .Add("pkg", Path.Combine(_fx.Path, "v3-cache"));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
    }

    public void Dispose() { _journal.Dispose(); _fx.Dispose(); _store.Dispose(); }

    // Pattern rule mirroring user-thumbnail-cache.
    private static CleanupRule ThumbRule() => new(
        "user-thumbnail-cache", 1, "thumbnail-cache", "current-user", "thumb",
        24, "low", true, "quarantine", 10_000, true, false,
        IncludePatterns: new[] { "thumbcache_*.db", "iconcache_*.db" });

    // Whole-root rule mirroring the gpu-shader-cache rules.
    private static CleanupRule GpuRule() => new(
        "user-d3d-shader-cache", 1, "gpu-shader-cache", "current-user", "gpu",
        24, "low", true, "quarantine", 50_000, true, false);

    private static CleanupRule PkgRule() => new(
        "user-nuget-http-cache", 1, "package-manager-caches", "current-user",
        "pkg", 24, "medium", true, "quarantine", 50_000, true, false);

    private ScanSessionStore Store(params CleanupRule[] rules) =>
        new(rules, new FileScanner(_roots));

    private QuarantineExecutor Executor(ScanSessionStore scans) => new(
        scans, _journal,
        new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);

    private ScanSession Populate(ScanSessionStore scans)
    {
        var s = scans.CreateExternal();
        scans.Populate(s);
        Assert.Equal(SessionStatus.Completed, s.Status);
        return s;
    }

    // --------------------------- rule validation ---------------------------

    [Fact]
    public void Include_patterns_validate_filename_globs_only()
    {
        Assert.Null(ThumbRule().Validate());
        var bad = new[]
        {
            new[] { "sub/dir.db" }, new[] { "sub\\dir.db" },
            new[] { "..\\x.db" }, new[] { "a..b.db" },
            new[] { "C:x.db" }, new[] { "" }, new[] { "   " },
            new[] { new string('x', 129) },
            Enumerable.Range(0, 33).Select(i => $"p{i}.db").ToArray(),
        };
        foreach (var pats in bad)
            Assert.NotNull((ThumbRule() with { IncludePatterns = pats }).Validate());
        Assert.Null((ThumbRule() with { IncludePatterns = null }).Validate());
        Assert.NotNull((ThumbRule() with { IncludePatterns = Array.Empty<string>() }).Validate());
    }

    // ------------------------- classification evidence ----------------------

    [Fact]
    public void Pattern_rule_admits_only_matching_filenames()
    {
        _fx.Write(@"Explorer\thumbcache_256.db", "thumb");
        _fx.Write(@"Explorer\iconcache_32.db", "icon");
        _fx.Write(@"Explorer\not_a_cache.dat", "keep");
        _fx.Write(@"Explorer\thumbcache_256.txt", "wrong-ext");

        var scans = Store(ThumbRule());
        var s = Populate(scans);
        var names = s.Items.Select(i => i.Public.DisplayName).ToArray();
        Assert.Equal(new[] { "iconcache_32.db", "thumbcache_256.db" },
            names.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Classification_reason_names_rule_age_root_and_pattern()
    {
        _fx.Write(@"Explorer\thumbcache_256.db", "thumb");
        var scans = Store(ThumbRule());
        var s = Populate(scans);
        var c = s.Items.Single().Public;
        Assert.Contains("user-thumbnail-cache v1", c.Reason);
        Assert.Contains("age", c.Reason);
        Assert.Contains("root 'thumb'", c.Reason);
        Assert.Contains("name matches 'thumbcache_*.db'", c.Reason);
    }

    [Fact]
    public void Whole_root_rule_includes_every_contained_file()
    {
        _fx.Write(@"D3DSCache\a.bin", "a");
        _fx.Write(@"D3DSCache\sub\b.bin", "b");
        var scans = Store(GpuRule());
        var s = Populate(scans);
        Assert.Equal(2, s.Items.Count);
        Assert.All(s.Items, i => Assert.Equal("gpu-shader-cache", i.Public.Category));
    }

    [Fact]
    public void Fresh_files_are_never_candidates_in_new_categories()
    {
        _fx.Write(@"D3DSCache\fresh.bin", "new", DateTimeOffset.UtcNow.AddMinutes(-5));
        var scans = Store(GpuRule());
        var s = Populate(scans);
        Assert.Empty(s.Items);
    }

    [Fact]
    public void Files_outside_roots_and_junction_escapes_are_excluded()
    {
        _fx.Write(@"Explorer\iconcache_96.db", "icon");
        using var outside = new FixtureDir();
        outside.Write("thumbcache_999.db", "outside — same name, outside root");
        var link = Path.Combine(_fx.Path, "Explorer", "escape");
        var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        pr.WaitForExit();
        Assert.True(Directory.Exists(link));

        var scans = Store(ThumbRule());
        var s = Populate(scans);
        var c = Assert.Single(s.Items);
        Assert.Contains("\\Explorer\\", c.File.CanonicalPath);
        Assert.DoesNotContain("outside", c.File.CanonicalPath);
    }

    // ------------------------------ catalog --------------------------------

    [Fact]
    public void Compiled_catalog_is_valid_bounded_and_expected()
    {
        var rules = DefaultRules.M1();
        Assert.All(rules, r =>
        {
            Assert.Null(r.Validate());
            Assert.Equal("current-user", r.Scope);
            Assert.Equal("quarantine", r.Action);
            Assert.False(r.FollowReparsePoints);
        });

        var ids = rules.Select(r => r.Id).ToArray();
        Assert.Equal(new[]
        {
            "user-temp-expired-files", "user-crash-dumps", "user-thumbnail-cache",
            "user-d3d-shader-cache", "user-nvidia-dx-shader-cache",
            "user-nvidia-gl-shader-cache", "user-amd-dx-shader-cache",
            "user-amd-gl-shader-cache", "user-nuget-http-cache",
            "user-nuget-plugins-cache", "user-npm-cache", "user-yarn-cache",
            "user-pip-cache", "user-wer-report-queue", "user-wer-report-archive",
        }, ids);

        var categories = rules.Select(r => r.Category).Distinct().ToArray();
        Assert.Equal(new[]
        {
            "temporary-files", "crash-dumps", "thumbnail-cache",
            "gpu-shader-cache", "package-manager-caches", "windows-error-reports",
        }, categories);

        // Every referenced root id is a real registered root name — a typo
        // here would silently produce zero candidates in production.
        var knownRoots = typeof(WindowsRoots).GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(rules, r => Assert.True(knownRoots.Contains(r.RootId),
            $"rule {r.Id} references unregistered root '{r.RootId}'"));
    }

    // ----------------------- lifecycle through the pipeline -----------------

    [Fact]
    public void New_category_files_quarantine_and_restore_byte_identical()
    {
        var p = _fx.Write(@"D3DSCache\shader.bin", "shader-bytes");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
        var scans = Store(GpuRule());
        var exec = Executor(scans);
        var s = Populate(scans);
        var cid = s.Items.Single().Public.Id;

        var res = exec.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, res.Status);
        Assert.False(File.Exists(p));

        var entry = _journal.Query(JournalState.Quarantined.ToString()).Single();
        Assert.Equal("gpu-shader-cache", entry.Category);
        var restored = exec.Restore(entry.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, restored.Status);
        Assert.Equal(hash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
    }

    [Fact]
    public void Locked_new_category_file_is_skipped_then_retry_safe()
    {
        var p = _fx.Write(@"D3DSCache\locked.bin", "locked");
        var scans = Store(GpuRule());
        var exec = Executor(scans);
        var s = Populate(scans);
        var cid = s.Items.Single().Public.Id;

        using (var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite,
                   FileShare.None))
        {
            var res = exec.Commit(s.Id, new[] { cid }, default).Single();
            Assert.Equal(Protocol.Errors.SkippedLocked, res.ErrorCode);
            Assert.True(File.Exists(p));
        }
        var retry = exec.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, retry.Status);
    }

    [Fact]
    public void Changed_and_vanished_files_fail_closed_in_new_categories()
    {
        var changed = _fx.Write(@"Explorer\iconcache_16.db", "before");
        var gone = _fx.Write(@"Explorer\thumbcache_64.db", "gone");
        var scans = Store(ThumbRule());
        var exec = Executor(scans);
        var s = Populate(scans);
        var changedId = s.Items.Single(i => i.Public.DisplayName == "iconcache_16.db").Public.Id;
        var goneId = s.Items.Single(i => i.Public.DisplayName == "thumbcache_64.db").Public.Id;

        File.WriteAllText(changed, "mutated-after-scan");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow);
        File.Delete(gone);

        var results = exec.Commit(s.Id, new[] { changedId, goneId }, default);
        Assert.Equal(Protocol.Errors.SkippedChanged, results[0].ErrorCode);
        Assert.Equal(Protocol.Errors.SkippedVanished, results[1].ErrorCode);
    }

    [Fact]
    public void Restore_never_overwrites_a_recreated_path()
    {
        var p = _fx.Write(@"v3-cache\pkg.dat", "cached-package");
        var scans = Store(PkgRule());
        var exec = Executor(scans);
        var s = Populate(scans);
        var cid = s.Items.Single().Public.Id;
        Assert.Equal(Protocol.Errors.ItemCleaned,
            exec.Commit(s.Id, new[] { cid }, default).Single().Status);
        var entry = _journal.Query(JournalState.Quarantined.ToString()).Single();

        // An app recreated the file while it was quarantined — restore must
        // refuse rather than overwrite it.
        File.WriteAllText(p, "newer-content");
        var res = exec.Restore(entry.OperationId);
        Assert.Equal("error", res.Status);
        Assert.Equal(Protocol.Errors.RestoreConflict, res.ErrorCode);
        Assert.Equal("newer-content", File.ReadAllText(p));
    }

    [Fact]
    public async Task Preview_and_approval_are_still_mandatory()
    {
        _fx.Write(@"D3DSCache\a.bin", "a");
        var scans = Store(GpuRule());
        var exec = Executor(scans);
        var s = Populate(scans);
        var cid = s.Items.Single().Public.Id;
        var d = new RequestDispatcher(scans, exec, _journal);

        var commit = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(
                new { sessionId = s.Id, candidateIds = new[] { cid } })).RootElement);
        Assert.Equal(Protocol.Errors.ApprovalRequired, commit.Error);

        var preview = await d.Dispatch("cleanup.preview",
            JsonDocument.Parse(JsonSerializer.Serialize(
                new { sessionId = s.Id, candidateIds = new[] { cid } })).RootElement);
        Assert.Null(preview.Error);
        var ok = await d.Dispatch("cleanup.commit",
            JsonDocument.Parse(JsonSerializer.Serialize(
                new { sessionId = s.Id, candidateIds = new[] { cid } })).RootElement);
        Assert.Null(ok.Error);
    }

    // ---------------------------- Smart Scan integration --------------------

    private sealed class FakeProducer : ISmartScanProducer
    {
        public string Id => "fake";
        public int Priority => 1;
        private readonly ProducerOutcome _outcome;
        public FakeProducer(ProducerOutcome o) => _outcome = o;
        public ProducerOutcome Run(SmartScanContext ctx, CancellationToken ct) => _outcome;
    }

    [Fact]
    public void Catalog_producer_groups_multi_rule_categories_into_one_finding()
    {
        _fx.Write(@"Explorer\thumbcache_256.db", "t");
        _fx.Write(@"D3DSCache\s1.bin", "g1");
        _fx.Write(@"v3-cache\p1.dat", "p1");
        // Two rules sharing gpu-shader-cache + one each for the other
        // categories → one finding per category.
        var secondGpu = GpuRule() with { Id = "user-nvidia-dx-shader-cache" };
        var scans = Store(ThumbRule(), GpuRule(), secondGpu, PkgRule());
        var outcome = new CatalogScanProducer(scans)
            .Run(new SmartScanContext(Array.Empty<string>()), default);

        var categories = outcome.Findings.Select(f => f.Category).ToArray();
        Assert.Equal(new[]
            { "gpu-shader-cache", "package-manager-caches", "thumbnail-cache" },
            categories.OrderBy(c => c).ToArray());
        Assert.All(outcome.Findings, f =>
        {
            Assert.True(f.Actionable);
            Assert.NotNull(f.SessionId);
            Assert.NotNull(f.CandidateIds);
        });
        var titles = outcome.Findings.ToDictionary(f => f.Category, f => f.Title);
        Assert.Contains("Explorer thumbnail & icon cache", titles["thumbnail-cache"]);
        Assert.Contains("GPU shader caches", titles["gpu-shader-cache"]);
        Assert.Contains("Package manager caches", titles["package-manager-caches"]);
        // The finding states its disposable-evidence, not just a rule id.
        Assert.Contains("rebuilds", outcome.Findings
            .Single(f => f.Category == "gpu-shader-cache").Reason);
        Assert.Contains("re-download", outcome.Findings
            .Single(f => f.Category == "package-manager-caches").Reason);
    }

    [Fact]
    public async Task Smart_scan_results_expose_new_categories_with_session_state()
    {
        _fx.Write(@"D3DSCache\s1.bin", "g1");
        var scans = Store(GpuRule());
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new CatalogScanProducer(scans) },
            TimeSpan.FromSeconds(30));
        var run = c.Start(Array.Empty<string>());
        var end = DateTime.UtcNow.AddSeconds(15);
        while (run.Status == SmartScanStatus.Running && DateTime.UtcNow < end)
            Thread.Sleep(10);
        Assert.Equal(SmartScanStatus.Completed, run.Status);
        var f = Assert.Single(run.Findings);
        Assert.Equal("gpu-shader-cache", f.Category);
        Assert.True(f.Actionable);

        var exec = Executor(scans);
        var d = new RequestDispatcher(scans, exec, _journal, smartScan: c);
        var res = await d.Dispatch("smartscan.results",
            JsonDocument.Parse($"{{\"smartScanId\":\"{run.Id}\"}}").RootElement);
        var json = JsonSerializer.Serialize(res.Result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var finding = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("live", finding.GetProperty("sessionState").GetString());
        Assert.Equal("gpu-shader-cache", finding.GetProperty("category").GetString());
    }

    [Fact]
    public void Recommendation_bridge_binds_new_categories_to_sessions()
    {
        _fx.Write(@"D3DSCache\s1.bin", "g1");
        _fx.Write(@"Explorer\thumbcache_256.db", "t");
        var scans = Store(ThumbRule(), GpuRule());
        var bridge = new ClearForge.Engine.Storage.RecommendationActionBridge(scans);
        var recs = bridge.Build();
        Assert.Equal(2, recs.Count);
        Assert.All(recs, r =>
        {
            Assert.NotNull(r.Action);
            Assert.Equal("quarantine", r.Action!.Kind);
            Assert.True(r.Action.CandidateCount > 0);
        });
        Assert.Contains(recs, r => r.Category == "gpu-shader-cache"
                                 && r.Title == "GPU shader caches");
        Assert.Contains(recs, r => r.Category == "thumbnail-cache"
                                 && r.Title == "Explorer thumbnail & icon cache");
    }

    // ------------------------------- bounds/perf ----------------------------

    [Fact]
    public void Scan_limits_still_bound_pattern_rule_results()
    {
        for (var i = 0; i < 12; i++)
            _fx.Write($@"Explorer\thumbcache_{i}.db", "t");
        _fx.Write(@"Explorer\unrelated.dat", "n");
        var scans = Store(ThumbRule() with { MaxFilesPerScan = 5 });
        var s = Populate(scans);
        Assert.Equal(5, s.Items.Count);
    }

    [Fact]
    public void Representative_scan_completes_within_bounded_time()
    {
        // Representative mixed fixture: ~4k files across a pattern-filtered
        // root and a whole-root dir — the shape M8.2 adds to production scans.
        for (var i = 0; i < 2000; i++)
            _fx.Write($@"Explorer\thumbcache_{i:D4}.db", "thumb-data");
        for (var i = 0; i < 500; i++)
            _fx.Write($@"Explorer\unrelated_{i}.dat", "skip-me");
        for (var i = 0; i < 2000; i++)
            _fx.Write($@"D3DSCache\shader_{i:D4}.bin", "shader-data");

        var scans = Store(ThumbRule(), GpuRule());
        var s = scans.CreateExternal();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scans.Populate(s);
        sw.Stop();
        _out.WriteLine($"populated {s.Items.Count} candidates in {sw.Elapsed}");
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal(4000, s.Items.Count);
        // Generous bound: this run typically completes in seconds; the guard
        // exists to catch accidental quadratic/IO-bound regressions.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"scan took {sw.Elapsed}");
    }
}
