using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearForge.Host;
using ClearForge.Models;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M7.2 install lifecycle against an injected transport — deterministic,
/// no live network. The loopback tests below run real sockets on
/// 127.0.0.1 with a tiny fixture artifact.
/// </summary>
public class M72InstallTests : IDisposable
{
    private readonly FixtureDir _root = new();
    private readonly List<IDisposable> _cleanup = new();

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        _root.Dispose();
    }

    // --- fixtures ---

    internal static byte[] FixtureBody(int size = 4096)
    {
        var b = new byte[size];
        b[0] = (byte)'G'; b[1] = (byte)'G'; b[2] = (byte)'U'; b[3] = (byte)'F';
        for (var i = 4; i < size; i++) b[i] = (byte)((i * 7) % 251);
        return b;
    }

    internal static string Sha256Hex(byte[] b) =>
        Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    internal static ModelCatalog CatalogFor(
        string url, long size, string sha,
        string hosts = "\"fixture.local\",\"*.fixture.local\"",
        string id = "test-model") => ModelCatalog.Parse($$"""
        {"catalogVersion":1,"models":[{
          "id": "{{id}}",
          "displayName": "Test Model",
          "publisher": "test",
          "repository": "test/repo",
          "revision": "0123456789abcdef0123456789abcdef01234567",
          "artifact": {
            "fileName": "test.gguf",
            "url": "{{url}}",
            "sha256": "{{sha}}",
            "sizeBytes": {{size}},
            "format": "gguf",
            "allowedRedirectHosts": [{{hosts}}]
          },
          "license": { "id": "apache-2.0", "url": "https://www.apache.org/licenses/LICENSE-2.0" },
          "runtime": { "backend": "test", "acceleration": "cpu", "architectures": ["x64"] },
          "requirements": { "minRamBytesEstimate": 1024, "minFreeStorageBytesEstimate": 2048, "estimatesAreApproximate": true },
          "supportStatus": "experimental",
          "qualityNotes": "fixture",
          "evaluation": null
        }]}
        """);

    internal const string ModelId = "test-model";

    private ModelManager Manager(
        ModelCatalog cat, IModelTransport t,
        Func<string, long?>? free = null,
        Func<string, FileMode, Stream>? openWrite = null) =>
        new(_root.Path, cat, t, free ?? (_ => long.MaxValue), openWrite);

    private static async Task<ModelManager.ModelState> WaitFor(
        ModelManager m, string id,
        Func<ModelManager.ModelState, bool> done, int ms = 10_000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            var s = m.StatusOf(id);
            if (done(s)) return s;
            await Task.Delay(10);
        }
        return m.StatusOf(id);
    }

    private ModelManager.ModelState AwaitTerminal(ModelManager m, string id)
        => WaitFor(m, id, s => s.State is ModelManager.States.Ready
            or ModelManager.States.Failed
            or ModelManager.States.Interrupted).GetAwaiter().GetResult();

    private string FinalPath => Path.Combine(_root.Path, "test.gguf");
    private string PartPath => Path.Combine(_root.Path, "tmp", "test-model.part");

    // --- scriptable transport ---

    internal sealed class FakeTransport : IModelTransport
    {
        public int Calls;
        public List<(Uri Uri, long RangeStart)> Requests = new();
        public Func<Uri, long, CancellationToken, Task<TransportResponse>>? Handler;

        public Task<TransportResponse> SendAsync(
            Uri uri, long rangeStart, CancellationToken ct)
        {
            Calls++;
            Requests.Add((uri, rangeStart));
            return Handler!(uri, rangeStart, ct);
        }

        public static TransportResponse Full(byte[] body) => new()
        {
            StatusCode = 200, ContentLength = body.Length,
            Body = new MemoryStream(body),
        };

        public static TransportResponse Partial(byte[] body, long start, long total) => new()
        {
            StatusCode = 206,
            ContentLength = body.Length,
            ContentRange = $"bytes {start}-{total - 1}/{total}",
            Body = new MemoryStream(body),
        };

        public static TransportResponse Redirect(string location) => new()
        {
            StatusCode = 302,
            RedirectLocation = new Uri(location, UriKind.RelativeOrAbsolute),
        };

        public static TransportResponse HttpError(int status) =>
            new() { StatusCode = status };
    }

    /// <summary>Body stream that yields a prefix then blocks until ct fires —
    /// deterministic mid-download cancellation point.</summary>
    private sealed class GateStream : Stream
    {
        private readonly byte[] _prefix;
        private int _pos;
        private readonly Task _gate;

        public GateStream(byte[] prefix, Task gate) { _prefix = prefix; _gate = gate; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _prefix.Length)
            {
                var n = Math.Min(_prefix.Length - _pos, buffer.Length);
                _prefix.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await _gate.WaitAsync(ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private static ModelCatalog DefaultCatalog(byte[] body) =>
        CatalogFor("https://fixture.local/repo/test.gguf", body.Length, Sha256Hex(body));

    // --- tests ---

    [Fact]
    public async Task Install_downloads_verifies_and_stages_atomically()
    {
        var body = FixtureBody();
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)) };
        var m = Manager(DefaultCatalog(body), t);

        var start = m.BeginInstall(ModelId);
        Assert.Equal(ModelManager.States.Downloading, start.State);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);

        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Equal(body.Length, end.InstalledBytes);
        Assert.True(File.Exists(FinalPath));
        Assert.Equal(body, await File.ReadAllBytesAsync(FinalPath));
        Assert.False(File.Exists(PartPath)); // staging cleaned up
    }

    [Fact]
    public async Task Wrong_sha_fails_and_removes_part_file()
    {
        var body = FixtureBody();
        var cat = CatalogFor("https://fixture.local/repo/test.gguf",
            body.Length, new string('0', 64));
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)) };
        var m = Manager(cat, t);

        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);

        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.False(File.Exists(FinalPath));
        Assert.False(File.Exists(PartPath));
    }

    [Fact]
    public async Task Truncated_response_is_interrupted_then_resumes()
    {
        var body = FixtureBody();
        var prefix = body[..2000];
        var rest = body[2000..];
        var t = new FakeTransport
        {
            Handler = (uri, range, ct) => Task.FromResult(range == 0
                ? new TransportResponse
                {
                    StatusCode = 200, ContentLength = body.Length,
                    Body = new MemoryStream(prefix),   // lies about length, ends early
                }
                : FakeTransport.Partial(rest, range, body.Length)),
        };
        var m = Manager(DefaultCatalog(body), t);

        m.BeginInstall(ModelId);
        var mid = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Interrupted);
        Assert.Equal(2000, mid.DownloadedBytes);
        Assert.True(File.Exists(PartPath));

        m.BeginInstall(ModelId); // resume
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);
        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Equal(body, await File.ReadAllBytesAsync(FinalPath));
        Assert.Equal(2000, t.Requests[1].RangeStart);
    }

    [Fact]
    public async Task Server_ignoring_range_restarts_download_cleanly()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            // 200 on a ranged request = resume refused → full restart.
            Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)),
        };
        var m = Manager(DefaultCatalog(body), t);
        Directory.CreateDirectory(Path.GetDirectoryName(PartPath)!);
        await File.WriteAllBytesAsync(PartPath, body[..2000]);

        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);
        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Equal(body, await File.ReadAllBytesAsync(FinalPath));
        Assert.Single(t.Requests.Where(r => r.RangeStart == 2000));
    }

    [Fact]
    public async Task Invalid_resume_range_is_rejected()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            // Server claims a different resume offset — cannot be trusted.
            Handler = (_, range, _) => Task.FromResult(
                FakeTransport.Partial(body[(int)range..], range - 1, body.Length)),
        };
        var m = Manager(DefaultCatalog(body), t);
        Directory.CreateDirectory(Path.GetDirectoryName(PartPath)!);
        await File.WriteAllBytesAsync(PartPath, body[..2000]);

        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.False(File.Exists(FinalPath));
    }

    [Fact]
    public async Task Redirect_to_allowed_host_succeeds()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (uri, _, _) => Task.FromResult(uri.Host == "fixture.local"
                ? FakeTransport.Redirect("https://cdn.fixture.local/test.gguf")
                : FakeTransport.Full(body)),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);
        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Equal(2, t.Calls);
    }

    [Fact]
    public async Task Redirect_to_http_is_refused()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(
                FakeTransport.Redirect("http://fixture.local/test.gguf")),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.Contains("policy_refused", end.Error);
        Assert.Single(t.Requests); // refused before following
    }

    [Fact]
    public async Task Redirect_to_unapproved_host_is_refused()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(
                FakeTransport.Redirect("https://evil.example/test.gguf")),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.Contains("unapproved host", end.Error);
    }

    [Fact]
    public async Task Redirect_loop_fails_bounded()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(
                FakeTransport.Redirect("https://fixture.local/loop")),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.True(t.Calls <= 6);
    }

    [Fact]
    public async Task Oversized_content_length_fails_before_writing()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(new TransportResponse
            {
                StatusCode = 200, ContentLength = body.Length + 1,
                Body = new MemoryStream(body),
            }),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.False(File.Exists(PartPath));
    }

    [Fact]
    public async Task Body_longer_than_pinned_size_aborts()
    {
        var body = FixtureBody();
        var bigger = body.Concat(new byte[] { 1 }).ToArray();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(new TransportResponse
            {
                StatusCode = 200, ContentLength = null,
                Body = new MemoryStream(bigger),
            }),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.False(File.Exists(PartPath));
        Assert.False(File.Exists(FinalPath));
    }

    [Fact]
    public async Task Non_gguf_content_is_refused()
    {
        var body = FixtureBody();
        body[0] = (byte)'X';
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)) };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.Contains("GGUF", end.Error);
    }

    [Fact]
    public async Task Http_error_status_fails()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(FakeTransport.HttpError(503)),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
    }

    [Fact]
    public async Task Cancel_mid_download_keeps_resumable_part()
    {
        var body = FixtureBody();
        var gate = new TaskCompletionSource();
        var t = new FakeTransport
        {
            Handler = (uri, range, ct) => Task.FromResult(new TransportResponse
            {
                StatusCode = 200, ContentLength = body.Length,
                Body = new GateStream(body[..512], gate.Task),
            }),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        await WaitFor(m, ModelId, s => s.DownloadedBytes >= 512);

        Assert.True(m.Cancel(ModelId));
        var mid = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Interrupted);
        Assert.Equal(ModelManager.States.Interrupted, mid.State);
        Assert.True(File.Exists(PartPath));
        Assert.True(new FileInfo(PartPath).Length > 0);
    }

    [Fact]
    public async Task Concurrent_install_is_refused()
    {
        var body = FixtureBody();
        var gate = new TaskCompletionSource();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(new TransportResponse
            {
                StatusCode = 200, ContentLength = body.Length,
                Body = new GateStream(body[..64], gate.Task),
            }),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var ex = Assert.Throws<ModelException>(() => m.BeginInstall(ModelId));
        Assert.Equal("model_busy", ex.Code);
        m.Cancel(ModelId);
        await WaitFor(m, ModelId, s => s.State == ModelManager.States.Interrupted);
    }

    [Fact]
    public async Task Insufficient_storage_fails_without_network()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)),
        };
        var m = Manager(DefaultCatalog(body), t, free: _ => 0);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        Assert.Contains("insufficient_storage", end.Error);
        Assert.Equal(0, t.Calls); // never touched the network
    }

    [Fact]
    public async Task Write_failure_marks_interrupted_not_crashed()
    {
        var body = FixtureBody();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)),
        };
        var m = Manager(DefaultCatalog(body), t,
            openWrite: (p, mode) => new FailingWriteStream());
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Interrupted);
        Assert.Equal(ModelManager.States.Interrupted, end.State);
        Assert.Contains("disk write failed", end.Error);
    }

    private sealed class FailingWriteStream : Stream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => throw new IOException("simulated disk failure");
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) => throw new IOException("simulated disk failure");
    }

    [Fact]
    public async Task Already_ready_install_is_a_noop()
    {
        var body = FixtureBody();
        var calls = 0;
        var t = new FakeTransport
        {
            Handler = (_, _, _) => { calls++; return Task.FromResult(FakeTransport.Full(body)); },
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);

        var again = m.BeginInstall(ModelId);
        Assert.Equal(ModelManager.States.Ready, again.State);
        Assert.Equal(1, calls); // explicit reinstall does not re-download
    }

    [Fact]
    public async Task Failed_download_never_touches_existing_artifact()
    {
        var body = FixtureBody();
        var stale = FixtureBody(); // same size, will produce wrong bytes
        stale[10] ^= 0xFF;
        Directory.CreateDirectory(_root.Path);
        await File.WriteAllBytesAsync(FinalPath, stale); // pre-existing file

        var bad = FixtureBody(); bad[20] ^= 0xFF; // server serves corrupt bytes
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(bad)) };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var end = await WaitFor(m, ModelId, s => s.State == ModelManager.States.Failed);
        Assert.Equal(ModelManager.States.Failed, end.State);
        // The pre-existing file is byte-identical — never swapped mid-verify.
        Assert.Equal(stale, await File.ReadAllBytesAsync(FinalPath));
    }

    [Fact]
    public async Task Remove_deletes_owned_artifact_and_part()
    {
        var body = FixtureBody();
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)) };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);

        var after = m.Remove(ModelId);
        Assert.Equal(ModelManager.States.NotInstalled, after.State);
        Assert.False(File.Exists(FinalPath));
        Assert.False(File.Exists(PartPath));
    }

    [Fact]
    public void Remove_refuses_unknown_or_path_shaped_ids()
    {
        var body = FixtureBody();
        var m = Manager(DefaultCatalog(body), new FakeTransport());
        Assert.Throws<ModelException>(() => m.Remove("../escape"));
        Assert.Throws<ModelException>(() => m.Remove("..\\..\\windows"));
        Assert.Throws<ModelException>(() => m.Remove("not-a-model"));
    }

    [Fact]
    public async Task Remove_while_download_active_is_refused()
    {
        var body = FixtureBody();
        var gate = new TaskCompletionSource();
        var t = new FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(new TransportResponse
            {
                StatusCode = 200, ContentLength = body.Length,
                Body = new GateStream(body[..64], gate.Task),
            }),
        };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        var ex = Assert.Throws<ModelException>(() => m.Remove(ModelId));
        Assert.Equal("model_busy", ex.Code);
        m.Cancel(ModelId);
        await WaitFor(m, ModelId, s => s.State == ModelManager.States.Interrupted);
    }

    [Fact]
    public async Task Remove_locked_file_reports_in_use()
    {
        var body = FixtureBody();
        var t = new FakeTransport { Handler = (_, _, _) => Task.FromResult(FakeTransport.Full(body)) };
        var m = Manager(DefaultCatalog(body), t);
        m.BeginInstall(ModelId);
        await WaitFor(m, ModelId, s => s.State == ModelManager.States.Ready);

        // Simulate the AI worker holding the artifact open without delete share.
        await using var held = new FileStream(FinalPath, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var ex = Assert.Throws<ModelException>(() => m.Remove(ModelId));
        Assert.Equal("model_in_use", ex.Code);
    }

    [Fact]
    public void Status_never_downloads_and_reports_not_installed()
    {
        var body = FixtureBody();
        var t = new FakeTransport();
        var m = Manager(DefaultCatalog(body), t);
        var states = m.Status();
        var s = Assert.Single(states);
        Assert.Equal(ModelManager.States.NotInstalled, s.State);
        Assert.Equal(0, t.Calls); // status is read-only, never a download trigger
    }

    [Fact]
    public void Path_shaped_install_ids_are_unknown_not_traversed()
    {
        var body = FixtureBody();
        var m = Manager(DefaultCatalog(body), new FakeTransport());
        Assert.Throws<ModelException>(() => m.BeginInstall("../../etc"));
        Assert.Throws<ModelException>(() => m.BeginInstall("test-model/.."));
    }

    [Fact]
    public async Task Hand_dropped_artifact_is_verified_before_ready()
    {
        var body = FixtureBody();
        var t = new FakeTransport();
        var m = Manager(DefaultCatalog(body), t);
        await File.WriteAllBytesAsync(FinalPath, body); // the M7.1 dev flow
        var s = m.StatusOf(ModelId);
        Assert.Equal(ModelManager.States.Ready, s.State);
        Assert.Equal(0, t.Calls);

        var wrong = FixtureBody(); wrong[5] ^= 1;
        await File.WriteAllBytesAsync(FinalPath, wrong);
        File.SetLastWriteTimeUtc(FinalPath, DateTime.UtcNow.AddMinutes(1));
        var bad = m.StatusOf(ModelId);
        Assert.Equal(ModelManager.States.Failed, bad.State);
    }
}

/// <summary>
/// Windows integration test: real TCP loopback, real HttpClient transport,
/// real NTFS install — tiny fixture artifact only, no live external service.
/// </summary>
public class M72LoopbackTests : IDisposable
{
    private readonly FixtureDir _root = new();
    private readonly MiniHttpServer _server;
    private readonly List<IDisposable> _cleanup = new();

    public M72LoopbackTests() { _server = new MiniHttpServer(); _cleanup.Add(_server); }

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Maps the catalogue's https://fixture.local endpoints onto the real
    /// loopback server, keeping the production catalogue schema (HTTPS-only,
    /// host-allowlisted) fully intact while still exercising real sockets,
    /// real Range headers and real redirects.
    /// </summary>
    private sealed class LoopbackTransport : IModelTransport
    {
        private readonly HttpModelTransport _inner = new(permitPlainHttp: true);
        private readonly int _port;
        public LoopbackTransport(int port) => _port = port;

        public Task<TransportResponse> SendAsync(
            Uri uri, long rangeStart, CancellationToken ct) =>
            _inner.SendAsync(new UriBuilder(uri)
            {
                Scheme = "http", Host = "127.0.0.1", Port = _port,
            }.Uri, rangeStart, ct);
    }

    private ModelManager Manager(byte[] body, string urlPath)
    {
        var cat = M72InstallTests.CatalogFor(
            $"https://fixture.local{urlPath}", body.Length,
            M72InstallTests.Sha256Hex(body));
        return new ModelManager(_root.Path, cat,
            new LoopbackTransport(_server.Port),
            freeSpaceProbe: _ => long.MaxValue);
    }

    private async Task<ModelManager.ModelState> WaitFor(ModelManager m)
    {
        for (var i = 0; i < 1000; i++)
        {
            var s = m.StatusOf(M72InstallTests.ModelId);
            if (s.State is ModelManager.States.Ready or ModelManager.States.Failed
                or ModelManager.States.Interrupted)
                return s;
            await Task.Delay(10);
        }
        return m.StatusOf(M72InstallTests.ModelId);
    }

    [Fact]
    public async Task Real_http_install_verify_and_remove()
    {
        var body = M72InstallTests.FixtureBody();
        _server.Handler = (path, range) =>
            path == "/m.gguf" ? MiniHttpServer.Ok(body) : MiniHttpServer.NotFound();
        var m = Manager(body, "/m.gguf");

        m.BeginInstall(M72InstallTests.ModelId);
        var end = await WaitFor(m);
        Assert.Equal(ModelManager.States.Ready, end.State);

        var file = Path.Combine(_root.Path, "test.gguf");
        Assert.True(File.Exists(file));
        Assert.Equal(M72InstallTests.Sha256Hex(body),
            M72InstallTests.Sha256Hex(await File.ReadAllBytesAsync(file)));

        var after = m.Remove(M72InstallTests.ModelId);
        Assert.Equal(ModelManager.States.NotInstalled, after.State);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Real_http_redirect_then_install()
    {
        var body = M72InstallTests.FixtureBody();
        _server.Handler = (path, range) => path switch
        {
            "/start" => MiniHttpServer.Redirect("/final.gguf"),
            "/final.gguf" => MiniHttpServer.Ok(body),
            _ => MiniHttpServer.NotFound(),
        };
        var m = Manager(body, "/start");
        m.BeginInstall(M72InstallTests.ModelId);
        var end = await WaitFor(m);
        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Equal(2, _server.Requests);
    }

    [Fact]
    public async Task Real_http_truncated_then_resumed_with_range()
    {
        var body = M72InstallTests.FixtureBody();
        var half = body[..2000];
        _server.Handler = (path, range) => range is null
            // Advertise full length, close after half — truncated response.
            ? MiniHttpServer.Ok(half, advertisedLength: body.Length)
            : MiniHttpServer.Partial(body, long.Parse(range));
        var m = Manager(body, "/m.gguf");

        m.BeginInstall(M72InstallTests.ModelId);
        var mid = await WaitFor(m);
        Assert.Equal(ModelManager.States.Interrupted, mid.State);

        m.BeginInstall(M72InstallTests.ModelId); // resume
        var end = await WaitFor(m);
        Assert.Equal(ModelManager.States.Ready, end.State);
        Assert.Contains(_server.Ranges, r => r == 2000);
    }

    /// <summary>Minimal HTTP/1.1 server on a real socket — no URL ACLs,
    /// no admin, deterministic bytes.</summary>
    private sealed class MiniHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public int Port { get; }
        public int Requests;
        public List<long> Ranges = new();
        public Func<string, string?, (int Status, Dictionary<string, string> Headers, byte[] Body)>?
            Handler;

        public MiniHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public static (int, Dictionary<string, string>, byte[]) Ok(
            byte[] body, long? advertisedLength = null) =>
            (200, new Dictionary<string, string>
            {
                ["Content-Length"] = (advertisedLength ?? body.Length).ToString(),
            }, body);

        public static (int, Dictionary<string, string>, byte[]) Partial(
            byte[] full, long start)
        {
            var slice = full[(int)start..];
            return (206, new Dictionary<string, string>
            {
                ["Content-Length"] = slice.Length.ToString(),
                ["Content-Range"] = $"bytes {start}-{full.Length - 1}/{full.Length}",
            }, slice);
        }

        public static (int, Dictionary<string, string>, byte[]) Redirect(string to) =>
            (302, new Dictionary<string, string> { ["Location"] = to },
                Array.Empty<byte>());

        public static (int, Dictionary<string, string>, byte[]) NotFound() =>
            (404, new Dictionary<string, string>(), Array.Empty<byte>());

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                _ = Task.Run(() => HandleAsync(c));
            }
        }

        private async Task HandleAsync(TcpClient c)
        {
            using (c)
            {
                var stream = c.GetStream();
                // Read request head (requests here are tiny).
                var head = new MemoryStream();
                var buf = new byte[4096];
                while (true)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n == 0) return;
                    head.Write(buf, 0, n);
                    if (Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length)
                        .Contains("\r\n\r\n")) break;
                    if (head.Length > 16384) return;
                }
                var text = Encoding.ASCII.GetString(head.ToArray());
                var lines = text.Split("\r\n");
                var path = lines[0].Split(' ')[1];
                string? range = null;
                foreach (var l in lines)
                    if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = l[6..].Trim()["bytes=".Length..].Split('-')[0];

                Requests++;
                if (range is not null) Ranges.Add(long.Parse(range));

                var h = Handler?.Invoke(path, range) ?? NotFound();
                var sb = new StringBuilder($"HTTP/1.1 {h.Status} X\r\n");
                foreach (var (k, v) in h.Headers) sb.Append($"{k}: {v}\r\n");
                if (!h.Headers.ContainsKey("Content-Length"))
                    sb.Append($"Content-Length: {h.Body.Length}\r\n");
                sb.Append("Connection: close\r\n\r\n");
                var hb = Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync(hb);
                await stream.WriteAsync(h.Body);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}

/// <summary>
/// Wire-surface tests: the models.* methods exist on the engine dispatcher,
/// validate params, and never interfere with cleanup operations.
/// </summary>
public class M72DispatcherTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions Camel = new()
        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private readonly FixtureDir _root = new();
    public void Dispose() => _root.Dispose();

    private RequestDispatcher Dispatcher(ModelManager? m) =>
        new(new ClearForge.Engine.Scanning.ScanSessionStore(
                Array.Empty<ClearForge.Engine.Policies.CleanupRule>(),
                new ClearForge.Engine.Scanning.FileScanner(
                    new ClearForge.Engine.Policies.TrustedRoots())),
            models: m);

    private ModelManager NewManager() => new(_root.Path,
        ModelCatalog.LoadBundled(), new M72InstallTests.FakeTransport
        {
            Handler = (_, _, _) => Task.FromResult(
                M72InstallTests.FakeTransport.HttpError(503)),
        }, freeSpaceProbe: _ => long.MaxValue);

    private static async Task<JsonElement> Call(
        RequestDispatcher d, string method, string? json = null)
    {
        var res = await d.Dispatch(method,
            json is null ? null : JsonDocument.Parse(json).RootElement);
        return JsonSerializer.SerializeToElement(
            res.Result ?? new { error = res.Error, message = res.Message }, Camel);
    }

    [Fact]
    public async Task Catalog_lists_curated_model_with_lifecycle_state()
    {
        var res = await Call(Dispatcher(NewManager()), "models.catalog");
        Assert.Equal(1, res.GetProperty("catalogVersion").GetInt32());
        var m = res.GetProperty("models").EnumerateArray().Single();
        Assert.Equal("qwen2.5-1.5b-instruct-q4_k_m", m.GetProperty("id").GetString());
        Assert.Equal("apache-2.0", m.GetProperty("license").GetProperty("id").GetString());
        Assert.Equal("not_installed",
            m.GetProperty("state").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Install_requires_a_catalogue_model_id()
    {
        var d = Dispatcher(NewManager());
        var noParam = await Call(d, "models.install", "{}");
        Assert.Equal("invalid_params", noParam.GetProperty("error").GetString());
        var bad = await Call(d, "models.install", "{\"modelId\":\"../../evil\"}");
        Assert.Equal("model_unknown", bad.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Failed_install_does_not_affect_engine_surface()
    {
        var d = Dispatcher(NewManager());
        await Call(d, "models.install",
            "{\"modelId\":\"qwen2.5-1.5b-instruct-q4_k_m\"}");
        // Engine remains healthy; unrelated methods untouched by the failure.
        var h = await Call(d, "health.check");
        Assert.Equal("ok", h.GetProperty("status").GetString());
        var scan = await Call(d, "scan.start");
        Assert.True(scan.GetProperty("sessionId").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Models_surface_absent_when_not_configured()
    {
        var res = await Call(Dispatcher(null), "models.catalog");
        Assert.Equal("policy_refused", res.GetProperty("error").GetString());
    }
}
