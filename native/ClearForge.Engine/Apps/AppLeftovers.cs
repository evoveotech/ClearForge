using System.Text;

namespace ClearForge.Engine.Apps;

/// <summary>
/// A directory that plausibly belongs to a named application, with the
/// evidence that produced the match. Review-only: no candidate/session
/// binding exists in this milestone — nothing here is executable.
/// </summary>
public sealed record LeftoverCandidate(
    string Path,
    string Root,            // "appdata-roaming" | "appdata-local" | "programdata"
    string MatchBasis,      // "name" | "publisher" | "install-leaf" | "nested" ("+"-joined)
    string Confidence,      // "exact" (app's own name/leaf/nested) | "publisher-context"
    string Reason);

/// <summary>One enumerated directory.</summary>
public sealed record LeftoverDir(string Path, string Name);

/// <summary>Injectable environment for leftover matching (roots + dir enumeration).</summary>
public sealed record LeftoverEnvironment(
    IReadOnlyDictionary<string, string> Roots,
    Func<string, IEnumerable<LeftoverDir>> ChildrenOf);

/// <summary>
/// Per-app leftover matching. Conservative by construction: only exact
/// normalized-name equality (never substring), generic vendor/app tokens are
/// rejected as a match basis, and an app whose tokens are all never-flagged
/// yields zero candidates rather than guessing.
/// </summary>
public static class LeftoverMatcher
{
    // Tokens that must never be a match basis — flagging them would sweep
    // shared/vendor directories (the classic cleaner-app FP class).
    private static readonly HashSet<string> NeverFlag = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "apple", "google", "mozilla", "intel", "nvidia",
        "amd", "adobe", "oracle", "common", "commonfiles", "temp", "cache",
        "caches", "logs", "fonts", "system", "packages", "programs", "default",
        "public", "user", "users", "app", "apps", "application", "software",
        "installer", "data", "new", "old", "backup", "config", "settings",
        "themes", "drivers", "driverstore", "servicing", "prefetch", "recent",
        "roaming", "locallow", "credentials", "crypto", "dpapi", "protect",
    };

    private static readonly HashSet<string> CorporateNoise = new(StringComparer.Ordinal)
    {
        "inc", "corp", "corporation", "ltd", "llc", "gmbh", "co", "company",
        "labs", "technologies", "technology", "systems", "solutions", "soft",
        "software", "the", "and", "of", "for",
    };

    /// <summary>Lowercase alphanumeric token stream minus corporate noise
    /// words and tokens &lt; 3 chars, concatenated. Null when nothing usable
    /// remains.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = new List<string>();
        var cur = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) cur.Append(char.ToLowerInvariant(c));
            else if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        var kept = tokens.Where(t => t.Length >= 3 && !CorporateNoise.Contains(t));
        var joined = string.Concat(kept);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// Find dirs that plausibly belong to the app described by its registry
    /// strings. <paramref name="roots"/> maps root label → root path;
    /// <paramref name="childrenOf"/> enumerates immediate child dirs of a
    /// path (tests inject fixtures). Exact normalized equality only —
    /// publisher dirs get one extra level for the app name
    /// (Vendor\AppName). Empty when no basis survives normalization.
    /// </summary>
    public static IReadOnlyList<LeftoverCandidate> Match(
        string? name, string? publisher, string? installLocation,
        IReadOnlyDictionary<string, string> roots,
        Func<string, IEnumerable<LeftoverDir>> childrenOf)
    {
        var bases = new List<(string Basis, string Token)>();
        var nameTok = Usable(Normalize(name));
        var pubTok = Usable(Normalize(publisher));
        var leafTok = Usable(Normalize(LeafName(installLocation)));
        if (nameTok is not null) bases.Add(("name", nameTok));
        if (pubTok is not null && pubTok != nameTok) bases.Add(("publisher", pubTok));
        if (leafTok is not null && leafTok != nameTok && leafTok != pubTok)
            bases.Add(("install-leaf", leafTok));
        if (bases.Count == 0) return Array.Empty<LeftoverCandidate>();

        var hits = new Dictionary<string, (string Root, List<string> Bases, string DirName)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (root, rootPath) in roots)
        {
            IEnumerable<LeftoverDir> top;
            try { top = childrenOf(rootPath).ToList(); }
            catch { continue; } // per-root failure is non-fatal

            foreach (var d in top)
            {
                var dNorm = Normalize(d.Name);
                if (dNorm is null) continue;
                var matched = bases.Where(b => b.Token == dNorm)
                    .Select(b => b.Basis).ToList();
                if (matched.Count > 0)
                    AddHit(hits, d.Path, root, matched, d.Name);

                // One level deeper under publisher-named dirs only:
                // %APPDATA%\Contoso\ContosoApp is the classic layout.
                if (pubTok is not null && nameTok is not null && dNorm == pubTok)
                {
                    IEnumerable<LeftoverDir> subs;
                    try { subs = childrenOf(d.Path).ToList(); }
                    catch { continue; }
                    foreach (var s in subs)
                    {
                        if (Normalize(s.Name) == nameTok)
                            AddHit(hits, s.Path, root, new List<string> { "nested" }, s.Name);
                    }
                }
            }
        }

        return hits.Select(h =>
        {
            var distinct = h.Value.Bases.Distinct().ToArray();
            // "exact" = the dir name equals the app's own name / install leaf
            // (or nested app name); "publisher-context" = only the publisher
            // name matched — a weaker signal, more likely shared.
            var exact = distinct.Any(b =>
                b is "name" or "install-leaf" or "nested");
            return new LeftoverCandidate(
                h.Key, h.Value.Root, string.Join("+", distinct),
                exact ? "exact" : "publisher-context",
                $"directory '{h.Value.DirName}' matches app {string.Join("/", distinct)}");
        }).ToArray();
    }

    private static void AddHit(
        Dictionary<string, (string Root, List<string> Bases, string DirName)> hits,
        string path, string root, List<string> bases, string dirName)
    {
        if (hits.TryGetValue(path, out var h)) h.Bases.AddRange(bases);
        else hits[path] = (root, new List<string>(bases), dirName);
    }

    private static string? Usable(string? token) =>
        token is not null && !NeverFlag.Contains(token) ? token : null;

    private static string? LeafName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null
            : path!.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault();
}
