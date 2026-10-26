namespace ClearForge.Engine.Policies;

/// <summary>
/// One deterministic cleanup rule. Mirrors the versioned rule schema in
/// config/cleanup-rules. Every field is validated at load time.
/// </summary>
public sealed record CleanupRule(
    string Id,
    int Version,
    string Category,
    string Scope,
    string RootId,
    long MinimumAgeHours,
    string Risk,
    bool RequiresConfirmation,
    string Action,
    long MaxFilesPerScan,
    bool ExcludeLocked,
    bool FollowReparsePoints,
    // M8.2: optional filename-only Win32 globs (e.g. "thumbcache_*.db") for
    // roots that mix disposable and non-target files. A pattern can only
    // NARROW a rule — it is matched against the file name inside an already
    // contained, canonical path; it can never widen the rule past its root.
    IReadOnlyList<string>? IncludePatterns = null)
{
    /// <summary>Bound on pattern list size — rules stay reviewable.</summary>
    public const int MaxIncludePatterns = 32;

    /// <summary>Returns null when the rule is structurally valid, else the failure reason.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "rule id is required";
        if (Version < 1) return "rule version must be >= 1";
        if (string.IsNullOrWhiteSpace(Category)) return "category is required";
        if (Scope is not ("current-user" or "machine")) return $"scope '{Scope}' is not supported";
        if (string.IsNullOrWhiteSpace(RootId)) return "root id is required";
        if (MinimumAgeHours < 0) return "minimum_age_hours must be >= 0";
        if (Risk is not ("low" or "medium" or "high" or "restricted")) return $"risk '{Risk}' is not supported";
        if (Action is not ("quarantine" or "report-only")) return $"action '{Action}' is not supported in this build";
        if (MaxFilesPerScan is < 1 or > 1_000_000) return "max_files_per_scan out of bounds";
        if (FollowReparsePoints) return "follow_reparse_points must be false; reparse traversal is not supported";
        return ValidatePatterns();
    }

    /// <summary>
    /// Patterns are filename globs, never paths: any separator, drive or
    /// traversal marker is rejected so a pattern cannot express a location.
    /// </summary>
    private string? ValidatePatterns()
    {
        if (IncludePatterns is null) return null;
        if (IncludePatterns.Count is 0 or > MaxIncludePatterns)
            return $"include_patterns count must be 1..{MaxIncludePatterns}";
        foreach (var p in IncludePatterns)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > 128)
                return "include_patterns entries must be 1..128 chars";
            if (p.Contains("..") || p.Contains('/') || p.Contains('\\')
                || p.Contains(':'))
                return $"include_patterns entry '{p}' must be a filename-only glob";
        }
        return null;
    }
}
