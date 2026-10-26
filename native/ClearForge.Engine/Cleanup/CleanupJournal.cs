using Microsoft.Data.Sqlite;

namespace ClearForge.Engine.Cleanup;

/// <summary>Journal states. Transitions: Prepared → Quarantining → Quarantined;
/// Quarantined → RestorePending → Restored; any → Failed | RecoveryRequired.</summary>
public enum JournalState
{
    Prepared,
    Quarantining,
    Quarantined,
    RestorePending,
    Restored,
    Failed,
    RecoveryRequired,
}

public sealed record JournalEntry(
    string OperationId,
    string SessionId,
    string CandidateId,
    string? Category,
    string? DisplayName,
    string SourcePath,
    string? QuarantinePath,
    long FileLength,
    long FileLastWriteUtcTicks,
    long FileCreatedUtcTicks,
    long FileAttributes,
    JournalState State,
    string? ErrorCode,
    DateTimeOffset UpdatedUtc,
    string? ContentSha256 = null,
    string? IntegrityState = null);

/// <summary>
/// Versioned SQLite cleanup journal. WAL + FULL sync; one row per
/// (session, candidate). Contains operation metadata only — never file
/// contents or secrets.
/// </summary>
public sealed class CleanupJournal : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _db;

    // Microsoft.Data.Sqlite connections are not thread-safe; the pipe host
    // dispatches requests concurrently, so every operation is serialized here.
    private readonly object _sync = new();

    public CleanupJournal(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                            "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            c.ExecuteNonQuery();
            EnsureSchema();
        }
    }

    private void EnsureSchema()
    {
        using var c = _db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS journal(
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
              content_sha256 TEXT,
              integrity_state TEXT,
              UNIQUE(session_id, candidate_id));
            """;
        c.ExecuteNonQuery();
        MigrateIfNeeded();
    }

    /// <summary>
    /// Explicit, transactional schema migration. v1 → v2 adds integrity
    /// columns and marks every pre-existing row 'legacy' — existing rows were
    /// written before content hashing, so no hash is ever backfilled and no
    /// row is ever claimed historically verified. The whole migration is one
    /// transaction: an interruption rolls it back and a later open retries.
    /// Unknown newer or malformed versions fail closed — the engine refuses
    /// to operate a journal it cannot interpret.
    /// </summary>
    private void MigrateIfNeeded()
    {
        string? raw;
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT v FROM meta WHERE k='schema_version'";
            raw = c.ExecuteScalar() as string;
        }

        // A journal table that exists but has no recorded version is a v1
        // database (v1 always inserted the row, but belt-and-suspenders for
        // any database built before the meta row was added).
        var version = raw is null ? 1
            : int.TryParse(raw, out var v) ? v
            : throw new InvalidOperationException(
                $"unsupported journal schema_version '{raw}' — refusing to open");

        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"journal schema_version {version} is newer than this engine " +
                $"supports ({SchemaVersion}) — refusing to open");

        var hasIntegrityColumns = false;
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "PRAGMA table_info(journal)";
            using var r = c.ExecuteReader();
            while (r.Read())
                if (r.GetString(1) == "content_sha256") hasIntegrityColumns = true;
        }
        if (hasIntegrityColumns)
        {
            // Already at v2 shape (fresh create or completed migration) — make
            // sure the recorded version agrees, then nothing else to do.
            using var c = _db.CreateCommand();
            c.CommandText =
                $"INSERT OR REPLACE INTO meta(k,v) VALUES('schema_version','{SchemaVersion}')";
            c.ExecuteNonQuery();
            return;
        }

        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            void Exec(string sql)
            {
                using var c = _db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = sql;
                c.ExecuteNonQuery();
            }
            // ADD COLUMN is additive: every existing row, id, state, path,
            // and timestamp is preserved; no table is recreated.
            Exec("ALTER TABLE journal ADD COLUMN content_sha256 TEXT");
            Exec("ALTER TABLE journal ADD COLUMN integrity_state TEXT");
            Exec($"UPDATE journal SET integrity_state='{JournalIntegrity.Legacy}' " +
                 "WHERE integrity_state IS NULL");
            Exec($"INSERT OR REPLACE INTO meta(k,v) VALUES('schema_version','{SchemaVersion}')");
            tx.Commit();
        }
    }

    public string InsertOrGetOperationId(
        string sessionId, string candidateId, string? category, string? displayName,
        string sourcePath, long length, long lastWriteTicks, long createdTicks, long attributes)
    {
        lock (_sync)
        {
            var existing = TryGet(sessionId, candidateId); // lock is reentrant
            if (existing is not null) return existing.OperationId;

            var opId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.UtcTicks;
            using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO journal(operation_id,session_id,candidate_id,category,display_name,
              source_path,file_length,file_last_write_utc,file_created_utc,file_attributes,
              state,error_code,created_utc,updated_utc)
            VALUES($op,$sid,$cid,$cat,$dn,$src,$len,$lw,$cr,$attr,$st,NULL,$now,$now)
            ON CONFLICT(session_id,candidate_id) DO NOTHING;
            """;
        c.Parameters.AddWithValue("$op", opId);
        c.Parameters.AddWithValue("$sid", sessionId);
        c.Parameters.AddWithValue("$cid", candidateId);
        c.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
        c.Parameters.AddWithValue("$dn", (object?)displayName ?? DBNull.Value);
        c.Parameters.AddWithValue("$src", sourcePath);
        c.Parameters.AddWithValue("$len", length);
        c.Parameters.AddWithValue("$lw", lastWriteTicks);
        c.Parameters.AddWithValue("$cr", createdTicks);
        c.Parameters.AddWithValue("$attr", attributes);
        c.Parameters.AddWithValue("$st", JournalState.Prepared.ToString());
        c.Parameters.AddWithValue("$now", now);
        c.ExecuteNonQuery();
            return TryGet(sessionId, candidateId)?.OperationId ?? opId;
        }
    }

    public JournalEntry? TryGet(string sessionId, string candidateId)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT * FROM journal WHERE session_id=$s AND candidate_id=$c";
            c.Parameters.AddWithValue("$s", sessionId);
            c.Parameters.AddWithValue("$c", candidateId);
            using var r = c.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public JournalEntry? TryGetByOperation(string operationId)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT * FROM journal WHERE operation_id=$o";
            c.Parameters.AddWithValue("$o", operationId);
            using var r = c.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public void SetState(string sessionId, string candidateId, JournalState state,
        string? errorCode = null, string? quarantinePath = null,
        string? sha256 = null, string? integrity = null)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand();
            var sets = new List<string> { "state=$st", "error_code=$ec", "updated_utc=$now" };
            if (quarantinePath is not null) sets.Add("quarantine_path=$qp");
            // Integrity evidence is only ever written, never silently cleared:
            // a null argument leaves the stored value untouched so failure
            // transitions keep the hash and provenance they were written with.
            if (sha256 is not null) sets.Add("content_sha256=$sha");
            if (integrity is not null) sets.Add("integrity_state=$int");
            c.CommandText =
                $"UPDATE journal SET {string.Join(",", sets)} WHERE session_id=$s AND candidate_id=$c";
            c.Parameters.AddWithValue("$st", state.ToString());
            c.Parameters.AddWithValue("$ec", (object?)errorCode ?? DBNull.Value);
            c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            c.Parameters.AddWithValue("$s", sessionId);
            c.Parameters.AddWithValue("$c", candidateId);
            if (quarantinePath is not null) c.Parameters.AddWithValue("$qp", quarantinePath);
            if (sha256 is not null) c.Parameters.AddWithValue("$sha", sha256);
            if (integrity is not null) c.Parameters.AddWithValue("$int", integrity);
            c.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JournalEntry> Query(string? state = null, int limit = 500,
        int offset = 0, string? sessionId = null)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand();
            var where = new List<string>();
            if (state is not null) where.Add("state=$st");
            if (sessionId is not null) where.Add("session_id=$sid");
            c.CommandText =
                $"SELECT * FROM journal{(where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")}" +
                " ORDER BY seq DESC LIMIT $n OFFSET $o";
            c.Parameters.AddWithValue("$n", limit);
            c.Parameters.AddWithValue("$o", offset);
            if (state is not null) c.Parameters.AddWithValue("$st", state);
            if (sessionId is not null) c.Parameters.AddWithValue("$sid", sessionId);
            var list = new List<JournalEntry>();
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    /// <summary>Total rows matching the optional filters — used to bound
    /// paginated enumeration (e.g. incident reconciliation).</summary>
    public long Count(string? state = null, string? sessionId = null)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand();
            var where = new List<string>();
            if (state is not null) where.Add("state=$st");
            if (sessionId is not null) where.Add("session_id=$sid");
            c.CommandText =
                $"SELECT COUNT(*) FROM journal{(where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")}";
            if (state is not null) c.Parameters.AddWithValue("$st", state);
            if (sessionId is not null) c.Parameters.AddWithValue("$sid", sessionId);
            return (long)(c.ExecuteScalar() ?? 0L);
        }
    }

    private static JournalEntry Map(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("operation_id")),
        r.GetString(r.GetOrdinal("session_id")),
        r.GetString(r.GetOrdinal("candidate_id")),
        r.IsDBNull(r.GetOrdinal("category")) ? null : r.GetString(r.GetOrdinal("category")),
        r.IsDBNull(r.GetOrdinal("display_name")) ? null : r.GetString(r.GetOrdinal("display_name")),
        r.GetString(r.GetOrdinal("source_path")),
        r.IsDBNull(r.GetOrdinal("quarantine_path")) ? null : r.GetString(r.GetOrdinal("quarantine_path")),
        r.GetInt64(r.GetOrdinal("file_length")),
        r.GetInt64(r.GetOrdinal("file_last_write_utc")),
        r.GetInt64(r.GetOrdinal("file_created_utc")),
        r.GetInt64(r.GetOrdinal("file_attributes")),
        Enum.Parse<JournalState>(r.GetString(r.GetOrdinal("state"))),
        r.IsDBNull(r.GetOrdinal("error_code")) ? null : r.GetString(r.GetOrdinal("error_code")),
        new DateTimeOffset(r.GetInt64(r.GetOrdinal("updated_utc")), TimeSpan.Zero),
        r.IsDBNull(r.GetOrdinal("content_sha256")) ? null : r.GetString(r.GetOrdinal("content_sha256")),
        r.IsDBNull(r.GetOrdinal("integrity_state")) ? null : r.GetString(r.GetOrdinal("integrity_state")));

    public void Dispose() => _db.Dispose();
}
