using Anthill.Core.Common;
using Anthill.Core.Conversations;
using Anthill.Core.Projects;

namespace Anthill.Core.Memory;

/// <summary>v0.3.8.48 — project schedules and their runs, persisted. See <see cref="ProjectSchedule"/>.</summary>
public sealed partial class SqliteMemory
{
    public void SaveSchedule(ProjectSchedule s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.Id)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO project_schedules
                    (id, project_id, name, prompt, trigger_type, cron, one_time_at, local_time,
                     timezone, approval_mode, provider, model, enabled, overlap_policy,
                     next_run_at, last_run_at, claimed_by, claimed_at, created_by, updated_by,
                     created_at, updated_at)
                  VALUES (@id, @pid, @name, @prompt, @trig, @cron, @once, @ltime, @tz, @appr,
                          @prov, @model, @en, @overlap, @next, @last, @cby, @cat, @createdby,
                          @updatedby, @created, @updated)
                  ON CONFLICT(id) DO UPDATE SET
                    name=@name, prompt=@prompt, trigger_type=@trig, cron=@cron, one_time_at=@once,
                    local_time=@ltime, timezone=@tz, approval_mode=@appr, provider=@prov,
                    model=@model, enabled=@en, overlap_policy=@overlap, next_run_at=@next,
                    last_run_at=@last, claimed_by=@cby, claimed_at=@cat, updated_by=@updatedby,
                    updated_at=@updated",
                ("@id", s.Id), ("@pid", s.ProjectId), ("@name", s.Name), ("@prompt", s.Prompt),
                ("@trig", s.TriggerType), ("@cron", (object?)s.Cron ?? DBNull.Value),
                ("@once", (object?)s.OneTimeAt?.ToIso() ?? DBNull.Value),
                ("@ltime", (object?)s.LocalTime ?? DBNull.Value), ("@tz", s.Timezone),
                ("@appr", s.ApprovalMode.ToString()),
                ("@prov", (object?)s.Provider ?? DBNull.Value), ("@model", (object?)s.Model ?? DBNull.Value),
                ("@en", s.Enabled ? 1 : 0), ("@overlap", s.OverlapPolicy),
                ("@next", (object?)s.NextRunAt?.ToIso() ?? DBNull.Value),
                ("@last", (object?)s.LastRunAt?.ToIso() ?? DBNull.Value),
                ("@cby", (object?)s.ClaimedBy ?? DBNull.Value),
                ("@cat", (object?)s.ClaimedAt?.ToIso() ?? DBNull.Value),
                ("@createdby", s.CreatedBy), ("@updatedby", s.UpdatedBy),
                ("@created", s.CreatedAt.ToIso()), ("@updated", s.UpdatedAt.ToIso()));
        }
    }

    public ProjectSchedule? LoadSchedule(string id) =>
        Query("SELECT * FROM project_schedules WHERE id=@id", ("@id", id ?? ""))
            .Select(ReadSchedule).FirstOrDefault();

    public IReadOnlyList<ProjectSchedule> LoadProjectSchedules(string projectId) =>
        Query("SELECT * FROM project_schedules WHERE project_id=@pid ORDER BY name",
            ("@pid", projectId ?? "")).Select(ReadSchedule).ToList();

    public IReadOnlyList<ProjectSchedule> LoadAllSchedules() =>
        Query("SELECT * FROM project_schedules ORDER BY project_id, name").Select(ReadSchedule).ToList();

    public void DeleteSchedule(string id)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM project_schedules WHERE id=@id", ("@id", id ?? ""));
        }
    }

    /// <summary>
    /// Atomically claim a DUE schedule for execution. The UPDATE's WHERE re-checks due-ness and
    /// unclaimed-ness, so two ticks (or two hosts on one database) cannot both take the same
    /// occurrence: exactly one UPDATE affects a row. A stale claim older than ten minutes is
    /// reclaimable — a crashed run must not pin its schedule forever.
    /// </summary>
    public bool TryClaimSchedule(string id, string claimant, DateTime nowUtc)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"UPDATE project_schedules
                  SET claimed_by=@who, claimed_at=@now
                  WHERE id=@id AND enabled=1 AND next_run_at IS NOT NULL AND next_run_at<=@now
                    AND (claimed_by IS NULL OR claimed_at<@stale)";
            cmd.Parameters.AddWithValue("@who", claimant);
            cmd.Parameters.AddWithValue("@now", nowUtc.ToIso());
            cmd.Parameters.AddWithValue("@id", id ?? "");
            cmd.Parameters.AddWithValue("@stale", nowUtc.AddMinutes(-10).ToIso());
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void SaveScheduleRun(ScheduleRun run)
    {
        if (run is null || string.IsNullOrWhiteSpace(run.Id)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO schedule_runs
                    (id, schedule_id, project_id, conversation_id, status, ""trigger"", summary,
                     started_at, finished_at)
                  VALUES (@id, @sid, @pid, @cid, @status, @trig, @summary, @started, @finished)",
                ("@id", run.Id), ("@sid", run.ScheduleId), ("@pid", run.ProjectId),
                ("@cid", (object?)run.ConversationId ?? DBNull.Value),
                ("@status", run.Status), ("@trig", run.Trigger),
                ("@summary", (object?)run.Summary ?? DBNull.Value),
                ("@started", run.StartedAt.ToIso()),
                ("@finished", (object?)run.FinishedAt?.ToIso() ?? DBNull.Value));
        }
    }

    public IReadOnlyList<ScheduleRun> LoadScheduleRuns(string scheduleId, int limit = 50) =>
        Query("SELECT * FROM schedule_runs WHERE schedule_id=@sid ORDER BY started_at DESC LIMIT @limit",
            ("@sid", scheduleId ?? ""), ("@limit", Math.Clamp(limit, 1, 500)))
        .Select(ReadRun).ToList();

    private static ProjectSchedule ReadSchedule(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        ProjectId = row.GetValueOrDefault("project_id")?.ToString() ?? "",
        Name = row.GetValueOrDefault("name")?.ToString() ?? "",
        Prompt = row.GetValueOrDefault("prompt")?.ToString() ?? "",
        TriggerType = row.GetValueOrDefault("trigger_type")?.ToString() ?? "manual",
        Cron = Nullable(row, "cron"),
        OneTimeAt = AnthillTime.ParseIsoOrNull(Nullable(row, "one_time_at")),
        LocalTime = Nullable(row, "local_time"),
        Timezone = row.GetValueOrDefault("timezone")?.ToString() ?? "UTC",
        ApprovalMode = Enum.TryParse<EscalationPolicy>(
            row.GetValueOrDefault("approval_mode")?.ToString(), out var p) ? p : EscalationPolicy.Ask,
        Provider = Nullable(row, "provider"),
        Model = Nullable(row, "model"),
        Enabled = Convert.ToInt64(row.GetValueOrDefault("enabled") ?? 0L) != 0,
        OverlapPolicy = row.GetValueOrDefault("overlap_policy")?.ToString() ?? "skip",
        NextRunAt = AnthillTime.ParseIsoOrNull(Nullable(row, "next_run_at")),
        LastRunAt = AnthillTime.ParseIsoOrNull(Nullable(row, "last_run_at")),
        ClaimedBy = Nullable(row, "claimed_by"),
        ClaimedAt = AnthillTime.ParseIsoOrNull(Nullable(row, "claimed_at")),
        CreatedBy = row.GetValueOrDefault("created_by")?.ToString() ?? "",
        UpdatedBy = row.GetValueOrDefault("updated_by")?.ToString() ?? "",
        CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        UpdatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("updated_at")?.ToString()),
    };

    private static ScheduleRun ReadRun(Dictionary<string, object?> row) => new(
        row.GetValueOrDefault("id")?.ToString() ?? "",
        row.GetValueOrDefault("schedule_id")?.ToString() ?? "",
        row.GetValueOrDefault("project_id")?.ToString() ?? "",
        Nullable(row, "conversation_id"),
        row.GetValueOrDefault("status")?.ToString() ?? "running",
        row.GetValueOrDefault("trigger")?.ToString() ?? "schedule",
        Nullable(row, "summary"),
        AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("started_at")?.ToString()),
        AnthillTime.ParseIsoOrNull(Nullable(row, "finished_at")));

    private static string? Nullable(Dictionary<string, object?> row, string col) =>
        row.GetValueOrDefault(col) is null or DBNull ? null : row.GetValueOrDefault(col)?.ToString();
}
