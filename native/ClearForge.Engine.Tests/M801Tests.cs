using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.0.1 safety-hardening regression gate: preview-before-commit approval
/// enforcement at the engine boundary, bounded session lifecycle, and
/// cancellation honoured between items without leaving un-journaled mutation.
/// </summary>
public class M801Tests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FixtureDir _temp = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ITrustedRoots _roots;

    public M801Tests()
    {
        _roots = new TrustedRoots().Add("t", _temp.Path);
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
    }

    public void Dispose() { _journal.Dispose(); _temp.Dispose(); _store.Dispose(); }

    private QuarantineExecutor Exec(ScanSessionStore scans,
        TimeSpan? sessionTtl = null, TimeSpan? approvalTtl = null) =>
        new(scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: sessionTtl, ApprovalTtl: approvalTtl), _roots);

    private RequestDispatcher Dispatcher(ScanSessionStore scans,
        QuarantineExecutor? exec = null) => new(scans, exec ?? Exec(scans), _journal);

    private ScanSessionStore Store(ScanSessionOptions? opts = null)
    {
        var rule = new CleanupRule("r", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 1000, true, false);
        return new ScanSessionStore(new[] { rule }, new FileScanner(_roots), opts);
    }

    private static ScanSession Scan(ScanSessionStore scans)
    {
        var s = scans.CreateExternal();
        scans.Populate(s);
        Assert.Equal(SessionStatus.Completed, s.Status);
        return s;
    }

    private static async Task<JsonElement> Call(RequestDispatcher d, string method, string? json = null)
    {
        var res = await d.Dispatch(method,
            json is null ? null : JsonDocument.Parse(json).RootElement);
        return JsonSerializer.SerializeToElement(
            res.Result ?? new { error = res.Error, message = res.Message }, Camel);
    }

    private static string Req(string sessionId, params string[] ids) =>
        JsonSerializer.Serialize(new { sessionId, candidateIds = ids });

    private static string? Err(JsonElement r) =>
        r.TryGetProperty("error", out var e) ? e.GetString() : null;

    // --- Approval gate ---

    [Fact]
    public async Task Commit_without_preview_is_rejected()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;

        var res = await Call(Dispatcher(scans), "cleanup.commit", Req(s.Id, id));
        Assert.Equal(Protocol.Errors.ApprovalRequired, Err(res));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    [Fact]
    public async Task Commit_with_missing_candidateIds_returns_invalid_params()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);

        var res = await Call(Dispatcher(scans), "cleanup.commit",
            $"{{\"sessionId\":\"{s.Id}\"}}");
        Assert.Equal(Protocol.Errors.InvalidParams, Err(res));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    [Fact]
    public async Task Preview_then_commit_succeeds()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans);

        var preview = await Call(d, "cleanup.preview", Req(s.Id, id));
        Assert.Null(Err(preview));
        var commit = await Call(d, "cleanup.commit", Req(s.Id, id));
        Assert.Null(Err(commit));
        Assert.Equal(Protocol.Errors.ItemCleaned,
            commit.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    [Fact]
    public async Task Commit_subset_of_previewed_ids_succeeds()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("b.tmp", "y", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, ids))));
        var commit = await Call(d, "cleanup.commit", Req(s.Id, ids[0]));
        Assert.Null(Err(commit));
        Assert.Single(commit.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Commit_ids_outside_preview_are_rejected()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("b.tmp", "y", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, ids[0]))));
        var res = await Call(d, "cleanup.commit", Req(s.Id, ids));
        Assert.Equal(Protocol.Errors.ApprovalMismatch, Err(res));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "b.tmp")));
    }

    [Fact]
    public async Task Stale_approval_is_rejected()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans, Exec(scans, approvalTtl: TimeSpan.FromMilliseconds(1)));

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, id))));
        Thread.Sleep(5);
        var res = await Call(d, "cleanup.commit", Req(s.Id, id));
        Assert.Equal(Protocol.Errors.ApprovalStale, Err(res));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    [Fact]
    public async Task Expired_session_commit_reports_session_expired()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans);

        // Approve under a non-expired executor, then commit against an
        // executor whose session TTL has already elapsed — stale approval
        // cannot resurrect an expired session.
        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, id))));
        Thread.Sleep(5);
        var expired = Dispatcher(scans,
            Exec(scans, sessionTtl: TimeSpan.FromMilliseconds(1)));
        var res = await Call(expired, "cleanup.commit", Req(s.Id, id));
        Assert.Equal(Protocol.Errors.SessionExpired, Err(res));
    }

    [Fact]
    public async Task Concurrent_commit_after_one_approval_only_moves_once()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, id))));
        var results = await Task.WhenAll(
            Task.Run(() => Call(d, "cleanup.commit", Req(s.Id, id))),
            Task.Run(() => Call(d, "cleanup.commit", Req(s.Id, id))));
        var statuses = results
            .Select(r => r.GetProperty("items")[0].GetProperty("status").GetString())
            .OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { Protocol.Errors.ItemAlreadyQuarantined, Protocol.Errors.ItemCleaned },
            statuses);
        var staged = Directory.GetFiles(
            Path.Combine(_store.Path, "q"), "*", SearchOption.AllDirectories);
        Assert.Single(staged);
    }

    // --- Session lifecycle ---

    [Fact]
    public async Task Session_capacity_is_bounded()
    {
        var scans = Store(new ScanSessionOptions(MaxSessions: 2));
        var d = Dispatcher(scans);
        Assert.Null(Err(await Call(d, "scan.start")));
        Assert.Null(Err(await Call(d, "scan.start")));
        var res = await Call(d, "scan.start");
        Assert.Equal(Protocol.Errors.SessionCapacity, Err(res));
    }

    [Fact]
    public async Task Scanning_sessions_are_never_evicted_but_terminal_ones_are()
    {
        var scans = Store(new ScanSessionOptions(
            MaxSessions: 3, RetentionPeriod: TimeSpan.Zero));
        var completed = Scan(scans);          // terminal, retention 0 → evictable
        var scanning = scans.CreateExternal(); // stays "scanning" — never evicted

        var d = Dispatcher(scans);
        Assert.Null(Err(await Call(d, "scan.start"))); // triggers sweep on register

        Assert.False(scans.TryGet(completed.Id, out _));
        Assert.True(scans.TryGet(scanning.Id, out _));
    }

    [Fact]
    public async Task Duplicates_start_reports_capacity()
    {
        var scans = Store(new ScanSessionOptions(MaxSessions: 1));
        scans.CreateExternal(); // fills capacity
        var scopes = new ClearForge.Engine.Duplicates.ScopeRegistry()
            .Add("fixtures", _temp.Path);
        var d = new RequestDispatcher(scans, Exec(scans), _journal,
            dupes: new ClearForge.Engine.Duplicates.DuplicateFinder(scopes),
            scopes: scopes);
        var res = await Call(d, "duplicates.start",
            "{\"scopeIds\":[\"fixtures\"]}");
        Assert.Equal(Protocol.Errors.SessionCapacity, Err(res));
    }

    [Fact]
    public async Task Restore_works_after_session_eviction()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store(new ScanSessionOptions(
            MaxSessions: 64, RetentionPeriod: TimeSpan.Zero));
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, id))));
        Assert.Null(Err(await Call(d, "cleanup.commit", Req(s.Id, id))));
        var history = await Call(d, "history.get");
        var opId = history.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("candidateId").GetString() == id)
            .GetProperty("operationId").GetString()!;

        scans.CreateExternal(); // triggers eviction of the completed session
        Assert.False(scans.TryGet(s.Id, out _));

        var restore = await Call(d, "cleanup.restore",
            JsonSerializer.Serialize(new { operationIds = new[] { opId } }));
        Assert.Equal(Protocol.Errors.RestoredOk,
            restore.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }

    // --- Cancellation ---

    [Fact]
    public async Task Cancel_between_items_cancels_rest_without_unjournaled_mutation()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("b.tmp", "y", DateTimeOffset.UtcNow.AddDays(-2));
        _temp.Write("c.tmp", "z", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, ids))));

        // Cancel the session inside the first per-item guard invocation — the
        // same mechanism cleanup.cancel drives — deterministically stopping
        // the batch between items.
        s.CommitGuard = () =>
        {
            s.Cancellation.Cancel();
            return null;
        };
        var commit = await Call(d, "cleanup.commit", Req(s.Id, ids));
        Assert.Null(Err(commit));
        var items = commit.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Single(items, i => i.GetProperty("status").GetString()
            == Protocol.Errors.ItemCleaned);
        Assert.Equal(2, items.Count(i => i.GetProperty("errorCode").GetString()
            == Protocol.Errors.Cancelled));
        // Exactly one file moved; the journaled quarantined row count agrees —
        // no mutation escaped the journal.
        Assert.Single(_journal.Query(JournalState.Quarantined.ToString()));
    }

    [Fact]
    public async Task Cancelled_session_commit_after_preview_yields_per_item_cancelled()
    {
        _temp.Write("a.tmp", "x", DateTimeOffset.UtcNow.AddDays(-2));
        var scans = Store();
        var s = Scan(scans);
        var id = s.Items[0].Public.Id;
        var d = Dispatcher(scans);

        Assert.Null(Err(await Call(d, "cleanup.preview", Req(s.Id, id))));
        await d.Dispatch("cleanup.cancel",
            JsonDocument.Parse($"{{\"sessionId\":\"{s.Id}\"}}").RootElement);
        var commit = await Call(d, "cleanup.commit", Req(s.Id, id));
        Assert.Equal(Protocol.Errors.Cancelled,
            commit.GetProperty("items")[0].GetProperty("errorCode").GetString());
        Assert.True(File.Exists(Path.Combine(_temp.Path, "a.tmp")));
    }
}
