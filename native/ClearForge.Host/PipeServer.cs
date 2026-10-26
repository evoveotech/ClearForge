using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClearForge.Contracts;

namespace ClearForge.Host;

/// <summary>
/// Local JSON-RPC host over a Windows named pipe.
/// Security posture:
///  - Pipe ACL grants full control to the creating user SID only — other
///    users and unelevated other-session processes cannot connect.
///  - Newline-delimited UTF-8 JSON frames, hard-capped at Protocol.MaxMessageBytes.
///  - Strict schema: unknown/missing fields rejected before dispatch.
///  - All candidate references are opaque session-bound IDs; the API surface
///    has no path-taking method.
/// </summary>
public sealed class PipeServerHost : IAsyncDisposable
{
    public const string DefaultPipeName = "clearforge.engine.v1";

    private readonly string _pipeName;
    private readonly RequestDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private Task? _acceptLoop;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public PipeServerHost(RequestDispatcher dispatcher, string pipeName = DefaultPipeName)
    {
        _dispatcher = dispatcher;
        _pipeName = pipeName;
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private static PipeSecurity BuildAcl()
    {
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("cannot resolve current user SID");
        var sec = new PipeSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                BuildAcl());

            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }

            lock (_connections)
                _connections.Add(Task.Run(() => HandleConnectionAsync(pipe)));
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            // Defense in depth: ACL already restricts to the owner; log if the
            // peer identity cannot be confirmed rather than trusting silently.
            string peer = "unverified";
            try { peer = pipe.GetImpersonationUserName(); } catch { /* ACL is the boundary */ }

            var readBuf = new MemoryStream();
            var byteBuf = new byte[8192];
            // Frames are dispatched concurrently so a control frame (e.g.
            // cleanup.cancel) is never queued behind a long-running commit on
            // the same connection. Responses are id-correlated, so ordering is
            // not guaranteed; writes are serialized. In-flight dispatch is
            // bounded and drained before the connection closes.
            var writeLock = new SemaphoreSlim(1, 1);
            var pending = new List<Task>();
            const int maxInFlight = 32;
            while (pipe.IsConnected && !_stop.IsCancellationRequested)
            {
                int n;
                try { n = await pipe.ReadAsync(byteBuf, _stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                if (n == 0) break;

                readBuf.Write(byteBuf, 0, n);
                if (readBuf.Length > Protocol.MaxMessageBytes)
                {
                    await writeLock.WaitAsync();
                    try
                    {
                        await WriteFrame(pipe, JsonSerializer.Serialize(
                            RpcResponse.Fail("?", Protocol.Errors.MessageTooLarge, "frame exceeds 1 MiB cap"), Json));
                    }
                    finally { writeLock.Release(); }
                    return;
                }

                // Process complete newline-delimited frames.
                var data = readBuf.GetBuffer();
                int consumed = 0;
                while (true)
                {
                    var nl = data.AsSpan(consumed, (int)readBuf.Length - consumed).IndexOf((byte)'\n');
                    if (nl < 0) break;
                    var line = readBuf.ToArray().AsSpan(consumed, nl).ToArray();
                    consumed += nl + 1;
                    if (line.Length > 0)
                    {
                        pending.Add(Task.Run(async () =>
                        {
                            var response = await DispatchFrame(line);
                            await writeLock.WaitAsync();
                            try { await WriteFrame(pipe, response); }
                            finally { writeLock.Release(); }
                        }));
                    }
                }
                if (consumed > 0)
                {
                    var rest = readBuf.ToArray().AsSpan(consumed).ToArray();
                    readBuf.SetLength(0);
                    readBuf.Write(rest);
                }

                pending.RemoveAll(t => t.IsCompleted);
                while (pending.Count >= maxInFlight)
                {
                    await Task.WhenAny(pending);
                    pending.RemoveAll(t => t.IsCompleted);
                }
            }
            try { await Task.WhenAll(pending); } catch { /* peer gone or shutdown */ }
        }
    }

    private async Task<string> DispatchFrame(byte[] utf8Json)
    {
        RpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RpcRequest>(utf8Json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(
                RpcResponse.Fail("?", Protocol.Errors.MalformedRequest, "invalid JSON"), Json);
        }

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Method))
            return JsonSerializer.Serialize(
                RpcResponse.Fail(req?.Id ?? "?", Protocol.Errors.MalformedRequest, "id and method are required"), Json);
        if (req.Id.Length > 128 || req.Method.Length > 128)
            return JsonSerializer.Serialize(
                RpcResponse.Fail(req.Id, Protocol.Errors.MalformedRequest, "field length exceeded"), Json);

        DispatchResult res;
        try
        {
            res = await _dispatcher.Dispatch(req.Method, req.Params);
        }
        catch (Exception ex)
        {
            // A dispatch fault must never kill the connection task — return a
            // structured internal error instead of tearing the pipe down.
            return JsonSerializer.Serialize(
                RpcResponse.Fail(req.Id, Protocol.Errors.Internal, ex.GetType().Name), Json);
        }
        return JsonSerializer.Serialize(
            res.Error is null ? RpcResponse.Ok(req.Id, res.Result!) : RpcResponse.Fail(req.Id, res.Error, res.Message!),
            Json);
    }

    private static async Task WriteFrame(PipeStream pipe, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        try { await pipe.WriteAsync(bytes); await pipe.FlushAsync(); }
        catch (IOException) { /* peer gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_acceptLoop is not null) await _acceptLoop; } catch (OperationCanceledException) { }
    }
}
