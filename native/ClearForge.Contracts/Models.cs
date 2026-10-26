using System.Text.Json.Serialization;

namespace ClearForge.Contracts;

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Restricted
}

/// <summary>
/// One classified file found by a scan. Id is opaque and session-bound;
/// the canonical path is never sent to the client.
/// </summary>
public sealed record ScanCandidate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("estimatedBytes")] long EstimatedBytes,
    [property: JsonPropertyName("risk")] RiskLevel Risk,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("requiresExplicitConfirmation")] bool RequiresExplicitConfirmation);

public sealed record CleanupPreview(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("estimatedBytes")] long EstimatedBytes,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ScanCandidate> Candidates);

public sealed record CleanupItemResult(
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bytesReclaimed")] long BytesReclaimed,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record StartScanRequest(
    [property: JsonPropertyName("categories")] IReadOnlyList<string>? Categories);

public sealed record ScanSessionInfo(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("estimatedBytes")] long EstimatedBytes);

public sealed record ScanResultsResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("estimatedBytes")] long EstimatedBytes,
    [property: JsonPropertyName("categories")] IReadOnlyDictionary<string, int> CategoryCounts,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ScanCandidate> Candidates);

public sealed record SessionRequest(
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record PreviewCleanupRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<string> CandidateIds);

public sealed record CommitCleanupRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<string> CandidateIds);

public sealed record CleanupCommitResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requested")] int Requested,
    [property: JsonPropertyName("processed")] int Processed,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("items")] IReadOnlyList<CleanupItemResult> Items,
    // Measured AvailableFreeSpace delta on the quarantine volume across the
    // commit, or null when unmeasurable. Informational only: quarantine moves
    // stay on the same volume, so ~0 is expected — quarantined bytes are NOT
    // reclaimed disk space.
    [property: JsonPropertyName("freeSpaceDeltaBytes")] long? FreeSpaceDeltaBytes = null);

public sealed record RestoreRequest(
    [property: JsonPropertyName("operationIds")] IReadOnlyList<string> OperationIds);

public sealed record RestoreItemResult(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record JournalEntryDto(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("updatedUtc")] string UpdatedUtc,
    [property: JsonPropertyName("integrity")] string? Integrity = null);

public sealed record HistoryResponse(
    [property: JsonPropertyName("entries")] IReadOnlyList<JournalEntryDto> Entries);

// --- M3 feature DTOs ---

public sealed record DuplicatesStartRequest(
    [property: JsonPropertyName("scopeIds")] IReadOnlyList<string> ScopeIds);

public sealed record DuplicateGroupDto(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("sha256Short")] string Sha256Short,
    [property: JsonPropertyName("fileBytes")] long FileBytes,
    [property: JsonPropertyName("copies")] int Copies,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<string> CandidateIds);

public sealed record DuplicatesGroupsResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("groups")] IReadOnlyList<DuplicateGroupDto> Groups,
    [property: JsonPropertyName("estimatedRecoverableBytes")] long EstimatedRecoverableBytes);

public sealed record StartupEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("publisher")] string? Publisher,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("modifiable")] bool Modifiable);

public sealed record StartupListResponse(
    [property: JsonPropertyName("entries")] IReadOnlyList<StartupEntryDto> Entries,
    [property: JsonPropertyName("note")] string Note);

public sealed record PrivacyCategoryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("unavailableReason")] string? UnavailableReason);

public sealed record BrowserProfileDto(
    [property: JsonPropertyName("browser")] string Browser,
    [property: JsonPropertyName("profileName")] string ProfileName,
    [property: JsonPropertyName("browserRunning")] bool BrowserRunning,
    [property: JsonPropertyName("cacheBytes")] long CacheBytes,
    [property: JsonPropertyName("categories")] IReadOnlyDictionary<string, PrivacyCategoryDto> Categories);

public sealed record PrivacyDetectResponse(
    [property: JsonPropertyName("profiles")] IReadOnlyList<BrowserProfileDto> Profiles);

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("engineVersion")] string EngineVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("pid")] int? Pid = null);
