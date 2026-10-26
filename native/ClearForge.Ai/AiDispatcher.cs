using System.Text.Json;
using ClearForge.Contracts;

namespace ClearForge.Ai;

public sealed record AiResult(object? Result, string? Error, string? Message)
{
    public static AiResult Ok(object result) => new(result, null, null);
    public static AiResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Tiny allowlisted AI surface. There is intentionally no path-taking,
/// engine-proxying, journal, or filesystem-mutation method — the worker can
/// only answer health and run one bounded intent task on supplied text.
/// </summary>
public sealed class AiDispatcher
{
    private readonly LlmRuntime _runtime;

    public AiDispatcher(LlmRuntime runtime) => _runtime = runtime;

    public Task<AiResult> Dispatch(string method, JsonElement? p) => method switch
    {
        "ai.health" => Task.FromResult(AiResult.Ok(new
        {
            status = "ok",
            runtime = LlmRuntime.RuntimeId,
            protocolVersion = Protocol.Version,
            modelLoaded = _runtime.ModelLoaded,
            modelId = _runtime.LoadedModelId,
        })),
        "ai.model.load" => Load(p),
        "ai.model.unload" => Task.FromResult(_runtime.Unload()),
        "ai.infer" => Infer(p),
        _ => Task.FromResult(AiResult.Fail(Protocol.Errors.UnknownMethod,
            $"method '{method}' is not supported")),
    };

    private async Task<AiResult> Load(JsonElement? p)
    {
        var modelId = Param(p, "modelId") ?? CuratedModel.DefaultId;
        try { return AiResult.Ok(await _runtime.LoadAsync(modelId)); }
        catch (AiException ex) { return AiResult.Fail(ex.Code, ex.Message); }
    }

    private async Task<AiResult> Infer(JsonElement? p)
    {
        if (p is not { ValueKind: JsonValueKind.Object } obj)
            return AiResult.Fail(Protocol.Errors.InvalidParams, "params object required");
        var goal = Param(p, "goal");
        if (string.IsNullOrWhiteSpace(goal))
            return AiResult.Fail(Protocol.Errors.InvalidParams, "goal text required");
        if (goal.Length > 2000)
            return AiResult.Fail(Protocol.Errors.InvalidParams, "goal text too long");

        var recs = new List<IntentTask.RecSummary>();
        if (obj.TryGetProperty("recommendations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray().Take(IntentTask.MaxRecommendations))
            {
                recs.Add(new IntentTask.RecSummary(
                    el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    el.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                    el.TryGetProperty("estimatedLogicalBytes", out var b) && b.TryGetInt64(out var v) ? v : 0,
                    el.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "" : ""));
            }
        }

        try { return AiResult.Ok(await _runtime.InferIntentAsync(goal, recs)); }
        catch (AiException ex) { return AiResult.Fail(ex.Code, ex.Message); }
        catch (OperationCanceledException) { return AiResult.Fail("cancelled", "inference cancelled"); }
    }

    private static string? Param(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}

public sealed class AiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
