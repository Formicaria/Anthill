using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Memory;

/// <summary>What the one-time learning reset did, for the audit event and the operator.</summary>
public sealed record LearningResetReport(
    bool AlreadyApplied,
    int ObjectivesReset,
    int TrailsMarkedLegacy,
    string? BackupPath,
    string AppliedAt);

public sealed partial class SqliteMemory
{
    /// <summary>Durable idempotency marker (anthill_meta key). Present ⇒ the reset already ran.</summary>
    public const string LearningResetMarkerKey = "learning_reset_v2_19";
    public const string LearningResetDateKey = "learning_reset_date";

    /// <summary>The neutral trail strength — identical to a freshly created trail with zero delta.</summary>
    internal const double NeutralTrailStrength = 0.5;

    /// <summary>
    /// v2.20.0 Stage 7: the one-time reset of derived learning state accumulated under the
    /// pre-v2.19.0 completion rule (structural completion counted as success; partial counted as
    /// success; nothing required a verifier PASS).
    ///
    /// Resets ONLY state derived under that rule:
    ///  - objectives.success_ema → NULL (neutral/unset); the old value is snapshotted into the
    ///    objective's metadata as legacy_success_ema so reporting keeps it.
    ///  - pheromone trail strength → the neutral 0.5 a fresh trail starts at, and the trail is
    ///    marked legacy (its pre-boundary success evidence cannot be reconstructed as verified).
    ///    Pre-reset success/failure counts are snapshotted into trail metadata; the live
    ///    success_count restarts at 0 so planning order carries no defective signal.
    ///
    /// Never touches: missions, tasks, events, autonomy_runs, approvals, patches, sources, agent
    /// messages, users, providers — all raw history. failure_count and consecutive_failures are
    /// failure history and are preserved in place.
    ///
    /// Legacy trails are retained for reporting (and protected from pruning) but excluded from
    /// planning reads until they record a post-reset success — at which point they re-enter on the
    /// strength of evidence earned under the corrected rule. See GetTopPheromoneTrails.
    ///
    /// Idempotent via a durable meta marker; a fresh database gets the marker and no backup
    /// (nothing to mutate ⇒ nothing to protect). When there IS state to reset, an online SQLite
    /// backup is taken BEFORE any mutation and its path is recorded.
    /// </summary>
    public LearningResetReport ApplyLearningReset()
    {
        var now = AnthillTime.NowUtc().ToIso();
        int objectivesReset;
        int trailsMarked;
        string? backupPath = null;

        lock (_writeLock)
        {
            using var conn = Connect();

            if (MetaValue(conn, LearningResetMarkerKey) is not null)
                return new LearningResetReport(true, 0, 0, null, MetaValue(conn, LearningResetDateKey) ?? now);

            var objectivesWithEma = Count(conn, "SELECT COUNT(*) FROM objectives WHERE success_ema IS NOT NULL");
            var trailRows = Query("SELECT id, trail_key, strength, success_count, failure_count, metadata_json FROM pheromone_trails");

            if (objectivesWithEma > 0 || trailRows.Count > 0)
            {
                // Backup BEFORE mutation, via the online backup API (WAL-safe, unlike File.Copy).
                backupPath = DbPath + $".pre-learning-reset.{AnthillTime.NowUtc():yyyyMMddHHmmss}.bak";
                using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
                       { DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
                {
                    dest.Open();
                    conn.BackupDatabase(dest);
                }
            }

            using var tx = conn.BeginTransaction();

            // 1. Objective EMA → neutral/unset, with the old value kept for reporting.
            objectivesReset = 0;
            foreach (var o in Query("SELECT id, success_ema, metadata_json FROM objectives WHERE success_ema IS NOT NULL"))
            {
                var snapshot = MergeMetadata(o["metadata_json"] as string, new()
                {
                    ["legacy_success_ema"] = o["success_ema"],
                    ["legacy_unverified"] = true,
                    ["learning_reset_at"] = now,
                });
                NonQuery(conn, tx,
                    "UPDATE objectives SET success_ema = NULL, metadata_json = @m WHERE id = @id",
                    ("@m", Json.SafeDumps(snapshot)), ("@id", o["id"]));
                objectivesReset++;
            }

            // 2. Every pre-boundary trail: neutral strength, legacy-marked, success signal
            //    snapshotted then restarted. failure_count is failure history — untouched.
            trailsMarked = 0;
            foreach (var t in trailRows)
            {
                var snapshot = MergeMetadata(t["metadata_json"] as string, new()
                {
                    ["legacy_unverified"] = true,
                    ["legacy_strength"] = t["strength"],
                    ["legacy_success_count"] = t["success_count"],
                    ["legacy_failure_count"] = t["failure_count"],
                    ["learning_reset_at"] = now,
                });
                NonQuery(conn, tx,
                    @"UPDATE pheromone_trails SET strength = @s, success_count = 0, legacy = 1,
                        last_updated = @u, metadata_json = @m WHERE id = @id",
                    ("@s", NeutralTrailStrength), ("@u", now), ("@m", Json.SafeDumps(snapshot)), ("@id", t["id"]));
                trailsMarked++;
            }

            SetMeta(conn, tx, LearningResetMarkerKey, AnthillRuntime.Version);
            SetMeta(conn, tx, LearningResetDateKey, now);
            SetMeta(conn, tx, "learning_reset_objectives", objectivesReset);
            SetMeta(conn, tx, "learning_reset_trails", trailsMarked);
            if (backupPath is not null) SetMeta(conn, tx, "learning_reset_backup", backupPath);
            tx.Commit();
        }
        InvalidateCache();

        // The audit event, outside the lock (LogEvent takes its own) — and only when something was
        // actually reset. A fresh database gets the boundary marker silently: an audit event
        // claiming a reset happened when nothing was touched would be noise in the audit trail,
        // and the test suite would (rightly) see a learning_reset event on every new database.
        if (objectivesReset == 0 && trailsMarked == 0)
            return new LearningResetReport(false, 0, 0, backupPath, now);

        LogEvent(AnthillRuntime.SystemApiMissionId, "learning_reset",
            $"Derived learning state reset at the v2.19.0 boundary: {objectivesReset} objective EMA(s) unset, " +
            $"{trailsMarked} pheromone trail(s) marked legacy_unverified at neutral strength.",
            metadata: new()
            {
                ["objectives_reset"] = objectivesReset,
                ["trails_marked_legacy"] = trailsMarked,
                ["backup_path"] = backupPath,
                ["applied_at"] = now,
                ["anthill_version"] = AnthillRuntime.Version,
                ["note"] = "raw history untouched; failure history untouched; metric correction, not regression",
            });

        return new LearningResetReport(false, objectivesReset, trailsMarked, backupPath, now);
    }

    /// <summary>The reset date, or null when the reset has not run (fresh DBs get it at creation).</summary>
    public string? LearningResetDate()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            return MetaValue(conn, LearningResetDateKey);
        }
    }

    private static string? MetaValue(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM anthill_meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() is string s ? System.Text.Json.JsonSerializer.Deserialize<object?>(s)?.ToString() ?? s : null;
    }

    private static void SetMeta(SqliteConnection conn, SqliteTransaction? tx, string key, object value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO anthill_meta (key, value, updated_at) VALUES (@k, @v, @u)
                            ON CONFLICT(key) DO UPDATE SET value = @v, updated_at = @u";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", Json.SafeDumps(value));
        cmd.Parameters.AddWithValue("@u", AnthillTime.NowUtc().ToIso());
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is long l ? l : 0;
    }
}
