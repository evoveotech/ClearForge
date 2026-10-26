using System.Diagnostics;

namespace ClearForge.Engine.Privacy;

public sealed record BrowserProfileInfo(
    string Browser,            // edge | chrome | firefox
    string ProfileName,        // e.g. "Default", "Profile 1", "xyz.default-release"
    string DisplayPath,        // profile dir (visible to the user; engine-only paths never leave via candidates)
    bool BrowserRunning,       // when true, all cleaning is refused
    long CacheBytes,           // measured cache size (read-only)
    IReadOnlyDictionary<string, PrivacyCategory> Categories);

public sealed record PrivacyCategory(
    string Id,                 // cache | history | cookies
    bool Available,
    string? Warning,           // consequences (sign-out etc.)
    string? UnavailableReason);

/// <summary>
/// Read-only browser profile detection + category preview.
/// Design rules (hard):
///  - Never enumerate or touch Login Data, Web Data, key4.db, cookies with
///    credential/session payloads, or any auth-bearing store.
///  - Cache preview only in M3; history/cookies reported as unavailable with
///    the consequence text the UI must display.
///  - A running browser makes every category unavailable — database locks.
/// </summary>
public sealed class BrowserPrivacyScanner
{
    /// <summary>Injectable roots so tests use fixtures, not real profiles.</summary>
    public sealed record BrowserSpec(
        string Browser, string UserDataRoot, string[] ProcessNames,
        string ProfileGlob, string[] CacheDirs);

    // M8.2: Chromium GPU/shader caches are equally disposable — documented
    // regenerable caches, still gated by the closed-browser requirement.
    private static readonly string[] ChromiumCacheDirs =
    {
        "Cache", "Code Cache",
        "GPUCache", "ShaderCache", "GrShaderCache",
        "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
    };

    public static IReadOnlyList<BrowserSpec> DefaultSpecs(string localAppData, string roamingAppData) => new[]
    {
        new BrowserSpec("edge", Path.Combine(localAppData, @"Microsoft\Edge\User Data"),
            new[] { "msedge" }, "*", ChromiumCacheDirs),
        new BrowserSpec("chrome", Path.Combine(localAppData, @"Google\Chrome\User Data"),
            new[] { "chrome" }, "*", ChromiumCacheDirs),
        new BrowserSpec("firefox", Path.Combine(roamingAppData, @"Mozilla\Firefox\Profiles"),
            new[] { "firefox" }, "*.default*", new[] { "cache2" }),
    };

    private readonly IReadOnlyList<BrowserSpec> _specs;
    private readonly Func<string, bool> _processRunning;

    public BrowserPrivacyScanner(IReadOnlyList<BrowserSpec> specs,
        Func<string, bool>? processRunning = null)
    {
        _specs = specs;
        _processRunning = processRunning ?? DefaultProcessCheck;
    }

    private static bool DefaultProcessCheck(string name) =>
        Process.GetProcessesByName(name).Length > 0;

    /// <summary>Live check: is any process of this browser currently running?</summary>
    public bool IsBrowserRunning(string browser)
    {
        var spec = _specs.FirstOrDefault(s =>
            s.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase));
        return spec is not null && spec.ProcessNames.Any(_processRunning);
    }

    /// <summary>Spec for a detected browser name, or null when unsupported.</summary>
    public BrowserSpec? SpecFor(string browser) =>
        _specs.FirstOrDefault(s =>
            s.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<BrowserProfileInfo> Detect()
    {
        var found = new List<BrowserProfileInfo>();
        foreach (var spec in _specs)
        {
            if (!Directory.Exists(spec.UserDataRoot)) continue;
            var running = spec.ProcessNames.Any(_processRunning);

            foreach (var profileDir in EnumerateProfiles(spec))
            {
                var cacheBytes = spec.CacheDirs
                    .Select(d => Path.Combine(profileDir, d))
                    .Where(Directory.Exists)
                    .Sum(d => SafeDirSize(d));

                var categories = new Dictionary<string, PrivacyCategory>
                {
                    ["cache"] = running
                        ? new PrivacyCategory("cache", false, null, "browser_running")
                        : new PrivacyCategory("cache", true,
                            "Cache rebuilds automatically; images/pages reload on next visit.", null),
                    // History and cookies are intentionally not available yet —
                    // per spec they require lifecycle/locking/sync review first.
                    ["history"] = new PrivacyCategory("history", false,
                        "Clears visited-site history; cannot be undone.",
                        "not_implemented_pending_lifecycle_review"),
                    ["cookies"] = new PrivacyCategory("cookies", false,
                        "Signs you out of sites and removes site preferences.",
                        "not_implemented_pending_lifecycle_review"),
                };

                found.Add(new BrowserProfileInfo(
                    spec.Browser, Path.GetFileName(profileDir), profileDir,
                    running, cacheBytes, categories));
            }
        }
        return found;
    }

    private IEnumerable<string> EnumerateProfiles(BrowserSpec spec)
    {
        // Chromium: User Data\<Default|Profile N>; Firefox: Profiles\*.default*
        foreach (var dir in Directory.EnumerateDirectories(spec.UserDataRoot, spec.ProfileGlob))
        {
            var name = Path.GetFileName(dir);
            if (spec.Browser != "firefox" &&
                !(name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                  || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (spec.Browser == "firefox" && !name.Contains(".default"))
                continue;
            yield return dir;
        }
    }

    private static long SafeDirSize(string dir)
    {
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        IEnumerator<string>? it = null;
        try { it = Directory.EnumerateFiles(dir, "*", options).GetEnumerator(); }
        catch { return 0; }
        using (it)
            while (true)
            {
                try
                {
                    if (!it.MoveNext()) break;
                    total += new FileInfo(it.Current).Length;
                }
                catch { /* skip */ }
            }
        return total;
    }
}
