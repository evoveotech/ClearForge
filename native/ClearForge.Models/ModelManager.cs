using System.Security.Cryptography;

namespace ClearForge.Models;

/// <summary>
/// Owns the model install lifecycle inside the application-owned model
/// root: download → verify → atomic stage-in → ready, plus pause/resume,
/// cancellation and removal. Downloads are explicit user actions — nothing
/// here runs without a models.install call, and every path is built from
/// validated catalogue identifiers and pinned filenames only.
/// </summary>
public sealed class ModelManager
{
    /// <summary>Wire-level lifecycle states.</summary>
    public static class States
    {
        public const string NotInstalled = "not_installed";
        public const string Downloading = "downloading";
        public const string Interrupted = "interrupted";
        public const string Verifying = "verifying";
        public const string Installing = "installing";
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Removing = "removing";
    }

    /// <summary>Public point-in-time view of one catalogue model.</summary>
    public sealed record ModelState(
        string ModelId, string State, long? InstalledBytes,
        long DownloadedBytes, long TotalBytes, string? Error);

    private sealed class Op
    {
        public required CancellationTokenSource Cts { get; init; }
        public Task Run = Task.CompletedTask;
        public volatile string State = States.Downloading;
        public volatile string? Error;
        public long Done;
    }

    private readonly string _root;
    private readonly ModelCatalog _catalog;
    private readonly IModelTransport _transport;
    private readonly Func<string, long?> _freeSpace;
    private readonly Func<string, FileMode, Stream> _openWrite;
    private readonly Dictionary<string, Op> _ops = new(StringComparer.Ordinal);
    // Last terminal state (failed / interrupted + reason) for models whose
    // on-disk footprint alone cannot reproduce it (e.g. a download that
    // failed before the first byte hit disk).
    private readonly Dictionary<string, (string State, string Error)> _lastTerminal
        = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="freeSpaceProbe">Injectable disk-space probe; default is
    /// DriveInfo on the model root volume.</param>
    /// <param name="openWrite">Injectable write-stream factory; tests use it
    /// to deterministically fail disk writes.</param>
    public ModelManager(
        string modelRoot,
        ModelCatalog catalog,
        IModelTransport transport,
        Func<string, long?>? freeSpaceProbe = null,
        Func<string, FileMode, Stream>? openWrite = null)
    {
        _root = Path.GetFullPath(modelRoot);
        _catalog = catalog;
        _transport = transport;
        _freeSpace = freeSpaceProbe ?? DefaultFreeSpace;
        _openWrite = openWrite ?? DefaultOpenWrite;
    }

    public ModelCatalog Catalog => _catalog;
    public string ModelRoot => _root;

    private static long? DefaultFreeSpace(string root)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            return drive is null ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch { return null; }
    }

    private static Stream DefaultOpenWrite(string path, FileMode mode) =>
        new FileStream(path, mode, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private string TmpDir => Path.Combine(_root, "tmp");
    private string PartPath(ModelCatalogEntry e) =>
        Path.Combine(TmpDir, e.Id + ".part");
    private string FinalPath(ModelCatalogEntry e) =>
        Path.Combine(_root, e.Artifact.FileName);

    /// <summary>
    /// Containment: the resolved path must live directly under the model
    /// root and no path component may be a reparse point. Filenames come
    /// only from the validated catalogue — user input never reaches here.
    /// </summary>
    private string ResolveInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
            throw new ModelException("policy_refused", "path escaped the model root");
        return full;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>Catalogue + on-disk truth merged per model.</summary>
    public IReadOnlyList<ModelState> Status()
    {
        var list = new List<ModelState>();
        foreach (var e in _catalog.Models)
        {
            Op? op;
            lock (_gate) _ops.TryGetValue(e.Id, out op);
            list.Add(CurrentState(e, op));
        }
        return list;
    }

    public ModelState StatusOf(string id)
    {
        var e = _catalog.Find(id)
            ?? throw new ModelException("model_unknown", $"unknown model '{id}'");
        Op? op;
        lock (_gate) _ops.TryGetValue(id, out op);
        return CurrentState(e, op);
    }

    // path → (size, mtimeUtc) of the last artifact that passed SHA-256
    // verification. A file whose size+mtime are unchanged is not re-hashed;
    // a hand-dropped artifact (M7.1 dev flow) is verified once, lazily.
    private readonly Dictionary<string, (long Size, DateTime MtimeUtc)> _verified = new();

    private bool InstalledArtifactVerified(ModelCatalogEntry e, string final)
    {
        var info = new FileInfo(final);
        if (!info.Exists || info.Length != e.Artifact.SizeBytes) return false;
        lock (_gate)
        {
            if (_verified.TryGetValue(final, out var v)
                && v.Size == info.Length && v.MtimeUtc == info.LastWriteTimeUtc)
                return true;
        }
        try
        {
            var hex = Sha256HexAsync(final, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!string.Equals(hex, e.Artifact.Sha256, StringComparison.Ordinal))
                return false;
            lock (_gate) _verified[final] = (info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch { return false; }
    }

    private ModelState CurrentState(ModelCatalogEntry e, Op? op)
    {
        var final = FinalPath(e);
        var part = PartPath(e);
        long? installed = File.Exists(final)
            ? new FileInfo(final).Length : null;
        var downloaded = File.Exists(part) ? new FileInfo(part).Length : 0;

        if (op is not null && !op.Run.IsCompleted)
            return new ModelState(e.Id, op.State, installed,
                op.Done, e.Artifact.SizeBytes, op.Error);
        if (op is not null)
            lock (_gate) { _ops.Remove(e.Id); } // retired — report disk truth

        if (installed == e.Artifact.SizeBytes)
        {
            // "ready" is only ever reported for a digest-verified artifact.
            return InstalledArtifactVerified(e, final)
                ? new ModelState(e.Id, States.Ready, installed,
                    installed.Value, e.Artifact.SizeBytes, null)
                : new ModelState(e.Id, States.Failed, installed,
                    downloaded, e.Artifact.SizeBytes,
                    "artifact present but failed integrity verification");
        }
        if (installed is not null)
            // Wrong size on disk: present but unverified — report honestly,
            // never "ready". Removal cleans it; install re-stages it.
            return new ModelState(e.Id, States.Failed, installed,
                downloaded, e.Artifact.SizeBytes,
                "artifact present with unexpected size");
        if (downloaded > 0)
            return new ModelState(e.Id, States.Interrupted, null,
                downloaded, e.Artifact.SizeBytes, null);
        lock (_gate)
        {
            if (_lastTerminal.TryGetValue(e.Id, out var t))
                return new ModelState(e.Id, t.State, null,
                    0, e.Artifact.SizeBytes, t.Error);
        }
        return new ModelState(e.Id, States.NotInstalled, null,
            0, e.Artifact.SizeBytes, null);
    }

    /// <summary>
    /// Explicit user-initiated install. Returns the live op state; the work
    /// runs in the background and is observable through Status/StatusOf.
    /// </summary>
    public ModelState BeginInstall(string id)
    {
        var e = _catalog.Find(id)
            ?? throw new ModelException("model_unknown", $"unknown model '{id}'");

        lock (_gate)
        {
            // Already-installed models never re-download; install is a no-op.
            // Checked before the busy gate: a just-finished op whose task
            // handle has not yet been observed completed must not turn a
            // legitimate "already installed" state into a false model_busy.
            var current = CurrentState(e, null);
            if (current.State == States.Ready)
                return current;

            if (_ops.TryGetValue(id, out var running) && !running.Run.IsCompleted)
                throw new ModelException("model_busy",
                    $"an operation is already running for '{id}'");
            _ops.Remove(id);

            var cts = new CancellationTokenSource();
            var op = new Op { Cts = cts };
            _ops[id] = op; // registered before the task starts so Cancel sees it
            _lastTerminal.Remove(id);
            op.Run = RunInstallAsync(e, op, cts.Token);
            return new ModelState(id, op.State, current.InstalledBytes,
                op.Done, e.Artifact.SizeBytes, null);
        }
    }

    /// <summary>Cancel the active download; the part file stays resumable.</summary>
    public bool Cancel(string id)
    {
        lock (_gate)
        {
            if (!_ops.TryGetValue(id, out var op) || op.Run.IsCompleted)
                return false;
            op.Cts.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Remove an installed artifact plus its resumable part file. Refuses
    /// while an operation is active, and refuses to delete a file the
    /// installer cannot prove it owns (reparse point, outside root, or
    /// locked by another process — e.g. a loaded AI worker).
    /// </summary>
    public ModelState Remove(string id)
    {
        var e = _catalog.Find(id)
            ?? throw new ModelException("model_unknown", $"unknown model '{id}'");
        lock (_gate)
        {
            if (_ops.TryGetValue(id, out var op) && !op.Run.IsCompleted)
                throw new ModelException("model_busy",
                    "cancel the active operation before removing");

            var final = ResolveInsideRoot(FinalPath(e));
            if (File.Exists(final))
            {
                if (IsReparsePoint(final))
                    throw new ModelException("policy_refused",
                        "installed path is a reparse point — refusing removal");
                try
                {
                    // An open handle that denies delete access (a loaded
                    // model) surfaces here instead of half-removing.
                    File.Delete(final);
                }
                catch (IOException)
                {
                    throw new ModelException("model_in_use",
                        "model file is in use — unload the AI worker first");
                }
                catch (UnauthorizedAccessException)
                {
                    throw new ModelException("model_in_use",
                        "model file could not be removed");
                }
            }
            var part = ResolveInsideRoot(PartPath(e));
            try { if (File.Exists(part) && !IsReparsePoint(part)) File.Delete(part); }
            catch (IOException) { /* part cleanup is best-effort */ }
            _lastTerminal.Remove(id);
        }
        return StatusOf(id);
    }

    private void SetState(Op op, string state) { op.State = state; }

    private async Task RunInstallAsync(
        ModelCatalogEntry e, Op op, CancellationToken ct)
    {
        var part = PartPath(e);
        var final = FinalPath(e);
        try
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(TmpDir);
            if (IsReparsePoint(_root) || IsReparsePoint(TmpDir))
                throw new ModelException("policy_refused",
                    "model storage path is a reparse point");

            // Pre-flight: enough room for the bytes still missing.
            var already = File.Exists(part) ? new FileInfo(part).Length : 0;
            var needed = e.Artifact.SizeBytes - already + (16L << 20);
            var free = _freeSpace(_root);
            if (free is not null && free < needed)
                throw new ModelException("insufficient_storage",
                    $"need ~{needed >> 20} MiB free on the model volume");

            op.State = States.Downloading;
            await ArtifactFetcher.FetchAsync(e, _transport, part, _openWrite,
                done => op.Done = done, ct);

            op.State = States.Verifying;
            var hex = await Sha256HexAsync(part, ct);
            if (!string.Equals(hex, e.Artifact.Sha256, StringComparison.Ordinal))
            {
                File.Delete(part); // corrupt bytes are never kept
                throw new ModelException("integrity_mismatch",
                    "downloaded artifact failed SHA-256 verification");
            }

            op.State = States.Installing;
            ResolveInsideRoot(final);
            if (File.Exists(final) && IsReparsePoint(final))
                throw new ModelException("policy_refused",
                    "install target is a reparse point");
            try
            {
                // Same-volume rename: tmp/ and the artifact share the model
                // root, so the swap is atomic. Only ever reached after the
                // staged bytes passed digest verification.
                File.Move(part, final, overwrite: true);
            }
            catch (IOException)
            {
                throw new ModelException("model_in_use",
                    "install target is in use — unload the AI worker first");
            }
            var staged = new FileInfo(final);
            lock (_gate) _verified[final] = (staged.Length, staged.LastWriteTimeUtc);
            op.State = States.Ready;
            op.Done = e.Artifact.SizeBytes;
        }
        catch (OperationCanceledException)
        {
            op.State = States.Interrupted;
            op.Error = "cancelled";
        }
        catch (ModelInterruptedException ex)
        {
            op.State = States.Interrupted;
            op.Error = ex.Message;
        }
        catch (ModelException ex)
        {
            // Failed states are not resumable — keeping partial bytes would
            // misreport the model as merely interrupted.
            try { File.Delete(part); } catch { }
            op.State = States.Failed;
            op.Error = ex.Code == "integrity_mismatch"
                ? ex.Message : $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            op.State = States.Failed;
            op.Error = $"unexpected: {ex.GetType().Name}";
        }
        finally
        {
            lock (_gate)
            {
                if (op.State is States.Failed or States.Interrupted
                    && op.Error is not null)
                    _lastTerminal[e.Id] = (op.State, op.Error);
            }
        }
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
