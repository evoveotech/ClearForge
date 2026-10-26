using System.Globalization;

namespace ClearForge.Engine.Apps;

/// <summary>
/// One installed-application record surfaced to the client. Interpretation
/// (classification, size conversion, confidence) is owned here; the platform
/// layer only produces raw hive rows.
/// </summary>
public sealed record InstalledApp(
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    long? SizeBytes,          // EstimatedSizeInKB * 1024; null when unreported
    string? InstallDate,      // ISO yyyy-MM-dd when the hive format parses; raw otherwise
    string Source,            // "registry-hklm-64" | "registry-hklm-32" | "registry-hkcu"
    string Scope,             // "machine" | "current-user"
    string Kind,              // "app" | "system-component" | "update"
    bool HasUninstaller,      // an UninstallString is registered
    string Confidence);       // "registered" | "listed-only"

/// <summary>
/// Raw uninstall-hive row as read by the platform layer. Keep this a faithful
/// transcription — no interpretation beyond type coercion — so fixtures test
/// the real classification logic.
/// </summary>
public sealed record RawUninstallEntry(
    string KeyName,
    string? DisplayName,
    string? Publisher,
    string? DisplayVersion,
    string? InstallLocation,
    long? EstimatedSizeKb,
    string? InstallDate,
    string? UninstallString,
    bool SystemComponent,
    string? ParentKeyName);

/// <summary>Read-only installed-application inventory.</summary>
public interface IAppInventorySource
{
    IReadOnlyList<InstalledApp> Enumerate();
}

/// <summary>Fans out across sources; a failing source never sinks the rest.</summary>
public sealed class CompositeAppInventory : IAppInventorySource
{
    private readonly IReadOnlyList<IAppInventorySource> _sources;
    public CompositeAppInventory(IReadOnlyList<IAppInventorySource> sources) => _sources = sources;

    public IReadOnlyList<InstalledApp> Enumerate() => _sources
        .SelectMany(s =>
        {
            try { return s.Enumerate(); }
            catch { return Array.Empty<InstalledApp>(); } // per-source failure is non-fatal
        })
        .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>
/// One registry Uninstall hive (HKLM 64-bit, HKLM 32-bit, or HKCU). Entries
/// without a display name are skipped — a record the OS itself would not show
/// is not evidence a user-facing app exists. No dedup across hives: the same
/// product legitimately registers per-machine and per-user, and the source is
/// reported honestly instead of guessed away.
/// </summary>
public sealed class UninstallHiveAppSource : IAppInventorySource
{
    private readonly Func<IEnumerable<RawUninstallEntry>> _read;
    private readonly string _source;
    private readonly string _scope;

    /// <summary>Injectable for tests; production wires real registry reads.</summary>
    public UninstallHiveAppSource(
        Func<IEnumerable<RawUninstallEntry>> read, string source, string scope)
    {
        _read = read;
        _source = source;
        _scope = scope;
    }

    public IReadOnlyList<InstalledApp> Enumerate()
    {
        var list = new List<InstalledApp>();
        foreach (var e in _read())
        {
            if (string.IsNullOrWhiteSpace(e.DisplayName)) continue;
            var hasUninstaller = !string.IsNullOrWhiteSpace(e.UninstallString);
            var kind = e.SystemComponent ? "system-component"
                : !string.IsNullOrWhiteSpace(e.ParentKeyName) ? "update"
                : "app";
            list.Add(new InstalledApp(
                e.DisplayName.Trim(),
                NullIfBlank(e.Publisher),
                NullIfBlank(e.DisplayVersion),
                NullIfBlank(e.InstallLocation),
                e.EstimatedSizeKb is { } kb and >= 0 ? kb * 1024 : null,
                NormalizeInstallDate(e.InstallDate),
                _source,
                _scope,
                kind,
                hasUninstaller,
                hasUninstaller ? "registered" : "listed-only"));
        }
        return list;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Registry InstallDate is usually yyyyMMdd; normalize that one
    /// format to ISO and pass anything else through untouched.</summary>
    internal static string? NormalizeInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        return s.Length == 8
            && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : s;
    }
}
