using System.Text.Json;
using ClearForge.Engine.Apps;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.4b per-app leftover map: normalization, exact-name matching, never-flag
/// rejection, nested vendor dirs, per-root failure isolation, review-only
/// contract. No executable binding exists by design.
/// </summary>
public class M84bTests
{
    private static IReadOnlyDictionary<string, string> Roots() =>
        new Dictionary<string, string> { ["appdata-local"] = "/local" };

    private static Func<string, IEnumerable<LeftoverDir>> Dirs(
        params (string parent, string name)[] dirs)
    {
        var map = dirs.GroupBy(d => d.parent)
            .ToDictionary(g => g.Key,
                g => g.Select(d => new LeftoverDir($"{d.parent}/{d.name}", d.name)));
        return path => map.TryGetValue(path, out var l) ? l : Enumerable.Empty<LeftoverDir>();
    }

    // ------------------------- normalization -------------------------------

    [Fact]
    public void Normalize_strips_case_punctuation_and_noise()
    {
        Assert.Equal("contosotoolbox",
            LeftoverMatcher.Normalize("Contoso Toolbox, Inc."));
        Assert.Equal("appv20", LeftoverMatcher.Normalize("My-App v20"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The Inc.")]
    [InlineData("ab")] // every token < 3 chars
    public void Normalize_rejects_unusable_names(string? raw) =>
        Assert.Null(LeftoverMatcher.Normalize(raw));

    // ------------------------- matching ------------------------------------

    [Fact]
    public void Exact_name_match_reports_candidate()
    {
        var c = LeftoverMatcher.Match("Contoso Toolbox", "Contoso Ltd", null,
            Roots(), Dirs(("/local", "ContosoToolbox")));
        var hit = Assert.Single(c);
        Assert.Equal("exact", hit.Confidence);
        Assert.Equal("name", hit.MatchBasis);
    }

    [Fact]
    public void Name_and_publisher_bases_combine_to_strong()
    {
        // Dir "Contoso" == publisher token; app name differs.
        var c = LeftoverMatcher.Match("Toolbox", "Contoso", null,
            Roots(), Dirs(("/local", "Contoso"), ("/local", "Toolbox")));
        Assert.Equal(2, c.Count);
        Assert.Single(c, x => x.Confidence == "publisher-context");
        Assert.Single(c, x => x.Confidence == "exact");
    }

    [Fact]
    public void Publisher_dir_holds_app_named_child()
    {
        var c = LeftoverMatcher.Match("Toolbox", "Contoso", null,
            Roots(),
            Dirs(("/local", "Contoso"), ("/local/Contoso", "Toolbox")));
        var hit = Assert.Single(c, x => x.MatchBasis == "nested");
        Assert.Equal("/local/Contoso/Toolbox", hit.Path);
    }

    [Fact]
    public void Substring_never_matches()
    {
        var c = LeftoverMatcher.Match("Box", "Contoso", null,
            Roots(), Dirs(("/local", "ContosoToolbox")));
        Assert.Empty(c);
    }

    [Fact]
    public void Never_flagged_publisher_yields_zero_candidates()
    {
        // Publisher "Microsoft" is never a basis — flagging it would sweep
        // every Microsoft directory on the machine.
        var c = LeftoverMatcher.Match("Microsoft", "Microsoft Corporation", null,
            Roots(), Dirs(("/local", "Microsoft")));
        Assert.Empty(c);
    }

    [Fact]
    public void Never_flagged_dir_name_cannot_be_flagged()
    {
        var c = LeftoverMatcher.Match("Backup", "Contoso", null,
            Roots(), Dirs(("/local", "Backup"), ("/local", "Contoso")));
        Assert.Single(c); // only the Contoso dir, never 'Backup'
        Assert.Equal("/local/Contoso", c[0].Path);
    }

    [Fact]
    public void Install_leaf_contributes_a_basis()
    {
        var c = LeftoverMatcher.Match("Toolbox", "Vendor", @"C:\Apps\TB2000",
            Roots(), Dirs(("/local", "TB2000")));
        var hit = Assert.Single(c);
        Assert.Equal("install-leaf", hit.MatchBasis);
    }

    [Fact]
    public void Leaf_and_name_bases_merge_on_one_dir()
    {
        // Install leaf "Toolbox" and app name "Toolbox" dedupe to one token;
        // the dir still reports the merged exact evidence.
        var c = LeftoverMatcher.Match("Toolbox", "Vendor", @"C:\Apps\Toolbox",
            Roots(), Dirs(("/local", "Toolbox")));
        var hit = Assert.Single(c);
        Assert.Equal("exact", hit.Confidence);
        Assert.Equal("name", hit.MatchBasis);
    }

    [Fact]
    public void Failing_root_does_not_sink_others()
    {
        var roots = new Dictionary<string, string>
        { ["bad"] = "/bad", ["appdata-local"] = "/local" };
        var c = LeftoverMatcher.Match("Toolbox", "Contoso", null, roots,
            path => path == "/bad"
                ? throw new IOException("denied")
                : new[] { new LeftoverDir("/local/Contoso", "Contoso") });
        Assert.Single(c);
    }

    // ------------------------- dispatch contract ---------------------------

    private static RequestDispatcher Dispatcher(LeftoverEnvironment? env)
    {
        var roots = new TrustedRoots();
        return new RequestDispatcher(
            new ScanSessionStore(Array.Empty<CleanupRule>(),
                new FileScanner(roots)),
            leftovers: env);
    }

    private static JsonElement Params(object o) => JsonSerializer
        .SerializeToElement(o, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [Fact]
    public async Task Leftovers_returns_candidates_and_review_only_flag()
    {
        var env = new LeftoverEnvironment(Roots(),
            Dirs(("/local", "Contoso")));
        var res = await Dispatcher(env).Dispatch("apps.leftovers",
            Params(new { name = "Toolbox", publisher = "Contoso" }));
        Assert.Null(res.Error);
        var payload = JsonSerializer.SerializeToElement(res.Result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(payload.GetProperty("reviewOnly").GetBoolean());
        Assert.Equal(1, payload.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task Missing_name_is_invalid_params()
    {
        var env = new LeftoverEnvironment(Roots(), Dirs());
        var res = await Dispatcher(env).Dispatch("apps.leftovers",
            Params(new { publisher = "Contoso" }));
        Assert.Equal("invalid_params", res.Error);
    }

    [Fact]
    public async Task Unconfigured_module_fails_closed()
    {
        var res = await Dispatcher(null).Dispatch("apps.leftovers",
            Params(new { name = "Toolbox" }));
        Assert.Equal("policy_refused", res.Error);
    }
}
