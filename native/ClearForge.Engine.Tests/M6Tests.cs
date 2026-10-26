using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M6 trust & approval safety: preview-time eligibility probes, expired
/// sessions refused at preview (not just commit), exact-subset execution,
/// and the journaled truth behind outcome reporting.
/// </summary>
public class M6Tests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FixtureDir _temp = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ITrustedRoots _roots;

    public M6Tests()
    {
        _roots = new TrustedRoots().Add("t", _temp.Path);
        var rule = new CleanupRule("r", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 100, true, false);
        _scans = new ScanSessionStore(new[] { rule }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        _executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
    }

    public void Dispose() { _journal.Dispose(); _temp.Dispose(); _store.Dispose(); }

    private RequestDispatcher Dispatcher(TimeSpan? ttl = null) => new(_scans,
        ttl is null ? _executor
            : new QuarantineExecutor(_scans, _journal,
                new QuarantineOptions(Path.Combine(_store.Path, "q"), SessionTtl: ttl), _roots),
        _journal);

    private ScanSession Scan()
    {
        var s = _scans.CreateExternal();
        _scans.Populate(s);
        return s;
    }

    private static async Task<JsonElement> Call(RequestDispatcher d, string method, string? json = null)
    {
        var res = await d.Dispatch(method,
            json is null ? null : JsonDocument.Parse(json).RootElement);
        return JsonSerializer.SerializeToElement(
            res.Result ?? new { error = res.Error, message = res.Message }, Camel);
    }

    [Fact]
    public async Task Preview_reports_per_item_eligibility()
    {
        _temp.Write("ok.tmp", "aaa", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("locked.tmp", "bbb", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("changed.tmp", "ccc", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("gone.tmp", "ddd", DateTimeOffset.UtcNow.AddDays(-2));
        var s = Scan();
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        Assert.Equal(4, ids.Length);

        File.Delete(Path.Combine(_temp.Path, "gone.tmp"));
        _temp.Write("changed.tmp", "different-bytes-now", DateTimeOffset.UtcNow.AddDays(-2));
        await using var lockHandle = new FileStream(
            Path.Combine(_temp.Path, "locked.tmp"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);

        var res = await Call(Dispatcher(), "cleanup.preview",
            JsonSerializer.Serialize(new { sessionId = s.Id, candidateIds = ids }));
        var byName = res.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("displayName").GetString()!,
                c => c.GetProperty("eligibility").GetString()!);
        Assert.Equal("eligible", byName["ok.tmp"]);
        Assert.Equal("locked", byName["locked.tmp"]);
        Assert.Equal("changed", byName["changed.tmp"]);
        Assert.Equal("missing", byName["gone.tmp"]);
    }

    [Fact]
    public async Task Preview_rejects_expired_session()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var s = Scan();
        var id = s.Items[0].Public.Id;
        Thread.Sleep(2);
        var res = await Call(Dispatcher(TimeSpan.FromMilliseconds(1)), "cleanup.preview",
            JsonSerializer.Serialize(new { sessionId = s.Id, candidateIds = new[] { id } }));
        Assert.Equal(Protocol.Errors.SessionExpired, res.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Commit_executes_exactly_the_approved_subset()
    {
        _temp.Write("approved.tmp", "yes", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("withheld.tmp", "no", DateTimeOffset.UtcNow.AddDays(-2));
        var s = Scan();
        var approved = s.Items
            .Single(i => i.Public.DisplayName == "approved.tmp").Public.Id;
        var withheld = s.Items
            .Single(i => i.Public.DisplayName == "withheld.tmp").Public.Id;

        // Approval gate: preview records the reviewed set; commit executes it.
        var d = Dispatcher();
        await Call(d, "cleanup.preview",
            JsonSerializer.Serialize(new { sessionId = s.Id, candidateIds = new[] { approved, withheld } }));
        var res = await Call(d, "cleanup.commit",
            JsonSerializer.Serialize(new { sessionId = s.Id, candidateIds = new[] { approved } }));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "approved.tmp")));
        // The withheld candidate in the same session is untouched.
        Assert.True(File.Exists(Path.Combine(_temp.Path, "withheld.tmp")));
        var items = res.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(approved, items[0].GetProperty("candidateId").GetString());
        // And committing the withheld id later is a separate explicit act.
        var res2 = await Call(d, "cleanup.commit",
            JsonSerializer.Serialize(new { sessionId = s.Id, candidateIds = new[] { withheld } }));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "withheld.tmp")));
        _ = res2;
    }
}
