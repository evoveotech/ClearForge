using ClearForge.Contracts;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;

namespace ClearForge.Engine.Classification;

/// <summary>
/// Deterministic classifier: maps a scanned file to a RiskLevel + human reason
/// using only the matched rule and file metadata. Never infers safety from
/// name/extension alone — rule membership + age + containment decide.
/// </summary>
public static class Classifier
{
    public static RiskLevel ToRiskLevel(CleanupRule rule) => rule.Risk switch
    {
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        "restricted" => RiskLevel.Restricted,
        _ => RiskLevel.Restricted, // unknown => most restrictive
    };

    /// <summary>
    /// Classify a scanned file. Returns null when the file does not meet the
    /// rule's threshold (e.g., too new) and is not a candidate at all.
    /// </summary>
    public static ScanCandidate? TryClassify(
        ScannedFile file, CleanupRule rule, DateTimeOffset nowUtc)
    {
        var age = nowUtc - file.LastWriteUtc;
        if (age < TimeSpan.FromHours(rule.MinimumAgeHours))
            return null;

        var risk = ToRiskLevel(rule);
        var reason = $"{rule.Category}: matched rule {rule.Id} v{rule.Version} " +
                     $"(age {Math.Floor(age.TotalHours)}h >= {rule.MinimumAgeHours}h, inside root '{rule.RootId}')";

        // M8.2: name the exact filename pattern that matched when the rule
        // carries one — the evidence cites the pattern, not just the rule.
        if (rule.IncludePatterns is { Count: > 0 } patterns)
        {
            var name = Path.GetFileName(file.CanonicalPath);
            var matched = patterns.FirstOrDefault(p =>
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    p, name, ignoreCase: true));
            if (matched is not null) reason += $", name matches '{matched}'";
        }

        var displayName = Path.GetFileName(file.CanonicalPath);
        if (string.IsNullOrEmpty(displayName)) displayName = file.CanonicalPath;

        return new ScanCandidate(
            file.CandidateId,
            file.RuleId,
            file.Category,
            displayName,
            file.LengthBytes,
            risk,
            reason,
            rule.RequiresConfirmation);
    }
}
