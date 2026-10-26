using System.Text.RegularExpressions;

namespace ClearForge.Ai;

/// <summary>
/// Deterministic extraction of explicit storage quantities from goal text.
/// Runs independently of the model: when the user states a target, this
/// parser — not the model — decides the canonical goalBytes. Model output
/// can never override an explicitly typed quantity.
///
/// Unit convention (documented contract): binary multiples, matching the
/// existing product behaviour (M7.1 prompt: "1 GB = 1073741824"; the UI's
/// GB field multiplies by 1024³). SI-prefixed names and IEC aliases both
/// map to the same binary multipliers — never silently mixed:
///   KB/KiB/kilobyte/k = 1024, MB/MiB/megabyte/m = 1024²,
///   GB/GiB/gigabyte/g = 1024³, TB/TiB/terabyte/t = 1024⁴, B/byte = 1.
/// </summary>
public static class GoalQuantities
{
    public const long KiB = 1L << 10;
    public const long MiB = 1L << 20;
    public const long GiB = 1L << 30;
    public const long TiB = 1L << 40;

    /// <summary>Result of scanning a goal string for explicit quantities.</summary>
    public sealed record QuantityParse(
        /// <summary>Distinct byte values found (deduped by value).</summary>
        IReadOnlyList<long> DistinctBytes,
        /// <summary>A quantity was written but exceeds the supported bound.</summary>
        bool OutOfRangeSeen,
        /// <summary>Every raw numeric+unit match, for diagnostics.</summary>
        IReadOnlyList<string> Matches);

    // Longest alternatives first inside each magnitude so "terabytes" beats
    // "t". \b prevents "mb" matching inside "mbps"-style words; the numeric
    // lookbehind rejects signs and mid-number fragments ("v1.5", "-5").
    private static readonly Regex Amount = new(
        @"(?<![-\d.])\d+(?:\.\d+)?\s*" +
        @"(terabytes?|tib|tb|gigabytes?|gib|gb|megabytes?|mib|mb|" +
        @"kilobytes?|kib|kb|bytes?|t|g|m|k|b)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, long> UnitFactors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["b"] = 1, ["byte"] = 1, ["bytes"] = 1,
            ["k"] = KiB, ["kb"] = KiB, ["kib"] = KiB,
            ["kilobyte"] = KiB, ["kilobytes"] = KiB,
            ["m"] = MiB, ["mb"] = MiB, ["mib"] = MiB,
            ["megabyte"] = MiB, ["megabytes"] = MiB,
            ["g"] = GiB, ["gb"] = GiB, ["gib"] = GiB,
            ["gigabyte"] = GiB, ["gigabytes"] = GiB,
            ["t"] = TiB, ["tb"] = TiB, ["tib"] = TiB,
            ["terabyte"] = TiB, ["terabytes"] = TiB,
        };

    /// <summary>
    /// Extract every explicit storage quantity. Bare numbers without a unit
    /// are never guessed; a value followed by "-" is not treated as an
    /// amount's sign; values beyond Int64 overflow or the supported goal
    /// bound are flagged out-of-range rather than clamped.
    /// </summary>
    public static QuantityParse Extract(string goal)
    {
        var values = new List<long>();
        var raw = new List<string>();
        var outOfRange = false;
        foreach (Match m in Amount.Matches(goal))
        {
            raw.Add(m.Value.Trim());
            var token = m.Value.Trim();
            // Split the numeric head from the unit tail.
            var i = 0;
            while (i < token.Length && (char.IsDigit(token[i]) || token[i] == '.')) i++;
            var numPart = token[..i];
            var unitPart = token[i..].Trim();
            if (!UnitFactors.TryGetValue(unitPart, out var factor)) continue;
            if (!double.TryParse(numPart, out var value) || value < 0) continue;
            var bytes = value * factor;
            if (bytes > long.MaxValue || bytes > IntentTask.MaxGoalBytes)
            {
                outOfRange = true;
                continue;
            }
            var v = (long)Math.Round(bytes);
            if (v > 0 && !values.Contains(v)) values.Add(v);
        }
        return new QuantityParse(values, outOfRange, raw);
    }

    /// <summary>Compact human-readable form using the binary convention.</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= TiB => $"{bytes / (double)TiB:0.#} TB",
        >= GiB => $"{bytes / (double)GiB:0.#} GB",
        >= MiB => $"{bytes / (double)MiB:0.#} MB",
        >= KiB => $"{bytes / (double)KiB:0.#} KB",
        _ => $"{bytes} B",
    };
}
