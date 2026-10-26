using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Startup;
using ClearForge.Engine.Policies;
using Xunit;

namespace ClearForge.Engine.Tests;

public class DuplicatesTests : IDisposable
{
    private readonly FixtureDir _scope = new();
    private readonly FixtureDir _store = new();
    private readonly ScopeRegistry _scopes;
    private readonly DuplicateFinder _finder;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;

    public DuplicatesTests()
    {
        _scopes = new ScopeRegistry().Add("fixtures", _scope.Path);
        _finder = new DuplicateFinder(_scopes);
        _scans = new ScanSessionStore(Array.Empty<CleanupRule>(), new FileScanner(new TrustedRoots()));
        var roots = new TrustedRoots().Add("fixtures", _scope.Path); // quarantine containment root
        _executor = new QuarantineExecutor(_scans,
            new CleanupJournal(Path.Combine(_store.Path, "j.db")),
            new QuarantineOptions(Path.Combine(_store.Path, "q")), roots);
    }

    public void Dispose() { _scope.Dispose(); _store.Dispose(); }

    private ScanSession Run()
    {
        var s = _scans.CreateExternal();
        _finder.ScanIntoSession(s, new[] { "fixtures" }, default);
        s.Status = SessionStatus.Completed;
        return s;
    }

    [Fact]
    public void Identical_content_forms_one_group()
    {
        _scope.Write("a.bin", "identical-payload");
        _scope.Write("b.bin", "identical-payload");
        _scope.Write("a.bin.copy", "different-content");
        var s = _scans.CreateExternal();
        var report = _finder.ScanIntoSession(s, new[] { "fixtures" }, default);
        Assert.Single(report.Groups);
        Assert.Equal(2, report.Groups[0].Copies);
    }

    [Fact]
    public void Size_filter_skips_hashing_non_candidates()
    {
        _scope.Write("one.bin", "same-length!");
        _scope.Write("two.bin", "same-length!");
        _scope.Write("uniq.bin", "different length entirely");
        var s = _scans.CreateExternal();
        var report = _finder.ScanIntoSession(s, new[] { "fixtures" }, default);
        Assert.Single(report.Groups);
        Assert.Equal(2, report.Groups[0].Copies);
        Assert.Equal(report.Groups[0].FileBytes, report.EstimatedRecoverableBytes);
    }

    [Fact]
    public void Three_identical_files_recoverable_is_2x()
    {
        _scope.Write("x1", "dup"); _scope.Write("x2", "dup"); _scope.Write("x3", "dup");
        var report = _finder.ScanIntoSession(_scans.CreateExternal(), new[] { "fixtures" }, default);
        Assert.Single(report.Groups);
        Assert.Equal(2 * report.Groups[0].FileBytes, report.EstimatedRecoverableBytes);
    }

    [Fact]
    public void Same_name_different_content_is_not_a_duplicate()
    {
        _scope.Write("sub/a.txt", "aaa");
        _scope.Write("sub2/a.txt", "bbb");
        var report = _finder.ScanIntoSession(_scans.CreateExternal(), new[] { "fixtures" }, default);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public void Junction_inside_scope_is_not_traversed()
    {
        using var outside = new FixtureDir();
        outside.Write("shared.txt", "payload");
        _scope.Write("inner.txt", "payload");
        var link = Path.Combine(_scope.Path, "link");
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        proc.WaitForExit();
        var report = _finder.ScanIntoSession(_scans.CreateExternal(), new[] { "fixtures" }, default);
        Assert.Empty(report.Groups); // junctioned copy never enumerated
    }

    [Fact]
    public void Duplicate_candidate_flows_through_existing_quarantine()
    {
        _scope.Write("d1.bin", "same"); _scope.Write("d2.bin", "same");
        var s = Run();
        var cid = s.Items.Last().Public.Id; // quarantine the "copy"
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Contracts.Protocol.Errors.ItemCleaned, res.Status);
        // original remains, copy quarantined
        Assert.Single(Directory.GetFiles(_scope.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void File_changed_between_scan_and_quarantine_is_skipped()
    {
        var p1 = _scope.Write("c1.bin", "same-content");
        _scope.Write("c2.bin", "same-content");
        var s = Run();
        var cid = s.Items.Single(i => i.File.CanonicalPath == p1).Public.Id;
        File.WriteAllText(p1, "mutated-and-longer");
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Contracts.Protocol.Errors.SkippedChanged, res.ErrorCode);
    }

    [Fact]
    public void Unknown_scope_is_rejected_by_registry()
    {
        Assert.Null(_scopes.Resolve("c:\\windows"));
        Assert.NotNull(_scopes.Resolve("fixtures"));
    }
}

public class StartupTests
{
    [Fact]
    public void Registry_run_entries_report_publisher_when_verifiable()
    {
        var src = new RunKeyStartupSource(
            () => new[]
            {
                ("Notepad", "\"C:\\Windows\\notepad.exe\" /x"),
                ("Weird", "some.cmd --flag"),
            },
            path => path.EndsWith("notepad.exe") ? "Microsoft" : null);
        var entries = src.Enumerate();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Microsoft", entries[0].Publisher);
        Assert.Equal("verified", entries[0].Confidence);
        Assert.Equal("unknown", entries[1].Confidence);
    }

    [Fact]
    public void Startup_folder_source_is_read_only_and_lists_files()
    {
        using var fx = new FixtureDir();
        fx.Write("app.lnk"); fx.Write("tool.exe");
        var src = new StartupFolderSource(fx.Path, _ => "Vendor");
        var entries = src.Enumerate();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("enabled", e.Status));
        Assert.True(Directory.GetFiles(fx.Path).Length == 2); // untouched
    }

    [Fact]
    public void Composite_survives_source_failures()
    {
        var src = new CompositeStartupInventory(new IStartupSource[]
        {
            new ThrowingSource(),
            new StartupFolderSource(Path.GetTempPath(), _ => null),
        });
        Assert.NotNull(src.Enumerate()); // no throw
    }

    private sealed class ThrowingSource : IStartupSource
    {
        public IReadOnlyList<StartupEntry> Enumerate() =>
            throw new UnauthorizedAccessException("nope");
    }
}

public class PrivacyTests
{
    private static BrowserPrivacyScanner.BrowserSpec Spec(string root) =>
        new("edge", root, new[] { "definitely-not-running-proc" },
            "*", new[] { "Cache" });

    [Fact]
    public void Detects_profiles_and_cache_size_readonly()
    {
        using var fx = new FixtureDir();
        var prof = Path.Combine(fx.Path, "Default");
        Directory.CreateDirectory(Path.Combine(prof, "Cache"));
        File.WriteAllText(Path.Combine(prof, "Cache", "c1"), new string('x', 512));
        Directory.CreateDirectory(Path.Combine(fx.Path, "System Profile")); // skipped

        var scanner = new BrowserPrivacyScanner(new[] { Spec(fx.Path) });
        var found = scanner.Detect();
        Assert.Single(found);
        Assert.Equal("edge", found[0].Browser);
        Assert.True(found[0].CacheBytes >= 512);
        Assert.True(found[0].Categories["cache"].Available);
        Assert.False(found[0].Categories["cookies"].Available); // gated
        Assert.False(found[0].Categories["history"].Available);
    }

    [Fact]
    public void Running_browser_disables_all_categories()
    {
        using var fx = new FixtureDir();
        Directory.CreateDirectory(Path.Combine(fx.Path, "Default"));
        var running = new BrowserPrivacyScanner(
            new[] { new BrowserPrivacyScanner.BrowserSpec("edge", fx.Path,
                new[] { "x" }, "*", new[] { "Cache" }) },
            _ => true); // simulate running process
        var p = running.Detect().Single();
        Assert.True(p.BrowserRunning);
        Assert.False(p.Categories["cache"].Available);
        Assert.Equal("browser_running", p.Categories["cache"].UnavailableReason);
    }

    [Fact]
    public void Credential_stores_are_never_enumerated()
    {
        using var fx = new FixtureDir();
        var prof = Path.Combine(fx.Path, "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "Login Data"), "credentials");
        File.WriteAllText(Path.Combine(prof, "Cookies"), "cookies");
        var scanner = new BrowserPrivacyScanner(new[] { Spec(fx.Path) });
        var found = scanner.Detect().Single();
        // CacheBytes must not include Login Data/Cookies (they're outside Cache dir)
        Assert.Equal(0, found.CacheBytes);
    }
}
