using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClearForge.Ai;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M7.2B fixed offline evaluation harness. Runs the curated Qwen model
/// against the versioned dataset in Fixtures/intent-benchmark.json.
///
/// Hard gates (test fails): every deterministic via=validate case, every
/// explicit-quantity intent producing the exact canonical bytes, and every
/// safety rejection. Soft metrics (measured, printed, not failed): schema
/// validity, semantic correctness, constraint accuracy, latency.
/// A schema-valid but semantically wrong case counts as a failed case.
/// </summary>
public class IntentBenchmarkTests
{
    private static readonly string ExePath = FindExe();
    private static readonly string DatasetPath = FindDataset();
    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClearForge", "models", CuratedModel.FileName);

    private static string FindExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "native", "ClearForge.Ai",
                "bin", "Debug", "net8.0", "ClearForge.Ai.exe");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return "";
    }

    private static string FindDataset()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "native", "ClearForge.Engine.Tests",
                "Fixtures", "intent-benchmark.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "intent-benchmark.json");
    }

    private sealed record CaseResult(
        string Id, string Category, string Via, string Kind, bool Passed,
        bool SchemaValid, bool QuantityExact, bool ClarifyCorrect,
        double LatencyMs, string Detail);

    [Fact]
    public async Task Fixed_benchmark_suite_against_real_model()
    {
        Assert.True(File.Exists(ModelPath),
            $"dev model absent at {ModelPath} — skipping benchmark");
        Assert.True(File.Exists(DatasetPath), $"dataset missing: {DatasetPath}");
        Assert.True(File.Exists(ExePath), $"worker exe missing: {ExePath}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(DatasetPath));
        var root = doc.RootElement;
        var recsJson = root.GetProperty("recommendations");
        var recs = recsJson.EnumerateArray().Select(r => new IntentTask.RecSummary(
            r.GetProperty("id").GetString()!,
            r.GetProperty("title").GetString()!,
            r.GetProperty("category").GetString()!,
            r.GetProperty("estimatedLogicalBytes").GetInt64(),
            r.GetProperty("confidence").GetString()!)).ToArray();

        var pipe = "clearforge.ai.bench.m72b";
        var proc = Process.Start(new ProcessStartInfo(ExePath, pipe)
        {
            RedirectStandardInput = true, UseShellExecute = false,
        })!;
        try
        {
            await using var pipeClient = new NamedPipeClientStream(".", pipe,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(15000);

            var load = await Call(pipeClient, "load", "ai.model.load",
                new { modelId = CuratedModel.DefaultId });
            Assert.True(load.GetProperty("error").ValueKind == JsonValueKind.Null,
                $"model load failed: {load.GetRawText()}");

            var results = new List<CaseResult>();
            foreach (var c in root.GetProperty("cases").EnumerateArray())
                results.Add(await RunCase(pipeClient, c, recs, recsJson));

            Report(results);

            // --- hard gates (deterministic invariants only — model sampling
            // flakiness is measured data, never a test failure) ---
            foreach (var r in results)
            {
                if (r.Via != "model")
                    Assert.True(r.Passed,
                        $"deterministic layer failed for case {r.Id}: {r.Detail}");
                Assert.True(r.QuantityExact,
                    $"explicit quantity was not canonical for case {r.Id}");
            }
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { }
            if (!proc.HasExited) proc.Kill();
        }
    }

    private static async Task<CaseResult> RunCase(
        NamedPipeClientStream pipe, JsonElement c,
        IntentTask.RecSummary[] recs, JsonElement recsJson)
    {
        var id = c.GetProperty("id").GetString()!;
        var cat = c.GetProperty("category").GetString()!;
        var via = c.GetProperty("via").GetString()!;
        var expect = c.GetProperty("expect");
        var kinds = expect.GetProperty("kind").EnumerateArray()
            .Select(k => k.GetString()!).ToArray();
        var input = c.TryGetProperty("input", out var inp)
            ? inp.GetString() ?? "" : "";

        var sw = Stopwatch.StartNew();
        string kind; bool schemaValid = true;
        JsonElement? result = null;
        string detail = "";

        if (via == "validate")
        {
            // Deterministic layer only — no model call.
            try
            {
                var n = IntentTask.Interpret(input,
                    c.GetProperty("raw").GetString()!, recs);
                kind = n.Status == "clarification_required" ? "clarify" : "intent";
                result = JsonSerializer.SerializeToElement(new
                {
                    status = n.Status,
                    goalBytes = n.Intent.GoalBytes,
                    goalBytesSource = n.GoalBytesSource,
                    n.UnmatchedExclusions,
                    constraints = n.Intent.Constraints,
                });
            }
            catch (AiException) { kind = "reject"; schemaValid = false; }
        }
        else
        {
            var r = await Call(pipe, $"b-{id}", "ai.infer",
                new { goal = input, recommendations = recsJson });
            var err = r.GetProperty("error");
            if (err.ValueKind != JsonValueKind.Null)
            {
                kind = "reject";
                schemaValid = false;
                detail = $"{err.GetProperty("code").GetString()}: "
                    + $"{err.GetProperty("message").GetString()}";
            }
            else
            {
                result = r.GetProperty("result");
                var status = result.Value.TryGetProperty("status", out var st)
                    ? st.GetString() : "ok";
                kind = status == "clarification_required" ? "clarify" : "intent";
            }
        }
        sw.Stop();

        var passed = kinds.Contains(kind);
        var qtyExact = true;
        var clarifyOk = true;

        if (result is { } res)
        {
            if (expect.TryGetProperty("goalBytes", out var gb)
                && res.TryGetProperty("goalBytes", out var actual))
            {
                qtyExact = actual.GetInt64() == gb.GetInt64();
                if (!qtyExact) passed = false;
            }
            if (expect.TryGetProperty("goalBytesSource", out var gbs)
                && res.TryGetProperty("goalBytesSource", out var src)
                && src.GetString() != gbs.GetString())
                passed = false;
            if (expect.TryGetProperty("unmatchedIncludes", out var um)
                && res.TryGetProperty("unmatchedExclusions", out var ums))
            {
                var got = ums.EnumerateArray().Select(x => x.GetString()).ToHashSet();
                foreach (var want in um.EnumerateArray())
                    if (!got.Contains(want.GetString())) passed = false;
            }
        }
        else if (expect.TryGetProperty("goalBytes", out _))
        {
            passed = false; // expected an intent but got rejection
        }
        if (kind == "clarify") clarifyOk = kinds.Contains("clarify");

        return new CaseResult(id, cat, via, kind, passed, schemaValid,
            qtyExact, clarifyOk, sw.Elapsed.TotalMilliseconds, detail);
    }

    private static void Report(List<CaseResult> results)
    {
        var sb = new StringBuilder("\n=== M7.2B intent benchmark (real model) ===\n");
        foreach (var r in results)
            sb.AppendLine($"{(r.Passed ? "PASS" : "FAIL"),-5} {r.Id,-22} "
                + $"{r.Category,-22} kind={r.Kind,-7} "
                + $"schema={(r.SchemaValid ? "ok" : "bad"),-3} "
                + $"{r.LatencyMs,7:F0}ms {r.Detail}");

        var modelCases = results.Where(r => r.SchemaValid || r.Kind != "reject").ToList();
        var qtyCases = results.Where(r =>
            r.Category == "explicit_quantity" && r.Kind == "intent").ToList();
        var clarifyExpected = results.Where(r => r.Category is "ambiguous").ToList();

        sb.AppendLine($"cases={results.Count} "
            + $"schemaValid={results.Count(r => r.SchemaValid)}/{results.Count} "
            + $"semanticPass={results.Count(r => r.Passed)}/{results.Count} "
            + $"quantityExact={qtyCases.Count(r => r.QuantityExact)}/{qtyCases.Count} "
            + $"clarifyCorrect={clarifyExpected.Count(r => r.Passed)}/{clarifyExpected.Count}");
        var lat = results.Where(r => r.LatencyMs > 0 && r.Kind != "reject")
            .Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
        if (lat.Length > 0)
            sb.AppendLine($"latency median={lat[lat.Length / 2]:F0}ms "
                + $"p95={lat[(int)(lat.Length * 0.95)]:F0}ms "
                + $"max={lat[^1]:F0}ms");
        Console.WriteLine(sb.ToString());
    }

    private static async Task<JsonElement> Call(
        NamedPipeClientStream pipe, string id, string method, object? args = null)
    {
        var req = JsonSerializer.Serialize(new { id, method, @params = args });
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(req + "\n"));
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
}
