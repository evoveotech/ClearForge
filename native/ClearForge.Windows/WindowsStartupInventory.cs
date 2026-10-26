using System.Diagnostics;
using ClearForge.Engine.Startup;

namespace ClearForge.Windows;

/// <summary>Production wiring (current user, read-only). Lives in the Windows
/// layer because it touches the registry and file-version metadata.</summary>
public static class WindowsStartupInventory
{
    public static CompositeStartupInventory CurrentUser() => new(new IStartupSource[]
    {
        new RunKeyStartupSource(ReadRunKey, PublisherOf),
        new StartupFolderSource(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), PublisherOf),
    });

    private static IEnumerable<(string, string)> ReadRunKey()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
        if (key is null) yield break;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string v) yield return (name, v);
        }
    }

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
