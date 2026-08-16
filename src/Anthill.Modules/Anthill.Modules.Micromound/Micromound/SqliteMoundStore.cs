using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Anthill.Modules.Micromound;

/// <summary>
/// SQLite persistence for the mound registry. Lives in the same database file as colony memory
/// so mound knowledge is linkable to missions, owns its own tables, and never touches the mission
/// schema — the same arrangement <c>HomelabRepository</c> uses, for the same reasons. Schema
/// creation is idempotent, so a fresh database, an existing one, and a re-run are all safe.
///
/// Read-only foundation, like everything else in M1: nothing here issues a charter or authorizes
/// work. It records what mounds reported and what the colony refused to believe.
/// </summary>
public sealed class SqliteMoundStore : IMoundStore, IDisposable
{
    /// <summary>How many beats are retained per mound. History is useful; unbounded history is a leak.</summary>
    public const int BeatsRetainedPerMound = 500;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _writeLock = new();

    public SqliteMoundStore(string? dbPath = null)
    {
        var raw = dbPath ?? MicromoundRuntime.Options.DatabasePath;
        DbPath = Path.GetFullPath(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        InitDb();
    }

    public string DbPath { get; }

    internal static readonly string[] TableNames =
    {
        "micromound_mounds", "micromound_enrollment_tokens", "micromound_beats", "micromound_widget_state"
    };

    public void Dispose()
    {
        try
        {
            using var c = new SqliteConnection(ConnString);
            SqliteConnection.ClearPool(c);
        }
        catch (Exception)
        {
            // Scoped to this database's pool; a failure to clear it is not worth surfacing.
        }
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

    private static readonly string[] SchemaStatements =
    {
        """
        CREATE TABLE IF NOT EXISTS micromound_mounds (
            mound_id         TEXT PRIMARY KEY,
            name             TEXT NOT NULL DEFAULT '',
            tier             TEXT NOT NULL DEFAULT 'edge_queen',
            public_key       TEXT NOT NULL DEFAULT '',
            hardware_profile TEXT NOT NULL DEFAULT '',
            capabilities     TEXT NOT NULL DEFAULT '[]',
            enrolled_at      TEXT NOT NULL DEFAULT '',
            last_seen        TEXT NOT NULL DEFAULT '',
            last_seq         INTEGER NOT NULL DEFAULT -1,
            last_digest      TEXT NOT NULL DEFAULT '',
            sync_interval_s  INTEGER NOT NULL DEFAULT 15,
            stopped          INTEGER NOT NULL DEFAULT 0,
            protocol_version INTEGER NOT NULL DEFAULT 0
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS micromound_enrollment_tokens (
            mound_id   TEXT PRIMARY KEY,
            token_hash TEXT NOT NULL,
            issued_at  TEXT NOT NULL DEFAULT '',
            expires_at TEXT NOT NULL DEFAULT '',
            burned_at  TEXT NOT NULL DEFAULT '',
            issued_by  TEXT NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS micromound_beats (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            mound_id       TEXT NOT NULL,
            received_at    TEXT NOT NULL DEFAULT '',
            seq            INTEGER NOT NULL DEFAULT -1,
            state          TEXT NOT NULL DEFAULT 'unknown',
            envelope_count INTEGER NOT NULL DEFAULT 0,
            accepted       INTEGER NOT NULL DEFAULT 0,
            refusals       TEXT NOT NULL DEFAULT '[]'
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_micromound_beats_mound ON micromound_beats (mound_id, id DESC)",
        """
        CREATE TABLE IF NOT EXISTS micromound_widget_state (
            widget_kind  TEXT PRIMARY KEY,
            payload_json TEXT NOT NULL DEFAULT '{}',
            updated_at   TEXT NOT NULL DEFAULT ''
        )
        """
    };

    private void InitDb()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            foreach (var ddl in SchemaStatements)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = ddl;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    // ---- Mounds ----------------------------------------------------------------------------

    public IReadOnlyList<MoundRecord> ListMounds()
    {
        var list = new List<MoundRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = MoundColumns + " FROM micromound_mounds ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMound(r));
        return list;
    }

    public MoundRecord? GetMound(string moundId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = MoundColumns + " FROM micromound_mounds WHERE mound_id=$id";
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
            cmd.CommandText =
                """
                INSERT INTO micromound_mounds
                    (mound_id,name,tier,public_key,hardware_profile,capabilities,enrolled_at,
                     last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version)
                VALUES ($id,$name,$tier,$pk,$hw,$caps,$enrolled,$seen,$seq,$digest,$interval,$stopped,$ver)
                ON CONFLICT(mound_id) DO UPDATE SET
                    name=$name, tier=$tier, public_key=$pk, hardware_profile=$hw, capabilities=$caps,
                    enrolled_at=$enrolled, last_seen=$seen, last_seq=$seq, last_digest=$digest,
                    sync_interval_s=$interval, stopped=$stopped, protocol_version=$ver
                """;

            Bind(cmd, "$id", mound.MoundId);
            Bind(cmd, "$name", mound.Name);
            Bind(cmd, "$tier", mound.Tier);
            Bind(cmd, "$pk", mound.PublicKey);
            Bind(cmd, "$hw", mound.HardwareProfile);
            Bind(cmd, "$caps", JsonSerializer.Serialize(mound.Capabilities, Json));
            Bind(cmd, "$enrolled", mound.EnrolledAt);
            Bind(cmd, "$seen", mound.LastSeen);
            Bind(cmd, "$seq", mound.LastSeq);
            Bind(cmd, "$digest", mound.LastDigest);
            Bind(cmd, "$interval", mound.SyncIntervalSeconds);
            Bind(cmd, "$stopped", mound.Stopped ? 1 : 0);
            Bind(cmd, "$ver", mound.ProtocolVersion);
            cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveMound(string moundId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();

            foreach (var sql in new[]
                     {
                         "DELETE FROM micromound_enrollment_tokens WHERE mound_id=$id",
                         "DELETE FROM micromound_beats WHERE mound_id=$id"
                     })
            {
                using var child = conn.CreateCommand();
                child.CommandText = sql;
                Bind(child, "$id", moundId);
                child.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM micromound_mounds WHERE mound_id=$id";
            Bind(cmd, "$id", moundId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private const string MoundColumns =
        "SELECT mound_id,name,tier,public_key,hardware_profile,capabilities,enrolled_at," +
        "last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version";

    private static MoundRecord ReadMound(SqliteDataReader r) => new()
    {
        MoundId = r.GetString(0),
        Name = r.GetString(1),
        Tier = r.GetString(2),
        PublicKey = r.GetString(3),
        HardwareProfile = r.GetString(4),
        Capabilities = ReadStringList(r, 5),
        EnrolledAt = r.GetString(6),
        LastSeen = r.GetString(7),
        LastSeq = r.GetInt64(8),
        LastDigest = r.GetString(9),
        SyncIntervalSeconds = r.GetInt32(10),
        Stopped = !r.IsDBNull(11) && r.GetInt32(11) == 1,
        ProtocolVersion = r.GetInt32(12)
    };

    // ---- Enrollment tokens -----------------------------------------------------------------

    /// <summary>
    /// Note what is NOT done here: the token hash is not run through the field cipher.
    ///
    /// The homelab encrypts stored credentials because those are secrets that must be recovered
    /// and replayed to a third-party API. This is a SHA-256 of 256 bits of CSPRNG output, compared
    /// against a hash of what the device presents. It is never recovered and cannot be brute
    /// forced, so encrypting it would buy nothing and imply a protection that isn't real.
    /// <see cref="MicromoundRuntime.Cipher"/> stays available for a future field that genuinely
    /// needs it.
    /// </summary>
    public void PutEnrollmentToken(EnrollmentToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO micromound_enrollment_tokens
                    (mound_id,token_hash,issued_at,expires_at,burned_at,issued_by)
                VALUES ($id,$hash,$issued,$expires,$burned,$by)
                ON CONFLICT(mound_id) DO UPDATE SET
                    token_hash=$hash, issued_at=$issued, expires_at=$expires,
                    burned_at=$burned, issued_by=$by
                """;

            Bind(cmd, "$id", token.MoundId);
            Bind(cmd, "$hash", token.TokenHash);
            Bind(cmd, "$issued", token.IssuedAt);
            Bind(cmd, "$expires", token.ExpiresAt);
            Bind(cmd, "$burned", token.BurnedAt);
            Bind(cmd, "$by", token.IssuedBy);
            cmd.ExecuteNonQuery();
        }
    }

    public EnrollmentToken? GetEnrollmentToken(string moundId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT mound_id,token_hash,issued_at,expires_at,burned_at,issued_by " +
            "FROM micromound_enrollment_tokens WHERE mound_id=$id";
        Bind(cmd, "$id", moundId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new EnrollmentToken
        {
            MoundId = r.GetString(0),
            TokenHash = r.GetString(1),
            IssuedAt = r.GetString(2),
            ExpiresAt = r.GetString(3),
            BurnedAt = r.GetString(4),
            IssuedBy = r.GetString(5)
        };
    }

    // ---- Beats -----------------------------------------------------------------------------

    public void RecordBeat(MoundBeat beat)
    {
        ArgumentNullException.ThrowIfNull(beat);

        lock (_writeLock)
        {
            using var conn = Connect();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO micromound_beats
                        (mound_id,received_at,seq,state,envelope_count,accepted,refusals)
                    VALUES ($id,$at,$seq,$state,$count,$accepted,$refusals)
                    """;

                Bind(cmd, "$id", beat.MoundId);
                Bind(cmd, "$at", beat.ReceivedAt);
                Bind(cmd, "$seq", beat.Seq);
                Bind(cmd, "$state", beat.State);
                Bind(cmd, "$count", beat.EnvelopeCount);
                Bind(cmd, "$accepted", beat.Accepted ? 1 : 0);
                Bind(cmd, "$refusals", JsonSerializer.Serialize(beat.Refusals, Json));
                cmd.ExecuteNonQuery();
            }

            // Ring buffer, mirroring the device's own retention discipline.
            using var trim = conn.CreateCommand();
            trim.CommandText =
                """
                DELETE FROM micromound_beats
                WHERE mound_id=$id AND id NOT IN (
                    SELECT id FROM micromound_beats WHERE mound_id=$id ORDER BY id DESC LIMIT $keep
                )
                """;
            Bind(trim, "$id", beat.MoundId);
            Bind(trim, "$keep", BeatsRetainedPerMound);
            trim.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit)
    {
        var list = new List<MoundBeat>();
        if (limit <= 0) return list;

        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT mound_id,received_at,seq,state,envelope_count,accepted,refusals " +
            "FROM micromound_beats WHERE mound_id=$id ORDER BY id DESC LIMIT $limit";
        Bind(cmd, "$id", moundId);
        Bind(cmd, "$limit", limit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MoundBeat
            {
                MoundId = r.GetString(0),
                ReceivedAt = r.GetString(1),
                Seq = r.GetInt64(2),
                State = r.GetString(3),
                EnvelopeCount = r.GetInt32(4),
                Accepted = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                Refusals = ReadStringList(r, 6)
            });
        }

        return list;
    }

    // ---- Widget payloads -------------------------------------------------------------------

    public void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO micromound_widget_state (widget_kind,payload_json,updated_at)
                VALUES ($kind,$payload,$at)
                ON CONFLICT(widget_kind) DO UPDATE SET payload_json=$payload, updated_at=$at
                """;

            Bind(cmd, "$kind", widgetKind);
            Bind(cmd, "$payload", payloadJson);
            Bind(cmd, "$at", updatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT payload_json,updated_at FROM micromound_widget_state WHERE widget_kind=$kind";
        Bind(cmd, "$kind", widgetKind);

        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>
    /// A malformed JSON list reads as empty rather than throwing. A row that cannot be parsed is
    /// a reporting gap; a repository that throws on read turns it into an unreachable fleet page.
    /// </summary>
    private static List<string> ReadStringList(SqliteDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(r.GetString(ordinal), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
