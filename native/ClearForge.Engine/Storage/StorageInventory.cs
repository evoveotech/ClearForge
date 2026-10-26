using ClearForge.Contracts;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Storage;

/// <summary>Top-level storage consumer under a trusted scope.</summary>
public sealed record StorageNode(
    string Name, string Kind, long LogicalBytes, string? CategoryHint);

public sealed record StorageInventoryReport(
    long TotalLogicalBytes,
    bool Partial,                    // scan budget exhausted → honest flag
    IReadOnlyDictionary<string, IReadOnlyList<StorageNode>> Scopes);

/// <summary>
/// Read-only bounded inventory over allowlisted scopes. Measures logical
/// sizes only (hard links/compression/sparse make physical usage differ —
/// all downstream claims must stay estimates).
/// </summary>
public sealed class StorageInventory
{
    private readonly ScopeRegistry _scopes;
    private readonly ScanLimits _limits;

    public StorageInventory(ScopeRegistry scopes, ScanLimits? limits = null)
    {
        _scopes = scopes;
        _limits = limits ?? new ScanLimits();
    }

    public StorageInventoryReport Scan(IEnumerable<string> scopeIds, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<StorageNode>>();
        var partial = false;
        long total = 0;
        long budget = _limits.MaxFilesTotal;

        foreach (var id in scopeIds)
        {
            var root = _scopes.Resolve(id);
            if (root is null) continue;
            var nodes = new List<StorageNode>();
            foreach (var dir in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                if (budget <= 0) { partial = true; break; }
                var (bytes, count) = Measure(dir, ref budget, ct);
                if (budget <= 0) partial = true;
                total += bytes;
                nodes.Add(new StorageNode(Path.GetFileName(dir), "dir", bytes,
                    Categorize(dir)));
            }
            result[id] = nodes.OrderByDescending(n => n.LogicalBytes).ToArray();
        }
        return new StorageInventoryReport(total, partial, result);
    }

    private (long, int) Measure(string dir, ref long budget, CancellationToken ct)
    {
        long bytes = 0; var count = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
            MaxRecursionDepth = _limits.MaxDepth,
        };
        IEnumerator<string>? it = null;
        try { it = Directory.EnumerateFiles(dir, "*", options).GetEnumerator(); }
        catch { return (0, 0); }
        using (it)
        {
            while (true)
            {
                if (budget-- <= 0) return (bytes, count);
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!it.MoveNext()) break;
                    bytes += new FileInfo(it.Current).Length;
                    count++;
                }
                catch { /* skip */ }
            }
        }
        return (bytes, count);
    }

    /// <summary>
    /// Evidence-based category hint. A dir is never classified by name alone —
    /// a marker file must corroborate (package.json for node_modules, csproj
    /// for bin/obj, pubspec for .dart_tool). Unknown → null (review-only).
    /// </summary>
    public static string? Categorize(string dir)
    {
        var name = Path.GetFileName(dir);
        var parent = Directory.GetParent(dir)?.FullName;
        bool HasSibling(string marker) =>
            parent is not null &&
            (File.Exists(Path.Combine(parent, marker)) ||
             Directory.GetFiles(parent, marker).Length > 0);

        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            && HasSibling("package.json")) return "dev-dependencies";
        if ((name.Equals("bin", StringComparison.OrdinalIgnoreCase)
             || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            && HasSibling("*.csproj")) return "dotnet-build-output";
        if (name.Equals(".dart_tool", StringComparison.OrdinalIgnoreCase)
            && HasSibling("pubspec.yaml")) return "dart-build-output";
        if (name.Equals("target", StringComparison.OrdinalIgnoreCase)
            && (HasSibling("Cargo.toml") || HasSibling("pom.xml"))) return "build-output";
        return null; // unknown → review-only
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        IEnumerator<string>? it = null;
        try { it = Directory.EnumerateDirectories(root).GetEnumerator(); }
        catch { yield break; }
        using (it)
            while (true)
            {
                string d;
                try { if (!it.MoveNext()) yield break; d = it.Current; }
                catch { yield break; }
                if (!new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    yield return d;
            }
    }
}
