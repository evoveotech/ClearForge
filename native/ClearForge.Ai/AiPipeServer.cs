using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClearForge.Contracts;

namespace ClearForge.Ai;

/// <summary>
/// Local NDJSON host for the AI worker. Mirrors PipeServerHost's security
/// posture exactly: ACL restricted to the creating user SID, newline-delimited
/// UTF-8 frames, 1 MiB cap, strict request validation before dispatch.
/// </summary>
public sealed class AiPipeServer : IAsyncDisposable
{
    public const string DefaultPipeName = "clearforge.ai.v1";

    private readonly string _pipeName;
    private readonly AiDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private Task? _acceptLoop;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AiPipeServer(AiDispatcher dispatcher, string pipeName = DefaultPipeName)
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
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, BuildAcl());
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }

            lock (_connections)
                _connections.Add(Task.Run(() => HandleConnectionAsync(pipe)));
            }
        }
        catch (Exception ex)
        {
            // A faulted accept loop must never fail silently — report and exit
            // so the client sees a worker that is actually serving or gone.
            Console.Error.WriteLine($"accept-loop-fatal: {ex.GetType().Name}: {ex.Message}");
            Environment.Exit(2);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            var readBuf = new MemoryStream();
            var byteBuf = new byte[8192];
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
                    await WriteFrame(pipe, JsonSerializer.Serialize(
                        RpcResponse.Fail("?", Protocol.Errors.MessageTooLarge,
                            "frame exceeds 1 MiB cap"), Json));
                    return;
                }

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
                        var response = await DispatchFrame(line);
                        await WriteFrame(pipe, response);
                    }
                }
                if (consumed > 0)
                {
                    var rest = readBuf.ToArray().AsSpan(consumed).ToArray();
                    readBuf.SetLength(0);
                    readBuf.Write(rest);
                }
            }
        }
    }

    private async Task<string> DispatchFrame(byte[] utf8Json)
    {
        RpcRequest? req;
        try { req = JsonSerializer.Deserialize<RpcRequest>(utf8Json); }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(
                RpcResponse.Fail("?", Protocol.Errors.MalformedRequest, "invalid JSON"), Json);
        }
        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Method))
            return JsonSerializer.Serialize(
                RpcResponse.Fail(req?.Id ?? "?", Protocol.Errors.MalformedRequest,
                    "id and method are required"), Json);
        if (req.Id.Length > 128 || req.Method.Length > 128)
            return JsonSerializer.Serialize(
                RpcResponse.Fail(req.Id, Protocol.Errors.MalformedRequest, "field length exceeded"), Json);

        var res = await _dispatcher.Dispatch(req.Method, req.Params);
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
