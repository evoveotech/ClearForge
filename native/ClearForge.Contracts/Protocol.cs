using System.Text.Json.Serialization;

namespace ClearForge.Contracts;

/// <summary>Wire protocol version. Bump on any breaking schema change.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1 * 1024 * 1024; // 1 MiB hard frame cap

    public static class Methods
    {
        public const string HealthCheck = "health.check";
        public const string StartScan = "scan.start";
        public const string GetScanResults = "scan.getResults";
        public const string CancelScan = "scan.cancel";
        public const string PreviewCleanup = "cleanup.preview";
        public const string CommitCleanup = "cleanup.commit";
        public const string GetOperationStatus = "operation.getStatus";
        public const string GetCleanupHistory = "history.get";
        public const string CleanupStatus = "cleanup.status";
        public const string CleanupRestore = "cleanup.restore";
        public const string CleanupCancel = "cleanup.cancel";
        public const string DuplicatesStart = "duplicates.start";
        public const string DuplicatesGroups = "duplicates.groups";
        public const string StartupList = "startup.list";
        public const string PrivacyDetect = "privacy.detect";
        public const string PrivacyScanCache = "privacy.scanCache";
        public const string StorageInventory = "storage.inventory";
        public const string StorageRecommend = "storage.recommend";
        public const string StoragePlan = "storage.plan";

        // M8.1 unified smart scan
        public const string SmartScanStart = "smartscan.start";
        public const string SmartScanStatus = "smartscan.status";
        public const string SmartScanResults = "smartscan.results";
        public const string SmartScanCancel = "smartscan.cancel";

        // M7.2 curated model catalogue + install lifecycle
        public const string ModelsCatalog = "models.catalog";
        public const string ModelsStatus = "models.status";
        public const string ModelsInstall = "models.install";
        public const string ModelsCancel = "models.cancel";
        public const string ModelsRemove = "models.remove";
    }

    /// <summary>Stable, UI-safe error codes. Never carry stack traces.</summary>
    public static class Errors
    {
        public const string MalformedRequest = "malformed_request";
        public const string MessageTooLarge = "message_too_large";
        public const string Unauthorized = "unauthorized";
        public const string UnknownMethod = "unknown_method";
        public const string InvalidParams = "invalid_params";
        public const string SessionNotFound = "session_not_found";
        public const string CandidateNotFound = "candidate_not_found";
        public const string PolicyRefused = "policy_refused";
        public const string CleanupNotEnabled = "cleanup_not_enabled";
        public const string Cancelled = "cancelled";
        public const string Internal = "internal";

        // M2 executor outcome codes (per-item, never raw exceptions)
        public const string ItemCleaned = "cleaned";
        public const string ItemAlreadyQuarantined = "already_quarantined";
        public const string SkippedLocked = "skipped_locked";
        public const string SkippedChanged = "skipped_changed";
        public const string SkippedVanished = "skipped_vanished";
        public const string SkippedElevationRequired = "skipped_elevation_required";
        public const string RefusedPolicy = "refused_policy";
        public const string QuarantineFull = "quarantine_full";
        public const string QuarantineCollision = "quarantine_collision";
        public const string CrossVolumeRefused = "cross_volume_refused";
        public const string FailedIo = "failed_io";
        public const string RestoredOk = "restored";
        public const string RestoreConflict = "restore_conflict";
        public const string RestoreSourceMissing = "restore_source_missing";
        public const string RecoveryRequired = "recovery_required";
        public const string OperationNotFound = "operation_not_found";
        public const string SessionExpired = "session_expired";

        // M8.0.3 content-integrity outcomes (per-item, structured)
        public const string IntegrityUnverified = "integrity_unverified";
        public const string IntegrityIoError = "integrity_io_error";
        public const string IntegrityRecoveryRequired = "integrity_recovery_required";
        public const string IntegrityLegacyUnverified = "integrity_legacy_unverified";

        // M8.0.1 approval + session-lifecycle gates
        public const string ApprovalRequired = "approval_required";
        public const string ApprovalStale = "approval_stale";
        public const string ApprovalMismatch = "approval_mismatch";
        public const string SessionCapacity = "session_capacity";

        // M3
        public const string ScopeNotAllowed = "scope_not_allowed";
        public const string BrowserRunning = "browser_running";
        public const string UnsupportedBrowser = "unsupported_browser";
        public const string NotModifiable = "not_modifiable";

        // M7.2 model lifecycle
        public const string ModelUnknown = "model_unknown";
        public const string ModelBusy = "model_busy";
        public const string ModelInUse = "model_in_use";
        public const string ModelInvalid = "model_invalid";
        public const string InsufficientStorage = "insufficient_storage";
        public const string DownloadFailed = "download_failed";
        public const string IntegrityMismatch = "integrity_mismatch";
    }
}

/// <summary>Engine version, stamped at build. Overridable in tests.</summary>
public static class EngineVersion
{
    public static string Value { get; set; } = "0.1.0-m1";
}

public sealed record RpcRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] System.Text.Json.JsonElement? Params);

public sealed record RpcError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record RpcResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("result")] object? Result,
    [property: JsonPropertyName("error")] RpcError? Error)
{
    public static RpcResponse Ok(string id, object result) =>
        new(id, Protocol.Version, result, null);

    public static RpcResponse Fail(string id, string code, string message) =>
        new(id, Protocol.Version, null, new RpcError(code, message));
}
