using System.IO.Enumeration;
using ClearForge.Engine.Policies;

namespace ClearForge.Engine.Scanning;

/// <summary>Internal scan record. The canonical path never leaves the engine.</summary>
public sealed record ScannedFile(
    string CandidateId,
    string CanonicalPath,
    string RuleId,
    string Category,
    long LengthBytes,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes);

public sealed record ScanLimits(
    int MaxFilesTotal = 50_000,
    int MaxDepth = 32);

/// <summary>
/// Read-only bounded filesystem enumeration over allowlisted roots.
/// Guarantees: never writes, never follows reparse points, never returns a
/// path outside the resolved canonical root, honors cancellation, bounded
/// file count.
/// </summary>
public sealed class FileScanner
{
    private readonly ITrustedRoots _roots;
    private readonly ScanLimits _limits;

    public FileScanner(ITrustedRoots roots, ScanLimits? limits = null)
    {
        _roots = roots;
        _limits = limits ?? new ScanLimits();
    }

    /// <summary>
    /// Enumerate files matching <paramref name="rule"/> under its trusted root.
    /// Files that fail containment, are inaccessible, or exceed limits are
    /// skipped — never thrown. When enumeration is truncated by the rule's
    /// file cap (not by exhaustion), <paramref name="onLimitReached"/> fires
    /// once so the caller can report partial coverage honestly.
    /// </summary>
    public IEnumerable<ScannedFile> Enumerate(
        CleanupRule rule, string sessionId, Func<int, string> candidateIdFor,
        CancellationToken ct, Action? onLimitReached = null)
    {
        var root = _roots.Resolve(rule.RootId);
        if (root is null) yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // Reparse points are never followed — enumeration options alone do
            // not guarantee this on every Windows API path, so we also check
            // attributes per entry below.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
            MaxRecursionDepth = _limits.MaxDepth,
            MatchType = MatchType.Win32,
        };

        // FileSystemEnumerable carries attributes/length/timestamps from the
        // directory record itself — no per-file stat syscall. Pattern
        // filtering happens in the predicate, before any FileInfo exists.
        var entries = new FileSystemEnumerable<FileInfo>(
            root, (ref FileSystemEntry entry) => (entry.ToFileSystemInfo() as FileInfo)!, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if (entry.IsDirectory) return false;
                if (rule.IncludePatterns is { Count: > 0 } patterns)
                {
                    var matched = false;
                    foreach (var p in patterns)
                    {
                        if (System.IO.Enumeration.FileSystemName
                            .MatchesSimpleExpression(p.AsSpan(), entry.FileName,
                                ignoreCase: true))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched) return false;
                }
                return true;
            },
        };

        var seen = 0;
        var limit = Math.Min(rule.MaxFilesPerScan, _limits.MaxFilesTotal);
        using var enumerator = SafeEnumerate(entries).GetEnumerator();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (seen >= limit)
            {
                // The cap — not exhaustion — ended this enumeration: report
                // truncated coverage so findings can say "bounded subset".
                try { onLimitReached?.Invoke(); } catch { }
                yield break;
            }

            FileInfo info;
            try
            {
                if (!enumerator.MoveNext()) yield break;
                info = enumerator.Current;
            // A file→directory swap mid-enumeration yields null — skip, never fatal.
            if (info is null) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // vanishing/locked entries are skipped, not fatal
            }

            // Canonical containment re-check per entry — defense in depth.
            var canonical = TrustedRoots.Canonicalize(info.FullName);
            if (!TrustedRoots.IsContainedIn(root, canonical)) continue;
            // Reparse points are excluded by AttributesToSkip; re-check here
            // as defense in depth on the cached attributes.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            yield return new ScannedFile(
                candidateIdFor(seen),
                canonical,
                rule.Id,
                rule.Category,
                info.Length,
                info.LastWriteTimeUtc,
                info.Attributes);
            seen++;
        }
    }

    /// <summary>Win32 filename-glob match (Explorer/DOS semantics), case-insensitive.</summary>
    internal static bool MatchesAny(IReadOnlyList<string> patterns, string fileName) =>
        patterns.Any(p => System.IO.Enumeration.FileSystemName
            .MatchesSimpleExpression(p, fileName, ignoreCase: true));

    private static IEnumerable<FileInfo> SafeEnumerate(FileSystemEnumerable<FileInfo> entries)
    {
        // Enumeration can throw lazily; wrap the whole sequence.
        IEnumerator<FileInfo> it;
        try
        {
            it = entries.GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }
        using (it)
        {
            while (true)
            {
                FileInfo current;
                try
                {
                    if (!it.MoveNext()) yield break;
                    current = it.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }
                yield return current;
            }
        }
    }
}
