using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>Fixture browser profiles; injectable process check — never real browsers.</summary>
public class BrowserCacheTests : IDisposable
{
    private readonly FixtureDir _profile = new();  // acts as "User Data" root
    private readonly FixtureDir _store = new();    // journal + quarantine
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ITrustedRoots _roots;
    private bool _browserRunning;

    private BrowserPrivacyScanner Profiles => new(
        new[] { new BrowserPrivacyScanner.BrowserSpec("edge", _profile.Path,
            new[] { "edge-fixture" }, "*", new[] { "Cache" }) },
        _ => _browserRunning);

    private BrowserCacheScanner CacheScanner => new(Profiles);

    public BrowserCacheTests()
    {
        _roots = new TrustedRoots();
        _scans = new ScanSessionStore(Array.Empty<CleanupRule>(), new FileScanner(_roots));
        _executor = new QuarantineExecutor(_scans,
            new CleanupJournal(Path.Combine(_store.Path, "j.db")),
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
    }

    public void Dispose() { _profile.Dispose(); _store.Dispose(); }

    private string MakeProfile()
    {
        var prof = Path.Combine(_profile.Path, "Default");
        var cache = Path.Combine(prof, "Cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "c1.dat"), new string('x', 64));
        File.WriteAllText(Path.Combine(cache, "c2.dat"), "y");
        File.WriteAllText(Path.Combine(prof, "Login Data"), "must-never-be-touched");
        File.WriteAllText(Path.Combine(prof, "Cookies"), "must-never-be-touched");
        File.WriteAllText(Path.Combine(prof, "History"), "must-never-be-touched");
        // roots must include the profile root so executor containment passes
        ((TrustedRoots)_roots).Add("browser-profile", prof);
        return prof;
    }

    private ScanSession ScanCache()
    {
        var s = _scans.CreateExternal();
        CacheScanner.ScanIntoSession(s, default);
        s.Status = SessionStatus.Completed;
        return s;
    }

    [Fact]
    public void Cache_candidates_generated_only_from_cache_dirs()
    {
        MakeProfile();
        var s = ScanCache();
        lock (s.Items)
        {
            Assert.Equal(2, s.Items.Count);
            Assert.All(s.Items, i =>
            {
                Assert.Equal("browser-cache", i.Public.Category);
                Assert.Contains("\\Cache\\", i.File.CanonicalPath);
            });
        }
    }

    [Fact]
    public void Credential_history_cookie_stores_are_never_candidates()
    {
        MakeProfile();
        var s = ScanCache();
        lock (s.Items)
            Assert.DoesNotContain(s.Items,
                i => i.File.CanonicalPath.Contains("Login Data")
                     || i.File.CanonicalPath.Contains("Cookies")
                     || i.File.CanonicalPath.Contains("History"));
    }

    [Fact]
    public void Quarantine_and_restore_roundtrip_via_existing_pipeline()
    {
        MakeProfile();
        var s = ScanCache();
        string cid; string src;
        lock (s.Items) { var i = s.Items.First(); cid = i.Public.Id; src = i.File.CanonicalPath; }
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemCleaned, res.Status);
        Assert.False(File.Exists(src));
        var entry = _journalEntry(s.Id, cid);
        Assert.NotNull(entry);
        var restored = _executor.Restore(entry!.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, restored.Status);
        Assert.True(File.Exists(src));
    }

    private JournalEntry? _journalEntry(string sid, string cid)
    {
        // tests share one journal file; find latest matching entry
        var j = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        try { return j.TryGet(sid, cid); } finally { j.Dispose(); }
    }

    [Fact]
    public void Browser_running_at_scan_produces_no_candidates()
    {
        _browserRunning = true;
        MakeProfile();
        var s = ScanCache();
        lock (s.Items) Assert.Empty(s.Items);
        Assert.Equal("no_supported_profiles_or_browsers_running", s.LastError);
    }

    [Fact]
    public void Browser_started_between_scan_and_commit_fails_closed()
    {
        MakeProfile();
        var s = ScanCache();
        string cid; lock (s.Items) cid = s.Items.First().Public.Id;
        _browserRunning = true; // simulate browser launch after scan
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.BrowserRunning, res.ErrorCode);
        lock (s.Items)
            Assert.True(File.Exists(s.Items.First().File.CanonicalPath));
    }

    [Fact]
    public void Reparse_point_inside_cache_is_not_traversed()
    {
        var prof = MakeProfile();
        using var outside = new FixtureDir();
        outside.Write("outside.dat");
        var link = Path.Combine(prof, "Cache", "junc");
        var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        pr.WaitForExit();
        var s = ScanCache();
        lock (s.Items)
            Assert.DoesNotContain(s.Items,
                i => i.File.CanonicalPath.Contains("junc"));
    }

    [Fact]
    public void Unsupported_or_missing_cache_layout_yields_no_candidates()
    {
        // profile dir exists but has no cache dir
        var prof = Path.Combine(_profile.Path, "Default");
        Directory.CreateDirectory(prof); // no Cache subdir
        var s = ScanCache();
        lock (s.Items) Assert.Empty(s.Items);
    }

    [Fact]
    public void Cache_commit_respects_quarantine_capacity()
    {
        MakeProfile();
        var s = ScanCache();
        string cid; lock (s.Items) cid = s.Items.First().Public.Id;
        var tight = new QuarantineExecutor(_scans,
            new CleanupJournal(Path.Combine(_store.Path, "j.db")),
            new QuarantineOptions(Path.Combine(_store.Path, "q"), MaxQuarantineBytes: 1),
            _roots);
        var res = tight.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.QuarantineFull, res.ErrorCode);
    }

    [Fact]
    public void Browser_cache_files_are_still_containment_checked()
    {
        MakeProfile();
        var s = ScanCache();
        // remove the allowlisted root → executor must refuse every item
        var freshRoots = new TrustedRoots();
        var strict = new QuarantineExecutor(_scans,
            new CleanupJournal(Path.Combine(_store.Path, "j.db")),
            new QuarantineOptions(Path.Combine(_store.Path, "q")), freshRoots);
        string cid; lock (s.Items) cid = s.Items.First().Public.Id;
        var res = strict.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.RefusedPolicy, res.ErrorCode);
    }
}
