using ClearForge.Contracts;

namespace ClearForge.Engine.Storage;

public enum Confidence { High, Medium, Low }
public enum Reversibility { Quarantinable, SupportedApi, ManualOnly, Unsupported }

/// <summary>
/// Binding from a recommendation to a real, session-bound executable action.
/// The only supported kind in this build is "quarantine" through the existing
/// preview → commit → journal pipeline — no second mutation path exists.
/// </summary>
public sealed record RecommendationAction(
    string Kind,              // "quarantine"
    string SessionId,         // owning scan session; engine-validated at commit
    string Status,            // session status when the action was bound
    int CandidateCount,
    long EligibleBytes);

/// <summary>Deterministic, evidence-backed recommendation. Never auto-acts.</summary>
public sealed record Recommendation(
    string Id,
    string Category,
    string Title,
    string Explanation,
    IReadOnlyList<string> Evidence,
    long EstimatedLogicalBytes,
    Confidence Confidence,
    RiskLevel Risk,
    Reversibility Reversibility,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> SkipReasons,
    RecommendationAction? Action = null) // null → review-only, never executable
{
    /// <summary>
    /// Opaque action reference, "session:&lt;id&gt;" when bound to a real
    /// session, else null. Kept for the wire contract; the structured
    /// <see cref="Action"/> carries the validated details.
    /// </summary>
    public string? ActionRef => Action is null ? null : $"session:{Action.SessionId}";
    public double Score => Confidence switch
    {
        Confidence.High => EstimatedLogicalBytes * 1.0,
        Confidence.Medium => EstimatedLogicalBytes * 0.6,
        _ => EstimatedLogicalBytes * 0.3,
    } - Risk switch
    {
        RiskLevel.Low => 0, RiskLevel.Medium => EstimatedLogicalBytes * 0.1,
        _ => EstimatedLogicalBytes * 0.5,
    };
}

public sealed record StoragePlan(
    long GoalBytes,
    long EstimatedTotalBytes,
    bool LikelyAchievable,
    IReadOnlyList<Recommendation> Selected,
    IReadOnlyList<PlanSkippedItem> Skipped,
    IReadOnlyList<string> Explanations,
    IReadOnlyList<string> MissingEvidence);

/// <summary>One plan candidate held out of the selected set, with its reason.</summary>
public sealed record PlanSkippedItem(string Id, string Title, string Reason);

/// <summary>
/// User constraints for planning. These can only narrow the eligible set —
/// they never override engine policy or turn review-only items executable.
/// </summary>
public sealed record PlanConstraints(
    IReadOnlyCollection<string>? ExcludeIds = null,
    IReadOnlyCollection<string>? ExcludeCategories = null,
    IReadOnlyCollection<string>? ExcludeScopes = null);

/// <summary>
/// Builds recommendations from observed evidence only. Eligibility comes
/// from explicit rules — unknowns stay review-only; no estimate is ever
/// presented as guaranteed savings.
/// </summary>
public static class RecommendationEngine
{
    public static IReadOnlyList<Recommendation> FromInventory(StorageInventoryReport inv)
    {
        var recs = new List<Recommendation>();
        foreach (var (scope, nodes) in inv.Scopes)
        {
            foreach (var n in nodes.Where(n => n.LogicalBytes > 0))
            {
                if (n.CategoryHint is null)
                {
                    recs.Add(new Recommendation(
                        StableId("rev", scope, n.Name),
                        "unclassified", $"Review '{n.Name}'",
                        "Large directory with no matching cleanup rule. Kept review-only — unknowns are never auto-actionable.",
                        new[] { $"size={n.LogicalBytes}B", $"scope={scope}", "no rule match" },
                        n.LogicalBytes, Confidence.Low, RiskLevel.Medium,
                        Reversibility.ManualOnly,
                        new[] { "user review required" },
                        Array.Empty<string>(), null));
                    continue;
                }
                recs.Add(new Recommendation(
                    StableId("dev", scope, n.Name),
                    n.CategoryHint, $"Regenerable build output '{n.Name}'",
                    $"{n.CategoryHint} detected via corroborating marker file; regenerates on next build. " +
                    "Bulk directory quarantine is not enabled in this build — kept review-only.",
                    new[] { $"size={n.LogicalBytes}B", $"scope={scope}",
                            $"marker-verified category={n.CategoryHint}" },
                    n.LogicalBytes, Confidence.Medium, RiskLevel.Medium,
                    Reversibility.Quarantinable,
                    new[] { "no active build using this directory",
                            "containment + identity revalidated at commit" },
                    new[] { "active builds may regenerate or lock files" },
                    null)); // review-only: bulk directory quarantine out of scope (M4.2)
            }
        }
        return recs.OrderByDescending(r => r.Score)
                   .ThenBy(r => r.Id, StringComparer.Ordinal)
                   .ToArray();
    }

    /// <summary>Process-stable id fragment (string.GetHashCode is randomized).</summary>
    private static string StableId(string prefix, string scope, string name)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var ch in scope + '\u001f' + name) { h ^= ch; h *= 1099511628211UL; }
            return $"{prefix}-{scope}-{h:x8}";
        }
    }

    /// <summary>
    /// Plan toward a byte goal from ranked eligible recommendations.
    /// Never claims the goal when evidence can't support it.
    /// </summary>
    public static StoragePlan Plan(long goalBytes, IReadOnlyList<Recommendation> ranked,
        PlanConstraints? constraints = null)
    {
        var excludeIds = constraints?.ExcludeIds is { } ids
            ? new HashSet<string>(ids, StringComparer.Ordinal) : null;
        var excludeCats = constraints?.ExcludeCategories is { } cats
            ? new HashSet<string>(cats, StringComparer.OrdinalIgnoreCase) : null;
        var excludeScopes = constraints?.ExcludeScopes is { } scopes
            ? new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase) : null;

        var selected = new List<Recommendation>();
        var skipped = new List<PlanSkippedItem>();
        var explanations = new List<string>();
        long sum = 0;

        foreach (var r in ranked)
        {
            string? reason = null;
            if (excludeIds?.Contains(r.Id) == true) reason = "excluded by user";
            else if (excludeCats?.Contains(r.Category) == true) reason = $"category '{r.Category}' excluded by user";
            else if (excludeScopes is not null && r.Evidence.Any(e => e.StartsWith("scope=", StringComparison.Ordinal)
                    && excludeScopes.Contains(e["scope=".Length..]))) reason = "scope excluded by user";
            else if (r.Reversibility is Reversibility.Unsupported or Reversibility.ManualOnly)
                reason = $"{r.Reversibility} (needs review, not an action)";
            else if (r.Action is null) reason = "review-only — no executable binding";

            if (reason is not null)
            {
                skipped.Add(new PlanSkippedItem(r.Id, r.Title, reason));
                explanations.Add($"'{r.Title}' skipped: {reason}");
                continue;
            }
            selected.Add(r);
            sum += (long)(r.EstimatedLogicalBytes *
                (r.Confidence == Confidence.High ? 0.9 : r.Confidence == Confidence.Medium ? 0.6 : 0.3));
            if (sum >= goalBytes) break;
        }

        var achievable = sum >= goalBytes;
        var missing = achievable
            ? Array.Empty<string>()
            : new[] { $"goal={goalBytes}B exceeds safe-estimate total={sum}B; " +
                      "no additional eligible evidence — target not promised" };
        return new StoragePlan(goalBytes, sum, achievable, selected, skipped, explanations, missing);
    }
}
