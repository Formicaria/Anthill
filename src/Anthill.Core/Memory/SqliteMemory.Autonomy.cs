using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Memory;

/// <summary>
/// Persistence for the 24/7 autonomy rails (Phase 0): the objective backlog and the
/// per-mission audit trail. No execution loop lives here — these are the durable stores the
/// Director will consume in Phase 1. Every query is parameterised, consistent with the rest
/// of <see cref="SqliteMemory"/>.
/// </summary>
public sealed partial class SqliteMemory
{
    // ---- objectives -------------------------------------------------------

    public void SaveObjective(Objective o)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO objectives (id, title, charter, priority, status, max_runs, run_count,
                    consecutive_failures, parent_objective_id, metadata_json, created_at, last_run_at, success_ema)
                  VALUES (@id, @title, @charter, @prio, @status, @max, @rc, @cf, @parent, @meta, @created, @last, @ema)",
                ("@id", o.Id), ("@title", o.Title), ("@charter", o.Charter), ("@prio", o.Priority),
                ("@status", o.Status.Value()), ("@max", o.MaxRuns), ("@rc", o.RunCount), ("@cf", o.ConsecutiveFailures),
                ("@parent", o.ParentObjectiveId), ("@meta", Json.SafeDumps(o.Metadata)),
                ("@created", o.CreatedAt.ToIso()), ("@last", o.LastRunAt.ToIsoOrNull()), ("@ema", o.SuccessEma));
        }
        InvalidateCache();
    }

    public Objective? GetObjective(string objectiveId)
    {
        var row = Query("SELECT * FROM objectives WHERE id = @id", ("@id", objectiveId)).FirstOrDefault();
        return row is null ? null : ObjectiveFromRow(row);
    }

    public List<Objective> ListObjectives(ObjectiveStatus? status = null, int limit = 100)
    {
        var rows = status is null
            ? Query("SELECT * FROM objectives ORDER BY priority DESC, created_at ASC LIMIT @lim", ("@lim", limit))
            : Query("SELECT * FROM objectives WHERE status = @s ORDER BY priority DESC, created_at ASC LIMIT @lim",
                ("@s", status.Value.Value()), ("@lim", limit));
        return rows.Select(ObjectiveFromRow).ToList();
    }

    /// <summary>
    /// The next objective the Director should work: pending or active, not paused/done/failed,
    /// still within its run budget. Highest effective priority first (see
    /// <see cref="NextReadyObjectives"/> for the aging rule), longest-queued first on ties.
    /// </summary>
    public Objective? NextReadyObjective() => NextReadyObjectives(1).FirstOrDefault();

    /// <summary>
    /// Up to <paramref name="limit"/> distinct ready objectives for the Director's concurrency
    /// slots (Phase 3), excluding any already in flight so no objective ever runs two missions at
    /// once. Ordering is strict priority with anti-starvation aging: effective priority =
    /// stored priority + (minutes waited since last run, or creation, ÷
    /// <see cref="AnthillRuntime.AutonomyAgingMinutes"/>); ties break toward the objective that
    /// has been queued longest. Aging is computed here (not persisted) so stored priorities never
    /// drift and the operator's numbers stay authoritative.
    /// </summary>
    public List<Objective> NextReadyObjectives(int limit, ICollection<string>? excludeObjectiveIds = null)
    {
        if (limit <= 0) return new List<Objective>();
        var rows = Query(
            @"SELECT * FROM objectives
              WHERE status IN ('pending','active') AND (max_runs = 0 OR run_count < max_runs)
              ORDER BY priority DESC, created_at ASC LIMIT 200");
        var now = AnthillTime.NowUtc();
        var excluded = excludeObjectiveIds is { Count: > 0 } ? new HashSet<string>(excludeObjectiveIds) : null;
        return rows.Select(ObjectiveFromRow)
            .Where(o => excluded is null || !excluded.Contains(o.Id))
            .OrderByDescending(o => EffectivePriority(o, now))
            .ThenBy(o => QueuedSince(o))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Stored priority plus read-time adjustments: aging credit (+1 per
    /// <see cref="AnthillRuntime.AutonomyAgingMinutes"/> waited; 0 disables) and the Phase 4
    /// learning bias (±<see cref="AnthillRuntime.AutonomyPriorityBiasMax"/> from the success EMA).
    /// </summary>
    internal static long EffectivePriority(Objective o, DateTime nowUtc)
    {
        var effective = (long)o.Priority + Autonomy.ObjectiveLearning.PriorityBias(o);
        var aging = AnthillRuntime.AutonomyAgingMinutes;
        if (aging <= 0) return effective;
        var waitedMinutes = Math.Max(0, (nowUtc - QueuedSince(o)).TotalMinutes);
        return effective + (long)(waitedMinutes / aging);
    }

    /// <summary>When the objective started waiting for its next run: last run if it has one, else creation.</summary>
    private static DateTime QueuedSince(Objective o) => o.LastRunAt ?? o.CreatedAt;

    public Objective? UpdateObjectiveStatus(string objectiveId, ObjectiveStatus status)
    {
        if (GetObjective(objectiveId) is null) return null;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE objectives SET status = @s WHERE id = @id",
                ("@s", status.Value()), ("@id", objectiveId));
        }
        InvalidateCache();
        return GetObjective(objectiveId);
    }

    public Objective? SetObjectivePriority(string objectiveId, int priority)
    {
        if (GetObjective(objectiveId) is null) return null;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE objectives SET priority = @p WHERE id = @id",
                ("@p", priority), ("@id", objectiveId));
        }
        InvalidateCache();
        return GetObjective(objectiveId);
    }

    public bool DeleteObjective(string objectiveId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM objectives WHERE id = @id", ("@id", objectiveId));
        }
        InvalidateCache();
        return true;
    }

    /// <summary>
    /// Applies a completed run's outcome to an objective: increments the run count, stamps
    /// last_run_at, resets/raises the consecutive-failure breaker, folds the run's success score
    /// into the learning EMA (Phase 4 — always recorded, even with learning disabled, so history
    /// exists when it's turned on), and transitions status (Done when the run budget is
    /// exhausted, Paused when the breaker trips, otherwise Active).
    /// Returns the updated objective, or null if it no longer exists.
    /// </summary>
    public Objective? RecordObjectiveRunOutcome(string objectiveId, bool success, double? successScore = null)
    {
        var o = GetObjective(objectiveId);
        if (o is null) return null;

        o.RunCount += 1;
        o.LastRunAt = AnthillTime.NowUtc();
        o.ConsecutiveFailures = success ? 0 : o.ConsecutiveFailures + 1;
        o.SuccessEma = Autonomy.ObjectiveLearning.UpdateEma(o.SuccessEma, successScore);

        if (o.MaxRuns > 0 && o.RunCount >= o.MaxRuns) o.Status = ObjectiveStatus.Done;
        else if (o.ConsecutiveFailures >= AnthillRuntime.AutonomyMaxConsecutiveFailures) o.Status = ObjectiveStatus.Paused;
        else o.Status = ObjectiveStatus.Active;

        SaveObjective(o);
        return o;
    }

    /// <summary>
    /// How many parent_objective_id hops separate this objective from a root (non-follow-up)
    /// objective. Root objectives are depth 0. Used by the Phase 2 Strategist to cap how deep a
    /// chain of self-enqueued follow-ups can grow. Bounded to 64 hops so a corrupted/cyclic
    /// parent chain can never loop forever.
    /// </summary>
    public int ObjectiveDepth(string objectiveId)
    {
        var depth = 0;
        var current = GetObjective(objectiveId);
        var seen = new HashSet<string>();
        while (current?.ParentObjectiveId is { Length: > 0 } parentId && seen.Add(current.Id) && depth < 64)
        {
            depth++;
            current = GetObjective(parentId);
        }
        return depth;
    }

    private static Objective ObjectiveFromRow(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Title = row.GetValueOrDefault("title")?.ToString() ?? "",
        Charter = row.GetValueOrDefault("charter")?.ToString() ?? "",
        Priority = (int)AsLong(row.GetValueOrDefault("priority")),
        Status = EnumExtensions.ParseObjectiveStatus(row.GetValueOrDefault("status")?.ToString() ?? "pending"),
        MaxRuns = (int)AsLong(row.GetValueOrDefault("max_runs")),
        RunCount = (int)AsLong(row.GetValueOrDefault("run_count")),
        ConsecutiveFailures = (int)AsLong(row.GetValueOrDefault("consecutive_failures")),
        ParentObjectiveId = row.GetValueOrDefault("parent_objective_id")?.ToString(),
        Metadata = Json.TryParseObject(row.GetValueOrDefault("metadata_json") as string),
        CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        LastRunAt = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("last_run_at")?.ToString()),
        SuccessEma = AsNullableDouble(row.GetValueOrDefault("success_ema")),
    };

    private static double? AsNullableDouble(object? value) =>
        value is null or DBNull ? null
        : double.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    // ---- autonomy run audit trail -----------------------------------------

    public void SaveAutonomyRun(AutonomyRun r)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO autonomy_runs (id, objective_id, mission_id, generated_goal, mission_status,
                    success_score, follow_ups_created, notes, started_at, finished_at)
                  VALUES (@id, @oid, @mid, @goal, @status, @score, @fu, @notes, @started, @finished)",
                ("@id", r.Id), ("@oid", r.ObjectiveId), ("@mid", r.MissionId), ("@goal", r.GeneratedGoal),
                ("@status", r.MissionStatus), ("@score", r.SuccessScore), ("@fu", r.FollowUpsCreated),
                ("@notes", r.Notes), ("@started", r.StartedAt.ToIso()), ("@finished", r.FinishedAt.ToIsoOrNull()));
        }
        InvalidateCache();
    }

    public List<Dictionary<string, object?>> ListAutonomyRuns(string? objectiveId = null, int limit = 50) =>
        objectiveId is null
            ? Query("SELECT * FROM autonomy_runs ORDER BY started_at DESC LIMIT @lim", ("@lim", limit))
            : Query("SELECT * FROM autonomy_runs WHERE objective_id = @oid ORDER BY started_at DESC LIMIT @lim",
                ("@oid", objectiveId), ("@lim", limit));

    /// <summary>Count of autonomous runs started at or after <paramref name="sinceUtc"/> — the basis for rate budgets.</summary>
    public int CountAutonomyRunsSince(DateTime sinceUtc) =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM autonomy_runs WHERE started_at >= @since", ("@since", sinceUtc.ToIso())));
}
