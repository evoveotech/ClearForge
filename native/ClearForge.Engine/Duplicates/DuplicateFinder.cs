using System.Security.Cryptography;
using ClearForge.Contracts;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Duplicates;

/// <summary>
/// Fixed, named scan scopes the user can select — the client picks a scope
/// id, never a path. Only user-library locations are offered.
/// </summary>
public sealed class ScopeRegistry
{
    private readonly Dictionary<string, string> _scopes = new(StringComparer.OrdinalIgnoreCase);

    public ScopeRegistry Add(string scopeId, string path)
    {
        _scopes[scopeId] = TrustedRoots.Canonicalize(path);
        return this;
    }

    public string? Resolve(string scopeId) =>
        _scopes.TryGetValue(scopeId, out var p) && Directory.Exists(p) ? p : null;

    public IReadOnlyDictionary<string, string> All =>
        _scopes.Where(kv => Directory.Exists(kv.Value))
               .ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>
/// One duplicate group: ≥2 files with identical SHA-256 content. Sha256 is
/// the full lowercase hex digest (the grouping proof); Sha256Short is a
/// display prefix for compact list rows.
/// </summary>
public sealed record DuplicateGroup(
    string GroupId, string Sha256Short, long FileBytes, int Copies,
    IReadOnlyList<ScanCandidate> Candidates, string Sha256);

public sealed record DuplicateReport(
    string SessionId, int GroupCount, long EstimatedRecoverableBytes,
    IReadOnlyList<DuplicateGroup> Groups);

/// <summary>
/// Find identical-content files under trusted scopes: enumerate (bounded,
/// no reparse traversal) → group by size → hash only size collisions →
/// full SHA-256 equality decides. Members become session-bound candidates,
/// quarantinable through the existing executor. Never auto-selects.
/// </summary>
public sealed class DuplicateFinder
{
    public const string RuleId = "duplicate-content";
    public const string Category = "duplicates";

    private readonly ScopeRegistry _scopes;
    private readonly ScanLimits _limits;

    public DuplicateFinder(ScopeRegistry scopes, ScanLimits? limits = null)
    {
        _scopes = scopes;
        _limits = limits ?? new ScanLimits();
    }

    /// <summary>
    /// Enumerate a scope without hashing; used to gather files per scope.
    /// </summary>
    private IEnumerable<(string Path, long Length, DateTimeOffset LastWriteUtc)> EnumerateScope(
        string root, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
            MaxRecursionDepth = _limits.MaxDepth,
        };
        long seen = 0;
        IEnumerator<string>? it = null;
        try { it = Directory.EnumerateFiles(root, "*", options).GetEnumerator(); }
        catch { yield break; }
        using (it)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (seen++ >= _limits.MaxFilesTotal) yield break;
                string p;
                try { if (!it.MoveNext()) yield break; p = it.Current; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

                var canonical = TrustedRoots.Canonicalize(p);
                if (!TrustedRoots.IsContainedIn(root, canonical)) continue;
                FileInfo info;
                try
                {
                    info = new FileInfo(canonical);
                    if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (info.Length == 0) continue; // zero-byte files are never "duplicates worth removing"
                }
                catch { continue; }
                yield return (canonical, info.Length, info.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>
    /// Populate a scan session with duplicate members as quarantinable
    /// candidates (ruleId=duplicate-content). Runs synchronously; caller
    /// wraps in Task.Run where async behaviour is wanted.
    /// </summary>
    public DuplicateReport ScanIntoSession(
        ScanSession session, IReadOnlyList<string> scopeIds, CancellationToken ct)
    {
        var groups = new List<DuplicateGroup>();
        var index = 0;
        var bySize = new Dictionary<long, List<(string Path, DateTimeOffset Lw)>>();

        foreach (var scopeId in scopeIds)
        {
            var root = _scopes.Resolve(scopeId);
            if (root is null) continue;
            foreach (var f in EnumerateScope(root, ct))
            {
                if (!bySize.TryGetValue(f.Length, out var list))
                    bySize[f.Length] = list = new();
                list.Add((f.Path, f.LastWriteUtc));
            }
        }

        foreach (var (size, members) in bySize)
        {
            if (members.Count < 2) continue;
            var byHash = new Dictionary<string, List<(string Path, DateTimeOffset Lw)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                ct.ThrowIfCancellationRequested();
                string hash;
                try
                {
                    using var fs = new FileStream(m.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    hash = Convert.ToHexString(SHA256.HashData(fs));
                }
                catch { continue; } // changed/vanished between size pass and hash pass
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new();
                list.Add(m);
            }

            foreach (var (hash, files) in byHash)
            {
                if (files.Count < 2) continue;
                var gid = $"g{groups.Count}";
                var cands = new List<ScanCandidate>();
                foreach (var f in files)
                {
                    var id = $"{session.Id}:d{index++}";
                    var name = Path.GetFileName(f.Path);
                    var candidate = new ScanCandidate(
                        id, RuleId, Category, name, size,
                        RiskLevel.Medium,
                        $"duplicate of {files.Count - 1} other file(s), sha256:{hash[..12]}… (group {gid})",
                        true);
                    cands.Add(candidate);
                    var scanned = new ScannedFile(id, f.Path, RuleId, Category, size,
                        f.Lw, FileAttributes.Normal);
                    lock (session.Items) session.Items.Add((scanned, candidate));
                }
                groups.Add(new DuplicateGroup(
                    gid, hash[..16], size, files.Count, cands,
                    hash.ToLowerInvariant()));
            }
        }

        var report = new DuplicateReport(
            session.Id, groups.Count,
            groups.Sum(g => g.FileBytes * (g.Copies - 1)), // keep one copy
            groups);
        // The session owns its group evidence — preview/commit enforcement
        // and the review endpoint both resolve membership from here,
        // regardless of whether the session came from duplicates.start or a
        // Smart Scan child producer.
        session.DuplicateReport = report;
        return report;
    }
}
