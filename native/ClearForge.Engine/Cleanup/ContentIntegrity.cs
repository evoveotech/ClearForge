using System.Security.Cryptography;

namespace ClearForge.Engine.Cleanup;

/// <summary>
/// Integrity provenance persisted in journal.integrity_state (M8.0.3, schema v2).
/// A hash alone is never proof — this column records WHERE in the lifecycle a
/// digest was captured and whether it has ever been confirmed. "legacy" rows
/// predate hashing and are never backfilled.
/// </summary>
public static class JournalIntegrity
{
    /// <summary>Expected hash persisted pre-move; post-move verify pending.</summary>
    public const string Captured = "captured";
    /// <summary>Quarantined artifact verified against the expected hash.</summary>
    public const string Verified = "verified";
    /// <summary>Restored destination verified against the expected hash.</summary>
    public const string Restored = "restored";
    /// <summary>Schema-v1 row; no trustworthy historical hash exists.</summary>
    public const string Legacy = "legacy";
    /// <summary>Legacy row restored; stored hash was observed at restore time
    /// and is NOT proof of the original quarantined content.</summary>
    public const string LegacyRestored = "legacy_restored";
    /// <summary>Last integrity check failed; paired with a non-success journal
    /// state and a structured integrity_* error code.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Streaming content hashing for quarantine evidence. Bounded memory — the
/// whole file is never loaded. Injectable so tests can drive the mutation and
/// failure windows without standing up racy filesystem timing.
/// </summary>
public interface IContentHasher
{
    /// <summary>
    /// Open a file for evidence hashing: read access shared for readers and
    /// delete (rename) but NOT for writers. Holding this handle prevents any
    /// new writer from attaching while the content is hashed and moved; a
    /// rename remains possible, which is why the caller re-validates file
    /// identity after hashing.
    /// </summary>
    FileStream OpenEvidenceRead(string path);

    /// <summary>SHA-256 hex over the full stream content, streaming.</summary>
    string ComputeHash(Stream content);

    /// <summary>Open + hash a path end to end.</summary>
    string ComputeHash(string path);
}

public sealed class Sha256ContentHasher : IContentHasher
{
    public static readonly Sha256ContentHasher Instance = new();
    private const int BufferBytes = 128 * 1024; // bounded streaming window

    private Sha256ContentHasher() { }

    public FileStream OpenEvidenceRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, BufferBytes, FileOptions.SequentialScan);

    public string ComputeHash(Stream content)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[BufferBytes];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public string ComputeHash(string path)
    {
        using var s = OpenEvidenceRead(path);
        return ComputeHash(s);
    }
}

public static class ContentIntegrity
{
    /// <summary>True when the value is a well-formed SHA-256 hex digest.
    /// Anything else — including a null from a legacy row — is unverifiable.</summary>
    public static bool IsWellFormedSha256(string? hex) =>
        hex is { Length: 64 } && hex.All(Uri.IsHexDigit);
}
