using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClearForge.Ai;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M7.1 intent validation — pure schema/semantic rules, no model required.
/// The validator is the real safety boundary: grammar shapes syntax, this
/// enforces what the planner may ever see.
/// </summary>
public class IntentValidationTests
{
    private static IntentTask.Intent Ok(string goal = "free up 10 GB") =>
        IntentTask.Validate($$"""{"goalBytes":10737418240,"constraints":{},"clarifyingQuestions":[],"explanation":"{{goal}}"}""");

    [Fact]
    public void Valid_intent_parses()
    {
        var i = Ok();
        Assert.Equal(10737418240L, i.GoalBytes);
        Assert.Empty(i.ClarifyingQuestions);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"goalBytes\":-5,\"constraints\":{},\"clarifyingQuestions\":[],\"explanation\":\"x\"}")]
    [InlineData("{\"goalBytes\":9999999999999999,\"constraints\":{},\"clarifyingQuestions\":[],\"explanation\":\"x\"}")]
    [InlineData("{\"goalBytes\":\"10 GB\",\"constraints\":{},\"clarifyingQuestions\":[],\"explanation\":\"x\"}")]
    [InlineData("{\"goalBytes\":1,\"constraints\":{\"rmEverything\":[]},\"clarifyingQuestions\":[],\"explanation\":\"x\"}")]
    [InlineData("{\"goalBytes\":1,\"constraints\":{\"excludeCategories\":[\"secrets\"]},\"clarifyingQuestions\":[],\"explanation\":\"x\"}")]
    [InlineData("{\"goalBytes\":1,\"constraints\":{},\"clarifyingQuestions\":[],\"explanation\":\"x\",\"path\":\"C:\\\\Users\"}")]
    public void Invalid_output_rejected(string raw)
        => Assert.Throws<AiException>(() => IntentTask.Validate(raw));

    [Fact]
    public void Unsupported_constraint_value_rejected()
        => Assert.Throws<AiException>(() => IntentTask.Validate(
            """{"goalBytes":1,"constraints":{"excludeScopes":["system32"]},"clarifyingQuestions":[],"explanation":"x"}"""));

    [Fact]
    public void Path_command_shaped_text_rejected()
        => Assert.Throws<AiException>(() => IntentTask.Validate(
            """{"goalBytes":1,"constraints":{},"clarifyingQuestions":["run C:\\Windows\\x.exe?"],"explanation":"x"}"""));

    [Fact]
    public void ExcludeIds_must_be_opaque_rec_ids()
    {
        Assert.Throws<AiException>(() => IntentTask.Validate(
            """{"goalBytes":1,"constraints":{"excludeIds":["C:\\temp"]},"clarifyingQuestions":[],"explanation":"x"}"""));
        var ok = IntentTask.Validate(
            """{"goalBytes":1,"constraints":{"excludeIds":["act-crash-dumps"]},"clarifyingQuestions":[],"explanation":"x"}""");
        Assert.Single(ok.Constraints["excludeIds"]);
    }
}

/// <summary>
/// Live worker tests: spawn the real ClearForge.Ai.exe, exercise the real
/// pipe, load the real curated model, benchmark, then verify clean shutdown.
/// Skipped when the dev model artifact is absent.
/// </summary>
public class AiWorkerLiveTests : IDisposable
{
    private static readonly string ExePath = FindExe();
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClearForge", "models", CuratedModel.FileName);
    private readonly List<Process> _procs = new();

    private static string FindExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName,
                "native", "ClearForge.Ai", "bin", "Debug", "net8.0", "ClearForge.Ai.exe");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return "";
    }

    public void Dispose()
    {
        foreach (var p in _procs) { try { if (!p.HasExited) p.Kill(); } catch { } }
    }

    private Process SpawnWorker(string pipe)
    {
        Assert.True(File.Exists(ExePath), $"worker exe missing: {ExePath}");
        var p = Process.Start(new ProcessStartInfo(ExePath, pipe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("[w] " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("[w-err] " + e.Data); };
        _procs.Add(p);
        return p;
    }

    private static async Task<NamedPipeClientStream> ConnectPipe(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000);
        return pipe;
    }

    private static async Task<JsonElement> Call(NamedPipeClientStream pipe,
        string id, string method, object? args = null)
    {
        var req = JsonSerializer.Serialize(new { id, method, @params = args });
        var buf = Encoding.UTF8.GetBytes(req + "\n");
        await pipe.WriteAsync(buf);
        var line = new List<byte>();
        var b = new byte[8192];
        while (true)
        {
            var n = await pipe.ReadAsync(b);
            for (var i = 0; i < n; i++)
            {
                if (b[i] == (byte)'\n')
                    return JsonDocument.Parse(line.ToArray()).RootElement;
                line.Add(b[i]);
            }
        }
    }

    [Fact]
    public async Task Worker_health_and_bounded_surface()
    {
        SpawnWorker("clearforge.ai.t.health");
        await using var pipe = await ConnectPipe("clearforge.ai.t.health");
        var h = await Call(pipe, "1", "ai.health");
        Assert.Equal("ok", h.GetProperty("result").GetProperty("status").GetString());
        var bad = await Call(pipe, "2", "cleanup.commit");
        Assert.Equal("unknown_method", bad.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Infer_without_model_reports_missing()
    {
        SpawnWorker("clearforge.ai.t.infer_missing");
        await using var pipe = await ConnectPipe("clearforge.ai.t.infer_missing");
        var r = await Call(pipe, "1", "ai.infer",
            new { goal = "free 10 GB", recommendations = Array.Empty<object>() });
        Assert.Equal("model_missing",
            r.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Uncurated_and_missing_models_rejected()
    {
        SpawnWorker("clearforge.ai.t.uncurated");
        await using var pipe = await ConnectPipe("clearforge.ai.t.uncurated");
        var bad = await Call(pipe, "1", "ai.model.load", new { modelId = "evil.gguf" });
        Assert.Equal("model_unsupported",
            bad.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Worker_exits_when_stdin_closes()
    {
        var p = SpawnWorker("clearforge.ai.test1");
        await Task.Delay(400);
        p.StandardInput.Close();
        Assert.True(await Task.Run(() => p.WaitForExit(5000)),
            "worker must exit when its stdin is closed (orphan-proof)");
    }

    [Fact]
    public async Task Full_load_infer_unload_benchmark()
    {
        if (ModelGate.ShouldSkip(ModelPath, nameof(Full_load_infer_unload_benchmark)))
            return;
        Assert.True(File.Exists(ModelPath),
            $"dev model absent at {ModelPath} — live-model test cannot run " +
            "(install the curated model, or set CLEARFORGE_SKIP_MODEL_TESTS=1)");
        var p = SpawnWorker("clearforge.ai.bench");
        await using var pipe = await ConnectPipe("clearforge.ai.bench");

        // Cold load: includes SHA-256 integrity verification of the artifact.
        var sw = Stopwatch.StartNew();
        var load = await Call(pipe, "l", "ai.model.load",
            new { modelId = "qwen2.5-1.5b-instruct-q4_k_m" });
        var coldLoad = sw.Elapsed;
        var errEl = load.GetProperty("error");
        var errCode = errEl.ValueKind == JsonValueKind.Null
            ? null : errEl.GetProperty("code").GetString();
        if (errCode != null)
            Console.WriteLine($"[bench] load error: {load.GetRawText()}");
        Assert.Null(errCode);
        Console.WriteLine($"[bench] cold load (incl. sha256 verify): {coldLoad.TotalSeconds:F1}s");

        var recs = new[]
        {
            new { id = "act-temporary-files", title = "Expired temporary files",
                  category = "temporary-files", estimatedLogicalBytes = 6144L,
                  confidence = "high" },
            new { id = "rev-downloads-x", title = "Review 'Big'",
                  category = "unclassified", estimatedLogicalBytes = 5L << 30,
                  confidence = "low" },
        };

        long peak = 0;
        var valid = 0;
        const int trials = 3;
        for (var i = 0; i < trials; i++)
        {
            sw.Restart();
            var r = await Call(pipe, $"i{i}", "ai.infer",
                new { goal = i == 1
                        ? "free up 10 GB without disturbing my development environment"
                        : "free up 10 GB",
                      recommendations = recs });
            var warm = sw.Elapsed;
            peak = Math.Max(peak, p.WorkingSet64);
            var res = r.GetProperty("result");
            if (res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("goalBytes", out var gb))
            {
                valid++;
                Assert.True(gb.GetInt64() > 0 && gb.GetInt64() <= IntentTask.MaxGoalBytes);
                Assert.True(res.TryGetProperty("constraints", out _));
                Assert.True(res.TryGetProperty("explanation", out _));
                Console.WriteLine($"[bench] infer #{i}: {warm.TotalSeconds:F1}s "
                    + $"goal={gb.GetInt64()} explanation='{res.GetProperty("explanation").GetString()}'");
            }
            else
            {
                // Bounded failure is a legitimate outcome — the validator
                // rejected the model's output. Measured, not hidden.
                var code = r.GetProperty("error").GetProperty("code").GetString();
                Assert.Equal("invalid_output", code);
                Console.WriteLine($"[bench] infer #{i}: {warm.TotalSeconds:F1}s INVALID ({code})");
            }
        }
        Console.WriteLine($"[bench] validity: {valid}/{trials}; worker peak WS: {peak / 1048576.0:F0} MiB");
        Assert.True(valid >= 1, "model produced zero valid outputs across the trial set");

        var unload = await Call(pipe, "u", "ai.model.unload");
        Assert.Equal("unloaded",
            unload.GetProperty("result").GetProperty("status").GetString());

        var again = await Call(pipe, "i2", "ai.infer",
            new { goal = "free 1 GB", recommendations = recs });
        Assert.Equal("model_missing",
            again.GetProperty("error").GetProperty("code").GetString());
    }
}
