using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.0.3 content-integrity + journal schema v2: hashing lifecycle, structured
/// integrity outcomes, crash-window recovery, transactional v1→v2 migration,
/// and the explicit legacy (unverified) record policy.
/// </summary>
public class M803Tests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly FixtureDir _fx = new();    // source root
    private readonly FixtureDir _store = new(); // journal + quarantine root
    private readonly CleanupJournal _journal;
    private readonly ScanSessionStore _scans;
    private readonly QuarantineExecutor _executor;
    private readonly ITrustedRoots _roots;
    private readonly CleanupRule _rule;

    public M803Tests(ITestOutputHelper output)
    {
        _out = output;
        _roots = new TrustedRoots().Add("t", _fx.Path);
        _rule = new CleanupRule("r1", 1, "temporary-files", "current-user", "t",
            0, "low", true, "quarantine", 1_000_000, true, false);
        _scans = new ScanSessionStore(new[] { _rule }, new FileScanner(_roots));
        _journal = new CleanupJournal(Path.Combine(_store.Path, "journal.db"));
        _executor = NewExecutor();
    }

    public void Dispose() { _journal.Dispose(); _fx.Dispose(); _store.Dispose(); }

    private QuarantineExecutor NewExecutor(IContentHasher? hasher = null) =>
        new(_scans, _journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots, hasher);

    private ScanSession Scan()
    {
        var s = _scans.CreateExternal();
        _scans.Populate(s);
        Assert.Equal(SessionStatus.Completed, s.Status);
        return s;
    }

    private string CommitOne(QuarantineExecutor exec, ScanSession s, string cid) =>
        exec.Commit(s.Id, new[] { cid }, default).Single().Status;

    private static string Sha256Of(string path) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();

    /// <summary>Test seam: wraps the real hasher to inject mutation/failure
    /// windows deterministically.</summary>
    private sealed class TestHasher : IContentHasher
    {
        private readonly Sha256ContentHasher _inner = Sha256ContentHasher.Instance;
        public Func<Stream, string>? OnStreamHash;
        public Func<string, string>? OnPathHash;
        public FileStream OpenEvidenceRead(string path) => _inner.OpenEvidenceRead(path);
        public string ComputeHash(Stream content) =>
            OnStreamHash?.Invoke(content) ?? _inner.ComputeHash(content);
        public string ComputeHash(string path) =>
            OnPathHash?.Invoke(path) ?? _inner.ComputeHash(path);
    }

    // ------------------------- integrity correctness -------------------------

    [Fact]
    public void Hash_of_known_content_matches_independent_sha256()
    {
        var p = _fx.Write("known.tmp", "hello-clearforge");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("hello-clearforge"))).ToLowerInvariant();
        Assert.Equal(expected, Sha256ContentHasher.Instance.ComputeHash(p));
        Assert.True(ContentIntegrity.IsWellFormedSha256(expected));
        Assert.False(ContentIntegrity.IsWellFormedSha256("not-a-hash"));
        Assert.False(ContentIntegrity.IsWellFormedSha256(null));
    }

    [Fact]
    public void Large_file_hashes_via_streaming_with_bounded_memory()
    {
        // 64 MiB written in chunks — the fixture itself never holds it whole.
        var p = Path.Combine(_fx.Path, "large.tmp");
        var chunk = new byte[1024 * 1024];
        for (var i = 0; i < chunk.Length; i++) chunk[i] = (byte)(i % 251);
        using (var w = new FileStream(p, FileMode.Create))
            for (var i = 0; i < 64; i++) w.Write(chunk, 0, chunk.Length);

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var hash = Sha256ContentHasher.Instance.ComputeHash(p);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        Assert.Equal(Sha256Of(p), hash);
        // Streaming must never allocate on the order of the file: bound at a
        // generous but structural 8 MiB of allocations per hash call.
        Assert.True(allocated < 8L * 1024 * 1024,
            $"hashing allocated {allocated} bytes — not bounded");
        _out.WriteLine($"64 MiB hash allocated {allocated} bytes");
    }

    [Fact]
    public void Quarantine_records_and_verifies_expected_hash()
    {
        var p = _fx.Write("payload.tmp", "verifiable-bytes");
        var expected = Sha256Of(p);
        var s = Scan();
        var cid = s.Items.Single().Public.Id;

        Assert.Equal(Protocol.Errors.ItemCleaned, CommitOne(_executor, s, cid));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();
        Assert.Equal(expected, e.ContentSha256);
        Assert.Equal(JournalIntegrity.Verified, e.IntegrityState);
        Assert.Equal(expected, Sha256Of(e.QuarantinePath!));
    }

    [Fact]
    public void Same_length_tampered_artifact_blocks_restore_with_integrity_mismatch()
    {
        var p = _fx.Write("tamper.tmp", "0123456789abcdef");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();

        // Corrupt content while preserving length + journal timestamps so the
        // metadata identity check still passes — only the hash can catch this.
        File.WriteAllText(e.QuarantinePath!, "0123456789abcdeX");
        File.SetLastWriteTimeUtc(e.QuarantinePath!,
            new DateTimeOffset(e.FileLastWriteUtcTicks, TimeSpan.Zero).UtcDateTime);

        var res = _executor.Restore(e.OperationId);
        Assert.Equal("error", res.Status);
        Assert.Equal(Protocol.Errors.IntegrityMismatch, res.ErrorCode);
        var after = _journal.TryGet(e.SessionId, e.CandidateId)!;
        Assert.Equal(JournalState.RecoveryRequired, after.State);
        Assert.Equal(JournalIntegrity.Failed, after.IntegrityState);
        Assert.True(File.Exists(e.QuarantinePath)); // only copy preserved
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void Truncated_or_replaced_artifact_blocks_restore()
    {
        var p = _fx.Write("trunc.tmp", "0123456789abcdef");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();

        using (var f = new FileStream(e.QuarantinePath!, FileMode.Open, FileAccess.Write))
            f.SetLength(4); // truncate — caught by identity before hashing
        var res = _executor.Restore(e.OperationId);
        Assert.Equal("error", res.Status);
        Assert.Equal(Protocol.Errors.RecoveryRequired, res.ErrorCode);
        Assert.Equal(JournalState.RecoveryRequired,
            _journal.TryGet(e.SessionId, e.CandidateId)!.State);
        Assert.True(File.Exists(e.QuarantinePath));
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void Missing_artifact_keeps_journal_evidence_and_reports_missing()
    {
        _fx.Write("gone.tmp", "data");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();
        File.Delete(e.QuarantinePath!);

        var res = _executor.Restore(e.OperationId);
        Assert.Equal(Protocol.Errors.RestoreSourceMissing, res.ErrorCode);
        var row = _journal.TryGet(e.SessionId, e.CandidateId)!;
        Assert.NotNull(row); // journal evidence retained
        Assert.Equal(JournalState.Quarantined, row.State);
        Assert.NotNull(row.ContentSha256); // expected hash still on record
    }

    [Fact]
    public void Staged_io_failure_returns_io_error_without_false_success()
    {
        var p = _fx.Write("ioerr.tmp", "data");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();

        // Hold the staged file exclusively: the restore-time hash cannot read.
        using var hold = new FileStream(e.QuarantinePath!, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        var res = _executor.Restore(e.OperationId);
        Assert.Equal("error", res.Status);
        Assert.Equal(Protocol.Errors.IntegrityIoError, res.ErrorCode);
        Assert.Equal(JournalState.Quarantined,
            _journal.TryGet(e.SessionId, e.CandidateId)!.State); // retry-safe
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void Malformed_recorded_hash_fails_closed()
    {
        _fx.Write("mal.tmp", "data");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();

        // Simulate corrupt integrity metadata directly in the journal.
        _journal.SetState(e.SessionId, e.CandidateId, JournalState.Quarantined,
            sha256: "not-hex-at-all");
        var res = _executor.Restore(e.OperationId);
        Assert.Equal(Protocol.Errors.IntegrityUnverified, res.ErrorCode);
        Assert.Equal(JournalState.RecoveryRequired,
            _journal.TryGet(e.SessionId, e.CandidateId)!.State);
        Assert.True(File.Exists(e.QuarantinePath)); // artifact preserved
    }

    [Fact]
    public void Destination_conflict_still_refuses_after_integrity_gate()
    {
        var p = _fx.Write("regrow.tmp", "v1-bytes");
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();
        File.WriteAllText(p, "recreated");
        var res = _executor.Restore(e.OperationId);
        Assert.Equal(Protocol.Errors.RestoreConflict, res.ErrorCode);
        Assert.Equal("recreated", File.ReadAllText(p));
        Assert.True(File.Exists(e.QuarantinePath));
    }

    [Fact]
    public void Verified_quarantine_then_restore_is_byte_identical_and_marked()
    {
        var p = _fx.Write("round.tmp", "roundtrip-payload-0123");
        var expected = Sha256Of(p);
        var s = Scan();
        Assert.Equal(Protocol.Errors.ItemCleaned,
            CommitOne(_executor, s, s.Items.Single().Public.Id));
        var e = _journal.Query(JournalState.Quarantined.ToString()).Single();

        var res = _executor.Restore(e.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, res.Status);
        Assert.Null(res.ErrorCode);
        Assert.Equal(expected, Sha256Of(p));
        var after = _journal.TryGet(e.SessionId, e.CandidateId)!;
        Assert.Equal(JournalState.Restored, after.State);
        Assert.Equal(JournalIntegrity.Restored, after.IntegrityState);
    }

    // ------------------- concurrency / state transitions ---------------------

    [Fact]
    public void Source_replaced_during_hashing_is_unverified_not_moved()
    {
        var p = _fx.Write("race.tmp", "original-content");
        var s = Scan();
        var cid = s.Items.Single().Public.Id;
        var hasher = new TestHasher
        {
            OnStreamHash = stream =>
            {
                // delete-share permits rename even while we hold the handle:
                // swap the file out mid-hash like a racy external writer would.
                var path = ((FileStream)stream).Name;
                File.Move(path, path + ".moved");
                File.WriteAllText(path, "injected-replacement");
                return Sha256ContentHasher.Instance.ComputeHash(stream);
            }
        };
        var res = NewExecutor(hasher).Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.IntegrityUnverified, res.ErrorCode);
        Assert.Equal("injected-replacement", File.ReadAllText(p)); // not moved
        Assert.Equal(JournalState.Failed,
            _journal.TryGet(s.Id, cid)!.State);
        Assert.Empty(_journal.Query(JournalState.Quarantined.ToString()));
    }

    [Fact]
    public void Post_move_verification_failure_preserves_only_copy()
    {
        var p = _fx.Write("postfail.tmp", "bytes-to-keep");
        var s = Scan();
        var cid = s.Items.Single().Public.Id;
        var hasher = new TestHasher
        {
            OnPathHash = path =>
                path.Contains($"{Path.DirectorySeparatorChar}q{Path.DirectorySeparatorChar}")
                    ? "badbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadb"
                    : Sha256ContentHasher.Instance.ComputeHash(path)
        };
        var res = NewExecutor(hasher).Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.IntegrityRecoveryRequired, res.Status);
        Assert.Equal(Protocol.Errors.IntegrityMismatch, res.ErrorCode);

        var e = _journal.TryGet(s.Id, cid)!;
        Assert.Equal(JournalState.RecoveryRequired, e.State);
        Assert.Equal(Protocol.Errors.IntegrityMismatch, e.ErrorCode);
        Assert.True(File.Exists(e.QuarantinePath)); // only copy preserved
        Assert.Equal("bytes-to-keep", File.ReadAllText(e.QuarantinePath!));
        Assert.False(File.Exists(p)); // source moved — single copy only
    }

    [Fact]
    public void Recovery_hash_verifies_completed_move_and_marks_verified()
    {
        var p = _fx.Write("crash-h.tmp", "crash-data");
        var expected = Sha256Of(p);
        var dest = Path.Combine(_store.Path, "q", "s1", "c1_item");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(p, dest);
        _journal.InsertOrGetOperationId("s1", "c1", "temporary-files", "crash-h.tmp",
            p, new FileInfo(dest).Length, DateTimeOffset.UtcNow.UtcTicks,
            DateTimeOffset.UtcNow.UtcTicks, 0);
        _journal.SetState("s1", "c1", JournalState.Quarantining,
            quarantinePath: dest, sha256: expected, integrity: JournalIntegrity.Captured);

        Recovery.Reconcile(_journal);
        var e = _journal.TryGet("s1", "c1")!;
        Assert.Equal(JournalState.Quarantined, e.State);
        Assert.Equal(JournalIntegrity.Verified, e.IntegrityState);
        Assert.Equal(expected, Sha256Of(dest)); // artifact intact
    }

    [Fact]
    public void Recovery_flags_hash_mismatch_after_interrupted_move()
    {
        var p = _fx.Write("crash-t.tmp", "real-data");
        var dest = Path.Combine(_store.Path, "q", "s2", "c2_item");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "tampered"); // length 8 ≠ recorded hash's bytes
        _journal.InsertOrGetOperationId("s2", "c2", "temporary-files", "crash-t.tmp",
            p, 8, DateTimeOffset.UtcNow.UtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        _journal.SetState("s2", "c2", JournalState.Quarantining,
            quarantinePath: dest, sha256: Sha256Of(p),
            integrity: JournalIntegrity.Captured);
        File.Delete(p); // move happened; journal was behind

        var attention = Recovery.Reconcile(_journal);
        var e = _journal.TryGet("s2", "c2")!;
        Assert.Equal(JournalState.RecoveryRequired, e.State);
        Assert.Equal(Protocol.Errors.IntegrityMismatch, e.ErrorCode);
        Assert.Contains(attention, a => a.CandidateId == "c2");
        Assert.Equal("tampered", File.ReadAllText(dest)); // never deleted
    }

    [Fact]
    public void Recovery_is_idempotent_and_never_loses_the_copy()
    {
        var p = _fx.Write("crash-i.tmp", "idem");
        var expected = Sha256Of(p);
        var dest = Path.Combine(_store.Path, "q", "s3", "c3_item");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(p, dest);
        _journal.InsertOrGetOperationId("s3", "c3", "temporary-files", "crash-i.tmp",
            p, 4, DateTimeOffset.UtcNow.UtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        _journal.SetState("s3", "c3", JournalState.Quarantining,
            quarantinePath: dest, sha256: expected, integrity: JournalIntegrity.Captured);

        Recovery.Reconcile(_journal);
        Recovery.Reconcile(_journal); // second pass must be a no-op
        var e = _journal.TryGet("s3", "c3")!;
        Assert.Equal(JournalState.Quarantined, e.State);
        Assert.Equal(JournalIntegrity.Verified, e.IntegrityState);
        Assert.Equal(expected, Sha256Of(dest));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_store.Path, "q"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Recovery_hash_verifies_completed_restore_move()
    {
        var p = _fx.Write("crash-r.tmp", "restored-bytes");
        var expected = Sha256Of(p);
        _journal.InsertOrGetOperationId("s4", "c4", "temporary-files", "crash-r.tmp",
            p, new FileInfo(p).Length, File.GetLastWriteTimeUtc(p).Ticks,
            File.GetCreationTimeUtc(p).Ticks, 0);
        _journal.SetState("s4", "c4", JournalState.RestorePending,
            quarantinePath: Path.Combine(_store.Path, "q", "gone"),
            sha256: expected, integrity: JournalIntegrity.Captured);

        Recovery.Reconcile(_journal);
        var e = _journal.TryGet("s4", "c4")!;
        Assert.Equal(JournalState.Restored, e.State);
        Assert.Equal(JournalIntegrity.Restored, e.IntegrityState);
    }

    // --------------------- migration & compatibility -------------------------

    /// <summary>Build a realistic schema-v1 database exactly as the M8.0.2-era
    /// engine wrote it.</summary>
    private string BuildV1Database()
    {
        var dbPath = Path.Combine(_store.Path, "legacy.db");
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
            CREATE TABLE journal(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              operation_id TEXT NOT NULL,
              session_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              category TEXT,
              display_name TEXT,
              source_path TEXT NOT NULL,
              quarantine_path TEXT,
              file_length INTEGER NOT NULL,
              file_last_write_utc INTEGER NOT NULL,
              file_created_utc INTEGER NOT NULL,
              file_attributes INTEGER NOT NULL,
              state TEXT NOT NULL,
              error_code TEXT,
              created_utc INTEGER NOT NULL,
              updated_utc INTEGER NOT NULL,
              UNIQUE(session_id, candidate_id));
            INSERT INTO meta(k,v) VALUES('schema_version','1');
            """;
        c.ExecuteNonQuery();
        return dbPath;
    }

    private static void InsertV1Row(string dbPath, string sid, string cid,
        string state, string srcPath, string? qPath, long len, long writeTicks,
        long createdTicks)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            INSERT INTO journal(operation_id,session_id,candidate_id,category,
              display_name,source_path,quarantine_path,file_length,
              file_last_write_utc,file_created_utc,file_attributes,state,
              error_code,created_utc,updated_utc)
            VALUES($op,$sid,$cid,'temporary-files',$dn,$src,$qp,$len,$lw,$cr,0,
              $st,NULL,$now,$now)
            """;
        c.Parameters.AddWithValue("$op", Guid.NewGuid().ToString("N"));
        c.Parameters.AddWithValue("$sid", sid);
        c.Parameters.AddWithValue("$cid", cid);
        c.Parameters.AddWithValue("$dn", Path.GetFileName(srcPath));
        c.Parameters.AddWithValue("$src", srcPath);
        c.Parameters.AddWithValue("$qp", (object?)qPath ?? DBNull.Value);
        c.Parameters.AddWithValue("$len", len);
        c.Parameters.AddWithValue("$lw", writeTicks);
        c.Parameters.AddWithValue("$cr", createdTicks);
        c.Parameters.AddWithValue("$st", state);
        c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        c.ExecuteNonQuery();
    }

    [Fact]
    public void V1_database_migrates_without_row_loss_and_marks_legacy()
    {
        var dbPath = BuildV1Database();
        var src = _fx.Write("legacy.tmp", "old-data");
        var staged = Path.Combine(_store.Path, "q", "sess-old", "c1_legacy.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "old-data");
        var fi = new FileInfo(staged);
        InsertV1Row(dbPath, "sess-old", "c1", "Quarantined", src, staged,
            fi.Length, fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks);
        InsertV1Row(dbPath, "sess-old", "c2", "Restored", _fx.Write("done.tmp"),
            null, 10, DateTimeOffset.UtcNow.UtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        InsertV1Row(dbPath, "sess-old", "c3", "Failed", _fx.Write("err.tmp"),
            null, 5, DateTimeOffset.UtcNow.UtcTicks, DateTimeOffset.UtcNow.UtcTicks);

        using (var migrated = new CleanupJournal(dbPath))
        {
            var rows = migrated.Query(limit: 100);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(JournalIntegrity.Legacy, r.IntegrityState));
            Assert.All(rows, r => Assert.Null(r.ContentSha256)); // never backfilled
            var q = rows.Single(r => r.CandidateId == "c1");
            Assert.Equal(JournalState.Quarantined, q.State);
            Assert.Equal(staged, q.QuarantinePath);
            Assert.Equal("sess-old", q.SessionId);
        }

        // Rerun safety: a second open must not re-migrate or corrupt rows.
        using (var reopened = new CleanupJournal(dbPath))
            Assert.Equal(3, reopened.Query(limit: 100).Count);
    }

    [Fact]
    public void Unsupported_future_and_malformed_versions_fail_closed()
    {
        var dbPath = BuildV1Database();
        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "UPDATE meta SET v='3' WHERE k='schema_version'";
            c.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new CleanupJournal(dbPath));

        var malformed = Path.Combine(_store.Path, "malformed.db");
        using (var db = new SqliteConnection($"Data Source={malformed}"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText =
                "CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);" +
                "CREATE TABLE journal(seq INTEGER PRIMARY KEY);" +
                "INSERT INTO meta(k,v) VALUES('schema_version','banana')";
            c.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new CleanupJournal(malformed));
    }

    [Fact]
    public void Legacy_row_restores_via_explicit_unverified_path()
    {
        var dbPath = BuildV1Database();
        var src = Path.Combine(_fx.Path, "incident.tmp");
        var stagedDir = Path.Combine(_store.Path, "q", "sess-inc");
        Directory.CreateDirectory(stagedDir);
        var staged = Path.Combine(stagedDir, "c9_incident.tmp");
        File.WriteAllText(staged, "historical-content");
        var fi = new FileInfo(staged);
        InsertV1Row(dbPath, "sess-inc", "c9", "Quarantined", src, staged,
            fi.Length, fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks);

        using var journal = new CleanupJournal(dbPath);
        var exec = new QuarantineExecutor(_scans, journal,
            new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots);
        var entry = journal.TryGet("sess-inc", "c9")!;
        Assert.Equal(JournalIntegrity.Legacy, entry.IntegrityState);

        var res = exec.Restore(entry.OperationId);
        Assert.Equal(Protocol.Errors.RestoredOk, res.Status);
        // Explicit marker — never misreported as verified.
        Assert.Equal(Protocol.Errors.IntegrityLegacyUnverified, res.ErrorCode);
        Assert.Equal("historical-content", File.ReadAllText(src));

        var after = journal.TryGet("sess-inc", "c9")!;
        Assert.Equal(JournalState.Restored, after.State);
        Assert.Equal(JournalIntegrity.LegacyRestored, after.IntegrityState);
        // The observed digest is recorded as restore-time evidence — but the
        // provenance marker prevents it being read as historical proof.
        Assert.True(ContentIntegrity.IsWellFormedSha256(after.ContentSha256));
        Assert.NotEqual(JournalIntegrity.Verified, after.IntegrityState);
        Assert.NotEqual(JournalIntegrity.Restored, after.IntegrityState);
    }

    [Fact]
    public async Task Legacy_rows_remain_visible_in_history_with_marker()
    {
        var dbPath = BuildV1Database();
        InsertV1Row(dbPath, "sess-h", "c1", "Quarantined", _fx.Write("h.tmp"),
            Path.Combine(_store.Path, "q", "x"), 3,
            DateTimeOffset.UtcNow.UtcTicks, DateTimeOffset.UtcNow.UtcTicks);

        using var journal = new CleanupJournal(dbPath);
        var dispatcher = new RequestDispatcher(_scans,
            new QuarantineExecutor(_scans, journal,
                new QuarantineOptions(Path.Combine(_store.Path, "q")), _roots),
            journal);
        var res = await dispatcher.Dispatch("history.get", null);
        var json = JsonSerializer.Serialize(res.Result);
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("legacy", entry.GetProperty("integrity").GetString());
        Assert.Equal("quarantined", entry.GetProperty("state").GetString());
    }

    // ------------------------- performance evidence --------------------------

    [Fact]
    public void Perf_measure_streaming_hash_cost_and_commit_overhead()
    {
        // Representative sizes: small (1 KiB), medium (16 MiB), large (256 MiB).
        var sizes = new (string name, int bytes)[]
        {
            ("1KiB", 1_024), ("16MiB", 16 * 1024 * 1024), ("256MiB", 256 * 1024 * 1024),
        };
        foreach (var (name, bytes) in sizes)
        {
            var p = Path.Combine(_fx.Path, $"perf-{name}.tmp");
            var chunk = new byte[1024 * 1024];
            new Random(42).NextBytes(chunk);
            using (var w = new FileStream(p, FileMode.Create))
            {
                var remaining = bytes;
                while (remaining > 0)
                {
                    var n = Math.Min(remaining, chunk.Length);
                    w.Write(chunk, 0, n);
                    remaining -= n;
                }
            }

            // Warm the file cache once so the timed pass is representative of
            // the steady-state hashing cost, not cold disk.
            _ = Sha256ContentHasher.Instance.ComputeHash(p);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hash = Sha256ContentHasher.Instance.ComputeHash(p);
            sw.Stop();
            var mib = bytes / 1024.0 / 1024.0;
            _out.WriteLine($"hash {name}: {sw.ElapsedMilliseconds} ms " +
                $"({mib / sw.Elapsed.TotalSeconds:F1} MiB/s)");
            Assert.True(ContentIntegrity.IsWellFormedSha256(hash));
        }
    }

    // ------------------------------ regression -------------------------------

    [Fact]
    public void Locked_source_still_skipped_before_any_hashing()
    {
        var p = _fx.Write("locked.tmp", "data");
        var s = Scan();
        var cid = s.Items.Single().Public.Id;
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None);
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.SkippedLocked, res.ErrorCode);
        Assert.True(File.Exists(p));
        var e = _journal.TryGet(s.Id, cid)!;
        Assert.Null(e.ContentSha256); // locked → nothing hashed or captured
    }

    [Fact]
    public void Changed_source_is_still_skipped_changed()
    {
        var p = _fx.Write("mutable.tmp", "v1");
        var s = Scan();
        var cid = s.Items.Single().Public.Id;
        File.WriteAllText(p, "v2-longer-content");
        var res = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.SkippedChanged, res.ErrorCode);
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void Repeat_commit_after_verified_quarantine_is_idempotent()
    {
        _fx.Write("dup.tmp", "x");
        var s = Scan();
        var cid = s.Items.Single().Public.Id;
        Assert.Equal(Protocol.Errors.ItemCleaned, CommitOne(_executor, s, cid));
        var again = _executor.Commit(s.Id, new[] { cid }, default).Single();
        Assert.Equal(Protocol.Errors.ItemAlreadyQuarantined, again.Status);
        Assert.Single(Directory.GetFiles(Path.Combine(_store.Path, "q"), "*",
            SearchOption.AllDirectories));
    }
}
