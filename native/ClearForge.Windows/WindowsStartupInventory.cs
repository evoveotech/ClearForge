using System.Diagnostics;
using ClearForge.Engine.Startup;
using Microsoft.Win32;

namespace ClearForge.Windows;

/// <summary>
/// Production wiring (read-only). M8.5 expanded coverage: HKCU Run and
/// RunOnce, HKLM Run and RunOnce (64- and 32-bit views where distinct), each
/// joined to its StartupApproved\Run state blob so disabled entries are
/// reported with their real status instead of being invisible. Startup
/// Approved blobs are not redirected — the HKLM lookup is shared across the
/// 64/32-bit Run views. Task Scheduler and services are a later milestone.
/// </summary>
public static class WindowsStartupInventory
{
    private const string RunPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRunPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static CompositeStartupInventory All() => new(new IStartupSource[]
    {
        new RunKeyStartupSource(
            () => ReadValues(RegistryHive.CurrentUser, RegistryView.Default, RunPath),
            PublisherOf, "registry-hkcu-run",
            ApprovalOf(RegistryHive.CurrentUser, RegistryView.Default)),
        new RunKeyStartupSource(
            () => ReadValues(RegistryHive.CurrentUser, RegistryView.Default, RunOncePath),
            PublisherOf, "registry-hkcu-runonce",
            ApprovalOf(RegistryHive.CurrentUser, RegistryView.Default)),
        new RunKeyStartupSource(
            () => ReadValues(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath),
            PublisherOf, "registry-hklm64-run",
            ApprovalOf(RegistryHive.LocalMachine, RegistryView.Registry64)),
        new RunKeyStartupSource(
            () => ReadValues(RegistryHive.LocalMachine, RegistryView.Registry64, RunOncePath),
            PublisherOf, "registry-hklm64-runonce",
            ApprovalOf(RegistryHive.LocalMachine, RegistryView.Registry64)),
        new RunKeyStartupSource(
            () => ReadValues(RegistryHive.LocalMachine, RegistryView.Registry32, RunPath),
            PublisherOf, "registry-hklm32-run",
            ApprovalOf(RegistryHive.LocalMachine, RegistryView.Registry64)),
        new StartupFolderSource(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), PublisherOf),
    });

    private static IEnumerable<(string, string)> ReadValues(
        RegistryHive hive, RegistryView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(path, writable: false);
        if (key is null) yield break;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string v) yield return (name, v);
        }
    }

    private static Func<string, byte[]?> ApprovalOf(
        RegistryHive hive, RegistryView view) => name =>
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view)
                .OpenSubKey(ApprovedRunPath, writable: false);
            return key?.GetValue(name) as byte[];
        }
        catch { return null; } // absent/inaccessible approval hive = enabled
    };

    private static string? PublisherOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var vi = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName;
        }
        catch { return null; }
    }
}
