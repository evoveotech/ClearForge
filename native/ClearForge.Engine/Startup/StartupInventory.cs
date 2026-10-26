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

/// <summary>
/// Decodes a StartupApproved\Run (REG_BINARY) state blob. Third-party
/// documented, not official: byte 0 = 0x02/0x03 enabled, 0x06 disabled.
/// Anything unrecognized reports "unknown" — never guessed.
/// </summary>
public static class StartupApprovedState
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Unknown = "unknown";

    public static string Decode(byte[]? blob) => blob is { Length: >= 1 }
        ? blob[0] switch
        {
            0x02 or 0x03 => Enabled,
            0x06 => Disabled,
            _ => Unknown,
        }
        : Enabled; // no approval record = enabled
}

/// <summary>HKCU/HKLM Run or RunOnce autostart registry values. The approval
/// lookup (when wired) joins each value name to its StartupApproved blob so
/// disabled entries are reported instead of silently skipped.</summary>
public sealed class RunKeyStartupSource : IStartupSource
{
    private readonly Func<IEnumerable<(string Name, string Value)>> _readValues;
    private readonly Func<string, string?> _companyOf;
    private readonly string _source;
    private readonly Func<string, byte[]?>? _approvalOf;

    /// <summary>Injectable for tests; production wires registry + version info.</summary>
    public RunKeyStartupSource(
        Func<IEnumerable<(string, string)>> readValues,
        Func<string, string?> companyOf,
        string source = "registry-run-user",
        Func<string, byte[]?>? approvalOf = null)
    {
        _readValues = readValues;
        _companyOf = companyOf;
        _source = source;
        _approvalOf = approvalOf;
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();
        foreach (var (name, value) in _readValues())
        {
            var exePath = ExtractExePath(value);
            var publisher = exePath is null ? null : _companyOf(exePath);
            var status = _approvalOf is null
                ? StartupApprovedState.Enabled
                : StartupApprovedState.Decode(_approvalOf(name));
            list.Add(new StartupEntry(
                name, _source, value, publisher, status,
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


