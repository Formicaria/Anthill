using System.Text.Json;
using Microsoft.Data.Sqlite;
using Micromound.Protocol;

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
        "micromound_widget_state", "micromound_controller_identity",
        "micromound_charters", "micromound_downlink", "micromound_manifests", "micromound_missions",
        "micromound_evidence", "micromound_actions", "micromound_mission_reports",
    };

    /// <summary>
    /// Every table holding rows that belong to ONE mound — what <see cref="RemoveMound"/> sweeps.
    ///
    /// Not `TableNames` minus a guess: `micromound_widget_state` is fleet-wide and
    /// `micromound_controller_identity` is the colony's own, and neither has a `mound_id` column
    /// at all. That is exactly what the guard checks — a table with a `mound_id` that is missing
    /// from this list is a table an unlink would leave behind.
    /// </summary>
    internal static readonly string[] PerMoundTables =
    {
        "micromound_enrollment_tokens", "micromound_beats", "micromound_charters",
        "micromound_downlink", "micromound_manifests", "micromound_missions",
        "micromound_evidence", "micromound_actions", "micromound_mission_reports",
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
            stopped INTEGER NOT NULL DEFAULT 0, protocol_version INTEGER NOT NULL DEFAULT 0,
            -- v0.3.8.114: authority and configuration. Every one carries a DEFAULT, so an existing
            -- database opened by this build reads them as absent rather than failing — and absent
            -- resolves downward everywhere: no charter, no lease, manual-only.
            charter_id TEXT NOT NULL DEFAULT '', charter_expires_at TEXT NOT NULL DEFAULT '',
            lease_expires_at TEXT NOT NULL DEFAULT '', quiesced INTEGER NOT NULL DEFAULT 0,
            autonomy_policy TEXT NOT NULL DEFAULT 'manual_only',
            manifest_id TEXT NOT NULL DEFAULT '', configuration_revision TEXT NOT NULL DEFAULT '')",
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
        // One row, ever. The primary key is a constant rather than an id because a colony has one
        // signing identity: a table that could hold two would eventually hold two, and "which key
        // did we sign that charter with" is not a question anybody should have to answer.
        @"CREATE TABLE IF NOT EXISTS micromound_controller_identity (
            id INTEGER PRIMARY KEY CHECK (id = 1), seed TEXT NOT NULL, created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS micromound_charters (
            charter_id TEXT PRIMARY KEY, mound_id TEXT NOT NULL, charter_json TEXT NOT NULL,
            issued_at TEXT NOT NULL DEFAULT '')",
        // The downlink queue. Ordered by insertion, drained on acknowledgement — an envelope handed
        // to a device that then failed to receive it is an envelope nobody has.
        @"CREATE TABLE IF NOT EXISTS micromound_downlink (
            id INTEGER PRIMARY KEY AUTOINCREMENT, mound_id TEXT NOT NULL,
            queued_at TEXT NOT NULL, envelope_json TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_micromound_downlink_mound ON micromound_downlink (mound_id, id)",
        @"CREATE TABLE IF NOT EXISTS micromound_manifests (
            manifest_id TEXT PRIMARY KEY, mound_id TEXT NOT NULL, manifest_json TEXT NOT NULL,
            issued_at TEXT NOT NULL DEFAULT '')",
        @"CREATE TABLE IF NOT EXISTS micromound_missions (
            mission_id TEXT PRIMARY KEY, mound_id TEXT NOT NULL, charter_id TEXT NOT NULL DEFAULT '',
            mission_json TEXT NOT NULL, dispatched_at TEXT NOT NULL DEFAULT '')",
        @"CREATE INDEX IF NOT EXISTS idx_micromound_missions_mound ON micromound_missions (mound_id)",
        // Evidence is keyed by (mound, evidence_id) rather than evidence_id alone: ids are minted
        // on the device, and two mounds are two independent id spaces with no coordination between
        // them. A global key would let one mound's proof answer for another's action.
        @"CREATE TABLE IF NOT EXISTS micromound_evidence (
            mound_id TEXT NOT NULL, evidence_id TEXT NOT NULL, item_json TEXT NOT NULL,
            captured_at TEXT NOT NULL DEFAULT '', PRIMARY KEY (mound_id, evidence_id))",
        // The device's report and the colony's verdict, side by side. colony_outcome is never
        // written back into record_json — the disagreement is the interesting part.
        @"CREATE TABLE IF NOT EXISTS micromound_actions (
            mound_id TEXT NOT NULL, action_id TEXT NOT NULL, mission_id TEXT NOT NULL DEFAULT '',
            record_json TEXT NOT NULL, colony_outcome TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (mound_id, action_id))",
        @"CREATE INDEX IF NOT EXISTS idx_micromound_actions_mission ON micromound_actions (mound_id, mission_id)",
        // The mound's own account of a mission. Keyed by (mound, mission) and replaced rather than
        // appended: a report is a final statement, and a backlog legitimately re-sends it.
        @"CREATE TABLE IF NOT EXISTS micromound_mission_reports (
            mound_id TEXT NOT NULL, mission_id TEXT NOT NULL, report_json TEXT NOT NULL,
            received_at TEXT NOT NULL DEFAULT '', PRIMARY KEY (mound_id, mission_id))",
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
                 last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version,
                 charter_id,charter_expires_at,lease_expires_at,quiesced,autonomy_policy,
                 manifest_id,configuration_revision)
                VALUES ($id,$name,$tier,$key,$hw,$caps,$enrolled,$seen,$seq,$digest,$interval,$stop,$proto,
                        $charter,$charterexp,$lease,$quiesced,$policy,$manifest,$configrev)
                ON CONFLICT(mound_id) DO UPDATE SET name=$name,tier=$tier,public_key=$key,
                hardware_profile=$hw,capabilities_json=$caps,enrolled_at=$enrolled,last_seen=$seen,
                last_seq=$seq,last_digest=$digest,sync_interval_s=$interval,stopped=$stop,
                protocol_version=$proto,charter_id=$charter,charter_expires_at=$charterexp,
                lease_expires_at=$lease,quiesced=$quiesced,autonomy_policy=$policy,
                manifest_id=$manifest,configuration_revision=$configrev";
            Bind(cmd, "$id", mound.MoundId); Bind(cmd, "$name", mound.Name); Bind(cmd, "$tier", mound.Tier);
            Bind(cmd, "$key", mound.PublicKey); Bind(cmd, "$hw", mound.HardwareProfile);
            Bind(cmd, "$caps", JsonSerializer.Serialize(mound.Capabilities));
            Bind(cmd, "$enrolled", mound.EnrolledAt); Bind(cmd, "$seen", mound.LastSeen);
            Bind(cmd, "$seq", mound.LastSeq); Bind(cmd, "$digest", mound.LastDigest);
            Bind(cmd, "$interval", mound.SyncIntervalSeconds);
            Bind(cmd, "$stop", mound.Stopped ? 1 : 0); Bind(cmd, "$proto", mound.ProtocolVersion);
            Bind(cmd, "$charter", mound.CharterId); Bind(cmd, "$charterexp", mound.CharterExpiresAt);
            Bind(cmd, "$lease", mound.LeaseExpiresAt); Bind(cmd, "$quiesced", mound.Quiesced ? 1 : 0);
            Bind(cmd, "$policy", MicromoundAutonomy.Value(mound.AutonomyPolicy));
            Bind(cmd, "$manifest", mound.ManifestId); Bind(cmd, "$configrev", mound.ConfigurationRevision);
            cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveMound(string moundId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            // EVERYTHING KEYED TO THIS MOUND LEAVES WITH IT. v0.3.8.114 — this list was two tables
            // when two tables existed, and the comment then said so. Unlinking a device that left
            // its charters, its queued downlink and its evidence behind would be worse than not
            // unlinking it: the id can be re-minted, and the next device to hold it would inherit
            // authority and proof belonging to the one an operator deliberately removed.
            //
            // `PerMoundTables` is the one list, and `EveryPerMoundTable_IsSweptOnRemoveMound`
            // reads the schema to check nothing was added to the database without being added here.
            foreach (var table in PerMoundTables)
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
                 last_seen,last_seq,last_digest,sync_interval_s,stopped,protocol_version,
                 charter_id,charter_expires_at,lease_expires_at,quiesced,autonomy_policy,
                 manifest_id,configuration_revision
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
        CharterId = r.GetString(13), CharterExpiresAt = r.GetString(14),
        LeaseExpiresAt = r.GetString(15), Quiesced = r.GetInt32(16) == 1,
        AutonomyPolicy = MicromoundAutonomy.Parse(r.GetString(17)),
        ManifestId = r.GetString(18), ConfigurationRevision = r.GetString(19),
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

    public IReadOnlyList<EnrollmentToken> AllEnrollmentTokens()
    {
        var list = new List<EnrollmentToken>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT mound_id,token_hash,issued_at,expires_at,burned_at,issued_by
                            FROM micromound_enrollment_tokens";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EnrollmentToken
            {
                MoundId = r.GetString(0),
                TokenHash = _cipher?.Unprotect(r.GetString(1)) ?? r.GetString(1),
                IssuedAt = r.GetString(2), ExpiresAt = r.GetString(3),
                BurnedAt = r.GetString(4), IssuedBy = r.GetString(5),
            });
        return list;
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

    // ---- Controller identity ------------------------------------------------------------------

    public byte[]? GetControllerSeed()
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seed FROM micromound_controller_identity WHERE id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var stored = r.GetString(0);
        var hex = _cipher?.Unprotect(stored) ?? stored;

        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }   // an unreadable seed is an absent one, not a crash
    }

    public void PutControllerSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            // INSERT OR IGNORE, not upsert. Overwriting the seed orphans every enrolled mound —
            // each holds the old public key and would refuse every later charter as `unknown_key`,
            // correctly, while looking to an operator like the fleet stopped obeying. Rotation is an
            // explicit act that re-enrolls the fleet, so it does not get to happen by accident here.
            cmd.CommandText = @"INSERT OR IGNORE INTO micromound_controller_identity (id,seed,created_at)
                VALUES (1,$seed,$at)";
            var hex = Convert.ToHexStringLower(seed);
            Bind(cmd, "$seed", _cipher?.Protect(hex) ?? hex);
            Bind(cmd, "$at", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Charters and downlink ------------------------------------------------------------------

    public Charter? GetCharter(string charterId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT charter_json FROM micromound_charters WHERE charter_id=$id";
        Bind(cmd, "$id", charterId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        try { return JsonSerializer.Deserialize<Charter>(r.GetString(0), ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public void PutCharter(Charter charter)
    {
        ArgumentNullException.ThrowIfNull(charter);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_charters (charter_id,mound_id,charter_json,issued_at)
                VALUES ($id,$mound,$json,$at)
                ON CONFLICT(charter_id) DO UPDATE SET charter_json=$json";
            Bind(cmd, "$id", charter.CharterId);
            Bind(cmd, "$mound", charter.MoundId);
            Bind(cmd, "$json", JsonSerializer.Serialize(charter, ProtocolJson.Options));
            Bind(cmd, "$at", charter.IssuedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public MoundManifest? GetManifest(string manifestId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT manifest_json FROM micromound_manifests WHERE manifest_id=$id";
        Bind(cmd, "$id", manifestId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        try { return JsonSerializer.Deserialize<MoundManifest>(r.GetString(0), ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public void PutManifest(MoundManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_manifests (manifest_id,mound_id,manifest_json,issued_at)
                VALUES ($id,$mound,$json,$at)
                ON CONFLICT(manifest_id) DO UPDATE SET manifest_json=$json";
            Bind(cmd, "$id", manifest.ManifestId);
            Bind(cmd, "$mound", manifest.MoundId);
            Bind(cmd, "$json", JsonSerializer.Serialize(manifest, ProtocolJson.Options));
            Bind(cmd, "$at", manifest.IssuedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public Mission? GetMission(string missionId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mission_json FROM micromound_missions WHERE mission_id=$id";
        Bind(cmd, "$id", missionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        try { return JsonSerializer.Deserialize<Mission>(r.GetString(0), ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public void PutMission(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_missions
                (mission_id,mound_id,charter_id,mission_json,dispatched_at)
                VALUES ($id,$mound,$charter,$json,$at)
                ON CONFLICT(mission_id) DO UPDATE SET mission_json=$json";
            Bind(cmd, "$id", mission.MissionId);
            Bind(cmd, "$mound", mission.MoundId);
            Bind(cmd, "$charter", mission.CharterId);
            Bind(cmd, "$json", JsonSerializer.Serialize(mission, ProtocolJson.Options));
            Bind(cmd, "$at", DateTimeOffset.UtcNow.ToWire());
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Mission> MissionsForMound(string moundId, int limit)
    {
        var list = new List<Mission>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT mission_json FROM micromound_missions WHERE mound_id=$mound
                            ORDER BY dispatched_at DESC, rowid DESC LIMIT $lim";
        Bind(cmd, "$mound", moundId);
        Bind(cmd, "$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var mission = JsonSerializer.Deserialize<Mission>(r.GetString(0), ProtocolJson.Options);
                if (mission is not null) list.Add(mission);
            }
            catch (JsonException) { }
        }
        return list;
    }

    public void PutMissionReport(string moundId, MissionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_mission_reports
                (mound_id,mission_id,report_json,received_at)
                VALUES ($mound,$mission,$json,$at)
                ON CONFLICT(mound_id,mission_id) DO UPDATE SET report_json=$json,received_at=$at";
            Bind(cmd, "$mound", moundId);
            Bind(cmd, "$mission", report.MissionId);
            Bind(cmd, "$json", JsonSerializer.Serialize(report, ProtocolJson.Options));
            Bind(cmd, "$at", DateTimeOffset.UtcNow.ToWire());
            cmd.ExecuteNonQuery();
        }
    }

    public MissionReport? GetMissionReport(string moundId, string missionId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT report_json FROM micromound_mission_reports
                            WHERE mound_id=$mound AND mission_id=$mission";
        Bind(cmd, "$mound", moundId);
        Bind(cmd, "$mission", missionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        try { return JsonSerializer.Deserialize<MissionReport>(r.GetString(0), ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    // ---- Physical evidence ----------------------------------------------------------------------

    public void PutEvidence(string moundId, EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_evidence (mound_id,evidence_id,item_json,captured_at)
                VALUES ($mound,$id,$json,$at)
                ON CONFLICT(mound_id,evidence_id) DO UPDATE SET item_json=$json,captured_at=$at";
            Bind(cmd, "$mound", moundId);
            Bind(cmd, "$id", item.EvidenceId);
            Bind(cmd, "$json", JsonSerializer.Serialize(item, ProtocolJson.Options));
            Bind(cmd, "$at", item.CapturedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<EvidenceItem> EvidenceFor(string moundId)
    {
        var items = new List<EvidenceItem>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_json FROM micromound_evidence WHERE mound_id=$mound";
        Bind(cmd, "$mound", moundId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var item = JsonSerializer.Deserialize<EvidenceItem>(r.GetString(0), ProtocolJson.Options);
                if (item is not null) items.Add(item);
            }
            catch (JsonException) { }   // one unreadable row must not blind the gate to the rest
        }

        return items;
    }

    public void PutAction(string moundId, ActionRecord record, string colonyOutcome, string reason)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_actions
                (mound_id,action_id,mission_id,record_json,colony_outcome,reason)
                VALUES ($mound,$id,$mission,$json,$outcome,$reason)
                ON CONFLICT(mound_id,action_id) DO UPDATE SET
                record_json=$json,colony_outcome=$outcome,reason=$reason";
            Bind(cmd, "$mound", moundId);
            Bind(cmd, "$id", record.ActionId);
            Bind(cmd, "$mission", record.MissionId);
            Bind(cmd, "$json", JsonSerializer.Serialize(record, ProtocolJson.Options));
            Bind(cmd, "$outcome", colonyOutcome);
            Bind(cmd, "$reason", reason);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IngestedAction> ActionsForMission(string moundId, string missionId)
    {
        var actions = new List<IngestedAction>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT record_json,colony_outcome,reason FROM micromound_actions
            WHERE mound_id=$mound AND mission_id=$mission";
        Bind(cmd, "$mound", moundId);
        Bind(cmd, "$mission", missionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var record = JsonSerializer.Deserialize<ActionRecord>(r.GetString(0), ProtocolJson.Options);
                if (record is not null) actions.Add(new IngestedAction(record, r.GetString(1), r.GetString(2)));
            }
            catch (JsonException) { }
        }

        return actions;
    }

    public void QueueDownlink(string moundId, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO micromound_downlink (mound_id,queued_at,envelope_json)
                VALUES ($mound,$at,$json)";
            Bind(cmd, "$mound", moundId);
            Bind(cmd, "$at", DateTimeOffset.UtcNow.ToWire());
            Bind(cmd, "$json", JsonSerializer.Serialize(envelope, ProtocolJson.Options));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Envelope> DrainDownlink(string moundId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            var taken = new List<Envelope>();

            using (var read = conn.CreateCommand())
            {
                read.CommandText =
                    "SELECT envelope_json FROM micromound_downlink WHERE mound_id=$mound ORDER BY id";
                Bind(read, "$mound", moundId);
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    // A row that will not parse is dropped rather than failing the whole drain: one
                    // corrupt envelope must not strand every later charter behind it forever.
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<Envelope>(r.GetString(0), ProtocolJson.Options);
                        if (envelope is not null) taken.Add(envelope);
                    }
                    catch (JsonException) { }
                }
            }

            using (var delete = conn.CreateCommand())
            {
                delete.CommandText = "DELETE FROM micromound_downlink WHERE mound_id=$mound";
                Bind(delete, "$mound", moundId);
                delete.ExecuteNonQuery();
            }

            return taken;
        }
    }

    public int PendingDownlinkCount(string moundId)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM micromound_downlink WHERE mound_id=$mound";
        Bind(cmd, "$mound", moundId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void DiscardDownlink(string moundId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM micromound_downlink WHERE mound_id=$mound";
            Bind(cmd, "$mound", moundId);
            cmd.ExecuteNonQuery();
        }
    }

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
