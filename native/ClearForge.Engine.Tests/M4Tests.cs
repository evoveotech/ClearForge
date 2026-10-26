using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Storage;
using Xunit;

namespace ClearForge.Engine.Tests;

public class M4Tests : IDisposable
{
    private readonly FixtureDir _scope = new();
    public void Dispose() => _scope.Dispose();

    private StorageInventory Inv(int maxFiles = 50_000)
        => new(new ScopeRegistry().Add("s", _scope.Path), new ScanLimits(MaxFilesTotal: maxFiles));

    [Fact]
    public void Dev_output_is_classified_by_evidence_not_name_alone()
    {
        // node_modules WITH package.json sibling → dev-dependencies
        var nm = Path.Combine(_scope.Path, "proj", "node_modules");
        Directory.CreateDirectory(nm);
        File.WriteAllText(Path.Combine(_scope.Path, "proj", "package.json"), "{}");
        Assert.Equal("dev-dependencies", StorageInventory.Categorize(nm));

        // node_modules WITHOUT marker → review-only (null)
        var bare = Path.Combine(_scope.Path, "loose", "node_modules");
        Directory.CreateDirectory(bare);
        Assert.Null(StorageInventory.Categorize(bare));
    }

    [Fact]
    public void Unknown_dirs_stay_review_only_with_low_confidence()
    {
        _scope.Write("mystery/big.bin", new string('z', 2048));
        var recs = RecommendationEngine.FromInventory(Inv().Scan(new[] { "s" }, default));
        var r = recs.Single(x => x.Category == "unclassified");
        Assert.Equal(Reversibility.ManualOnly, r.Reversibility);
        Assert.Null(r.ActionRef);
    }

    [Fact]
    public void Plan_admits_goal_it_cannot_reach()
    {
        _scope.Write("proj/node_modules/x", "small");
        File.WriteAllText(Path.Combine(_scope.Path, "proj", "package.json"), "{}");
        var recs = RecommendationEngine.FromInventory(Inv().Scan(new[] { "s" }, default));
        var plan = RecommendationEngine.Plan(long.MaxValue / 4, recs);
        Assert.False(plan.LikelyAchievable);
        Assert.NotEmpty(plan.MissingEvidence);
    }

    [Fact]
    public void Inventory_is_bounded_and_reports_partial()
    {
        for (var i = 0; i < 20; i++) _scope.Write($"d/f{i}.bin", "data");
        var r = Inv(maxFiles: 5).Scan(new[] { "s" }, default);
        Assert.True(r.Partial);
    }

    [Fact]
    public void Inventory_honours_cancellation()
    {
        for (var i = 0; i < 50; i++) _scope.Write($"d/f{i}.bin", "data");
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => Inv().Scan(new[] { "s" }, cts.Token));
    }

    [Fact]
    public void Reparse_point_inside_scope_is_not_measured()
    {
        using var outside = new FixtureDir();
        outside.Write("big.bin", new string('x', 4096));
        _scope.Write("inner.bin", "tiny");
        var link = Path.Combine(_scope.Path, "junc");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        var r = Inv().Scan(new[] { "s" }, default);
        Assert.DoesNotContain(r.Scopes["s"], n => n.Name == "junc");
    }
}
