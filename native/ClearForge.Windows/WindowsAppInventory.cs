using ClearForge.Engine.Apps;
using Microsoft.Win32;

namespace ClearForge.Windows;

/// <summary>
/// Production wiring for the installed-app inventory (read-only). Reads the
/// three classic Uninstall hives — HKLM 64-bit, HKLM 32-bit (WOW6432Node),
/// HKCU. Appx/MSIX packages are not covered here; that inventory is a
/// separate milestone with its own evidence rules.
/// </summary>
public static class WindowsAppInventory
{
    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static CompositeAppInventory All() => new(new IAppInventorySource[]
    {
        new UninstallHiveAppSource(
            () => ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64),
            "registry-hklm-64", "machine"),
        new UninstallHiveAppSource(
            () => ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32),
            "registry-hklm-32", "machine"),
        new UninstallHiveAppSource(
            () => ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64),
            "registry-hkcu", "current-user"),
    });

    private static IEnumerable<RawUninstallEntry> ReadHive(
        RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
        if (uninstall is null) yield break;
        foreach (var subName in uninstall.GetSubKeyNames())
        {
            RawUninstallEntry? entry = null;
            try
            {
                using var sub = uninstall.OpenSubKey(subName, writable: false);
                if (sub is not null)
                {
                    entry = new RawUninstallEntry(
                        KeyName: subName,
                        DisplayName: Str(sub, "DisplayName"),
                        Publisher: Str(sub, "Publisher"),
                        DisplayVersion: Str(sub, "DisplayVersion"),
                        InstallLocation: Str(sub, "InstallLocation"),
                        EstimatedSizeKb: Num(sub, "EstimatedSizeInKB"),
                        InstallDate: Str(sub, "InstallDate"),
                        UninstallString: Str(sub, "UninstallString"),
                        SystemComponent: Num(sub, "SystemComponent") is > 0,
                        ParentKeyName: Str(sub, "ParentKeyName"));
                }
            }
            catch { /* a single unreadable subkey must not sink the hive */ }
            if (entry is not null) yield return entry;
        }
    }

    private static string? Str(RegistryKey key, string name) =>
        key.GetValue(name) as string;

    private static long? Num(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => i,
        long l => l,
        _ => null,
    };
}
