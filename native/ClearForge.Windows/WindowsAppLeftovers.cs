using ClearForge.Engine.Apps;

namespace ClearForge.Windows;

/// <summary>
/// Production leftovers roots + directory enumeration for the per-app
/// leftover map (read-only). Only three bounded roots — per-user Roaming,
/// per-user Local, and shared ProgramData — matching the documented M8.4
/// scope. No machine-wide sweep.
/// </summary>
public static class WindowsAppLeftovers
{
    public static IReadOnlyDictionary<string, string> Roots()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string path) { if (Directory.Exists(path)) map[id] = path; }
        Add("appdata-roaming",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add("appdata-local",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add("programdata",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return map;
    }

    public static IEnumerable<LeftoverDir> ChildrenOf(string path)
    {
        foreach (var d in Directory.EnumerateDirectories(path))
            yield return new LeftoverDir(d, Path.GetFileName(d)!);
    }
}
