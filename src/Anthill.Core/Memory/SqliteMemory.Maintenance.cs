using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Memory;

/// <summary>
/// Data-hygiene operations for the maintenance/console tools: reclaim disk, clear mission history,
/// clear the objective backlog, and report sizes. Destructive operations run behind admin-only,
/// audited API endpoints — this layer just does the SQL/VACUUM and reports byte deltas.
/// </summary>
public sealed partial class SqliteMemory
{
    // Mission-execution tables cleared by "Clear missions" (the system_api mission row is kept so
    // the API event stream keeps working). objectives, pheromones, users, providers are preserved.
    /// <summary>
    /// Everything that hangs off a mission and must go when the mission does.
    ///
    /// v0.3.8.38 adds `mission_jobs` and `mission_attempts`. They were omitted, so clearing history
    /// left durable job rows whose `mission_id` pointed at a mission that no longer existed —
    /// dangling references that `/jobs` still listed and `/jobs/{id}` would now happily project,
    /// describing work whose record had been deleted underneath it.
    /// </summary>
    private static readonly string[] MissionChildTables =
    {
        "tasks", "patch_proposals", "patch_sets", "approval_requests", "source_records",
        "task_result_summaries", "agent_messages", "message_metrics", "autonomy_runs",
        // v0.3.8.38. These four were omitted, so clearing history left rows whose mission_id
        // pointed at a mission that no longer existed — dangling references that `/jobs` still
        // listed and, as of this release, `/jobs/{id}` would happily project, describing work whose
        // record had been deleted underneath it.
        //
        // `mission_jobs` and `mission_attempts` are created LAZILY by EnsureJobTables rather than in
        // the schema list, so they may legitimately not exist yet on a fresh install; `TableExists`
        // below is why naming them here is safe.
        "mission_jobs", "mission_attempts", "task_attempts",
    };

    /// <summary>
    /// True when the table is actually present.
    ///
    /// Needed because two of the tables above are created on first use rather than at startup, and
    /// `DELETE FROM` a table that does not exist throws — which would turn "clear history on a fresh
    /// install" into an error for a database that simply had nothing to clear.
    /// </summary>
    private static bool TableExists(SqliteConnection conn, SqliteTransaction? tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Current on-disk size of the SQLite database file in bytes (0 if it doesn't exist yet).</summary>
    public long DatabaseFileBytes()
    {
        try { return File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Row counts for the big tables — powers the maintenance panel's "what's using space" view.</summary>
    public Dictionary<string, object?> TableCounts() => new()
    {
        ["missions"] = CountRows("missions"),
        ["tasks"] = CountRows("tasks"),
        ["events"] = CountRows("events"),
        ["agent_messages"] = CountRows("agent_messages"),
        ["patch_proposals"] = CountRows("patch_proposals"),
        ["objectives"] = CountRows("objectives"),
        ["autonomy_runs"] = CountRows("autonomy_runs"),
        ["source_records"] = CountRows("source_records"),
        ["pheromone_trails"] = CountRows("pheromone_trails"),
    };

    private long CountRows(string table)
    {
        try { return AsLong(Scalar($"SELECT COUNT(*) FROM {table}")); } catch { return 0; }
    }

    /// <summary>
    /// Flush Cache: prune old events (when a retention window is configured) and reclaim free pages
    /// with VACUUM. Backup pruning is handled by the caller (it lives in FileSecurity). Returns the
    /// DB file size before/after and how many event rows were deleted.
    /// </summary>
    public (long DbBefore, long DbAfter, int EventsDeleted) FlushCache(int eventRetentionDays)
    {
        var before = DatabaseFileBytes();
        var eventsDeleted = 0;
        lock (_writeLock)
        {
            using var conn = Connect();
            if (eventRetentionDays > 0)
            {
                var cutoff = AnthillTime.NowUtc().AddDays(-eventRetentionDays).ToIso();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM events WHERE created_at < @c AND mission_id != @sys";
                cmd.Parameters.AddWithValue("@c", cutoff);
                cmd.Parameters.AddWithValue("@sys", AnthillRuntime.SystemApiMissionId);
                eventsDeleted = cmd.ExecuteNonQuery();
            }
            Vacuum(conn);
        }
        InvalidateCache();
        return (before, DatabaseFileBytes(), eventsDeleted);
    }

    /// <summary>
    /// Clear missions: deletes all mission-execution history (missions, tasks, events, patches,
    /// approvals, sources, agent messages/metrics, autonomy runs), keeping the objective backlog,
    /// pheromone memory, users, providers, and config. VACUUMs afterward. Returns bytes reclaimed.
    /// </summary>
    public (long Freed, int MissionsDeleted) ClearMissionHistory()
    {
        var before = DatabaseFileBytes();
        var missionsDeleted = 0;
        lock (_writeLock)
        {
            using var conn = Connect();
            using (var fk = conn.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=OFF"; fk.ExecuteNonQuery(); }
            using (var tx = conn.BeginTransaction())
            {
                foreach (var t in MissionChildTables)
                    if (TableExists(conn, tx, t)) Exec(conn, tx, $"DELETE FROM {t}");
                Exec(conn, tx, $"DELETE FROM events WHERE mission_id != '{AnthillRuntime.SystemApiMissionId}'");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM missions WHERE id != '{AnthillRuntime.SystemApiMissionId}'";
                    missionsDeleted = cmd.ExecuteNonQuery();
                }
                TryExec(conn, tx, "DELETE FROM missions_fts"); // FTS mirror (present only when FTS is enabled)
                tx.Commit();
            }
            Vacuum(conn);
        }
        InvalidateCache();
        return (Math.Max(0, before - DatabaseFileBytes()), missionsDeleted);
    }

    /// <summary>
    /// Dump directives: deletes the entire objective backlog and its autonomy-run audit trail,
    /// leaving missions, pheromones, users, providers, and config intact. VACUUMs afterward.
    /// </summary>
    public (long Freed, int ObjectivesDeleted) ClearObjectives()
    {
        var before = DatabaseFileBytes();
        var deleted = 0;
        lock (_writeLock)
        {
            using var conn = Connect();
            using (var fk = conn.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=OFF"; fk.ExecuteNonQuery(); }
            using (var tx = conn.BeginTransaction())
            {
                Exec(conn, tx, "DELETE FROM autonomy_runs");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM objectives";
                deleted = cmd.ExecuteNonQuery();
                tx.Commit();
            }
            Vacuum(conn);
        }
        InvalidateCache();
        return (Math.Max(0, before - DatabaseFileBytes()), deleted);
    }

    private static void Vacuum(SqliteConnection conn)
    {
        try { using var cmd = conn.CreateCommand(); cmd.CommandText = "VACUUM"; cmd.ExecuteNonQuery(); }
        catch { /* VACUUM can fail if another connection holds a lock — safe to skip */ }
    }

    private static void TryExec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        try { Exec(conn, tx, sql); } catch { /* table may not exist (e.g. FTS disabled) */ }
    }
}
