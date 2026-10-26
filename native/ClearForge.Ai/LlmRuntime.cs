using System.Security.Cryptography;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace ClearForge.Ai;

/// <summary>The single curated model for this proof of concept.</summary>
public static class CuratedModel
{
    public const string DefaultId = "qwen2.5-1.5b-instruct-q4_k_m";
    public const string FileName = "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf";
    public const string HfRepo = "bartowski/Qwen2.5-1.5B-Instruct-GGUF";
    public const string HfRevision = "9eadc66189c7641e1ddd226b8267a9119b2ce2d4";
    public const long ExpectedSize = 986048768;
    public const string ExpectedSha256 =
        "1adf0b11065d8ad2e8123ea110d1ec956dab4ab038eab665614adba04b6c3370";
    public const string License = "Apache-2.0";
}

/// <summary>
/// The only AI boundary in the worker: owns native LLamaSharp resources,
/// confines model files to the dedicated model root, verifies integrity
/// before loading, serializes inference, and enforces time/output bounds.
/// No network access — inference is fully local by construction.
/// </summary>
public sealed class LlmRuntime : IAsyncDisposable
{
    public const string RuntimeId = "llamasharp/0.30.0 llama.cpp";

    private static readonly TimeSpan InferTimeout = TimeSpan.FromSeconds(60);

    private readonly string _modelRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;

    public LlmRuntime(string modelRoot) => _modelRoot = modelRoot;

    public bool ModelLoaded => _weights is not null;
    public string? LoadedModelId => ModelLoaded ? CuratedModel.DefaultId : null;

    /// <summary>
    /// Resolve a curated model id to a canonical path inside the model root.
    /// The allowlist maps ids to filenames — client-supplied paths do not
    /// exist in this surface.
    /// </summary>
    private string ResolveModelPath(string modelId)
    {
        if (!string.Equals(modelId, CuratedModel.DefaultId, StringComparison.Ordinal))
            throw new AiException("model_unsupported", $"model '{modelId}' is not curated");
        var root = Path.GetFullPath(_modelRoot);
        var path = Path.GetFullPath(Path.Combine(root, CuratedModel.FileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AiException("policy_refused", "model path escaped the model root");
        return path;
    }

    public async Task<object> LoadAsync(string modelId)
    {
        var path = ResolveModelPath(modelId);
        await _gate.WaitAsync();
        try
        {
            if (_weights is not null)
                return new { status = "already_loaded", modelId };

            if (!File.Exists(path))
                throw new AiException("model_missing",
                    "curated model artifact not present under the model root");
            var info = new FileInfo(path);
            if (info.Length != CuratedModel.ExpectedSize)
                throw new AiException("model_invalid",
                    $"model size {info.Length} != expected {CuratedModel.ExpectedSize}");

            // Integrity: verify the artifact against the pinned digest.
            using var sha = SHA256.Create();
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                await sha.ComputeHashAsync(fs);
            }
            var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (hex != CuratedModel.ExpectedSha256)
                throw new AiException("model_invalid",
                    "model digest mismatch — refusing to load");

            try
            {
                var mp = new ModelParams(path)
                {
                    ContextSize = 2048,
                    GpuLayerCount = 0, // CPU only — deterministic baseline
                };
                _weights = LLamaWeights.LoadFromFile(mp);
                _executor = new StatelessExecutor(_weights, mp);
            }
            catch (Exception ex)
            {
                _weights = null;
                _executor = null;
                throw new AiException("model_load_failed",
                    $"runtime rejected the model artifact ({ex.GetType().Name}: {ex.Message}" +
                    $"{(ex.InnerException is null ? "" : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}")}" +
                    $"{(ex.InnerException?.InnerException is null ? "" : $" <- {ex.InnerException.InnerException.GetType().Name}: {ex.InnerException.InnerException.Message}")})");
            }
            return new { status = "loaded", modelId, contextSize = 2048 };
        }
        finally { _gate.Release(); }
    }

    public AiResult Unload()
    {
        _gate.Wait();
        try
        {
            _executor = null;
            _weights?.Dispose();
            _weights = null;
            return AiResult.Ok(new { status = "unloaded" });
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The single approved task: goal + bounded summary → validated intent.
    /// Grammar-constrained generation, serialized under the gate, bounded by
    /// a hard timeout and a token cap, then independently schema-validated.
    /// </summary>
    public async Task<object> InferIntentAsync(
        string goal, IReadOnlyList<IntentTask.RecSummary> recs,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_weights is null || _executor is null)
                throw new AiException("model_missing", "no model loaded — call ai.model.load first");

            var prompt = IntentTask.BuildPrompt(goal, recs);
            var ip = new InferenceParams
            {
                MaxTokens = 256,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Grammar = new Grammar(IntentTask.Grammar, "root"),
                },
            };

            var sb = new StringBuilder(512);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InferTimeout);
            try
            {
                await foreach (var piece in _executor.InferAsync(prompt, ip, timeout.Token))
                    sb.Append(piece);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiException("inference_timeout",
                    "inference exceeded the time bound");
            }
            catch (AiException) { throw; }
            catch (Exception ex)
            {
                throw new AiException("inference_failed",
                    $"inference failed ({ex.GetType().Name})");
            }

            // Validate, then reconcile deterministically: explicit user
            // quantities are canonical, ambiguous goals force clarification,
            // unmatched exclusion ids become reported no-ops.
            var norm = IntentTask.Interpret(goal, sb.ToString(), recs);
            return new
            {
                intentVersion = 1,
                status = norm.Status,
                goalBytes = norm.Intent.GoalBytes,
                goalBytesSource = norm.GoalBytesSource,
                modelGoalBytes = norm.ModelGoalBytes,
                constraints = norm.Intent.Constraints,
                unmatchedExclusions = norm.UnmatchedExclusions,
                clarifyingQuestions = norm.Intent.ClarifyingQuestions,
                explanation = norm.Intent.Explanation,
                issues = norm.Issues,
            };
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Wait();
        try
        {
            _executor = null;
            _weights?.Dispose();
            _weights = null;
        }
        finally { _gate.Release(); }
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
