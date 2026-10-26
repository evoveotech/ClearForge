using ClearForge.Ai;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M7.2B deterministic intent normalisation: explicit user quantities are
/// canonical (binary convention, 1 GB = 2^30 — the existing product
/// contract), ambiguity forces clarification, and model output stays
/// untrusted even when schema-valid.
/// </summary>
public class M72BQuantityTests
{
    [Theory]
    [InlineData("free up 10 GB", 10L * 1024 * 1024 * 1024)]
    [InlineData("free up 10 gb", 10L * 1024 * 1024 * 1024)]       // case-insensitive
    [InlineData("FREE 10 Gb!", 10L * 1024 * 1024 * 1024)]
    [InlineData("clean 1.5 GB", 1610612736L)]                     // decimal value
    [InlineData("make 500 MB of room", 500L * 1024 * 1024)]
    [InlineData("need 2 gigabytes", 2L * 1024 * 1024 * 1024)]     // alias
    [InlineData("about 20 g please", 20L * 1024 * 1024 * 1024)]   // short alias
    [InlineData("2048 KB", 2048L * 1024)]
    [InlineData("1073741824 bytes", 1073741824L)]
    [InlineData("clear 0.5 TB", 512L * 1024 * 1024 * 1024)]
    [InlineData("10GB", 10L * 1024 * 1024 * 1024)]                // no space
    [InlineData("10 gB", 10L * 1024 * 1024 * 1024)]
    public void Explicit_quantities_parse(string goal, long expected)
    {
        var p = GoalQuantities.Extract(goal);
        Assert.False(p.OutOfRangeSeen);
        var v = Assert.Single(p.DistinctBytes);
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("free up some space")]        // no quantity at all
    [InlineData("clean things up")]
    [InlineData("free 10 of them")]           // bare number, no unit
    [InlineData("version 1.5 notes")]         // number without unit
    [InlineData("10 mbps link")]              // unit boundary — "mbps" is not mb
    public void Missing_units_are_never_guessed(string goal)
        => Assert.Empty(GoalQuantities.Extract(goal).DistinctBytes);

    [Fact]
    public void Multiple_distinct_quantities_are_detected()
    {
        var p = GoalQuantities.Extract("free 10 GB or maybe 20 GB");
        Assert.Equal(2, p.DistinctBytes.Count);
    }

    [Fact]
    public void Same_quantity_repeated_is_not_ambiguous()
    {
        var p = GoalQuantities.Extract("free 10 GB, that's 10240 MB");
        var v = Assert.Single(p.DistinctBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, v);
    }

    [Fact]
    public void Negative_numbers_are_not_quantities()
        => Assert.Empty(GoalQuantities.Extract("remove -5 GB").DistinctBytes);

    [Fact]
    public void Out_of_range_quantities_are_flagged_not_clamped()
        => Assert.True(GoalQuantities.Extract("free 8 TB").OutOfRangeSeen);

    [Fact]
    public void Overflow_values_do_not_wrap()
        => Assert.True(GoalQuantities.Extract("free 9999999999 TB").OutOfRangeSeen);

    [Theory]
    [InlineData(1L << 40, "1 TB")]
    [InlineData(10L << 30, "10 GB")]
    [InlineData(1536L << 20, "1.5 GB")]
    [InlineData(500L << 20, "500 MB")]
    [InlineData(42, "42 B")]
    public void Format_uses_binary_convention(long bytes, string expected)
        => Assert.Equal(expected, GoalQuantities.Format(bytes));
}

/// <summary>
/// The canonical intent contract: validation + deterministic reconciliation
/// of model output against the explicit goal text and known recommendations.
/// </summary>
public class M72BIntentTests
{
    private static readonly IntentTask.RecSummary[] Recs =
    {
        new("act-temp", "Expired temp files", "temporary-files", 6144, "high"),
        new("rev-downloads-x", "Review 'Big'", "unclassified", 5L << 30, "low"),
        new("dev-out", "Regenerable output", "dev-dependencies", 1 << 20, "medium"),
    };

    private static string Raw(long goalBytes, string constraints = "{}",
        string questions = "[]", string explanation = "\"x\"") =>
        $"{{\"goalBytes\":{goalBytes},\"constraints\":{constraints}," +
        $"\"clarifyingQuestions\":{questions},\"explanation\":{explanation}}}";

    [Fact]
    public void Explicit_quantity_overrides_model_value()
    {
        // The M7.1 defect: model said 100 MB for "free up 10 GB".
        var n = IntentTask.Interpret("free up 10 GB",
            Raw(104857600), Recs);
        Assert.Equal("ok", n.Status);
        Assert.Equal("explicit_text", n.GoalBytesSource);
        Assert.Equal(10L << 30, n.Intent.GoalBytes);          // canonical
        Assert.Equal(104857600, n.ModelGoalBytes);            // recorded, unused
        Assert.Contains(n.Issues, i => i.Contains("canonical"));
    }

    [Fact]
    public void Matching_model_quantity_is_accepted()
    {
        var n = IntentTask.Interpret("free up 10 GB", Raw(10L << 30), Recs);
        Assert.Equal("explicit_text", n.GoalBytesSource);
        Assert.Null(n.ModelGoalBytes);
    }

    [Fact]
    public void No_explicit_quantity_uses_model_value_flagged()
    {
        var n = IntentTask.Interpret("free up some space", Raw(5L << 30), Recs);
        Assert.Equal("model", n.GoalBytesSource);
        Assert.Equal(5L << 30, n.Intent.GoalBytes);
        Assert.Contains(n.Issues, i => i.Contains("inferred"));
    }

    [Fact]
    public void Multiple_quantities_force_clarification()
    {
        var n = IntentTask.Interpret("free 10 GB or maybe 20 GB",
            Raw(10L << 30), Recs);
        Assert.Equal("clarification_required", n.Status);
        Assert.Contains(n.Intent.ClarifyingQuestions,
            q => q.Contains("10 GB") && q.Contains("20 GB"));
    }

    [Fact]
    public void Out_of_range_quantity_forces_clarification()
    {
        // Valid model output, but the user's stated quantity is out of range.
        var n = IntentTask.Interpret("free 8 TB", Raw(1L << 30), Recs);
        Assert.Equal("clarification_required", n.Status);
    }

    [Fact]
    public void Known_exclusion_ids_are_kept_hallucinated_ids_are_noops()
    {
        var n = IntentTask.Interpret("free 10 GB but skip temp files",
            Raw(10L << 30,
                "{\"excludeIds\":[\"act-temp\",\"act-fake-999\"]}"), Recs);
        Assert.Equal("ok", n.Status);
        var ids = Assert.Single(n.Intent.Constraints["excludeIds"]);
        Assert.Equal("act-temp", ids);
        var unmatched = Assert.Single(n.UnmatchedExclusions);
        Assert.Equal("act-fake-999", unmatched);
        Assert.Contains(n.Issues, i => i.Contains("ignored"));
    }

    [Fact]
    public void Null_recs_pass_exclusion_ids_through()
    {
        var n = IntentTask.Interpret("free 10 GB",
            Raw(10L << 30, "{\"excludeIds\":[\"act-x\"]}"), null);
        Assert.Equal("act-x", Assert.Single(n.Intent.Constraints["excludeIds"]));
        Assert.Empty(n.UnmatchedExclusions);
    }

    [Fact]
    public void Duplicate_json_keys_are_rejected()
        => Assert.Throws<AiException>(() => IntentTask.Validate(
            """{"goalBytes":1,"goalBytes":2,"constraints":{},"clarifyingQuestions":[],"explanation":"x"}"""));

    [Fact]
    public void Constraints_can_only_narrow()
    {
        var n = IntentTask.Interpret("free 10 GB",
            Raw(10L << 30, "{\"excludeCategories\":[\"downloads\"]}"), Recs);
        Assert.Equal("downloads",
            Assert.Single(n.Intent.Constraints["excludeCategories"]));
    }
}
