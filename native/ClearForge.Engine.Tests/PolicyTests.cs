using ClearForge.Engine.Policies;
using Xunit;

namespace ClearForge.Engine.Tests;

public class RuleValidationTests
{
    private static CleanupRule Valid() => new(
        Id: "r1", Version: 1, Category: "temporary-files", Scope: "current-user",
        RootId: "root", MinimumAgeHours: 24, Risk: "low", RequiresConfirmation: true,
        Action: "quarantine", MaxFilesPerScan: 100, ExcludeLocked: true, FollowReparsePoints: false);

    [Fact] public void Valid_rule_passes() => Assert.Null(Valid().Validate());

    [Theory]
    [InlineData("quarantine")] [InlineData("report-only")]
    public void Supported_actions_pass(string action) =>
        Assert.Null((Valid() with { Action = action }).Validate());

    [Fact] public void Reparse_following_is_rejected() =>
        Assert.NotNull((Valid() with { FollowReparsePoints = true }).Validate());

    [Fact] public void Unknown_risk_is_rejected() =>
        Assert.NotNull((Valid() with { Risk = "yolo" }).Validate());

    [Fact] public void Negative_age_is_rejected() =>
        Assert.NotNull((Valid() with { MinimumAgeHours = -1 }).Validate());

    [Fact] public void Machine_scope_is_structurally_valid_but_requires_root() =>
        Assert.Null((Valid() with { Scope = "machine" }).Validate());
}

public class TrustedRootsTests
{
    [Fact]
    public void Containment_accepts_children()
    {
        var root = TrustedRoots.Canonicalize(@"C:\temp\root");
        Assert.True(TrustedRoots.IsContainedIn(root, @"C:\temp\root\a\b\c.txt"));
        Assert.True(TrustedRoots.IsContainedIn(root, @"C:\temp\root"));
    }

    [Fact]
    public void Containment_rejects_sibling_prefix_attack()
    {
        // "C:\temp\rooted" shares the string prefix but is not inside "C:\temp\root"
        var root = TrustedRoots.Canonicalize(@"C:\temp\root");
        Assert.False(TrustedRoots.IsContainedIn(root, @"C:\temp\rooted\x.txt"));
    }

    [Fact]
    public void Containment_rejects_parent_and_traversal()
    {
        var root = TrustedRoots.Canonicalize(@"C:\temp\root");
        Assert.False(TrustedRoots.IsContainedIn(root, @"C:\temp"));
        // Canonicalization collapses ".." before the containment check
        Assert.False(TrustedRoots.IsContainedIn(root,
            TrustedRoots.Canonicalize(@"C:\temp\root\..\escape.txt")));
    }

    [Fact]
    public void Unresolved_root_returns_null()
    {
        var roots = new TrustedRoots();
        Assert.Null(roots.Resolve("does-not-exist"));
    }
}
