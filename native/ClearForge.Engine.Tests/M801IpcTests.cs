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
/// M8.0.1 pipe-level regression: a cancel frame sent on the SAME connection
/// while a commit is in-flight must be dispatched concurrently — the serial
/// read loop must not hold it behind the blocking commit.
/// </summary>
public sealed class M801IpcTests : IAsyncLifetime
{
    private readonly string _pipe = "cf-m801-" + Guid.NewGuid().ToString("N");
    private readonly FixtureDir _fx = new();
    private readonly FixtureDir _store = new();

    private PipeServerHost _host = null!;
    private CleanupJournal _journal = null!;
    private ScanSessionStore _scans = null!;
    private NamedPipeClientStream _client = null!;
    private readonly byte[] _buf = new byte[64 * 1024];

    public async Task InitializeAsync()
    {
        var roots = new TrustedRoots().Add("t", _fx.Path);
        var rule = new CleanupRule("r", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 1000, true, false);
        _scans = new ScanSessionStore(new[] { rule }, new FileScanner(roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "j.db"));
        var executor = new QuarantineExecutor(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), roots);
        _host = new PipeServerHost(new RequestDispatcher(_scans, executor, _journal), _pipe);
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
        _store.Dispose();
    }

    private async Task Send(object request)
    {
        var frame = JsonSerializer.Serialize(request) + "\n";
        await _client.WriteAsync(Encoding.UTF8.GetBytes(frame));
        await _client.FlushAsync();
    }

    private async Task<JsonElement> ReadResponse(string? wantId = null)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var n = await _client.ReadAsync(_buf);
            if (n == 0) throw new IOException("pipe closed");
            ms.Write(_buf, 0, n);
            var idx = ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf((byte)'\n');
            if (idx < 0) continue;
            var doc = JsonDocument.Parse(ms.ToArray().AsSpan(0, idx).ToArray()).RootElement.Clone();
            if (wantId is null || doc.GetProperty("id").GetString() == wantId)
                return doc;
            // Not the response we're waiting for — keep only the remainder.
            var rest = ms.ToArray().AsSpan(idx + 1).ToArray();
            ms.SetLength(0);
            ms.Write(rest);
        }
    }

    [Fact]
    public async Task Cancel_on_same_connection_is_honored_mid_commit()
    {
        for (var i = 0; i < 3; i++)
            _fx.Write($"f{i}.tmp", $"data-{i}", DateTimeOffset.UtcNow.AddDays(-2));

        var s = _scans.CreateExternal();
        _scans.Populate(s);
        var ids = s.Items.Select(i => i.Public.Id).ToArray();
        Assert.Equal(3, ids.Length);

        // Gate closed → commit blocks inside the first item's CommitGuard.
        var gate = new ManualResetEventSlim(true);
        s.CommitGuard = () => { gate.Wait(TimeSpan.FromSeconds(30)); return null; };

        // Preview first (approval gate) — gate open, guard returns fast.
        await Send(new { id = "p", method = "cleanup.preview",
            @params = new { sessionId = s.Id, candidateIds = ids } });
        var preview = await ReadResponse("p");
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("error").ValueKind);

        // Commit — blocks at the first item's guard once the gate closes.
        gate.Reset();
        await Send(new { id = "c", method = "cleanup.commit",
            @params = new { sessionId = s.Id, candidateIds = ids } });
        await Task.Delay(300); // let dispatch reach the blocked guard

        // Cancel on the SAME connection: serial dispatch would queue this
        // behind the blocked commit; concurrent dispatch must answer now.
        await Send(new { id = "x", method = "cleanup.cancel",
            @params = new { sessionId = s.Id } });
        var cancelAck = await ReadResponse("x");
        Assert.True(cancelAck.GetProperty("error").ValueKind == JsonValueKind.Null,
            "cancel must be processed while commit is in-flight");

        gate.Set();
        var commit = await ReadResponse("c");
        var items = commit.GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Single(items, i => i.GetProperty("status").GetString()
            == Protocol.Errors.ItemCleaned);
        Assert.Equal(2, items.Count(i => i.GetProperty("errorCode").GetString()
            == Protocol.Errors.Cancelled));
    }
}
