using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.SmartScan;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.3 duplicate review + keeper selection: explicit keeper choice enforced
/// engine-side at preview/commit, group membership never trusted from the
/// client, review detail via duplicates.group, and the unchanged
/// preview → approval → journaled quarantine → restore pipeline.
/// </summary>
public class M83Tests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FixtureDir _scope = new();
    private readonly FixtureDir _store = new();
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ScopeRegistry _scopes;
    private readonly DuplicateFinder _finder;

    public M83Tests()
    {
        var roots = new TrustedRoots().Add("t", _scope.Path);
        _scans = new ScanSessionStore(Array.Empty<CleanupRule>(),
            new FileScanner(roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        _executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q"),
                SessionTtl: TimeSpan.FromMinutes(30),
                ApprovalTtl: TimeSpan.FromMinutes(10)),
            roots);
        _scopes = new ScopeRegistry().Add("fixtures", _scope.Path);
        _finder = new DuplicateFinder(_scopes);
    }

    public void Dispose() { _journal.Dispose(); _scope.Dispose(); _store.Dispose(); }

    private RequestDispatcher Dispatcher() =>
        new(_scans, _executor, _journal, _finder, _scopes);

    private ScanSession Run()
    {
        var s = _scans.CreateExternal();
        _finder.ScanIntoSession(s, new[] { "fixtures" }, default);
        s.Status = SessionStatus.Completed;
        return s;
    }

    private static async Task<JsonElement> Call(
        RequestDispatcher d, string method, object req)
    {
        var res = await d.Dispatch(method,
            JsonDocument.Parse(JsonSerializer.Serialize(req, Camel)).RootElement);
        return JsonSerializer.SerializeToElement(
            res.Result ?? new { error = res.Error, message = res.Message }, Camel);
    }

    private static string? Err(JsonElement r) =>
        r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    private static object Prev(string sid, string group, string keeper, params string[] ids) =>
        new { sessionId = sid, candidateIds = ids, duplicateGroupId = group, keeperId = keeper };

    private static string[] GroupIds(JsonElement detail) =>
        detail.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("candidateId").GetString()!).ToArray();

    /// <summary>Three byte-identical files, one distractor.</summary>
    private (ScanSession s, string groupId, string[] ids) ThreeMemberGroup()
    {
        _scope.Write("dup/a.bin", "same-payload");
        _scope.Write("dup/b.bin", "same-payload");
        _scope.Write("dup/c.bin", "same-payload");
        _scope.Write("dup/uniq.bin", "different");
        var s = Run();
        var g = s.DuplicateReport!.Groups.Single();
        return (s, g.GroupId, g.Candidates.Select(c => c.Id).ToArray());
    }

    // ---------------- duplicates.group review detail ----------------

    [Fact]
    public async Task Group_review_exposes_members_paths_and_full_hash()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var res = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        Assert.Null(Err(res));
        Assert.Equal(3, res.GetProperty("copies").GetInt32());
        Assert.Equal(64, res.GetProperty("sha256").GetString()!.Length);
        Assert.Equal("live", res.GetProperty("sessionState").GetString());
        Assert.True(res.GetProperty("actionable").GetBoolean());
        Assert.NotNull(res.GetProperty("suggestedKeeperId").GetString());
        var members = res.GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal(ids.OrderBy(x => x).ToArray(),
            members.Select(m => m.GetProperty("candidateId").GetString()!).OrderBy(x => x).ToArray());
        foreach (var m in members)
        {
            var path = m.GetProperty("path").GetString()!;
            Assert.StartsWith(_scope.Path, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("eligible", m.GetProperty("eligibility").GetString());
        }
        // Suggestion is the newest member (all fixtures share a timestamp →
        // ordinal path order decides) — a member of the group, never implied.
        Assert.Contains(res.GetProperty("suggestedKeeperId").GetString()!, ids);
    }

    [Fact]
    public async Task Group_review_rejects_unknown_session_and_group()
    {
        var (s, gid, _) = ThreeMemberGroup();
        var d = Dispatcher();
        Assert.Equal(Protocol.Errors.SessionNotFound, Err(await Call(d,
            "duplicates.group", new { sessionId = "ghost", groupId = gid })));
        Assert.Equal(Protocol.Errors.GroupNotFound, Err(await Call(d,
            "duplicates.group", new { sessionId = s.Id, groupId = "g99" })));
        // A non-duplicate session has no report.
        var plain = _scans.CreateExternal();
        plain.Status = SessionStatus.Completed;
        Assert.Equal(Protocol.Errors.GroupNotFound, Err(await Call(d,
            "duplicates.group", new { sessionId = plain.Id, groupId = gid })));
    }

    [Fact]
    public async Task Missing_and_locked_members_report_eligibility()
    {
        _scope.Write("a.bin", "same");
        var pb = _scope.Write("b.bin", "same");
        var s = Run();
        var gid = s.DuplicateReport!.Groups.Single().GroupId;
        File.Delete(pb);
        var d = Dispatcher();
        var res = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        Assert.Null(Err(res));
        var members = res.GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal("missing", members
            .Single(m => m.GetProperty("path").GetString() == pb)
            .GetProperty("eligibility").GetString());
        Assert.Equal("eligible", members
            .Single(m => m.GetProperty("path").GetString() != pb)
            .GetProperty("eligibility").GetString());
        // Still actionable: one member remains eligible (kept-copy choice is
        // unaffected by the vanished member).
        Assert.True(res.GetProperty("actionable").GetBoolean());
    }

    [Fact]
    public async Task Group_with_no_eligible_candidates_is_review_only()
    {
        var p1 = _scope.Write("l1.bin", "same");
        var p2 = _scope.Write("l2.bin", "same");
        var s = Run();
        var gid = s.DuplicateReport!.Groups.Single().GroupId;
        // Lock every member — none can be quarantined; the group must report
        // not actionable rather than pretending a selection could succeed.
        await using var h1 = new FileStream(p1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var h2 = new FileStream(p2, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var res = await Call(Dispatcher(), "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        Assert.Null(Err(res));
        Assert.False(res.GetProperty("actionable").GetBoolean());
        Assert.All(res.GetProperty("members").EnumerateArray(),
            m => Assert.Equal("locked", m.GetProperty("eligibility").GetString()));
    }

    [Fact]
    public async Task Expired_session_group_reports_expired()
    {
        var (s, gid, _) = ThreeMemberGroup();
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q2"),
                SessionTtl: TimeSpan.FromMilliseconds(1)),
            new TrustedRoots().Add("t", _scope.Path));
        var d = new RequestDispatcher(_scans, executor, _journal, _finder, _scopes);
        await Task.Delay(10);
        var res = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        Assert.Equal("expired", res.GetProperty("sessionState").GetString());
        Assert.False(res.GetProperty("actionable").GetBoolean());
    }

    // ---------------- keeper / selection invariants ----------------

    [Fact]
    public async Task Each_member_can_be_the_explicit_keeper()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        foreach (var keeper in ids)
        {
            var quarantine = ids.Where(i => i != keeper).ToArray();
            Assert.Null(Err(await Call(d, "cleanup.preview",
                Prev(s.Id, gid, keeper, quarantine))));
        }
    }

    [Fact]
    public async Task Selecting_the_keeper_for_quarantine_is_rejected()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var res = await Call(d, "cleanup.preview",
            Prev(s.Id, gid, ids[0], ids)); // keeper inside selection
        Assert.Equal(Protocol.Errors.KeeperInvalid, Err(res));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(_scope.Path, "dup")).Length);
    }

    [Fact]
    public async Task Candidate_from_another_group_is_rejected()
    {
        var (s, gid, _) = ThreeMemberGroup();
        _scope.Write("o1.bin", "other-dup"); _scope.Write("o2.bin", "other-dup");
        var s2 = Run(); // rescan — two groups now
        var g1 = s2.DuplicateReport!.Groups.Single(g =>
            g.Candidates.All(c => c.DisplayName != "o1.bin" && c.DisplayName != "o2.bin"));
        var g2 = s2.DuplicateReport!.Groups.Single(g => g != g1);
        var d = Dispatcher();
        var res = await Call(d, "cleanup.preview", new
        {
            sessionId = s2.Id,
            candidateIds = g1.Candidates.Skip(1).Select(c => c.Id)
                .Concat(g2.Candidates.Skip(1).Select(c => c.Id)).ToArray(),
            duplicateGroupId = g1.GroupId,
            keeperId = g1.Candidates[0].Id,
        });
        Assert.Equal(Protocol.Errors.GroupMismatch, Err(res));
    }

    [Fact]
    public async Task Keeper_not_a_member_is_rejected()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        _scope.Write("x1.bin", "other-content");
        _scope.Write("x2.bin", "other-content");
        var s2 = Run();
        var groups = s2.DuplicateReport!.Groups;
        var d = Dispatcher();
        var res = await Call(d, "cleanup.preview", new
        {
            sessionId = s2.Id,
            candidateIds = groups[0].Candidates.Skip(1).Select(c => c.Id).ToArray(),
            duplicateGroupId = groups[0].GroupId,
            keeperId = groups[1].Candidates[0].Id, // member of the other group
        });
        Assert.Equal(Protocol.Errors.KeeperInvalid, Err(res));
    }

    [Fact]
    public async Task Keeperless_and_partial_group_params_are_rejected()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        // Any group-member selection without the binding is refused — an
        // implicit keeper is never inferred.
        Assert.Equal(Protocol.Errors.KeeperRequired, Err(await Call(d,
            "cleanup.preview", new { sessionId = s.Id, candidateIds = new[] { ids[1] } })));
        // Both fields required together.
        Assert.Equal(Protocol.Errors.InvalidParams, Err(await Call(d,
            "cleanup.preview", new
            { sessionId = s.Id, candidateIds = new[] { ids[1] }, duplicateGroupId = gid })));
        // Stale group id.
        Assert.Equal(Protocol.Errors.GroupNotFound, Err(await Call(d,
            "cleanup.preview", Prev(s.Id, "g99", ids[0], ids[1]))));
        // Unresolvable candidate id inside a group selection.
        Assert.Equal(Protocol.Errors.CandidateNotFound, Err(await Call(d,
            "cleanup.preview", Prev(s.Id, gid, ids[0], "ghost-id"))));
    }

    [Fact]
    public async Task Duplicate_candidate_ids_are_rejected_in_group_flow()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var res = await Call(d, "cleanup.preview",
            Prev(s.Id, gid, ids[0], ids[1], ids[1]));
        Assert.Equal(Protocol.Errors.InvalidParams, Err(res));
    }

    // ---------------- execution ----------------

    [Fact]
    public async Task End_to_end_review_keeper_commit_and_byte_identical_restore()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var detail = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        var members = detail.GetProperty("members").EnumerateArray().ToArray();
        var keeper = members[0].GetProperty("candidateId").GetString()!;
        var keeperPath = members[0].GetProperty("path").GetString()!;
        var chosen = members.Skip(1)
            .Select(m => m.GetProperty("candidateId").GetString()!).ToArray();
        var chosenPaths = members.Skip(1)
            .Select(m => m.GetProperty("path").GetString()!).ToArray();
        var content = File.ReadAllBytes(keeperPath);

        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(s.Id, gid, keeper, chosen))));
        var commit = await Call(d, "cleanup.commit",
            Prev(s.Id, gid, keeper, chosen));
        Assert.Null(Err(commit));
        Assert.All(commit.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(Protocol.Errors.ItemCleaned,
                i.GetProperty("status").GetString()));

        Assert.True(File.Exists(keeperPath));            // keeper untouched
        Assert.Equal(content, File.ReadAllBytes(keeperPath));
        Assert.All(chosenPaths, p => Assert.False(File.Exists(p)));

        // Restore both through the existing integrity-protected path.
        var history = await Call(d, "history.get", new { sessionId = s.Id });
        var ops = history.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("operationId").GetString()!).ToArray();
        Assert.Equal(2, ops.Length);
        var restore = await Call(d, "cleanup.restore", new { operationIds = ops });
        Assert.All(restore.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(Protocol.Errors.RestoredOk,
                i.GetProperty("status").GetString()));
        Assert.All(chosenPaths, p =>
            Assert.Equal(content, File.ReadAllBytes(p))); // byte-identical
    }

    [Fact]
    public async Task Restore_conflict_never_overwrites()
    {
        _scope.Write("k1.bin", "same"); var p2 = _scope.Write("k2.bin", "same");
        var s = Run();
        var g = s.DuplicateReport!.Groups.Single();
        var ids = g.Candidates.Select(c => c.Id).ToArray();
        var keeper = g.Candidates.Single(c => c.DisplayName == "k1.bin").Id;
        var victim = ids.Single(i => i != keeper);
        var d = Dispatcher();
        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(s.Id, g.GroupId, keeper, victim))));
        Assert.Null(Err(await Call(d, "cleanup.commit",
            Prev(s.Id, g.GroupId, keeper, victim))));
        File.WriteAllText(p2, "new occupant"); // a file appears at the source
        var op = _journal.Query(JournalState.Quarantined.ToString())
            .Single().OperationId;
        var restore = await Call(d, "cleanup.restore", new { operationIds = new[] { op } });
        Assert.Equal(Protocol.Errors.RestoreConflict,
            restore.GetProperty("items")[0].GetProperty("errorCode").GetString());
        Assert.Equal("new occupant", File.ReadAllText(p2));
    }

    [Fact]
    public async Task Commit_echo_must_match_the_previewed_binding()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(s.Id, gid, ids[0], ids[1]))));
        // Omitted echo.
        Assert.Equal(Protocol.Errors.ApprovalMismatch, Err(await Call(d,
            "cleanup.commit", new { sessionId = s.Id, candidateIds = new[] { ids[1] } })));
        // Different keeper echo.
        Assert.Equal(Protocol.Errors.ApprovalMismatch, Err(await Call(d,
            "cleanup.commit", Prev(s.Id, gid, ids[2], ids[1]))));
        // Matching echo commits.
        Assert.Null(Err(await Call(d, "cleanup.commit",
            Prev(s.Id, gid, ids[0], ids[1]))));
        Assert.True(File.Exists(Directory.GetFiles(
            Path.Combine(_scope.Path, "dup"), "a.bin")[0]));
    }

    [Fact]
    public async Task Keeper_changed_between_review_and_commit_fails_closed()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var detail = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        var keeperPath = detail.GetProperty("members")[0]
            .GetProperty("path").GetString()!;
        var keeper = detail.GetProperty("members")[0]
            .GetProperty("candidateId").GetString()!;
        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(s.Id, gid, keeper, ids[1]))));
        File.AppendAllText(keeperPath, "-mutated"); // keeper drifted
        var res = await Call(d, "cleanup.commit",
            Prev(s.Id, gid, keeper, ids[1]));
        Assert.Equal(Protocol.Errors.KeeperStale, Err(res));
        Assert.All(Directory.GetFiles(Path.Combine(_scope.Path, "dup")),
            p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task Candidate_changed_after_review_is_skipped_at_commit()
    {
        var (s, gid, ids) = ThreeMemberGroup();
        var d = Dispatcher();
        var detail = await Call(d, "duplicates.group",
            new { sessionId = s.Id, groupId = gid });
        var changed = detail.GetProperty("members")[1]
            .GetProperty("path").GetString()!;
        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(s.Id, gid, ids[0], ids[1]))));
        File.AppendAllText(changed, "-mutated");
        var commit = await Call(d, "cleanup.commit",
            Prev(s.Id, gid, ids[0], ids[1]));
        Assert.Null(Err(commit));
        Assert.Equal(Protocol.Errors.SkippedChanged,
            commit.GetProperty("items")[0].GetProperty("errorCode").GetString());
        Assert.True(File.Exists(changed));
    }

    [Fact]
    public async Task Expired_session_refuses_review_bound_commit()
    {
        _scope.Write("e1.bin", "same"); _scope.Write("e2.bin", "same");
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q3"),
                SessionTtl: TimeSpan.FromMilliseconds(150)),
            new TrustedRoots().Add("t", _scope.Path));
        var d = new RequestDispatcher(_scans, executor, _journal, _finder, _scopes);
        var s = Run();
        var g = s.DuplicateReport!.Groups.Single();
        var ids = g.Candidates.Select(c => c.Id).ToArray();
        await Task.Delay(300);
        Assert.Equal(Protocol.Errors.SessionExpired, Err(await Call(d,
            "cleanup.preview", Prev(s.Id, g.GroupId, ids[0], ids[1]))));
    }

    [Fact]
    public async Task Empty_selection_never_mutates()
    {
        var (s, gid, _) = ThreeMemberGroup();
        var d = Dispatcher();
        Assert.Equal(Protocol.Errors.InvalidParams, Err(await Call(d,
            "cleanup.preview", new
            { sessionId = s.Id, candidateIds = Array.Empty<string>(),
              duplicateGroupId = gid, keeperId = "x" })));
        Assert.Empty(_journal.Query((string?)null));
    }

    // ---------------- Smart Scan integration ----------------

    private async Task<(SmartScanCoordinator c, SmartScanSession s, SmartFinding f)>
        SmartDupRun()
    {
        _scope.Write("g/a.bin", "shared");
        _scope.Write("g/b.bin", "shared");
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new DuplicatesProducer(_scans, _finder) },
            TimeSpan.FromSeconds(30));
        var s = c.Start(new[] { "fixtures" });
        var end = DateTime.UtcNow.AddSeconds(15);
        while (s.Status == SmartScanStatus.Running && DateTime.UtcNow < end)
            await Task.Delay(25);
        Assert.Equal(SmartScanStatus.Completed, s.Status);
        var f = s.Findings.Single();
        return (c, s, f);
    }

    [Fact]
    public async Task Smart_finding_binds_all_members_and_marks_group()
    {
        var (_, s, f) = await SmartDupRun();
        Assert.True(f.Actionable);
        Assert.NotNull(f.GroupId);
        var child = s.ChildSessions.Single();
        Assert.Equal(child.Id, f.SessionId);
        // Every member is bound — the user chooses the keeper at review,
        // never the producer.
        Assert.Equal(2, f.CandidateIds!.Count);
        Assert.Equal(child.DuplicateReport!.Groups.Single().Candidates.Count,
            f.CandidateIds!.Count);
    }

    [Fact]
    public async Task Smart_finding_review_and_commit_through_same_pipeline()
    {
        var (c, s, f) = await SmartDupRun();
        var d = new RequestDispatcher(_scans, _executor, _journal,
            _finder, _scopes, smartScan: c);
        // Review detail resolves on the finding's child session.
        var detail = await Call(d, "duplicates.group",
            new { sessionId = f.SessionId!, groupId = f.GroupId! });
        Assert.Null(Err(detail));
        Assert.Equal(2, detail.GetProperty("members").GetArrayLength());
        var keeper = detail.GetProperty("suggestedKeeperId").GetString()!;
        var victim = detail.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("candidateId").GetString()!)
            .Single(id => id != keeper);
        // Keeperless commit of the finding's candidates is refused — a
        // bulk commit must never empty a group.
        Assert.Equal(Protocol.Errors.KeeperRequired, Err(await Call(d,
            "cleanup.preview",
            new { sessionId = f.SessionId!, candidateIds = f.CandidateIds })));
        Assert.Null(Err(await Call(d, "cleanup.preview",
            Prev(f.SessionId!, f.GroupId!, keeper, victim))));
        Assert.Null(Err(await Call(d, "cleanup.commit",
            Prev(f.SessionId!, f.GroupId!, keeper, victim))));
        Assert.Single(Directory.GetFiles(Path.Combine(_scope.Path, "g")));
    }

    [Fact]
    public async Task Cancelled_child_session_never_yields_actionable_dup_finding()
    {
        _scope.Write("c/a.bin", "shared");
        _scope.Write("c/b.bin", "shared");
        var c = new SmartScanCoordinator(new SmartScanStore(),
            new ISmartScanProducer[] { new DuplicatesProducer(_scans, _finder) },
            TimeSpan.FromSeconds(30));
        var s = c.Start(new[] { "fixtures" });
        c.Store.Cancel(s.Id); // cancel mid-run
        var end = DateTime.UtcNow.AddSeconds(15);
        while (s.Status == SmartScanStatus.Running && DateTime.UtcNow < end)
            await Task.Delay(25);
        // Either the run finished before the cancel landed (findings keep
        // honest 'live' state semantics) or its bindings were dropped —
        // never a cancelled session presented as executable.
        foreach (var f in s.Findings.Where(f => f.Actionable))
        {
            var child = s.ChildSessions.Single(x => x.Id == f.SessionId);
            Assert.False(child.Cancellation.IsCancellationRequested);
        }
    }
}
