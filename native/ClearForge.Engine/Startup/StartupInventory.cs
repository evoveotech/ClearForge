namespace ClearForge.Engine.Startup;

public sealed record StartupEntry(
    string Name,
    string Source,        // "registry-run-user" | "startup-folder"
    string Target,        // command line or .lnk path
    string? Publisher,    // FileVersionInfo company when resolvable
    string Status,        // enabled | disabled (Run-disabled entries skipped for now)
    string Confidence);   // "verified" | "unknown"

/// <summary>
/// Read-only startup inventory (current user only). Reads HKCU Run and the
/// per-user Startup folder. Never writes — modifications are a separate,
/// separately-tested feature or a Windows Settings handoff.
/// </summary>
public interface IStartupSource
{
    IReadOnlyList<StartupEntry> Enumerate();
}

public sealed class CompositeStartupInventory : IStartupSource
{
    private readonly IReadOnlyList<IStartupSource> _sources;
    public CompositeStartupInventory(IReadOnlyList<IStartupSource> sources) => _sources = sources;

    public IReadOnlyList<StartupEntry> Enumerate() =>
        _sources.SelectMany(s =>
        {
            try { return s.Enumerate(); }
            catch { return Array.Empty<StartupEntry>(); } // per-source failure is non-fatal
        }).ToArray();
}

/// <summary>HKCU\...\Run — current-user autostart registry values.</summary>
public sealed class RunKeyStartupSource : IStartupSource
{
    private readonly Func<IEnumerable<(string Name, string Value)>> _readValues;
    private readonly Func<string, string?> _companyOf;

    /// <summary>Injectable for tests; production wires registry + version info.</summary>
    public RunKeyStartupSource(
        Func<IEnumerable<(string, string)>> readValues,
        Func<string, string?> companyOf)
    {
        _readValues = readValues;
        _companyOf = companyOf;
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();
        foreach (var (name, value) in _readValues())
        {
            var exePath = ExtractExePath(value);
            var publisher = exePath is null ? null : _companyOf(exePath);
            list.Add(new StartupEntry(
                name, "registry-run-user", value, publisher, "enabled",
                publisher is null ? "unknown" : "verified"));
        }
        return list;
    }

    /// <summary>Best-effort exe extraction from a Run command line.</summary>
    internal static string? ExtractExePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIdx > 0 ? s[..(exeIdx + 4)] : s.Split(' ', 2)[0];
    }
}

/// <summary>%APPDATA%\...\Startup folder — per-user autostart files.</summary>
public sealed class StartupFolderSource : IStartupSource
{
    private readonly string _folder;
    private readonly Func<string, string?> _companyOf;

    public StartupFolderSource(string folder, Func<string, string?> companyOf)
    {
        _folder = folder;
        _companyOf = companyOf;
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        if (!Directory.Exists(_folder)) return Array.Empty<StartupEntry>();
        var list = new List<StartupEntry>();
        foreach (var f in Directory.EnumerateFiles(_folder))
        {
            var name = Path.GetFileName(f);
            // .lnk target resolution needs COM (IShellLink) — out of M3 scope;
            // report the link itself honestly.
            string? publisher = f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? _companyOf(f) : null;
            list.Add(new StartupEntry(name, "startup-folder", f, publisher,
                "enabled", publisher is null ? "unknown" : "verified"));
        }
        return list;
    }
}


