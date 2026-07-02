using Microsoft.Data.Sqlite;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Memory;

/// <summary>
/// Data operations for <see cref="SqliteMemory"/>. Returns rows as
/// <c>Dictionary&lt;string, object?&gt;</c> to mirror the dict-shaped results the Queen
/// formatters and API contracts consumed in the Python build, keeping those call sites
/// almost line-for-line faithful.
/// </summary>
public sealed partial class SqliteMemory
{
    // ---- low-level helpers ------------------------------------------------

    private List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private void NonQuery(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static long AsLong(object? value) => value switch
    {
        long l => l, int i => i, double d => (long)d, null => 0, _ => long.TryParse(value.ToString(), out var p) ? p : 0,
    };

    // ---- missions + tasks -------------------------------------------------

    /// <summary>
    /// Idempotently seeds a sentinel mission row so system-level events/messages (e.g. the
    /// <c>system_api</c> channel and self-test probes) satisfy the events→missions foreign key
    /// on a fresh database. Safe to call repeatedly; never overwrites an existing mission.
    /// </summary>
    public void EnsureSystemMission(string id, string goal = "System mission")
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            var now = AnthillTime.NowUtc().ToIso();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO missions (id, goal, status, created_at, saved_at)
                  VALUES (@id, @goal, @status, @created, @saved)",
                ("@id", id), ("@goal", goal), ("@status", MissionStatus.Complete.Value()),
                ("@created", now), ("@saved", now));
        }
    }

    public void SaveMission(Mission mission)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            NonQuery(conn, tx,
                @"INSERT OR REPLACE INTO missions (id, goal, status, user_result, debug_result, final_result,
                    best_output_task_id, success_score, created_at, saved_at)
                  VALUES (@id, @goal, @status, @ur, @dr, @fr, @best, @score, @created, @saved)",
                ("@id", mission.Id), ("@goal", mission.Goal), ("@status", mission.Status.Value()),
                ("@ur", mission.UserResult), ("@dr", mission.DebugResult), ("@fr", mission.FinalResult),
                ("@best", mission.BestOutputTaskId), ("@score", mission.SuccessScore),
                ("@created", mission.CreatedAt.ToIso()), ("@saved", AnthillTime.NowUtc().ToIso()));

            foreach (var task in mission.Tasks)
                UpsertTask(conn, tx, mission.Id, task);

            if (FtsAvailable)
            {
                try
                {
                    NonQuery(conn, tx, "DELETE FROM missions_fts WHERE id = @id", ("@id", mission.Id));
                    NonQuery(conn, tx,
                        "INSERT INTO missions_fts (id, goal, user_result, final_result) VALUES (@id, @goal, @ur, @fr)",
                        ("@id", mission.Id), ("@goal", mission.Goal), ("@ur", mission.UserResult ?? ""), ("@fr", mission.FinalResult ?? ""));
                }
                catch (SqliteException) { FtsAvailable = false; }
            }
            tx.Commit();
        }
        InvalidateCache();
    }

    /// <summary>
    /// Persists one task's current state mid-mission so /graph (and the colony canvas driven by
    /// it) reflects live execution — running, complete, failed, skipped — instead of only seeing
    /// tasks after the mission finishes. Called on every task status transition.
    /// </summary>
    public void SaveTask(string missionId, Task task)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            UpsertTask(conn, null, missionId, task);
        }
        InvalidateCache();
    }

    private void UpsertTask(SqliteConnection conn, SqliteTransaction? tx, string missionId, Task task) =>
        NonQuery(conn, tx,
            @"INSERT OR REPLACE INTO tasks (id, mission_id, title, description, assigned_ant, task_type,
                parent_task_id, parent_task_ids_json, depends_on_json, status, result, result_summary,
                result_chars, estimated_tokens, created_at, started_at, finished_at, completed_at, failed_at,
                skipped_at, elapsed_seconds, attempt_count, max_attempts, failure_reason, failure_type,
                skipped_reason, blocked_reason)
              VALUES (@id, @mid, @title, @desc, @ant, @tt, @pid, @pids, @deps, @status, @result, @summary,
                @rc, @et, @created, @started, @finished, @completed, @failed, @skipped, @elapsed, @attempts,
                @max, @freason, @ftype, @sreason, @breason)",
            ("@id", task.Id), ("@mid", missionId), ("@title", task.Title), ("@desc", task.Description),
            ("@ant", task.AssignedAnt), ("@tt", task.TaskType), ("@pid", task.ParentTaskId),
            ("@pids", Json.SafeDumps(task.ParentTaskIds)), ("@deps", Json.SafeDumps(task.DependsOn)),
            ("@status", task.Status.Value()), ("@result", task.Result), ("@summary", task.ResultSummary),
            ("@rc", task.ResultChars), ("@et", task.EstimatedTokens),
            ("@created", ((DateTime?)task.CreatedAt).ToIsoOrNull()), ("@started", task.StartedAt.ToIsoOrNull()),
            ("@finished", task.FinishedAt.ToIsoOrNull()), ("@completed", task.CompletedAt.ToIsoOrNull()),
            ("@failed", task.FailedAt.ToIsoOrNull()), ("@skipped", task.SkippedAt.ToIsoOrNull()),
            ("@elapsed", task.ElapsedSeconds), ("@attempts", task.AttemptCount),
            ("@max", Math.Max(1, task.MaxAttempts)), ("@freason", task.FailureReason),
            ("@ftype", task.FailureType), ("@sreason", task.SkippedReason), ("@breason", task.BlockedReason));

    public void SavePatchSet(PatchSet patchSet)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            NonQuery(conn, tx,
                @"INSERT OR REPLACE INTO patch_sets (id, mission_id, task_id, summary, proposal_count, created_at)
                  VALUES (@id, @mid, @tid, @summary, @count, @created)",
                ("@id", patchSet.Id), ("@mid", patchSet.MissionId), ("@tid", patchSet.TaskId),
                ("@summary", patchSet.Summary), ("@count", patchSet.Proposals.Count), ("@created", patchSet.CreatedAt.ToIso()));

            foreach (var p in patchSet.Proposals)
                NonQuery(conn, tx,
                    @"INSERT OR REPLACE INTO patch_proposals (id, patch_set_id, mission_id, task_id, file_path,
                        change_type, reason, risk, old_content, new_content, requires_approval, status, created_at)
                      VALUES (@id, @pid, @mid, @tid, @fp, @ct, @reason, @risk, @old, @new, @ra, @status, @created)",
                    ("@id", p.Id), ("@pid", patchSet.Id), ("@mid", patchSet.MissionId), ("@tid", patchSet.TaskId),
                    ("@fp", p.FilePath), ("@ct", p.ChangeType.Value()), ("@reason", p.Reason), ("@risk", p.Risk),
                    // Patch bodies may contain proprietary source — sealed at rest with AES-GCM.
                    ("@old", _cipher.Protect(p.OldContent)), ("@new", _cipher.Protect(p.NewContent)),
                    ("@ra", p.RequiresApproval ? 1 : 0), ("@status", p.Status.Value()), ("@created", p.CreatedAt.ToIso()));
            tx.Commit();
        }
        InvalidateCache();
    }

    public void SaveTaskResultSummary(string missionId, Task task)
    {
        if (!AnthillRuntime.EnableResultSummaries) return;
        var summary = task.ResultSummary ?? TextUtil.CreateResultSummary(task.Result);
        task.ResultSummary = summary;
        task.ResultChars = (task.Result ?? "").Length;
        task.EstimatedTokens = TextUtil.EstimateTokenCount(task.Result);
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO task_result_summaries (task_id, mission_id, ant_name, task_type, status,
                    summary, result_chars, estimated_tokens, created_at)
                  VALUES (@tid, @mid, @ant, @tt, @status, @summary, @rc, @et, @created)",
                ("@tid", task.Id), ("@mid", missionId), ("@ant", task.AssignedAnt), ("@tt", task.TaskType),
                ("@status", task.Status.Value()), ("@summary", summary), ("@rc", task.ResultChars),
                ("@et", task.EstimatedTokens), ("@created", AnthillTime.NowUtc().ToIso()));
        }
    }

    public void LogMessageMetric(string missionId, string? taskId, string? antName, string metricType,
        int inputChars, int outputChars, Dictionary<string, object?>? metadata = null)
    {
        if (!AnthillRuntime.EnableMessageMetrics) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO message_metrics (id, mission_id, task_id, ant_name, metric_type, input_chars,
                    output_chars, input_tokens_est, output_tokens_est, metadata_json, created_at)
                  VALUES (@id, @mid, @tid, @ant, @mt, @ic, @oc, @ite, @ote, @meta, @created)",
                ("@id", Guid.NewGuid().ToString()), ("@mid", missionId), ("@tid", taskId), ("@ant", antName),
                ("@mt", metricType), ("@ic", inputChars), ("@oc", outputChars),
                ("@ite", TextUtil.EstimateTokenCount(new string('x', Math.Max(0, inputChars)))),
                ("@ote", TextUtil.EstimateTokenCount(new string('x', Math.Max(0, outputChars)))),
                ("@meta", Json.SafeDumps(metadata ?? new())), ("@created", AnthillTime.NowUtc().ToIso()));
        }
    }

    // ---- agent messages ---------------------------------------------------

    public void SaveAgentMessage(AgentMessage message)
    {
        if (!AnthillRuntime.EnableAgentCommunicationLedger) return;
        message.Content = TextUtil.Truncate(message.Content ?? "", AnthillRuntime.MaxAgentMessageContentChars, "...[agent message truncated]");
        message.ContentChars = (message.Content ?? "").Length;
        message.EstimatedTokens = TextUtil.EstimateTokenCount(message.Content);
        var metaJson = TextUtil.Truncate(Json.SafeDumps(message.Metadata ?? new()), AnthillRuntime.MaxAgentMessageContentChars, "...[metadata truncated]");
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO agent_messages (id, mission_id, task_id, sender, recipient, message_type, content,
                    content_chars, estimated_tokens, metadata_json, schema_version, created_at)
                  VALUES (@id, @mid, @tid, @sender, @rcpt, @mt, @content, @cc, @et, @meta, @sv, @created)",
                ("@id", message.Id), ("@mid", message.MissionId), ("@tid", message.TaskId), ("@sender", message.Sender),
                ("@rcpt", message.Recipient), ("@mt", message.MessageType), ("@content", message.Content),
                ("@cc", message.ContentChars), ("@et", message.EstimatedTokens), ("@meta", metaJson),
                ("@sv", message.SchemaVersion), ("@created", message.CreatedAt.ToIso()));
        }
    }

    public AgentMessage LogAgentMessage(string missionId, string sender, string recipient, string messageType,
        string content = "", string? taskId = null, Dictionary<string, object?>? metadata = null)
    {
        var message = new AgentMessage
        {
            MissionId = missionId, TaskId = taskId, Sender = sender, Recipient = recipient, MessageType = messageType,
            Content = TextUtil.Truncate(content ?? "", AnthillRuntime.MaxAgentMessageContentChars, "...[agent message truncated]"),
            Metadata = metadata ?? new(),
        };
        message.ContentChars = (message.Content ?? "").Length;
        message.EstimatedTokens = TextUtil.EstimateTokenCount(message.Content);
        SaveAgentMessage(message);
        return message;
    }

    public List<Dictionary<string, object?>> GetRecentAgentMessages(int limit = 30, string? missionId = null) =>
        missionId is null
            ? Query("SELECT * FROM agent_messages ORDER BY created_at DESC LIMIT @lim", ("@lim", limit))
            : Query("SELECT * FROM agent_messages WHERE mission_id = @mid ORDER BY created_at DESC LIMIT @lim",
                ("@mid", missionId), ("@lim", limit));

    public Dictionary<string, object?> SummarizeAgentMessages()
    {
        var summary = Query(
            @"SELECT COUNT(*) AS message_count, COALESCE(SUM(content_chars),0) AS content_chars,
                COALESCE(SUM(estimated_tokens),0) AS estimated_tokens, COUNT(DISTINCT mission_id) AS mission_count,
                MAX(created_at) AS last_message_at FROM agent_messages").FirstOrDefault() ?? new();
        summary["top_message_types"] = Query(
            "SELECT message_type, COUNT(*) AS count FROM agent_messages GROUP BY message_type ORDER BY count DESC, message_type ASC LIMIT 12");
        return summary;
    }

    // ---- source records ---------------------------------------------------

    public void SaveSourceRecord(SourceRecord s)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO source_records (id, mission_id, task_id, ant_name, title, url, domain,
                    snippet, summary, provider, relevance_score, freshness_score, authority_score, confidence_score,
                    confidence_label, quality_notes, created_at)
                  VALUES (@id, @mid, @tid, @ant, @title, @url, @domain, @snip, @summary, @prov, @rel, @fresh, @auth,
                    @conf, @label, @notes, @created)",
                ("@id", s.Id), ("@mid", s.MissionId), ("@tid", s.TaskId), ("@ant", s.AntName), ("@title", s.Title),
                ("@url", s.Url), ("@domain", s.Domain), ("@snip", s.Snippet), ("@summary", s.Summary), ("@prov", s.Provider),
                ("@rel", s.RelevanceScore), ("@fresh", s.FreshnessScore), ("@auth", s.AuthorityScore),
                ("@conf", s.ConfidenceScore), ("@label", s.ConfidenceLabel), ("@notes", s.QualityNotes), ("@created", s.CreatedAt.ToIso()));
        }
        InvalidateCache();
    }

    public Dictionary<string, object?>? GetSourceRecord(string sourceId) =>
        Query("SELECT * FROM source_records WHERE id = @id", ("@id", sourceId)).FirstOrDefault();

    public List<Dictionary<string, object?>> GetRecentSources(int limit = 20) =>
        Query("SELECT * FROM source_records ORDER BY created_at DESC LIMIT @lim", ("@lim", limit));

    public int CountSourcesForMission(string missionId) =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM source_records WHERE mission_id = @mid", ("@mid", missionId)));

    public int CountWebSearchAttemptsForMission(string missionId) =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM events WHERE mission_id = @mid AND event_type = @t",
            ("@mid", missionId), ("@t", "web_search_attempted")));

    public List<Dictionary<string, object?>> GetSourceQualitySummary(int limit = 20) =>
        Query(@"SELECT domain, COUNT(*) AS source_count, ROUND(AVG(relevance_score),3) AS avg_relevance,
                  ROUND(AVG(freshness_score),3) AS avg_freshness, ROUND(AVG(authority_score),3) AS avg_authority,
                  ROUND(AVG(confidence_score),3) AS avg_confidence, MAX(created_at) AS last_seen
                FROM source_records GROUP BY domain
                ORDER BY avg_confidence DESC, source_count DESC, last_seen DESC LIMIT @lim", ("@lim", limit));

    // ---- metrics / tasks --------------------------------------------------

    public List<Dictionary<string, object?>> GetRecentMessageMetrics(int limit = 20) =>
        Query("SELECT * FROM message_metrics ORDER BY created_at DESC LIMIT @lim", ("@lim", limit));

    public Dictionary<string, object?> SummarizeMessageMetrics() =>
        Query(@"SELECT COUNT(*) AS metric_count, COALESCE(SUM(input_chars),0) AS input_chars,
                  COALESCE(SUM(output_chars),0) AS output_chars, COALESCE(SUM(input_tokens_est),0) AS input_tokens_est,
                  COALESCE(SUM(output_tokens_est),0) AS output_tokens_est FROM message_metrics").FirstOrDefault() ?? new();

    public List<Dictionary<string, object?>> GetRecentTasks(int limit = 20) =>
        Query(@"SELECT t.id, t.mission_id, t.title, t.assigned_ant, t.task_type, t.status, t.result_summary,
                  t.result_chars, t.estimated_tokens, t.created_at, t.started_at, t.finished_at, t.completed_at,
                  t.failed_at, t.skipped_at, t.elapsed_seconds, t.attempt_count, t.max_attempts, t.failure_type,
                  t.failure_reason, t.skipped_reason, t.blocked_reason, m.goal AS mission_goal
                FROM tasks t LEFT JOIN missions m ON t.mission_id = m.id
                ORDER BY COALESCE(t.finished_at, t.started_at, m.saved_at, m.created_at) DESC LIMIT @lim", ("@lim", limit));

    public Dictionary<string, object?> SummarizeTaskMetrics() =>
        Query(@"SELECT COUNT(*) AS task_count, COALESCE(AVG(elapsed_seconds),0) AS avg_elapsed_seconds,
                  COALESCE(MAX(elapsed_seconds),0) AS max_elapsed_seconds,
                  COALESCE(SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END),0) AS failed_count,
                  COALESCE(SUM(CASE WHEN status='skipped' THEN 1 ELSE 0 END),0) AS skipped_count
                FROM tasks").FirstOrDefault() ?? new();

    // ---- patches ----------------------------------------------------------

    public Dictionary<string, object?>? GetPatchProposal(string patchId)
    {
        var row = Query(@"SELECT pp.*, ps.summary AS patch_set_summary, m.goal AS mission_goal
                          FROM patch_proposals pp LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id
                          LEFT JOIN missions m ON pp.mission_id = m.id WHERE pp.id = @id", ("@id", patchId)).FirstOrDefault();
        if (row is null) return null;
        // Unseal the encrypted patch bodies for the caller.
        if (row.TryGetValue("old_content", out var oc)) row["old_content"] = _cipher.Unprotect(oc as string);
        if (row.TryGetValue("new_content", out var nc)) row["new_content"] = _cipher.Unprotect(nc as string);
        return row;
    }

    public List<Dictionary<string, object?>> ListPatchProposals(PatchStatus? status = null, int limit = 20)
    {
        const string baseSql = @"SELECT pp.id, pp.patch_set_id, pp.mission_id, pp.task_id, pp.file_path, pp.change_type,
            pp.reason, pp.risk, pp.requires_approval, pp.status, pp.created_at, pp.applied_at, pp.backup_path,
            pp.last_error, ps.summary AS patch_set_summary
            FROM patch_proposals pp LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id";
        return status is null
            ? Query(baseSql + " ORDER BY pp.created_at DESC LIMIT @lim", ("@lim", limit))
            : Query(baseSql + " WHERE pp.status = @s ORDER BY pp.created_at DESC LIMIT @lim",
                ("@s", status.Value.Value()), ("@lim", limit));
    }

    public void UpdatePatchStatus(string patchId, PatchStatus status, string? appliedAt = null,
        string? backupPath = null, string? lastError = null)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE patch_proposals SET status = @s, applied_at = COALESCE(@a, applied_at),
                    backup_path = COALESCE(@b, backup_path), last_error = @e WHERE id = @id",
                ("@s", status.Value()), ("@a", appliedAt), ("@b", backupPath), ("@e", lastError), ("@id", patchId));
        }
    }

    // ---- approvals --------------------------------------------------------

    public void SaveApprovalRequest(ApprovalRequest a)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO approval_requests (id, mission_id, task_id, action_type, target_id, title,
                    description, status, requested_by, decision_note, metadata_json, created_at, decided_at)
                  VALUES (@id, @mid, @tid, @at, @target, @title, @desc, @status, @by, @note, @meta, @created, @decided)",
                ("@id", a.Id), ("@mid", a.MissionId), ("@tid", a.TaskId), ("@at", a.ActionType.Value()),
                ("@target", a.TargetId), ("@title", a.Title), ("@desc", a.Description), ("@status", a.Status.Value()),
                ("@by", a.RequestedBy), ("@note", _cipher.Protect(a.DecisionNote)), ("@meta", Json.SafeDumps(a.Metadata)),
                ("@created", a.CreatedAt.ToIso()), ("@decided", a.DecidedAt.ToIsoOrNull()));
        }
    }

    public Dictionary<string, object?>? GetApprovalRequest(string approvalId)
    {
        var row = Query("SELECT * FROM approval_requests WHERE id = @id", ("@id", approvalId)).FirstOrDefault();
        if (row is not null && row.TryGetValue("decision_note", out var note)) row["decision_note"] = _cipher.Unprotect(note as string);
        return row;
    }

    public Dictionary<string, object?>? GetApprovalForTarget(string targetId, ApprovalActionType actionType = ApprovalActionType.PatchProposal) =>
        Query("SELECT * FROM approval_requests WHERE target_id = @t AND action_type = @at ORDER BY created_at DESC LIMIT 1",
            ("@t", targetId), ("@at", actionType.Value())).FirstOrDefault();

    public List<Dictionary<string, object?>> ListApprovalRequests(ApprovalStatus? status = ApprovalStatus.Pending, int limit = 20) =>
        status is null
            ? Query("SELECT * FROM approval_requests ORDER BY created_at DESC LIMIT @lim", ("@lim", limit))
            : Query("SELECT * FROM approval_requests WHERE status = @s ORDER BY created_at DESC LIMIT @lim",
                ("@s", status.Value.Value()), ("@lim", limit));

    public int CountPendingApprovals() =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM approval_requests WHERE status = @s", ("@s", ApprovalStatus.Pending.Value())));

    // ---- per-mission observability (the mission report) --------------------

    /// <summary>Secret-free patch proposals for one mission — the report's "tangible changes" list.</summary>
    public List<Dictionary<string, object?>> ListPatchProposalsForMission(string missionId, int limit = 100) =>
        Query(@"SELECT pp.id, pp.patch_set_id, pp.task_id, pp.file_path, pp.change_type, pp.reason, pp.risk,
                    pp.status, pp.created_at, pp.applied_at, pp.last_error, ps.summary AS patch_set_summary
                FROM patch_proposals pp LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id
                WHERE pp.mission_id = @m ORDER BY pp.created_at ASC LIMIT @lim",
            ("@m", missionId), ("@lim", limit));

    /// <summary>Approval requests raised by one mission, any status.</summary>
    public List<Dictionary<string, object?>> ListApprovalRequestsForMission(string missionId, int limit = 100) =>
        Query("SELECT * FROM approval_requests WHERE mission_id = @m ORDER BY created_at ASC LIMIT @lim",
            ("@m", missionId), ("@lim", limit));

    public Dictionary<string, object?>? UpdateApprovalStatus(string approvalId, ApprovalStatus newStatus, string? decisionNote = null)
    {
        if (GetApprovalRequest(approvalId) is null) return null;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE approval_requests SET status = @s, decision_note = @note, decided_at = @decided WHERE id = @id",
                ("@s", newStatus.Value()), ("@note", _cipher.Protect(decisionNote)),
                ("@decided", AnthillTime.NowUtc().ToIso()), ("@id", approvalId));
        }
        return GetApprovalRequest(approvalId);
    }

    // ---- events -----------------------------------------------------------

    public Event LogEvent(string missionId, string eventType, string message, string? taskId = null,
        string? antName = null, Dictionary<string, object?>? metadata = null)
    {
        var ev = new Event
        {
            MissionId = missionId, TaskId = taskId, AntName = antName, EventType = eventType,
            Message = message, Metadata = metadata ?? new(),
        };
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO events (id, mission_id, task_id, ant_name, event_type, message, metadata_json, created_at)
                  VALUES (@id, @mid, @tid, @ant, @et, @msg, @meta, @created)",
                ("@id", ev.Id), ("@mid", ev.MissionId), ("@tid", ev.TaskId), ("@ant", ev.AntName),
                ("@et", ev.EventType), ("@msg", ev.Message), ("@meta", Json.SafeDumps(ev.Metadata)), ("@created", ev.CreatedAt.ToIso()));
        }
        return ev;
    }

    public List<Dictionary<string, object?>> GetRecentEvents(int limit = 30, string? eventType = null, string? missionId = null)
    {
        var sql = "SELECT id, mission_id, task_id, ant_name, event_type, message, metadata_json, created_at FROM events";
        var conditions = new List<string>();
        var args = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(eventType)) { conditions.Add("event_type = @et"); args.Add(("@et", eventType)); }
        if (!string.IsNullOrEmpty(missionId)) { conditions.Add("mission_id = @mid"); args.Add(("@mid", missionId)); }
        if (conditions.Count > 0) sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY created_at DESC LIMIT @lim";
        args.Add(("@lim", limit));
        return Query(sql, args.ToArray());
    }

    public Dictionary<string, object?> SummarizeEvents()
    {
        var summary = Query(
            @"SELECT COUNT(*) AS event_count,
                COALESCE(SUM(CASE WHEN event_type IN ('task_failed','tool_failed','patch_apply_failed',
                    'patch_proposal_parse_failed','mission_timeout','task_timeout','model_call_failed')
                    THEN 1 ELSE 0 END),0) AS failure_event_count,
                COALESCE(SUM(CASE WHEN event_type='task_completed' THEN 1 ELSE 0 END),0) AS task_completed_count,
                COALESCE(SUM(CASE WHEN event_type='model_call_completed' THEN 1 ELSE 0 END),0) AS model_call_count,
                MAX(created_at) AS last_event_at FROM events").FirstOrDefault() ?? new();
        summary["top_event_types"] = Query(
            "SELECT event_type, COUNT(*) AS count FROM events GROUP BY event_type ORDER BY count DESC, event_type ASC LIMIT 12");
        return summary;
    }

    public List<Dictionary<string, object?>> GetRecentFailureEvents(int limit = 12)
    {
        var types = AnthillRuntime.FailureEventTypes.ToList();
        var placeholders = string.Join(",", types.Select((_, i) => $"@t{i}"));
        var args = types.Select((t, i) => ($"@t{i}", (object?)t)).ToList();
        args.Add(("@lim", limit));
        return Query(
            $"SELECT id, mission_id, task_id, ant_name, event_type, message, metadata_json, created_at FROM events " +
            $"WHERE event_type IN ({placeholders}) ORDER BY created_at DESC LIMIT @lim", args.ToArray());
    }

    // ---- pheromones -------------------------------------------------------

    public void UpdatePheromoneTrail(string trailKey, string trailType, bool success, double strengthDelta,
        Dictionary<string, object?>? metadata = null)
    {
        metadata ??= new();
        lock (_writeLock)
        {
            using var conn = Connect();
            var existing = Query("SELECT * FROM pheromone_trails WHERE trail_key = @k", ("@k", trailKey)).FirstOrDefault();
            if (existing is not null)
            {
                // Faithful trail strength: round(strength + delta, 4) clamped to [0,1].
                var newStrength = Math.Clamp(Math.Round(Convert.ToDouble(existing["strength"]) + strengthDelta, 4), 0.0, 1.0);
                var successCount = (int)AsLong(existing["success_count"]) + (success ? 1 : 0);
                var failureCount = (int)AsLong(existing["failure_count"]) + (success ? 0 : 1);
                var merged = MergeMetadata(existing["metadata_json"] as string, metadata);
                NonQuery(conn, null,
                    @"UPDATE pheromone_trails SET strength=@s, success_count=@sc, failure_count=@fc,
                        last_updated=@u, metadata_json=@m WHERE trail_key=@k",
                    ("@s", newStrength), ("@sc", successCount), ("@fc", failureCount),
                    ("@u", AnthillTime.NowUtc().ToIso()), ("@m", Json.SafeDumps(merged)), ("@k", trailKey));
            }
            else
            {
                var initial = Math.Clamp(Math.Round(0.5 + strengthDelta, 4), 0.0, 1.0);
                NonQuery(conn, null,
                    @"INSERT INTO pheromone_trails (id, trail_key, trail_type, strength, success_count, failure_count,
                        last_updated, metadata_json) VALUES (@id, @k, @t, @s, @sc, @fc, @u, @m)",
                    ("@id", Guid.NewGuid().ToString()), ("@k", trailKey), ("@t", trailType), ("@s", initial),
                    ("@sc", success ? 1 : 0), ("@fc", success ? 0 : 1), ("@u", AnthillTime.NowUtc().ToIso()),
                    ("@m", Json.SafeDumps(metadata)));
            }
        }
        InvalidateCache();
    }

    private static Dictionary<string, object?> MergeMetadata(string? existingJson, Dictionary<string, object?> incoming)
    {
        var merged = new Dictionary<string, object?>();
        try
        {
            if (!string.IsNullOrEmpty(existingJson))
            {
                var old = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson);
                if (old is not null) foreach (var (k, v) in old) merged[k] = v;
            }
        }
        catch { /* ignore malformed prior metadata */ }
        foreach (var (k, v) in incoming) merged[k] = v;
        return merged;
    }

    public void UpdateMissionPheromones(Mission mission)
    {
        var success = mission.Status is MissionStatus.Complete or MissionStatus.Partial;
        var score = mission.SuccessScore ?? 0.0;
        var delta = mission.Status switch
        {
            MissionStatus.Complete => 0.05 + score * 0.05,
            MissionStatus.Partial => 0.01 + score * 0.02,
            _ => -0.08,
        };
        var antPath = mission.Tasks.Select(t => t.AssignedAnt).ToList();
        var taskTypePath = mission.Tasks.Select(t => t.TaskType).ToList();

        UpdatePheromoneTrail("planner_pattern:" + string.Join("_", antPath), "planner_pattern", success, delta,
            new() { ["mission_id"] = mission.Id, ["goal"] = mission.Goal, ["score"] = score, ["ant_path"] = antPath, ["mission_status"] = mission.Status.Value() });
        UpdatePheromoneTrail("task_pattern:" + string.Join("_", taskTypePath), "task_pattern", success, delta,
            new() { ["mission_id"] = mission.Id, ["goal"] = mission.Goal, ["score"] = score, ["task_type_path"] = taskTypePath, ["mission_status"] = mission.Status.Value() });

        foreach (var task in mission.Tasks)
        {
            var taskSuccess = task.Status == TaskStatus.Complete;
            var taskDelta = task.Status == TaskStatus.Skipped ? -0.01 : taskSuccess && success ? 0.03 : -0.04;
            UpdatePheromoneTrail($"ant:{task.AssignedAnt}", "ant", taskSuccess, taskDelta,
                new() { ["last_mission_id"] = mission.Id, ["last_task_id"] = task.Id, ["task_type"] = task.TaskType, ["task_status"] = task.Status.Value() });
            UpdatePheromoneTrail($"task_type:{task.TaskType}", "task_type", taskSuccess, taskDelta,
                new() { ["last_mission_id"] = mission.Id, ["last_task_id"] = task.Id, ["assigned_ant"] = task.AssignedAnt, ["task_status"] = task.Status.Value() });
        }
    }

    // ---- mission reads / memory views ------------------------------------

    public Dictionary<string, object?>? GetMission(string missionId) =>
        Query(@"SELECT id, goal, status, user_result, debug_result, final_result, best_output_task_id,
                  success_score, created_at, saved_at FROM missions WHERE id = @id", ("@id", missionId)).FirstOrDefault();

    public List<Dictionary<string, object?>> GetTasksForMission(string missionId, int limit = 200) =>
        Query(@"SELECT id, mission_id, title, description, assigned_ant, task_type, parent_task_id,
                  parent_task_ids_json, depends_on_json, status, result, result_summary, result_chars,
                  estimated_tokens, created_at, started_at, finished_at, completed_at, failed_at, skipped_at,
                  elapsed_seconds, attempt_count, max_attempts, failure_reason, failure_type, skipped_reason, blocked_reason
                FROM tasks WHERE mission_id = @mid ORDER BY COALESCE(started_at, finished_at, id) ASC LIMIT @lim",
            ("@mid", missionId), ("@lim", limit));

    public List<Dictionary<string, object?>> GetRecentMissions(int limit = 5) =>
        CacheRead($"recent_missions::{limit}", () =>
            Query(@"SELECT id, goal, status, user_result, debug_result, final_result, best_output_task_id,
                      success_score, created_at, saved_at FROM missions ORDER BY saved_at DESC LIMIT @lim", ("@lim", limit)));

    public List<Dictionary<string, object?>> SearchRelevantMissions(string goal, int limit = 5)
    {
        var keywords = TextUtil.ExtractKeywords(goal);
        if (keywords.Count == 0) return new();

        if (FtsAvailable)
        {
            var ftsQuery = string.Join(" OR ", keywords.OrderBy(x => x, StringComparer.Ordinal).Take(8));
            try
            {
                return Query(@"SELECT m.id, m.goal, m.status, m.user_result, m.final_result, m.success_score, m.saved_at
                               FROM missions_fts f JOIN missions m ON m.id = f.id
                               WHERE missions_fts MATCH @q ORDER BY bm25(missions_fts) LIMIT @lim",
                    ("@q", ftsQuery), ("@lim", limit));
            }
            catch (SqliteException) { FtsAvailable = false; }
        }
        return KeywordSearchRelevantMissions(goal, limit);
    }

    private List<Dictionary<string, object?>> KeywordSearchRelevantMissions(string goal, int limit)
    {
        var keywords = TextUtil.ExtractKeywords(goal);
        var rows = Query("SELECT id, goal, status, user_result, final_result, success_score, saved_at FROM missions ORDER BY saved_at DESC LIMIT 50");
        var scored = new List<(int Overlap, Dictionary<string, object?> Row)>();
        foreach (var row in rows)
        {
            var memoryText = $"{row.GetValueOrDefault("goal")} {row.GetValueOrDefault("user_result")} {row.GetValueOrDefault("final_result")}";
            var overlap = keywords.Intersect(TextUtil.ExtractKeywords(memoryText)).Count();
            if (overlap > 0) scored.Add((overlap, row));
        }
        return scored.OrderByDescending(x => x.Overlap).Take(limit).Select(x => x.Row).ToList();
    }

    public List<Dictionary<string, object?>> GetTopPheromoneTrails(int limit = 10) =>
        CacheRead($"top_pheromones::{limit}", () =>
            Query(@"SELECT trail_key, trail_type, strength, success_count, failure_count, last_updated
                    FROM pheromone_trails ORDER BY strength DESC, success_count DESC LIMIT @lim", ("@lim", limit)));

    public string FormatRecentMemory(int limit = 3, int maxResultChars = 300)
    {
        var missions = GetRecentMissions(limit);
        if (missions.Count == 0) return "No recent mission memory found.";
        var blocks = missions.Select(m =>
        {
            var resultSummary = (m.GetValueOrDefault("user_result") ?? m.GetValueOrDefault("final_result") ?? "")?.ToString() ?? "";
            return $"Previous Goal: {m.GetValueOrDefault("goal")}\nPrevious Status: {m.GetValueOrDefault("status")}\n" +
                   $"Previous Pheromone Score: {m.GetValueOrDefault("success_score")}\n" +
                   $"Previous Result Summary:\n{TextUtil.Truncate(resultSummary, maxResultChars, "...[memory result truncated]")}\n";
        });
        return string.Join("\n---\n", blocks);
    }

    public string FormatRelevantMemory(string goal, int limit = 5, int maxResultChars = 300)
    {
        var missions = SearchRelevantMissions(goal, limit);
        if (missions.Count == 0) return "No relevant mission memory found.";
        var blocks = missions.Select(m =>
        {
            var resultSummary = (m.GetValueOrDefault("user_result") ?? m.GetValueOrDefault("final_result") ?? "")?.ToString() ?? "";
            return $"Relevant Goal: {m.GetValueOrDefault("goal")}\nRelevant Status: {m.GetValueOrDefault("status")}\n" +
                   $"Relevant Pheromone Score: {m.GetValueOrDefault("success_score")}\n" +
                   $"Relevant Result Summary:\n{TextUtil.Truncate(resultSummary, maxResultChars, "...[relevant memory truncated]")}\n";
        });
        return string.Join("\n---\n", blocks);
    }

    public string FormatPheromoneContext(int limit = 8)
    {
        var trails = GetTopPheromoneTrails(limit);
        if (trails.Count == 0) return "No pheromone trail memory found.";
        return string.Join("\n", trails.Select(t =>
            $"{t.GetValueOrDefault("trail_key")} | type={t.GetValueOrDefault("trail_type")} | strength={t.GetValueOrDefault("strength")} | " +
            $"success={t.GetValueOrDefault("success_count")} | failure={t.GetValueOrDefault("failure_count")}"));
    }

    public Dictionary<string, object?> GetSchemaStatus()
    {
        var migrations = Query("SELECT id, name, description, applied_at, anthill_version FROM schema_migrations ORDER BY id");
        var metaRows = Query("SELECT key, value, updated_at FROM anthill_meta ORDER BY key");
        var meta = new Dictionary<string, object?>();
        object? schemaVersion = null;
        foreach (var row in metaRows)
        {
            var key = row["key"]?.ToString() ?? "";
            object? parsed;
            try { parsed = System.Text.Json.JsonSerializer.Deserialize<object?>((row["value"] as string) ?? "null"); }
            catch { parsed = row["value"]; }
            meta[key] = new Dictionary<string, object?> { ["value"] = parsed, ["updated_at"] = row["updated_at"] };
            if (key == "schema_version") schemaVersion = parsed;
        }
        return new Dictionary<string, object?>
        {
            ["anthill_version"] = AnthillRuntime.Version,
            ["expected_schema_version"] = AnthillRuntime.SchemaVersion,
            ["schema_version"] = schemaVersion,
            ["migration_count"] = migrations.Count,
            ["migrations"] = migrations,
            ["meta"] = meta,
        };
    }

    public List<string> TableNames() =>
        Query("SELECT name FROM sqlite_master WHERE type='table'").Select(r => r["name"]?.ToString() ?? "").ToList();
}
