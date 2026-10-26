using ClearForge.Engine.Policies;

namespace ClearForge.Windows;

/// <summary>
/// M1 rule catalog — compiled-in, versioned, reviewed. Mirrors
/// config/cleanup-rules/*.yaml (the YAML is the human-review surface; the
/// compiled set is the executed truth — they are kept in sync by tests).
/// M8.2 extended the catalog with evidence-backed disposable-cache categories;
/// every rule still targets a bounded, current-user trusted root.
/// </summary>
public static class DefaultRules
{
    public static IReadOnlyList<CleanupRule> M1() => new[]
    {
        new CleanupRule(
            Id: "user-temp-expired-files",
            Version: 1,
            Category: "temporary-files",
            Scope: "current-user",
            RootId: WindowsRoots.UserTemp,
            MinimumAgeHours: 24,
            Risk: "low",
            RequiresConfirmation: true,
            Action: "quarantine",
            MaxFilesPerScan: 50_000,
            ExcludeLocked: true,
            FollowReparsePoints: false),
        new CleanupRule(
            Id: "user-crash-dumps",
            Version: 1,
            Category: "crash-dumps",
            Scope: "current-user",
            RootId: WindowsRoots.UserCrashDumps,
            MinimumAgeHours: 24,
            Risk: "medium",
            RequiresConfirmation: true,
            Action: "quarantine",
            MaxFilesPerScan: 10_000,
            ExcludeLocked: true,
            FollowReparsePoints: false),

        // M8.2 — Explorer thumbnail/icon cache. The root mixes target and
        // non-target files, so the rule narrows by exact filename patterns;
        // Explorer usually holds these open — locked files report
        // skipped_locked rather than being forced.
        new CleanupRule(
            Id: "user-thumbnail-cache",
            Version: 1,
            Category: "thumbnail-cache",
            Scope: "current-user",
            RootId: WindowsRoots.UserThumbnailCache,
            MinimumAgeHours: 24,
            Risk: "low",
            RequiresConfirmation: true,
            Action: "quarantine",
            MaxFilesPerScan: 10_000,
            ExcludeLocked: true,
            FollowReparsePoints: false,
            IncludePatterns: new[] { "thumbcache_*.db", "iconcache_*.db" }),

        // M8.2 — GPU shader caches: pure regenerable caches matching the
        // observed "D3D Shader Cache" cleanup handler; vendor roots are
        // registered only when present.
        GpuRule("user-d3d-shader-cache", WindowsRoots.UserD3DShaderCache),
        GpuRule("user-nvidia-dx-shader-cache", WindowsRoots.UserNvidiaDxShaderCache),
        GpuRule("user-nvidia-gl-shader-cache", WindowsRoots.UserNvidiaGlShaderCache),
        GpuRule("user-amd-dx-shader-cache", WindowsRoots.UserAmdDxShaderCache),
        GpuRule("user-amd-gl-shader-cache", WindowsRoots.UserAmdGlShaderCache),

        // M8.2 — package-manager caches: documented disposable caches
        // (`dotnet nuget locals`, `npm cache`, `yarn cache`, `pip cache`).
        // Regeneration needs network for remote packages → medium risk.
        PackageCacheRule("user-nuget-http-cache", WindowsRoots.UserNuGetHttpCache),
        PackageCacheRule("user-nuget-plugins-cache", WindowsRoots.UserNuGetPluginsCache),
        PackageCacheRule("user-npm-cache", WindowsRoots.UserNpmCache),
        PackageCacheRule("user-yarn-cache", WindowsRoots.UserYarnCache),
        PackageCacheRule("user-pip-cache", WindowsRoots.UserPipCache),

        // M8.2 — Windows Error Reporting report artifacts (user-approved;
        // mirrors the observed "Windows Error Reporting Files" handler).
        new CleanupRule(
            Id: "user-wer-report-queue",
            Version: 1,
            Category: "windows-error-reports",
            Scope: "current-user",
            RootId: WindowsRoots.UserWerReportQueue,
            MinimumAgeHours: 24,
            Risk: "medium",
            RequiresConfirmation: true,
            Action: "quarantine",
            MaxFilesPerScan: 10_000,
            ExcludeLocked: true,
            FollowReparsePoints: false),
        new CleanupRule(
            Id: "user-wer-report-archive",
            Version: 1,
            Category: "windows-error-reports",
            Scope: "current-user",
            RootId: WindowsRoots.UserWerReportArchive,
            MinimumAgeHours: 24,
            Risk: "medium",
            RequiresConfirmation: true,
            Action: "quarantine",
            MaxFilesPerScan: 10_000,
            ExcludeLocked: true,
            FollowReparsePoints: false),
    };

    private static CleanupRule GpuRule(string id, string rootId) => new(
        Id: id,
        Version: 1,
        Category: "gpu-shader-cache",
        Scope: "current-user",
        RootId: rootId,
        MinimumAgeHours: 24,
        Risk: "low",
        RequiresConfirmation: true,
        Action: "quarantine",
        MaxFilesPerScan: 50_000,
        ExcludeLocked: true,
        FollowReparsePoints: false);

    private static CleanupRule PackageCacheRule(string id, string rootId) => new(
        Id: id,
        Version: 1,
        Category: "package-manager-caches",
        Scope: "current-user",
        RootId: rootId,
        MinimumAgeHours: 24,
        Risk: "medium",
        RequiresConfirmation: true,
        Action: "quarantine",
        MaxFilesPerScan: 50_000,
        ExcludeLocked: true,
        FollowReparsePoints: false);
}
