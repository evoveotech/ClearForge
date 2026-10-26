namespace ClearForge.Engine.Tests;

/// <summary>
/// Gate for tests that require the ~1 GB curated dev model artifact.
/// Convention: the artifact's absence fails locally so developers notice a
/// missing model; on CI (where the model is never provisioned) the gate
/// converts the failure into a skip-return. Explicit opt-out also available
/// via CLEARFORGE_SKIP_MODEL_TESTS=1 for dev machines without the model.
/// </summary>
internal static class ModelGate
{
    public static bool SkipWhenAbsent =>
        Environment.GetEnvironmentVariable("CI") == "true"
        || Environment.GetEnvironmentVariable("CLEARFORGE_SKIP_MODEL_TESTS") == "1";

    /// <summary>Returns true when the caller must return early (skip).</summary>
    public static bool ShouldSkip(string modelPath, string testName)
    {
        if (File.Exists(modelPath)) return false;
        if (!SkipWhenAbsent) return false;
        Console.WriteLine(
            $"SKIP {testName}: dev model absent at {modelPath} " +
            "(CI/opt-out gate)");
        return true;
    }
}
