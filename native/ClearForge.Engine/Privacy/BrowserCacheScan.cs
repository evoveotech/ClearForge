using ClearForge.Contracts;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Privacy;

/// <summary>
/// Produces session-bound quarantinable candidates for allowlisted browser
/// cache directories of detected, supported profiles.
///
/// Safety contract:
///  - Locations come only from detected profiles of known browser specs —
///    no client-supplied path can ever reach the pipeline.
///  - Credentials, cookies, history, autofill and session stores are never
///    enumerated: only the spec's CacheDirs are scanned.
///  - A running browser blocks both candidate generation and, via the
///    session CommitGuard, every commit attempt — including the
///    browser-started-after-scan race (process check is not a lock; the
///    file-level lock probe in the executor is the last line of defence).
/// </summary>
public sealed class BrowserCacheScanner
{
    public const string Category = "browser-cache";

    private readonly BrowserPrivacyScanner _profiles;
    private readonly ScanLimits _limits;

    public BrowserCacheScanner(BrowserPrivacyScanner profiles, ScanLimits? limits = null)
    {
        _profiles = profiles;
        _limits = limits ?? new ScanLimits();
    }

    /// <summary>
    /// Populate a session with cache candidates. Refuses entirely (empty
    /// session + LastError) while a contributing browser is running.
    /// </summary>
    public void ScanIntoSession(ScanSession session, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var index = 0;
        var contributed = new List<BrowserProfileInfo>();

        foreach (var profile in _profiles.Detect())
        {
            ct.ThrowIfCancellationRequested();
            if (profile.BrowserRunning) continue; // running browsers never contribute
            if (!profile.Categories.TryGetValue("cache", out var cacheCat) || !cacheCat.Available)
                continue;

            var spec = _profiles.SpecFor(profile.Browser);
            if (spec is null) continue; // unsupported layout → skip, don't guess

            var cacheRoot = ResolveCacheRoot(profile, spec);
            if (cacheRoot is null) continue;

            foreach (var path in EnumerateCache(cacheRoot, ct,
                onLimitReached: () =>
                {
                    // File-cap truncation: the candidate set is a bounded
                    // subset — flagged so findings can say coverage is partial.
                    lock (session.Items)
                        session.TruncatedRules.Add($"browser-cache:{profile.Browser}");
                }))
            {
                var info = new FileInfo(path);
                var id = $"{session.Id}:b{index++}";
                var scanned = new ScannedFile(id, path, $"browser-cache:{profile.Browser}",
                    Category, info.Length, info.LastWriteTimeUtc, info.Attributes);
                var pub = new ScanCandidate(
                    id, scanned.RuleId, Category,
                    $"{profile.Browser} cache — {info.Name}",
                    info.Length, RiskLevel.Low,
                    $"browser cache under detected {profile.Browser} profile '{profile.ProfileName}'; regenerates automatically",
                    true);
                lock (session.Items) session.Items.Add((scanned, pub));
            }
            contributed.Add(profile);
        }

        if (contributed.Count == 0)
        {
            session.LastError = "no_supported_profiles_or_browsers_running";
            return;
        }

        // Commit-time revalidation gate: refuse every item while any
        // contributing browser is running. Re-evaluated per item inside the
        // executor's candidate lock — covers browser-started-after-scan.
        var browsers = contributed.Select(p => p.Browser).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        session.CommitGuard = () =>
            browsers.Any(b => _profiles.IsBrowserRunning(b))
                ? Protocol.Errors.BrowserRunning
                : null;
    }

    private static string? ResolveCacheRoot(BrowserProfileInfo profile, BrowserPrivacyScanner.BrowserSpec spec)
    {
        // Multiple cache dirs (Cache + Code Cache): enumerate each; all must
        // live inside the profile dir and not be reparse points.
        var profileDir = TrustedRoots.Canonicalize(profile.DisplayPath);
        var dirs = spec.CacheDirs
            .Select(d => Path.Combine(profileDir, d))
            .Where(Directory.Exists)
            .Where(d =>
            {
                var canon = TrustedRoots.Canonicalize(d);
                return TrustedRoots.IsContainedIn(profileDir, canon)
                       && !new DirectoryInfo(canon).Attributes.HasFlag(FileAttributes.ReparsePoint);
            })
            .ToArray();
        return dirs.Length == 0 ? null : profileDir; // root = profile; dirs enumerated per CacheDirs
    }

    private IEnumerable<string> EnumerateCache(string profileDir, CancellationToken ct,
        Action? onLimitReached = null)
    {
        // Enumerate only known cache subdirectories — never the profile root.
        var specDirs = Directory.EnumerateDirectories(profileDir, "*", SearchOption.TopDirectoryOnly);
        var seen = 0;
        foreach (var dir in specDirs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (!IsCacheDirName(name)) continue;
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true,
                MaxRecursionDepth = _limits.MaxDepth,
            };
            IEnumerator<string>? it = null;
            try { it = Directory.EnumerateFiles(dir, "*", options).GetEnumerator(); }
            catch { continue; }
            using (it)
            {
                while (true)
                {
                    if (seen++ >= _limits.MaxFilesTotal)
                    {
                        try { onLimitReached?.Invoke(); } catch { }
                        yield break;
                    }
                    string p;
                    try { if (!it.MoveNext()) break; p = it.Current; }
                    catch { break; }
                    var canon = TrustedRoots.Canonicalize(p);
                    if (!TrustedRoots.IsContainedIn(TrustedRoots.Canonicalize(dir), canon)) continue;
                    yield return canon;
                }
            }
        }
    }

    // M8.2: profile-level GPU/shader caches join the allowlist — same
    // disposability evidence as Cache/Code Cache, same closed-browser gate.
    private static readonly HashSet<string> CacheDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "cache2",
        "GPUCache", "ShaderCache", "GrShaderCache",
        "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
    };

    private static bool IsCacheDirName(string name) => CacheDirNames.Contains(name);
}
