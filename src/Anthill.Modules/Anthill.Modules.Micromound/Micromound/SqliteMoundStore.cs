using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Anthill.Modules.Micromound;

/// <summary>
/// SQLite persistence for the mound registry — the store the README promised would land with the
/// Api wiring, against the same semantics <see cref="InMemoryMoundStore"/> proved network-free.
///
/// Lives in the same database file as colony memory and the homelab tables, for the same reason
/// they do: mound knowledge should be linkable and searchable, not siloed. It owns its three
/// tables and touches nothing else. Schema creation is idempotent (CREATE TABLE IF NOT EXISTS),
/// writes serialize through one lock, and connections run WAL with a busy timeout — all of it
/// deliberately indistinguishable from <c>HomelabRepository</c>, because a second persistence
/// convention is a second thing to get wrong.
///
/// The enrollment token hash goes through the field cipher when one is configured. The hash is
/// already one-way; encrypting it at rest means a copied database file does not even yield the
/// oracle a hash provides. Null cipher stores plaintext hashes — the same supported state the
/// homelab credential store accepts.
/// </summary>
public sealed class SqliteMoundStore : IMoundStore, IDisposable
{
    public string DbPath { get; }
    private readonly object _writeLock = new();
    private readonly IFieldCipher? _cipher;

    /// <summary>Ring-buffer bound per mound, matching <see cref="InMemoryMoundStore"/>: history
    /// is useful, unbounded history is a leak.</summary>
    private const int BeatsKeptPerMound = 500;

    public SqliteMoundStore(string? dbPath = null, IFieldCipher? cipher = null)
    {
        var raw = dbPath ?? MicromoundRuntime.Options.DatabasePath;
        DbPath = Path.GetFullPath(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _cipher = cipher ?? MicromoundRuntime.Cipher;
        InitDb();
    }

    public void Dispose()
    {
        // Scoped to THIS database's pool (see HomelabRepository.Dispose for why not ClearAllPools).
        try { using var c = new SqliteConnection(ConnString); SqliteConnection.ClearPool(c); } catch { }
    }

    private string ConnString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(ConnString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    internal static readonly string[] TableNames =
    {
        "micromound_mounds", "micromound_enrollment_tokens", "micromound_beats",
        "micromound_widget_state",
    };

    private static readonly string[] SchemaStatements =
    {
        @"CREATE TABLE IF NOT EXISTS micromound_mounds (
            mound_id TEXT PRIMARY KEY, name TEXT NOT NULL, tier TEXT NOT NULL,
            public_key TEXT NOT NULL DEFAULT '', hardware_profile TEXT NOT NULL DEFAULT '',
            capabilities_json TEXT NOT NULL DEFAULT '[]',
            enrolled_at TEXT NOT NULL DEFAULT '', last_seen TEXT NOT NULL DEFAULT '',
            last_seq INTEGER NOT NULL DEFAULT -1, last_digest TEXT NOT NULL DEFAULT '',
            sync_interval_s INTEGER NOT NULL DEFAULT 15,
            stopped INTEGER NOT NULL DEFAULT 0, protocol_version INTEGER NOT NULL DEFAULT 0)",
        @"CREATE TABLE IF NOT EXISTS micromound_enrollment_tokens (
            mound_id TEXT PRIMARY KEY, token_hash TEXT NOT NULL,
            issued_at TEXT NOT NULL, expires_at TEXT NOT NULL,
            burned_at TEXT NOT NULL DEFAULT '', issued_by TEXT NOT NULL DEFAULT '')",
        @"CREATE TABLE IF NOT EXISTS micromound_beats (
            id INTEGER PRIMARY KEY AUTOINCREMENT, mound_id TEXT NOT NULL,
            received_at TEXT NOT NULL, seq INTEGER NOT NULL DEFAULT -1,
            state TEXT NOT NULL DEFAULT 'unknown', envelopes INTEGER NOT NULL DEFAULT 0,
            accepted INTEGER NOT NULL DEFAULT 0, refusals_json TEXT NOT NULL DEFAULT '[]')",
        @"CREATE INDEX IF NOT EXISTS idx_micromound_beats_mound ON micromound_beats (mound_id, id)",
        @"CREATE TABLE IF NOT EXISTS micromound_widget_state (
            widget_kind TEXT PRIMARY KEY, payload_json TEXT NOT NULL, updated_at TEXT NOT NULL)",
    };

    private void InitDb()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            foreach (var statement in SchemaStatements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ---- Mounds -------------------------------------------------------------------------------

    public IReadOnlyList<MoundRecord> ListMounds()
    {
        var list = new List<MoundRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectMound + " ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMound(r));
        return list;
    }

    public MoundRecord? GetMound(string moundId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectMound + " WHERE mound_id=$id";
        Bind(cmd, "$id", moundId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMound(r) : null;
    }

    public void UpsertMound(MoundRecord mound)
    {
        ArgumentNullException.ThrowIfNull(mound);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_mounds
                (mound_id,name,tier,public_key,hardware_profile,capabilities_json,enrolled_at,
                 last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version)
                VALUES ($id,$name,$tier,$key,$hw,$caps,$enrolled,$seen,$seq,$digest,$interval,$stop,$proto)
                ON CONFLICT(mound_id) DO UPDATE SET name=$name,tier=$tier,public_key=$key,
                hardware_profile=$hw,capabilities_json=$caps,enrolled_at=$enrolled,last_seen=$seen,
                last_seq=$seq,last_digest=$digest,sync_interval_s=$interval,stopped=$stop,
                protocol_version=$proto";
            Bind(cmd, "$id", mound.MoundId); Bind(cmd, "$name", mound.Name); Bind(cmd, "$tier", mound.Tier);
            Bind(cmd, "$key", mound.PublicKey); Bind(cmd, "$hw", mound.HardwareProfile);
            Bind(cmd, "$caps", JsonSerializer.Serialize(mound.Capabilities));
            Bind(cmd, "$enrolled", mound.EnrolledAt); Bind(cmd, "$seen", mound.LastSeen);
            Bind(cmd, "$seq", mound.LastSeq); Bind(cmd, "$digest", mound.LastDigest);
            Bind(cmd, "$interval", mound.SyncIntervalSeconds);
            Bind(cmd, "$stop", mound.Stopped ? 1 : 0); Bind(cmd, "$proto", mound.ProtocolVersion);
            cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveMound(string moundId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            // Same containment RemoveMound has in memory: token and beats leave with the record.
            foreach (var table in new[] { "micromound_enrollment_tokens", "micromound_beats" })
            {
                using var sweep = conn.CreateCommand();
                sweep.CommandText = $"DELETE FROM {table} WHERE mound_id=$id";
                Bind(sweep, "$id", moundId);
                sweep.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM micromound_mounds WHERE mound_id=$id";
            Bind(cmd, "$id", moundId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private const string SelectMound =
        @"SELECT mound_id,name,tier,public_key,hardware_profile,capabilities_json,enrolled_at,
                 last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version
          FROM micromound_mounds";

    private static MoundRecord ReadMound(SqliteDataReader r) => new()
    {
        MoundId = r.GetString(0), Name = r.GetString(1), Tier = r.GetString(2),
        PublicKey = r.GetString(3), HardwareProfile = r.GetString(4),
        Capabilities = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [],
        EnrolledAt = r.GetString(6), LastSeen = r.GetString(7),
        LastSeq = r.GetInt64(8), LastDigest = r.GetString(9),
        SyncIntervalSeconds = r.GetInt32(10), Stopped = r.GetInt32(11) == 1,
        ProtocolVersion = r.GetInt32(12),
    };

    // ---- Enrollment tokens --------------------------------------------------------------------

    public void PutEnrollmentToken(EnrollmentToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_enrollment_tokens
                (mound_id,token_hash,issued_at,expires_at,burned_at,issued_by)
                VALUES ($id,$hash,$issued,$expires,$burned,$by)
                ON CONFLICT(mound_id) DO UPDATE SET token_hash=$hash,issued_at=$issued,
                expires_at=$expires,burned_at=$burned,issued_by=$by";
            Bind(cmd, "$id", token.MoundId);
            Bind(cmd, "$hash", _cipher?.Protect(token.TokenHash) ?? token.TokenHash);
            Bind(cmd, "$issued", token.IssuedAt); Bind(cmd, "$expires", token.ExpiresAt);
            Bind(cmd, "$burned", token.BurnedAt); Bind(cmd, "$by", token.IssuedBy);
            cmd.ExecuteNonQuery();
        }
    }

    public EnrollmentToken? GetEnrollmentToken(string moundId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT mound_id,token_hash,issued_at,expires_at,burned_at,issued_by
                            FROM micromound_enrollment_tokens WHERE mound_id=$id";
        Bind(cmd, "$id", moundId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new EnrollmentToken
        {
            MoundId = r.GetString(0),
            TokenHash = _cipher?.Unprotect(r.GetString(1)) ?? r.GetString(1),
            IssuedAt = r.GetString(2), ExpiresAt = r.GetString(3),
            BurnedAt = r.GetString(4), IssuedBy = r.GetString(5),
        };
    }

    // ---- Beats --------------------------------------------------------------------------------

    public void RecordBeat(MoundBeat beat)
    {
        ArgumentNullException.ThrowIfNull(beat);
        lock (_writeLock)
        {
            using var conn = Connect();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO micromound_beats
                    (mound_id,received_at,seq,state,envelopes,accepted,refusals_json)
                    VALUES ($id,$at,$seq,$state,$env,$ok,$ref)";
                Bind(cmd, "$id", beat.MoundId); Bind(cmd, "$at", beat.ReceivedAt);
                Bind(cmd, "$seq", beat.Seq); Bind(cmd, "$state", beat.State);
                Bind(cmd, "$env", beat.EnvelopeCount); Bind(cmd, "$ok", beat.Accepted ? 1 : 0);
                Bind(cmd, "$ref", JsonSerializer.Serialize(beat.Refusals));
                cmd.ExecuteNonQuery();
            }
            using (var trim = conn.CreateCommand())
            {
                trim.CommandText = @"DELETE FROM micromound_beats WHERE mound_id=$id AND id NOT IN
                    (SELECT id FROM micromound_beats WHERE mound_id=$id ORDER BY id DESC LIMIT $keep)";
                Bind(trim, "$id", beat.MoundId); Bind(trim, "$keep", BeatsKeptPerMound);
                trim.ExecuteNonQuery();
            }
        }
    }

    public IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit)
    {
        var list = new List<MoundBeat>();
        if (limit <= 0) return list;
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT mound_id,received_at,seq,state,envelopes,accepted,refusals_json
                            FROM micromound_beats WHERE mound_id=$id ORDER BY id DESC LIMIT $n";
        Bind(cmd, "$id", moundId); Bind(cmd, "$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MoundBeat
            {
                MoundId = r.GetString(0), ReceivedAt = r.GetString(1), Seq = r.GetInt64(2),
                State = r.GetString(3), EnvelopeCount = r.GetInt32(4), Accepted = r.GetInt32(5) == 1,
                Refusals = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? [],
            });
        }
        return list;
    }

    // ---- Widget payloads ----------------------------------------------------------------------

    public void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_widget_state (widget_kind,payload_json,updated_at)
                VALUES ($kind,$payload,$at)
                ON CONFLICT(widget_kind) DO UPDATE SET payload_json=$payload,updated_at=$at";
            Bind(cmd, "$kind", widgetKind); Bind(cmd, "$payload", payloadJson); Bind(cmd, "$at", updatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json,updated_at FROM micromound_widget_state WHERE widget_kind=$kind";
        Bind(cmd, "$kind", widgetKind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    private static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
