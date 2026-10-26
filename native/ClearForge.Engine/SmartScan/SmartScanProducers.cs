using ClearForge.Contracts;
using ClearForge.Engine.Duplicates;
using ClearForge.Engine.Privacy;
using ClearForge.Engine.Scanning;
using ClearForge.Engine.Startup;
using ClearForge.Engine.Storage;

namespace ClearForge.Engine.SmartScan;

/// <summary>
/// Catalog-rule scan: all quarantinable categories populate ONE session.
/// Emits one actionable finding per category — bounded regardless of file
/// count — whose candidates commit through the normal preview→commit flow.
/// </summary>
public sealed class CatalogScanProducer : ISmartScanProducer
{
    private readonly ScanSessionStore _scans;
    public string Id => "catalog";
    public int Priority => 10;

    public CatalogScanProducer(ScanSessionStore scans) => _scans = scans;

    public ProducerOutcome Run(SmartScanContext context, CancellationToken ct)
    {
        var session = _scans.CreateExternal();
        // The producer's deadline/cancel token must reach the work the child
        // session performs — Populate only watches session.Cancellation.
        // Registering BEFORE the session starts scanning means a timeout or
        // user cancel interrupts enumeration mid-flight, not just on return.
        using var cancelReg = ct.Register(() => session.Cancellation.Cancel());
        _scans.Populate(session, _scans.QuarantinableCategories.ToArray());
        if (session.Status == SessionStatus.Failed)
            return new ProducerOutcome(Array.Empty<SmartFinding>(), session,
                session.LastError ?? "scan failed");

        var findings = session.Items
            .GroupBy(i => i.Public.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SmartFinding(
                "", Id, g.Key,
                $"{CategoryText.Title(g.Key)} ({g.Count()} file(s))",
                CategoryText.Reason(g.Key) +
                "every item is revalidated " +
                "against its scan snapshot immediately before the quarantine move",
                g.SelectMany(i => new[]
                {
                    $"rule={i.File.RuleId}",
                    $"files={g.Count()}",
                    $"estimatedLogicalBytes={g.Sum(x => x.File.LengthBytes)}",
                }).Distinct()
                .Concat(g.Select(i => i.File.RuleId)
                    .Where(r => session.TruncatedRules.Contains(r))
                    .Distinct()
                    .Select(r => $"enumerationLimitReached={r} — bounded subset, coverage is partial"))
                .ToArray(),
                "high",
                g.Select(i => i.Public.Risk).DefaultIfEmpty(RiskLevel.Low).Max(),
                g.Sum(i => i.File.LengthBytes),
                "quarantinable", true, null, session.Id,
                g.Select(i => i.Public.Id).ToArray()))
            .ToArray();

        return new ProducerOutcome(findings, session,
            session.Status == SessionStatus.Cancelled ? "cancelled" : null);
    }

    /// <summary>
    /// Per-category presentation text: a human-readable name plus the
    /// disposable-evidence statement for the category (why these files are
    /// safe to quarantine). Unknown categories fall back to the generic
    /// catalog wording — the category name is never hidden.
    /// </summary>
    internal static class CategoryText
    {
        private static readonly IReadOnlyDictionary<string, (string Title, string Reason)> Map =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["temporary-files"] = (
                    "Expired temporary files",
                    "files in your temp folder, unchanged for 24h+ — Windows and apps regenerate what they still need; "),
                ["crash-dumps"] = (
                    "Old crash dumps",
                    "application crash dumps kept for diagnostics, unchanged for 24h+ — removing them loses only crash forensics; "),
                ["thumbnail-cache"] = (
                    "Explorer thumbnail & icon cache",
                    "thumbcache_*.db/iconcache_*.db — Explorer regenerates them on demand (same target as Windows' own 'Thumbnail Cache' cleanup); files locked by a running Explorer are skipped, never forced; "),
                ["gpu-shader-cache"] = (
                    "GPU shader caches",
                    "D3D/vendor shader caches — the GPU driver rebuilds them on demand (same target as the documented 'D3D Shader Cache' cleanup); "),
                ["package-manager-caches"] = (
                    "Package manager caches",
                    "documented disposable caches (nuget/npm/yarn/pip 'cache clean'); packages re-download on next install — needs network for remote packages; "),
                ["windows-error-reports"] = (
                    "Windows error reports",
                    "WER report artifacts in ReportQueue/ReportArchive, unchanged for 24h+ (same target as the documented 'Windows Error Reporting Files' cleanup); "),
            };

        public static string Title(string category) =>
            Map.TryGetValue(category, out var t) ? t.Title : $"Expired {category}";

        public static string Reason(string category) =>
            Map.TryGetValue(category, out var t)
                ? t.Reason
                : "matches versioned cleanup catalog; ";
    }
}
/// browser; the session CommitGuard still refuses commit while a contributing
/// browser runs.</summary>
public sealed class BrowserCacheProducer : ISmartScanProducer
{
    private readonly ScanSessionStore _scans;
    private readonly BrowserCacheScanner _cache;
    public string Id => "browser-cache";
    public int Priority => 20;

    public BrowserCacheProducer(ScanSessionStore scans, BrowserCacheScanner cache)
    {
        _scans = scans;
        _cache = cache;
    }

    public ProducerOutcome Run(SmartScanContext context, CancellationToken ct)
    {
        var session = _scans.CreateExternal();
        // Same rule as the catalog producer: the coordinator's deadline must
        // reach the child session's own cancellation source as well as the
        // token threaded through the scan call.
        using var cancelReg = ct.Register(() => session.Cancellation.Cancel());
        try
        {
            _cache.ScanIntoSession(session, ct);
            session.Status = session.Cancellation.IsCancellationRequested
                ? SessionStatus.Cancelled : SessionStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            // The session must never be left in "scanning" — such sessions
            // are exempt from eviction and their latched cancellation makes
            // every later commit item report cancelled.
            session.Status = SessionStatus.Cancelled;
            return new ProducerOutcome(Array.Empty<SmartFinding>(), session, "cancelled");
        }

        var findings = session.Items
            .GroupBy(i => i.File.RuleId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SmartFinding(
                "", Id, BrowserCacheScanner.Category,
                $"{g.Key[(g.Key.IndexOf(':') + 1)..]} browser cache ({g.Count()} file(s))",
                "cache under a detected, closed browser profile; regenerates " +
                "automatically — commit is refused if the browser is running",
                new[] { $"files={g.Count()}",
                        $"estimatedLogicalBytes={g.Sum(x => x.File.LengthBytes)}",
                        "commitGuard=browser-not-running" }
                    .Concat(session.TruncatedRules.Contains(g.Key)
                        ? new[] { $"enumerationLimitReached={g.Key} — bounded subset, coverage is partial" }
                        : Array.Empty<string>())
                    .ToArray(),
                "high", RiskLevel.Low,
                g.Sum(i => i.File.LengthBytes),
                "quarantinable", true, null, session.Id,
                g.Select(i => i.Public.Id).ToArray()))
            .ToArray();

        // A running browser or absent profiles is a scope note, not a crash —
        // the producer completed and found nothing actionable.
        return new ProducerOutcome(findings, session, session.LastError);
    }
}

/// <summary>
/// Duplicate-content scan: one actionable finding per group, offering the
/// redundant copies for quarantine (the newest member is always kept — never
/// selected). Deterministic grouping on unchanged input.
/// </summary>
public sealed class DuplicatesProducer : ISmartScanProducer
{
    private readonly ScanSessionStore _scans;
    private readonly DuplicateFinder _dupes;
    public string Id => "duplicates";
    public int Priority => 30;

    public DuplicatesProducer(ScanSessionStore scans, DuplicateFinder dupes)
    {
        _scans = scans;
        _dupes = dupes;
    }

    public ProducerOutcome Run(SmartScanContext context, CancellationToken ct)
    {
        var session = _scans.CreateExternal();
        using var cancelReg = ct.Register(() => session.Cancellation.Cancel());
        DuplicateReport report;
        try
        {
            report = _dupes.ScanIntoSession(session, context.ScopeIds, ct);
            session.Status = session.Cancellation.IsCancellationRequested
                ? SessionStatus.Cancelled : SessionStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            session.Status = SessionStatus.Cancelled;
            return new ProducerOutcome(Array.Empty<SmartFinding>(), session, "cancelled");
        }

        var pathOf = session.Items.ToDictionary(
            i => i.Public.Id, i => i.File, StringComparer.Ordinal);
        var findings = new List<SmartFinding>();
        foreach (var g in report.Groups.OrderBy(g => g.GroupId, StringComparer.Ordinal))
        {
            // Kept copy: newest write wins, ties break on ordinal path order.
            var members = g.Candidates
                .Select(c => (c.Id, pathOf[c.Id].CanonicalPath, pathOf[c.Id].LastWriteUtc))
                .OrderByDescending(m => m.LastWriteUtc)
                .ThenBy(m => m.CanonicalPath, StringComparer.Ordinal)
                .ToArray();
            var kept = members[0];
            var redundant = members.Skip(1).Select(m => m.Id).ToArray();
            findings.Add(new SmartFinding(
                "", Id, DuplicateFinder.Category,
                $"Duplicate group {g.GroupId} — keep newest, quarantine {redundant.Length} cop{(redundant.Length == 1 ? "y" : "ies")}",
                $"{members.Length} byte-identical files (sha256:{g.Sha256Short}…); " +
                "redundant copies quarantinable — identical content remains on the kept copy",
                members.Select(m => $"{(m.Id == kept.Id ? "keep" : "quarantine")}: {m.CanonicalPath}")
                    .Append($"estimatedLogicalBytes={g.FileBytes * redundant.Length}")
                    .ToArray(),
                "high", RiskLevel.Medium,
                g.FileBytes * redundant.Length,
                "quarantinable", true, null, session.Id, redundant));
        }
        return new ProducerOutcome(findings, session, null);
    }
}

/// <summary>Startup inventory: review-only — this build has no startup
/// modification capability, so entries are informational findings.</summary>
public sealed class StartupProducer : ISmartScanProducer
{
    private readonly IStartupSource _startup;
    public string Id => "startup";
    public int Priority => 40;

    public StartupProducer(IStartupSource startup) => _startup = startup;

    public ProducerOutcome Run(SmartScanContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var findings = _startup.Enumerate()
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new SmartFinding(
                "", Id, "startup-items", $"Startup item '{e.Name}'",
                "autostart entry — informational only; startup modification is " +
                "not supported in this build",
                new[] { $"source={e.Source}", $"target={e.Target}",
                        $"publisher={e.Publisher ?? "unknown"}",
                        $"confidence={e.Confidence}" },
                e.Confidence == "verified" ? "medium" : "low",
                RiskLevel.Medium, 0,
                "manualonly", false,
                "no supported action — review in Windows Settings",
                null, null))
            .ToArray();
        return new ProducerOutcome(findings);
    }
}

/// <summary>Storage review: bounded logical-byte inventory + the deterministic
/// recommendation engine — all findings stay review-only in this build.</summary>
public sealed class StorageReviewProducer : ISmartScanProducer
{
    private readonly StorageInventory _inventory;
    public string Id => "storage-review";
    public int Priority => 50;

    public StorageReviewProducer(StorageInventory inventory) => _inventory = inventory;

    public ProducerOutcome Run(SmartScanContext context, CancellationToken ct)
    {
        var inv = _inventory.Scan(context.ScopeIds, ct);
        var recs = RecommendationEngine.FromInventory(inv);
        var findings = recs.Select(r => new SmartFinding(
                "", Id, r.Category, r.Title, r.Explanation, r.Evidence,
                r.Confidence.ToString().ToLowerInvariant(), r.Risk,
                r.EstimatedLogicalBytes,
                r.Reversibility.ToString().ToLowerInvariant(),
                false,
                r.Action is null
                    ? string.Join("; ", r.Preconditions.Concat(r.SkipReasons)) is { Length: > 0 } s
                        ? s : "review-only — no executable binding"
                    : "review-only",
                null, null))
            .ToArray();
        return new ProducerOutcome(findings, null,
            inv.Partial ? "file budget exhausted — coverage is partial" : null);
    }
}
