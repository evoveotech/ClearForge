using ClearForge.Contracts;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Storage;

/// <summary>
/// M4.2 bridge: turns already-supported cleanup categories into executable
/// recommendations bound to real, session-bound candidates.
///
/// Candidates come only from the existing, verified scanners — catalog rules
/// via <see cref="ScanSessionStore.Populate"/> and browser cache via
/// <see cref="BrowserCacheScanner"/> (whose CommitGuard still gates commit).
/// Every candidate flows through the one journaled quarantine executor;
/// this class never touches the filesystem for mutation and never invents a
/// second execution path.
///
/// A category produces no recommendation at all when its scan finds zero
/// eligible candidates or fails — absence is honest, never padded.
/// </summary>
public sealed class RecommendationActionBridge
{
    public const string ActionKind = "quarantine";

    private readonly ScanSessionStore _scans;
    private readonly BrowserCacheScanner? _cache;

    public RecommendationActionBridge(ScanSessionStore scans, BrowserCacheScanner? cache = null)
    {
        _scans = scans;
        _cache = cache;
    }

    /// <summary>
    /// Populate one session per supported category (synchronously, bounded by
    /// the same scan limits as everywhere else) and emit one executable
    /// recommendation per non-empty completed session. Deterministic category
    /// order; per-path dedupe across categories prevents double-counting when
    /// two configured roots overlap.
    /// </summary>
    public IReadOnlyList<Recommendation> Build(CancellationToken ct = default)
    {
        var recs = new List<Recommendation>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in _scans.QuarantinableCategories.OrderBy(c => c, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var session = _scans.CreateExternal();
            _scans.Populate(session, new[] { category });
            var rec = FromSession(session, category, claimed);
            if (rec is not null) recs.Add(rec);
        }

        if (_cache is not null)
        {
            ct.ThrowIfCancellationRequested();
            var session = _scans.CreateExternal();
            try
            {
                _cache.ScanIntoSession(session, session.Cancellation.Token);
                session.Status = session.Cancellation.IsCancellationRequested
                    ? SessionStatus.Cancelled : SessionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                session.Status = SessionStatus.Cancelled;
            }
            var rec = FromSession(session, BrowserCacheScanner.Category, claimed);
            if (rec is not null) recs.Add(rec);
        }

        return recs;
    }

    /// <summary>
    /// One executable recommendation per completed, non-empty session, or null
    /// when the scan produced nothing actionable. Candidates already claimed by
    /// an earlier category are removed from this session so byte totals never
    /// double-count the same file.
    /// </summary>
    private static Recommendation? FromSession(
        ScanSession session, string category, HashSet<string> claimed)
    {
        if (session.Status != SessionStatus.Completed) return null;

        List<(ScannedFile File, ScanCandidate Public)> items;
        lock (session.Items) items = session.Items.ToList();
        if (items.Count == 0) return null;

        var fresh = items.Where(i => claimed.Add(i.File.CanonicalPath)).ToList();
        if (fresh.Count == 0) return null;
        if (fresh.Count != items.Count)
        {
            var keep = new HashSet<string>(fresh.Select(i => i.Public.Id), StringComparer.Ordinal);
            lock (session.Items) session.Items.RemoveAll(i => !keep.Contains(i.Public.Id));
        }

        var bytes = fresh.Sum(i => i.File.LengthBytes);
        var risk = fresh.Select(i => i.Public.Risk).DefaultIfEmpty(RiskLevel.Low).Max();
        var rules = fresh.Select(i => i.File.RuleId)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        return new Recommendation(
            $"act-{category}",
            category,
            TitleFor(category),
            $"{fresh.Count} file(s) matched the versioned cleanup catalog " +
            $"({string.Join(", ", rules)}); every item is revalidated against " +
            "its scan snapshot immediately before the quarantine move.",
            new[]
            {
                $"session={session.Id}",
                $"candidates={fresh.Count}",
                $"eligible={bytes}B",
                $"rules={string.Join("|", rules)}",
            },
            bytes,
            Confidence.High,
            risk,
            Reversibility.Quarantinable,
            new[]
            {
                "explicit user approval in the preview step",
                "per-item identity, containment, lock and capacity revalidation at commit",
            },
            Array.Empty<string>(),
            new RecommendationAction(ActionKind, session.Id, "completed", fresh.Count, bytes));
    }

    private static string TitleFor(string category) => category switch
    {
        "temporary-files" => "Expired temporary files",
        "crash-dumps" => "Old crash dumps",
        "thumbnail-cache" => "Explorer thumbnail & icon cache",
        "gpu-shader-cache" => "GPU shader caches",
        "package-manager-caches" => "Package manager caches",
        "windows-error-reports" => "Windows error reports",
        BrowserCacheScanner.Category => "Browser cache (closed profiles)",
        _ => $"Clean {category}",
    };
}
