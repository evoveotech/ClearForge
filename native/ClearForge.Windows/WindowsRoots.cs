using ClearForge.Engine.Policies;

namespace ClearForge.Windows;

/// <summary>
/// Production trusted-root registry: current-user locations only.
/// Machine-scope roots (Windows temp, component store, delivery optimization,
/// etc.) are deliberately absent — they require elevation and are out of scope.
/// Every root is registered only when the directory actually exists; a missing
/// vendor/tooling dir simply produces no candidates.
/// </summary>
public static class WindowsRoots
{
    public const string UserTemp = "user-temp";
    public const string UserCrashDumps = "user-crash-dumps";

    // M8.2 coverage — all under %LOCALAPPDATA% (SpecialFolder.LocalApplicationData
    // follows the real profile, including redirected/unusual layouts; env-var
    // overrides are deliberately NOT honored for these well-known locations —
    // only TEMP/TMP carry user-controlled values, matching M1).
    public const string UserThumbnailCache = "user-thumbnail-cache";
    public const string UserD3DShaderCache = "user-d3d-shader-cache";
    public const string UserNvidiaDxShaderCache = "user-nvidia-dx-shader-cache";
    public const string UserNvidiaGlShaderCache = "user-nvidia-gl-shader-cache";
    public const string UserAmdDxShaderCache = "user-amd-dx-shader-cache";
    public const string UserAmdGlShaderCache = "user-amd-gl-shader-cache";
    public const string UserNuGetHttpCache = "user-nuget-http-cache";
    public const string UserNuGetPluginsCache = "user-nuget-plugins-cache";
    public const string UserNpmCache = "user-npm-cache";
    public const string UserYarnCache = "user-yarn-cache";
    public const string UserPipCache = "user-pip-cache";
    public const string UserWerReportQueue = "user-wer-report-queue";
    public const string UserWerReportArchive = "user-wer-report-archive";

    public static ITrustedRoots CurrentUser()
    {
        var roots = new TrustedRoots();
        var temp = Environment.GetEnvironmentVariable("TEMP")
                   ?? Path.Combine(LocalAppData(), "Temp");
        roots.Add(UserTemp, temp);

        var crashDumps = Path.Combine(LocalAppData(), "CrashDumps");
        if (Directory.Exists(crashDumps))
            roots.Add(UserCrashDumps, crashDumps);

        // M8.2: bounded disposable-cache locations. Each is registered only
        // when present — the categories degrade gracefully on machines where
        // the vendor/tool was never installed.
        AddIfExists(roots, UserThumbnailCache,
            "Microsoft", "Windows", "Explorer");
        AddIfExists(roots, UserD3DShaderCache, "D3DSCache");
        AddIfExists(roots, UserNvidiaDxShaderCache, "NVIDIA", "DXCache");
        AddIfExists(roots, UserNvidiaGlShaderCache, "NVIDIA", "GLCache");
        AddIfExists(roots, UserAmdDxShaderCache, "AMD", "DxCache");
        AddIfExists(roots, UserAmdGlShaderCache, "AMD", "GLCache");
        AddIfExists(roots, UserNuGetHttpCache, "NuGet", "v3-cache");
        AddIfExists(roots, UserNuGetPluginsCache, "NuGet", "plugins-cache");
        AddIfExists(roots, UserNpmCache, "npm-cache");
        AddIfExists(roots, UserYarnCache, "Yarn", "Cache");
        AddIfExists(roots, UserPipCache, "pip", "Cache");
        AddIfExists(roots, UserWerReportQueue,
            "Microsoft", "Windows", "WER", "ReportQueue");
        AddIfExists(roots, UserWerReportArchive,
            "Microsoft", "Windows", "WER", "ReportArchive");

        return roots;
    }

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static void AddIfExists(TrustedRoots roots, string rootId, params string[] parts)
    {
        var path = Path.Combine(new[] { LocalAppData() }.Concat(parts).ToArray());
        if (Directory.Exists(path))
            roots.Add(rootId, path);
    }
}
