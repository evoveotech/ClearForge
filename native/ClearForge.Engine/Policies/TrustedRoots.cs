namespace ClearForge.Engine.Policies;

/// <summary>
/// Resolves symbolic root names to canonical filesystem paths.
/// Rules reference roots by id only; arbitrary paths can never be injected.
/// </summary>
public interface ITrustedRoots
{
    /// <summary>Canonical, normalized absolute path for the root id, or null when unknown/unavailable.</summary>
    string? Resolve(string rootId);

    IReadOnlyCollection<string> RootIds { get; }
}

/// <summary>In-memory root registry. Canonicalizes on registration.</summary>
public sealed class TrustedRoots : ITrustedRoots
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

    public TrustedRoots Add(string rootId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _roots[rootId] = Canonicalize(path);
        return this;
    }

    public string? Resolve(string rootId) =>
        _roots.TryGetValue(rootId, out var p) && Directory.Exists(p) ? p : null;

    public IReadOnlyCollection<string> RootIds => _roots.Keys.ToArray();

    /// <summary>Long-path-safe absolute path with consistent separators and no trailing slash.</summary>
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> is strictly inside
    /// <paramref name="canonicalRoot"/> (or is the root itself). Both inputs
    /// must already be canonicalized.
    /// </summary>
    public static bool IsContainedIn(string canonicalRoot, string candidatePath)
    {
        if (candidatePath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
