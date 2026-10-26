using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// End-to-end IPC tests: real named pipe, real ACL, real dispatcher.
/// Each test uses a unique pipe name to isolate sessions.
/// </summary>
public sealed class IpcTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private readonly string _pipe = "cf-test-" + Guid.NewGuid().ToString("N");
    private readonly FixtureDir _fx = new();
    public IpcTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private PipeServerHost _host = null!;
    private ClearForge.Engine.Cleanup.CleanupJournal _journal = null!;
    private NamedPipeClientStream _client = null!;
    private readonly byte[] _buf = new byte[64 * 1024];

    public async Task InitializeAsync()
    {
        _fx.Write("old.tmp", "data", DateTimeOffset.UtcNow.AddDays(-2));
        var roots = new TrustedRoots().Add("t", _fx.Path);
        var rule = new CleanupRule("r1", 1, "temporary-files", "current-user", "t",
            24, "low", true, "quarantine", 100, true, false);
        var scans = new ScanSessionStore(new[] { rule }, new FileScanner(roots));
        _journal = new CleanupJournal(Path.Combine(_fx.Path, "journal.db"));
        var executor = new QuarantineExecutor(scans, _journal,
            new QuarantineOptions(Path.Combine(_fx.Path, "q")), roots);
        _host = new PipeServerHost(new RequestDispatcher(scans, executor, _journal), _pipe);
        _host.Start();

        _client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _client.ConnectAsync(10_000);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _journal.Dispose();
        await _host.DisposeAsync();
        _fx.Dispose();
    }

    private async Task<JsonElement> Call(object request)
    {
        var frame = JsonSerializer.Serialize(request) + "\n";
        await _client.WriteAsync(Encoding.UTF8.GetBytes(frame));
        var ms = new MemoryStream();
        while (true)
        {
            var n = await _client.ReadAsync(_buf);
            if (n == 0) throw new IOException("pipe closed");
            ms.Write(_buf, 0, n);
            var idx = ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf((byte)'\n');
            if (idx >= 0)
                return JsonDocument.Parse(ms.ToArray().AsSpan(0, idx).ToArray()).RootElement.Clone();
        }
    }

    private async Task<string> CallRaw(string rawLine)
    {
        await _client.WriteAsync(Encoding.UTF8.GetBytes(rawLine + "\n"));
        var ms = new MemoryStream();
        while (true)
        {
            var n = await _client.ReadAsync(_buf);
            if (n == 0) throw new IOException("pipe closed");
            ms.Write(_buf, 0, n);
            var idx = ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf((byte)'\n');
            if (idx >= 0) return Encoding.UTF8.GetString(ms.ToArray(), 0, idx);
        }
    }

    private static string? ErrorCode(JsonElement r) =>
        r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.GetProperty("code").GetString() : null;

    [Fact]
    public async Task Health_check_returns_ok()
    {
        var r = await Call(new { id = "1", method = "health.check" });
        Assert.Null(ErrorCode(r));
        Assert.Equal("ok", r.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(Protocol.Version, r.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var raw = await CallRaw("{not json");
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(Protocol.Errors.MalformedRequest, ErrorCode(doc.RootElement));
    }

    [Fact]
    public async Task Unknown_method_is_rejected()
    {
        var r = await Call(new { id = "2", method = "admin.formatDisk" });
        Assert.Equal(Protocol.Errors.UnknownMethod, ErrorCode(r));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_and_connection_closed()
    {
        var huge = "{\"id\":\"x\",\"method\":\"" + new string('a', Protocol.MaxMessageBytes) + "\"}";
        await _client.WriteAsync(Encoding.UTF8.GetBytes(huge));
        var ms = new MemoryStream();
        var n = await _client.ReadAsync(_buf);
        ms.Write(_buf, 0, n);
        Assert.Contains(Protocol.Errors.MessageTooLarge, Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Scan_preview_commit_flow()
    {
        var start = await Call(new { id = "a", method = "scan.start" });
        var sessionId = start.GetProperty("result").GetProperty("sessionId").GetString()!;

        // Poll until scan completes (bounded)
        JsonElement results = default;
        for (var i = 0; i < 100; i++)
        {
            results = await Call(new { id = "b", method = "scan.getResults", @params = new { sessionId } });
            if (results.GetProperty("result").GetProperty("status").GetString() == "completed") break;
            await Task.Delay(50);
        }
        _out.WriteLine("results: " + results.GetRawText());
        Assert.Equal("completed", results.GetProperty("result").GetProperty("status").GetString());
        var candidates = results.GetProperty("result").GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() >= 1);
        var cid = candidates[0].GetProperty("id").GetString()!;

        var preview = await Call(new { id = "c", method = "cleanup.preview",
            @params = new { sessionId, candidateIds = new[] { cid } } });
        Assert.Null(ErrorCode(preview));
        Assert.Equal(1, preview.GetProperty("result").GetProperty("candidateCount").GetInt32());

        // M2: commit now quarantines (move, never delete).
        var commit = await Call(new { id = "d", method = "cleanup.commit",
            @params = new { sessionId, candidateIds = new[] { cid } } });
        Assert.Null(ErrorCode(commit));
        var item = commit.GetProperty("result").GetProperty("items")[0];
        Assert.Equal(Protocol.Errors.ItemCleaned, item.GetProperty("status").GetString());
        Assert.False(File.Exists(_fx.Path + "\\old.tmp"), "source must move to quarantine");

        // Journal must carry the quarantined entry with an operation id.
        var history = await Call(new { id = "e", method = "history.get" });
        var entries = history.GetProperty("result").GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        var entry = entries.EnumerateArray()
            .First(e => e.GetProperty("candidateId").GetString() == cid);
        Assert.Equal("quarantined", entry.GetProperty("state").GetString());
        var opId = entry.GetProperty("operationId").GetString()!;

        // Restore over IPC.
        var restore = await Call(new { id = "f", method = "cleanup.restore",
            @params = new { operationIds = new[] { opId } } });
        Assert.Null(ErrorCode(restore));
        Assert.Equal(Protocol.Errors.RestoredOk,
            restore.GetProperty("result").GetProperty("items")[0]
                .GetProperty("status").GetString());
        Assert.True(File.Exists(_fx.Path + "\\old.tmp"), "restore must return the file");
    }

    [Fact]
    public async Task Arbitrary_path_injection_is_impossible()
    {
        // The API has no path surface; a hostile client sending a path gets a
        // structured refusal, not a deletion attempt.
        var r = await Call(new { id = "e", method = "cleanup.commit",
            @params = new { sessionId = "x", path = "C:\\Windows\\System32\\kernel32.dll" } });
        Assert.Equal(Protocol.Errors.SessionNotFound, ErrorCode(r));
        Assert.True(File.Exists(@"C:\Windows\System32\kernel32.dll"));
    }

    [Fact]
    public async Task Forged_candidate_id_is_rejected()
    {
        var start = await Call(new { id = "f", method = "scan.start" });
        var sessionId = start.GetProperty("result").GetProperty("sessionId").GetString()!;
        var r = await Call(new { id = "g", method = "cleanup.preview",
            @params = new { sessionId, candidateIds = new[] { "../../etc/passwd", "s:0" } } });
        Assert.Equal(Protocol.Errors.CandidateNotFound, ErrorCode(r));
    }

    [Fact]
    public async Task Unknown_session_is_rejected()
    {
        var r = await Call(new { id = "h", method = "scan.getResults",
            @params = new { sessionId = "nope" } });
        Assert.Equal(Protocol.Errors.SessionNotFound, ErrorCode(r));
    }
}
