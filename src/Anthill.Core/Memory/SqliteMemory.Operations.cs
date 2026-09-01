using Microsoft.Data.Sqlite;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.SDK.Events;

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
                @"INSERT OR REPLACE INTO missions (id, goal, status, project_id, user_result, debug_result, final_result,
                    best_output_task_id, success_score, created_at, saved_at)
                  VALUES (@id, @goal, @status, @proj, @ur, @dr, @fr, @best, @score, @created, @saved)",
                ("@id", mission.Id), ("@goal", mission.Goal), ("@status", mission.Status.Value()),
                ("@proj", mission.ProjectId),
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
            @"INSERT OR REPLACE INTO tasks (id, mission_id, title, description, assigned_ant, assigned_worker, task_type,
                parent_task_id, parent_task_ids_json, depends_on_json, input_artifact_ids_json, status, result, result_summary,
                result_chars, estimated_tokens, created_at, started_at, finished_at, completed_at, failed_at,
                skipped_at, elapsed_seconds, attempt_count, max_attempts, failure_reason, failure_type,
                skipped_reason, blocked_reason, skill_id, critical, cancellation_reason, deterministic_block)
              VALUES (@id, @mid, @title, @desc, @ant, @worker, @tt, @pid, @pids, @deps, @inputs, @status, @result, @summary,
                @rc, @et, @created, @started, @finished, @completed, @failed, @skipped, @elapsed, @attempts,
                @max, @freason, @ftype, @sreason, @breason, @skill, @critical, @cancel, @dblock)",
            ("@id", task.Id), ("@mid", missionId), ("@title", task.Title), ("@desc", task.Description),
            ("@ant", task.AssignedAnt), ("@worker", task.AssignedWorker), ("@tt", task.TaskType), ("@pid", task.ParentTaskId),
            ("@pids", Json.SafeDumps(task.ParentTaskIds)), ("@deps", Json.SafeDumps(task.DependsOn)),
            ("@inputs", Json.SafeDumps(task.InputArtifactIds)),
            ("@status", task.Status.Value()), ("@result", task.Result), ("@summary", task.ResultSummary),
            ("@rc", task.ResultChars), ("@et", task.EstimatedTokens),
            ("@created", ((DateTime?)task.CreatedAt).ToIsoOrNull()), ("@started", task.StartedAt.ToIsoOrNull()),
            ("@finished", task.FinishedAt.ToIsoOrNull()), ("@completed", task.CompletedAt.ToIsoOrNull()),
            ("@failed", task.FailedAt.ToIsoOrNull()), ("@skipped", task.SkippedAt.ToIsoOrNull()),
            ("@elapsed", task.ElapsedSeconds), ("@attempts", task.AttemptCount),
            ("@max", Math.Max(1, task.MaxAttempts)), ("@freason", task.FailureReason),
            ("@ftype", task.FailureType), ("@sreason", task.SkippedReason), ("@breason", task.BlockedReason),
            ("@skill", task.SkillId),
            // v2.26.0: criticality persisted so row-based evaluation can never disagree with the
            // live mission object about which failures fail the mission; cancellation reason so a
            // drained task's terminal state survives restart.
            // v0.3.8.91: the deterministic block, which had no column at all. It gates the bypass
            // apply path and the finalizer, and it lived only on the in-memory object — so a restart
            // forgot every refusal and `PatchPromotionGate`, which reads persisted state, could not
            // see one. A safety decision that does not survive a process is not a safety decision.
            ("@critical", task.Critical ? 1 : 0), ("@cancel", task.CancellationReason),
            ("@dblock", task.DeterministicBlock));

    public void SavePatchSet(PatchSet patchSet)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            NonQuery(conn, tx,
                @"INSERT OR REPLACE INTO patch_sets (id, mission_id, task_id, workspace_id, summary, proposal_count, created_at)
                  VALUES (@id, @mid, @tid, @wid, @summary, @count, @created)",
                ("@id", patchSet.Id), ("@mid", patchSet.MissionId), ("@tid", patchSet.TaskId),
                ("@wid", patchSet.WorkspaceId),
                ("@summary", patchSet.Summary), ("@count", patchSet.Proposals.Count), ("@created", patchSet.CreatedAt.ToIso()));

            foreach (var p in patchSet.Proposals)
                NonQuery(conn, tx,
                    @"INSERT OR REPLACE INTO patch_proposals (id, patch_set_id, mission_id, task_id, file_path,
                        change_type, reason, risk, old_content, new_content, base_hash, destination_path,
                        requires_approval, status, created_at)
                      VALUES (@id, @pid, @mid, @tid, @fp, @ct, @reason, @risk, @old, @new, @bh, @dp, @ra, @status, @created)",
                    ("@id", p.Id), ("@pid", patchSet.Id), ("@mid", patchSet.MissionId), ("@tid", patchSet.TaskId),
                    ("@fp", p.FilePath), ("@ct", p.ChangeType.Value()), ("@reason", p.Reason), ("@risk", p.Risk),
                    // Patch bodies may contain proprietary source — sealed at rest with AES-GCM.
                    ("@old", _cipher.Protect(p.OldContent)), ("@new", _cipher.Protect(p.NewContent)),
                    // v0.3.8.37: NOT encrypted. A hash of content is not content, and the apply path
                    // needs to compare it without holding the decryption key.
                    ("@bh", (object?)p.BaseHash ?? DBNull.Value),
                    // v0.3.8.52: NOT encrypted either. A destination path is a path, the same class
                    // of thing as file_path in the column beside it, and the apply path compares it
                    // against the workspace guard without holding the key.
                    ("@dp", (object?)p.DestinationPath ?? DBNull.Value),
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
                // v2.26.0: arithmetic over the counts — the old form allocated a megabyte-scale
                // throwaway string of 'x' characters just to divide its length by four.
                ("@ite", TextUtil.EstimateTokenCountFromChars(inputChars)),
                ("@ote", TextUtil.EstimateTokenCountFromChars(outputChars)),
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
        Query(@"SELECT t.id, t.mission_id, t.title, t.assigned_ant, t.assigned_worker, t.task_type, t.status, t.result_summary,
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

    public Dictionary<string, object?> SummarizeWorkerTelemetry(int limit = 40)
    {
        var workers = Query(@"SELECT assigned_worker, assigned_ant, COUNT(*) AS task_count,
                  COALESCE(SUM(CASE WHEN status='complete' THEN 1 ELSE 0 END),0) AS complete_count,
                  COALESCE(SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END),0) AS failed_count,
                  COALESCE(SUM(CASE WHEN status='skipped' THEN 1 ELSE 0 END),0) AS skipped_count,
                  COALESCE(ROUND(AVG(elapsed_seconds),3),0) AS avg_elapsed_seconds,
                  COALESCE(MAX(finished_at), MAX(started_at), MAX(created_at)) AS last_seen_at
                FROM tasks
                WHERE assigned_worker IS NOT NULL AND assigned_worker <> ''
                GROUP BY assigned_worker, assigned_ant
                ORDER BY task_count DESC, last_seen_at DESC LIMIT @lim", ("@lim", limit));
        var audits = Query(@"SELECT ant_name AS runtime_node, COUNT(*) AS audit_count, MAX(created_at) AS last_audit_at
                FROM events WHERE event_type='worker_permission_audited'
                GROUP BY ant_name ORDER BY audit_count DESC, last_audit_at DESC LIMIT @lim", ("@lim", limit));
        var metrics = Query(@"SELECT ant_name AS runtime_node, COUNT(*) AS metric_count,
                  COALESCE(SUM(input_chars),0) AS input_chars, COALESCE(SUM(output_chars),0) AS output_chars,
                  COALESCE(SUM(input_tokens_est),0) AS input_tokens_est, COALESCE(SUM(output_tokens_est),0) AS output_tokens_est
                FROM message_metrics
                WHERE metric_type='worker_task_result'
                GROUP BY ant_name ORDER BY metric_count DESC LIMIT @lim", ("@lim", limit));
        return new Dictionary<string, object?>
        {
            ["workers"] = workers,
            ["permission_audits"] = audits,
            ["message_metrics"] = metrics,
        };
    }

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
            pp.last_error, pp.destination_path, ps.summary AS patch_set_summary
            FROM patch_proposals pp LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id";
        return status is null
            ? Query(baseSql + " ORDER BY pp.created_at DESC LIMIT @lim", ("@lim", limit))
            : Query(baseSql + " WHERE pp.status = @s ORDER BY pp.created_at DESC LIMIT @lim",
                ("@s", status.Value.Value()), ("@lim", limit));
    }

    public void UpdatePatchStatus(string patchId, PatchStatus status, string? appliedAt = null,
        string? backupPath = null, string? lastError = null, string? appliedHash = null)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE patch_proposals SET status = @s, applied_at = COALESCE(@a, applied_at),
                    backup_path = COALESCE(@b, backup_path), last_error = @e,
                    applied_hash = COALESCE(@h, applied_hash) WHERE id = @id",
                ("@s", status.Value()), ("@a", appliedAt), ("@b", backupPath), ("@e", lastError),
                ("@h", appliedHash), ("@id", patchId));
        }
    }

    // ---- Patch Center (v1.8.16) -------------------------------------------

    /// <summary>
    /// Structured patch list for the visual Patch Center. Joins each proposal to its patch-set
    /// summary, mission goal, driving objective/run (via autonomy_runs), and latest approval — so
    /// the UI can render, filter, and act on patches without extra round trips. Content bodies are
    /// intentionally NOT returned here (list view stays light + secret-free); the diff view calls
    /// <see cref="GetPatchProposal"/> for the sealed old/new content. Filters applied in SQL:
    /// status, mission, objective, file-path substring. Risk is normalized/filtered by the caller.
    /// </summary>
    public List<Dictionary<string, object?>> ListPatchesForCenter(
        PatchStatus? status = null, string? missionId = null, string? objectiveId = null,
        string? filePathContains = null, int limit = 200)
    {
        // Subqueries (not a join to autonomy_runs) so a mission with multiple runs never duplicates
        // a patch row. Objective/run are the most recent run for the patch's mission.
        var sql = new System.Text.StringBuilder(@"
            SELECT pp.id, pp.patch_set_id, pp.mission_id, pp.task_id, pp.file_path, pp.change_type,
                   pp.reason, pp.risk, pp.requires_approval, pp.status, pp.created_at, pp.applied_at,
                   pp.backup_path, pp.last_error, ps.summary AS patch_set_summary,
                   m.goal AS mission_goal,
                   (SELECT r.objective_id FROM autonomy_runs r WHERE r.mission_id = pp.mission_id ORDER BY r.started_at DESC LIMIT 1) AS objective_id,
                   (SELECT r.id           FROM autonomy_runs r WHERE r.mission_id = pp.mission_id ORDER BY r.started_at DESC LIMIT 1) AS run_id,
                   (SELECT areq.id     FROM approval_requests areq WHERE areq.target_id = pp.id ORDER BY areq.created_at DESC LIMIT 1) AS approval_id,
                   (SELECT areq.status FROM approval_requests areq WHERE areq.target_id = pp.id ORDER BY areq.created_at DESC LIMIT 1) AS approval_status
            FROM patch_proposals pp
            LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id
            LEFT JOIN missions m ON pp.mission_id = m.id");
        var conds = new List<string>();
        var args = new List<(string, object?)>();
        if (status is not null) { conds.Add("pp.status = @st"); args.Add(("@st", status.Value.Value())); }
        if (!string.IsNullOrWhiteSpace(missionId)) { conds.Add("pp.mission_id = @mid"); args.Add(("@mid", missionId)); }
        if (!string.IsNullOrWhiteSpace(objectiveId)) { conds.Add("pp.mission_id IN (SELECT mission_id FROM autonomy_runs WHERE objective_id = @oid AND mission_id IS NOT NULL)"); args.Add(("@oid", objectiveId)); }
        if (!string.IsNullOrWhiteSpace(filePathContains)) { conds.Add("pp.file_path LIKE @fp"); args.Add(("@fp", $"%{filePathContains}%")); }
        if (conds.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", conds));
        sql.Append(" ORDER BY pp.created_at DESC LIMIT @lim");
        args.Add(("@lim", Math.Clamp(limit, 1, 1000)));
        return Query(sql.ToString(), args.ToArray());
    }

    /// <summary>Patch status counts (proposed/approved/applied/rejected/failed/superseded + total) for one mission.</summary>
    /// <summary>
    /// How many patch proposals a mission produced, any status. v2.24.0 objective verification
    /// asks "did the mission deliver the file change its goal asked for" — PROPOSED, not applied,
    /// because ants propose and a human (or gated auto-apply) applies. Requiring an applied change
    /// would fail every correctly operating mission awaiting approval.
    /// </summary>
    public int CountPatchProposalsForMission(string missionId) =>
        (int)AsLong(Scalar("SELECT COUNT(*) FROM patch_proposals WHERE mission_id = @m", ("@m", missionId)));

    public Dictionary<string, object?> PatchCountsForMission(string missionId) =>
        NormalizePatchCounts(Query(
            "SELECT status, COUNT(*) AS n FROM patch_proposals WHERE mission_id = @m GROUP BY status", ("@m", missionId)));

    /// <summary>Patch status counts for every mission this objective's autonomy runs launched.</summary>
    public Dictionary<string, object?> PatchCountsForObjective(string objectiveId) =>
        NormalizePatchCounts(Query(
            @"SELECT pp.status AS status, COUNT(*) AS n FROM patch_proposals pp
              WHERE pp.mission_id IN (SELECT mission_id FROM autonomy_runs WHERE objective_id = @oid AND mission_id IS NOT NULL)
              GROUP BY pp.status", ("@oid", objectiveId)));

    private static Dictionary<string, object?> NormalizePatchCounts(List<Dictionary<string, object?>> rows)
    {
        var d = new Dictionary<string, object?>
        {
            ["total"] = 0, ["proposed"] = 0, ["approved"] = 0, ["applied"] = 0,
            ["rejected"] = 0, ["failed"] = 0, ["superseded"] = 0, ["reverted"] = 0,
        };
        var total = 0;
        foreach (var r in rows)
        {
            var st = r.GetValueOrDefault("status")?.ToString() ?? "";
            var n = (int)AsLong(r.GetValueOrDefault("n"));
            if (d.ContainsKey(st)) d[st] = n;
            total += n;
        }
        d["total"] = total;
        // "pending" is the operator-facing label for proposed patches awaiting approval.
        d["pending"] = d["proposed"];
        return d;
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

    /// <summary>
    /// True when an identical change (same file, change type, and old/new content) is already
    /// sitting in the pending approval queue. Autonomous objectives re-propose the same fix run
    /// after run while nobody has reviewed the first one — without this check every rerun stacks
    /// another identical approval request. Contents are sealed at rest, so the comparison
    /// decrypts candidates matching on file+change first (bounded by <paramref name="scanLimit"/>).
    /// </summary>
    public bool HasDuplicatePendingApproval(PatchProposal proposal, int scanLimit = 200)
    {
        var candidates = Query(
            @"SELECT pp.old_content, pp.new_content
              FROM approval_requests ar JOIN patch_proposals pp ON pp.id = ar.target_id
              WHERE ar.status = @pending AND ar.action_type = @at
                AND pp.file_path = @fp AND pp.change_type = @ct
              ORDER BY ar.created_at DESC LIMIT @lim",
            ("@pending", ApprovalStatus.Pending.Value()), ("@at", ApprovalActionType.PatchProposal.Value()),
            ("@fp", proposal.FilePath), ("@ct", proposal.ChangeType.Value()), ("@lim", scanLimit));
        foreach (var row in candidates)
        {
            var oldContent = _cipher.Unprotect(row.GetValueOrDefault("old_content") as string);
            var newContent = _cipher.Unprotect(row.GetValueOrDefault("new_content") as string);
            if (oldContent == proposal.OldContent && newContent == proposal.NewContent) return true;
        }
        return false;
    }

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

    /// <summary>
    /// THE QUESTIONS THIS MISSION IS STILL WAITING ON. v0.3.8.105.
    ///
    /// Pending `ToolUse` approvals only, and the narrowing is load-bearing rather than tidy. A
    /// pending PATCH approval is the normal, healthy end state of every coding mission this colony
    /// runs — the patch is proposed, the mission finishes, the operator reviews it afterwards.
    /// Reading those as "the mission is waiting" would put every successful coding mission into
    /// `waiting_for_approval` and stop it ever reaching `completed_verified`, which auto-apply
    /// consumes. `ToolUse` means something the mission needed to DO and could not, which is the
    /// only case where the mission itself is the thing that is unfinished.
    ///
    /// Returns the TOOL NAMES, because an outcome that says a mission is waiting must be able to
    /// say what for; a list of approval ids names nothing an operator recognises.
    /// </summary>
    public IReadOnlyList<string> PendingOperatorDecisions(string missionId, int limit = 50) =>
        Query(@"SELECT target_id, metadata_json FROM approval_requests
                WHERE mission_id = @m AND action_type = @at AND status = @s
                ORDER BY created_at ASC LIMIT @lim",
                ("@m", missionId), ("@at", ApprovalActionType.ToolUse.Value()),
                ("@s", ApprovalStatus.Pending.Value()), ("@lim", limit))
            .Select(row =>
            {
                // The target is `<mission>:<tool>` — the tool is everything after the LAST colon,
                // because a mission id may legitimately contain one and the tool name may not.
                var target = row.GetValueOrDefault("target_id")?.ToString() ?? "";
                var cut = target.LastIndexOf(':');
                return cut >= 0 && cut + 1 < target.Length ? target[(cut + 1)..] : target;
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

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

    /// <summary>
    /// Record an event durably, then announce it.
    ///
    /// v3.8.3 — the second half of that sentence is new; the first half is unchanged, and that is
    /// the whole design of the retrofit. This method already WAS the colony's event stream: ~85
    /// call sites across the Queen, the execution service, the tool layer and the scheduler,
    /// producing seventy-odd distinct event types, read back by the dashboard through
    /// <see cref="GetRecentEvents"/>. What it lacked was a live outlet, so every observer had to
    /// poll the table.
    ///
    /// Publication is therefore additive: no call site changed, no signature changed, and with the
    /// default <see cref="NullEventBus"/> the behaviour is byte-for-byte what it was.
    ///
    /// ORDER IS LOAD-BEARING. The insert happens first, inside the write lock, and publication
    /// happens after it — never the reverse, and never inside the lock. Publishing first would mean
    /// a subscriber could observe and act on an event that a subsequent database failure leaves
    /// unrecorded, turning a durable log into a best-effort one the moment a bus was introduced.
    /// Publishing inside the lock would put subscriber dispatch on the colony's write path.
    /// </summary>
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

        // Persisted. Now, and only now, tell whoever is listening.
        EventBus.Publish(ToColonyEvent(ev));
        return ev;
    }

    /// <summary>
    /// Translate the stored row into the SDK's transport shape.
    ///
    /// The two types carry identical fields by deliberate construction — see the note on
    /// <see cref="ColonyEvent"/> — so this is a rename, not a projection, and nothing is dropped on
    /// the way to a subscriber. If this method ever needs a decision in it, the two shapes have
    /// drifted and one of them is wrong.
    /// </summary>
    private static ColonyEvent ToColonyEvent(Event ev) => new()
    {
        Id = ev.Id,
        MissionId = ev.MissionId,
        TaskId = ev.TaskId,
        AntName = ev.AntName,
        EventType = ev.EventType,
        Message = ev.Message,
        Metadata = ev.Metadata,
        CreatedAt = ev.CreatedAt,
    };

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

    /// <summary>
    /// The counters behind the operator's system summary. v0.3.8.90 — rebuilt on the shared set.
    ///
    /// TWO IMPLEMENTATIONS OF ONE RULE, and they had already disagreed. This method spelled the
    /// failure vocabulary as seven SQL literals while <see cref="GetRecentFailureEvents"/> built the
    /// same query from <c>AnthillRuntime.FailureEventTypes</c>. Three of those literals named events
    /// nothing emits (see the note on that set), so `failure_event_count` under-counted by exactly
    /// the failures an operator most wants counted, and `model_call_count` — filtered on
    /// `model_call_completed`, a name that has never existed — was structurally always zero while
    /// being rendered as a real figure on `/status`.
    ///
    /// Now parameterised from the one set, so the two can no longer drift. A count that is always
    /// zero is worse than a missing count: the operator reads "no model calls" and believes it.
    /// </summary>
    public Dictionary<string, object?> SummarizeEvents()
    {
        var failureTypes = AnthillRuntime.FailureEventTypes.ToList();
        var failurePlaceholders = string.Join(",", failureTypes.Select((_, i) => $"@f{i}"));
        var failureArgs = failureTypes.Select((t, i) => ($"@f{i}", (object?)t)).ToArray();

        var summary = Query(
            $@"SELECT COUNT(*) AS event_count,
                COALESCE(SUM(CASE WHEN event_type IN ({failurePlaceholders})
                    THEN 1 ELSE 0 END),0) AS failure_event_count,
                COALESCE(SUM(CASE WHEN event_type='{EventTypes.TaskCompleted}' THEN 1 ELSE 0 END),0) AS task_completed_count,
                COALESCE(SUM(CASE WHEN event_type='{EventTypes.ModelCall}' THEN 1 ELSE 0 END),0) AS model_call_count,
                MAX(created_at) AS last_event_at FROM events", failureArgs).FirstOrDefault() ?? new();
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

    /// <summary>
    /// v2.26.0: what KIND of signal a trail type carries. Derived in the ONE write path so all
    /// call sites inherit it and none can drift. Planning may read only procedural_learning /
    /// routing_preference — a provider answering HTTP 200 is telemetry, not strategy.
    /// </summary>
    internal static string SignalCategoryFor(string trailType) => (trailType ?? "").ToLowerInvariant() switch
    {
        // v0.3.8.90: four arms removed — "objective", "objective_pattern", "model_provider" and
        // "provider". None is a declared `TrailKind`, and `PheromoneVocabularyTests` makes writing an
        // undeclared kind a build failure, so no trail could ever have reached them. Harmless in
        // effect (the `_` fallback caught nothing) and worth deleting anyway: a switch arm for a
        // value that cannot arrive reads as coverage. The three existing pheromone guards check
        // declared→written, written→declared and declared→categorised; none checked
        // categorised→declared, which is the direction these four lived in. That fourth direction is
        // now `EverySignalCategoryArm_NamesADeclaredTrailKind`.
        //
        // Which ant/worker/task/pattern verifiably works — the strategy signals planning may read.
        "planner_pattern" or "worker_pattern" or "task_pattern" or "capability"
            or "ant" or "worker" or "task_type" => "procedural_learning",
        // Which model route serves a role well — a routing preference, also plannable.
        "model_route" => "routing_preference",
        // Advisory source heuristics — never proven truth.
        "source_domain" => "quality_signal",
        // Did the tool/provider answer — reliability, not strategy.
        "external_research_tool" or "tool" => "reliability_signal",
        _ => "operational_telemetry",
    };

    /// <summary>
    /// v0.3.8.93 — one trail's numbers, for the deterministic consumers. The trails have been
    /// write-only since the layer existed (their one reader was a formatted prompt block);
    /// <see cref="Pheromones.TrailGuidedSelection"/> is the first code path that decides on them,
    /// and it reads through here. Legacy-flagged trails are invisible — a reset that quarantined
    /// old learning must not keep steering new selections.
    /// </summary>
    public Pheromones.TrailView? GetPheromoneTrail(string trailKey)
    {
        var row = Query(
            "SELECT strength, success_count, failure_count FROM pheromone_trails WHERE trail_key = @k AND legacy = 0",
            ("@k", trailKey)).FirstOrDefault();
        return row is null
            ? null
            : new Pheromones.TrailView(
                Convert.ToDouble(row["strength"]),
                (int)AsLong(row["success_count"]),
                (int)AsLong(row["failure_count"]));
    }

    public void UpdatePheromoneTrail(string trailKey, string trailType, bool success, double strengthDelta,
        Dictionary<string, object?>? metadata = null)
    {
        metadata ??= new();

        // v3.8.31 — the trail vocabulary becomes ENFORCED rather than declared.
        //
        // `TrailKind` named the kinds in v3.8.29 and nothing checked against it, so the list could
        // drift from the call sites in both directions — and it had: two kinds declared that nothing
        // writes, one written that nothing declared. A vocabulary no writer is checked against is a
        // document, not a guard.
        //
        // WARNS RATHER THAN THROWS. A new trail kind is a design decision someone is midway through
        // making, and refusing the write would lose the observation while they finish; the point is
        // that it cannot happen SILENTLY. The build-time guard in PheromoneVocabularyTests is where
        // this becomes binding.
        if (!Pheromones.TrailKind.IsKnown(trailType))
            Console.Error.WriteLine(
                $"[pheromone] trail kind '{trailType}' is not in TrailKind — the trail is still "
                + "recorded, but nothing can reason about its subject. Declare it or use an existing kind.");

        var category = SignalCategoryFor(trailType);
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
                        last_updated=@u, metadata_json=@m, signal_category=@cat WHERE trail_key=@k",
                    ("@s", newStrength), ("@sc", successCount), ("@fc", failureCount),
                    ("@u", AnthillTime.NowUtc().ToIso()), ("@m", Json.SafeDumps(merged)), ("@cat", category),
                    ("@k", trailKey));
            }
            else
            {
                var initial = Math.Clamp(Math.Round(0.5 + strengthDelta, 4), 0.0, 1.0);
                NonQuery(conn, null,
                    @"INSERT INTO pheromone_trails (id, trail_key, trail_type, strength, success_count, failure_count,
                        last_updated, metadata_json, signal_category) VALUES (@id, @k, @t, @s, @sc, @fc, @u, @m, @cat)",
                    ("@id", Guid.NewGuid().ToString()), ("@k", trailKey), ("@t", trailType), ("@s", initial),
                    ("@sc", success ? 1 : 0), ("@fc", success ? 0 : 1), ("@u", AnthillTime.NowUtc().ToIso()),
                    ("@m", Json.SafeDumps(metadata)), ("@cat", category));
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

    public void UpdateMissionPheromones(Mission mission, string? outcomeCode = null)
    {
        // v2.19.0: positive reinforcement requires completed_verified — nothing else. Partial
        // reinforces NOTHING (delta 0): not punished as a failure either, because partial work is
        // genuinely ambiguous evidence. Only verified success may pull a trail up.
        // v2.26.0: the outcome is CONSUMED from the canonical evaluation when the caller has one;
        // the local re-derivation survives only as a fallback for legacy callers and reads the
        // same inputs the evaluator's floor reads.
        var outcome = outcomeCode
            ?? MissionOutcome.Resolve(mission.Status, MissionVerification.IsSatisfied(mission.Tasks));
        var success = MissionOutcome.IsPositiveSuccess(outcome);
        var score = mission.SuccessScore ?? 0.0;
        var delta = outcome switch
        {
            MissionOutcome.CompletedVerified => 0.05 + score * 0.05,
            MissionOutcome.CompletedUnverified => 0.0,   // finished, unproven — no signal either way
            MissionOutcome.Partial => 0.0,               // ambiguous — no signal either way

            // v0.3.8.105 — A MISSION THAT NEVER RAN IS NOT EVIDENCE ABOUT THE ANTS THAT DID NOT
            // RUN IT. Carried debt from `.104`, and its own documentation is the accusation:
            // `MissionOutcome.BlockedMissingCapability` says, in the release that introduced it,
            // "it is also NOT a signal for learning… nothing reinforces, promotes or retires on the
            // strength of it." It fell through to `_` and charged -0.08 against every ant, worker
            // and task-type path in the plan — the heaviest negative in this switch, applied to the
            // one outcome defined as carrying no information. A colony repeatedly asked for
            // something it cannot do would have demoted the workers that were never invoked.
            //
            // `waiting_for_approval` joins it for the same reason and would have been a fresh
            // instance of the identical bug on its first release: a mission paused for an operator's
            // answer says nothing about any worker's competence.
            //
            // The default stays -0.08. A mission that FAILED is evidence, and this is not a
            // widening of what counts as forgiven — it is the removal of two outcomes that were
            // never failures from a branch that only ever meant failure.
            MissionOutcome.BlockedMissingCapability => 0.0,
            MissionOutcome.WaitingForApproval => 0.0,
            _ => -0.08,
        };
        var antPath = mission.Tasks.Select(t => t.AssignedAnt).ToList();
        var workerPath = mission.Tasks.Select(t => string.IsNullOrWhiteSpace(t.AssignedWorker) ? t.AssignedAnt : t.AssignedWorker!).ToList();
        var taskTypePath = mission.Tasks.Select(t => t.TaskType).ToList();

        UpdatePheromoneTrail("planner_pattern:" + string.Join("_", antPath), "planner_pattern", success, delta,
            new() { ["mission_id"] = mission.Id, ["goal"] = mission.Goal, ["score"] = score, ["ant_path"] = antPath, ["mission_status"] = mission.Status.Value(), ["outcome"] = outcome });
        UpdatePheromoneTrail("worker_pattern:" + string.Join("_", workerPath), "worker_pattern", success, delta,
            new() { ["mission_id"] = mission.Id, ["goal"] = mission.Goal, ["score"] = score, ["worker_path"] = workerPath, ["mission_status"] = mission.Status.Value(), ["outcome"] = outcome });
        UpdatePheromoneTrail("task_pattern:" + string.Join("_", taskTypePath), "task_pattern", success, delta,
            new() { ["mission_id"] = mission.Id, ["goal"] = mission.Goal, ["score"] = score, ["task_type_path"] = taskTypePath, ["mission_status"] = mission.Status.Value(), ["outcome"] = outcome });

        foreach (var task in mission.Tasks)
        {
            // v3.8.26 Stage E — ATTRIBUTION. Who is this outcome actually evidence about?
            //
            // The line this replaces was:
            //     task.Status == Skipped ? -0.01 : taskSuccess && success ? 0.03 : -0.04
            //
            // which pushed a role's trail DOWN for being SKIPPED — punished for not running — and
            // swept Blocked, Cancelled and Pending into the same -0.04 as a genuine failure. It also
            // charged provider outages and dependency failures to the worker, while ModelRouter and
            // ToolRegistry were already recording those against the provider and the tool: the same
            // fact counted twice, once against the wrong subject.
            //
            // Survivable while six of twelve roles never ran. Not survivable in the release that
            // gives all twelve a trigger — a specialist enabled for the first time would arrive
            // carrying negative reputation from missions it was gated out of, and the colony would
            // learn to route away from roles it had never tried.
            var attribution = Pheromones.LearningAttribution.For(task, missionVerified: success);
            var taskDelta = Pheromones.LearningAttribution.DeltaFor(attribution);
            var taskSuccess = attribution == Pheromones.LearningAttribution.Attribution.Positive;

            // NEUTRAL writes nothing at all. A zero-delta write would still stamp last_seen and
            // count as an observation, which is how "this role was in nine missions" becomes true
            // for a role that ran in none of them.
            if (attribution == Pheromones.LearningAttribution.Attribution.Neutral) continue;

            var attributionName = attribution.ToString().ToLowerInvariant();
            UpdatePheromoneTrail($"ant:{task.AssignedAnt}", "ant", taskSuccess, taskDelta,
                new() { ["last_mission_id"] = mission.Id, ["last_task_id"] = task.Id, ["task_type"] = task.TaskType, ["task_status"] = task.Status.Value(), ["attribution"] = attributionName });
            if (!string.IsNullOrWhiteSpace(task.AssignedWorker))
                UpdatePheromoneTrail($"worker:{task.AssignedWorker}", "worker", taskSuccess, taskDelta,
                    new() { ["last_mission_id"] = mission.Id, ["last_task_id"] = task.Id, ["assigned_ant"] = task.AssignedAnt, ["task_type"] = task.TaskType, ["task_status"] = task.Status.Value(), ["attribution"] = attributionName });
            UpdatePheromoneTrail($"task_type:{task.TaskType}", "task_type", taskSuccess, taskDelta,
                new() { ["last_mission_id"] = mission.Id, ["last_task_id"] = task.Id, ["assigned_ant"] = task.AssignedAnt, ["task_status"] = task.Status.Value(), ["attribution"] = attributionName });
        }
    }

    // ---- mission reads / memory views ------------------------------------

    public Dictionary<string, object?>? GetMission(string missionId) =>
        Query(@"SELECT id, goal, status, project_id, user_result, debug_result, final_result, best_output_task_id,
                  success_score, created_at, saved_at FROM missions WHERE id = @id", ("@id", missionId)).FirstOrDefault();

    /// <summary>
    /// v0.3.8.95 — whether a workspace-derived patch set already exists for this mission and
    /// workspace. The acting-coder path captures the workspace diff at coder-task completion
    /// (while the task graph is still open, so reviewers can be inserted); finalization asks this
    /// before harvesting again, so one workspace's changes become one patch set, not two.
    /// </summary>
    public bool HasPatchSetForWorkspace(string missionId, string workspaceId) =>
        AsLong(Scalar(
            "SELECT COUNT(*) FROM patch_sets WHERE mission_id = @m AND workspace_id = @w",
            ("@m", missionId), ("@w", workspaceId))) > 0;

    public List<Dictionary<string, object?>> GetTasksForMission(string missionId, int limit = 200) =>
        Query(@"SELECT id, mission_id, title, description, assigned_ant, assigned_worker, task_type, parent_task_id,
                  parent_task_ids_json, depends_on_json, input_artifact_ids_json, status, result, result_summary, result_chars,
                  estimated_tokens, created_at, started_at, finished_at, completed_at, failed_at, skipped_at,
                  elapsed_seconds, attempt_count, max_attempts, failure_reason, failure_type, skipped_reason, blocked_reason,
                  skill_id, critical, outcome_code, cancellation_reason, deterministic_block
                FROM tasks WHERE mission_id = @mid ORDER BY COALESCE(started_at, finished_at, id) ASC LIMIT @lim",
            ("@mid", missionId), ("@lim", limit));

    /// <summary>
    /// Every proposal in one patch set. v0.3.8.91 — the read a set-level apply needs.
    ///
    /// It did not exist: there is no `GetPatchSet` anywhere, and every caller that wanted "the whole
    /// set" either had the in-memory `PatchSet` in hand or worked one proposal at a time. That is not
    /// a coincidence — it is the shape that let the ordinary apply path be per-proposal while the
    /// documents said the set applies as a unit.
    ///
    /// Same projection as <see cref="GetPatchProposal"/> minus the joins, so a caller can build the
    /// domain object without a second read per row.
    /// </summary>
    /// <summary>
    /// Record what the live tree looked like when this set was verified. v0.3.8.91.
    ///
    /// Written at the moment the revision is registered, because that is the moment the sandbox the
    /// verifiers read was built from the live tree. Recording it later would fingerprint a tree that
    /// had already had time to move.
    /// </summary>
    public void SetPatchSetBaseFingerprint(string patchSetId, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(patchSetId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE patch_sets SET base_fingerprint = @f WHERE id = @id",
                ("@f", fingerprint ?? ""), ("@id", patchSetId));
        }
    }

    /// <summary>The recorded fingerprint, or null when this set never had one.</summary>
    public string? GetPatchSetBaseFingerprint(string patchSetId) =>
        Query("SELECT base_fingerprint FROM patch_sets WHERE id = @id", ("@id", patchSetId ?? ""))
            .FirstOrDefault()?.GetValueOrDefault("base_fingerprint")?.ToString();

    /// <summary>
    /// The patch set's own row — identity, producer, and above all <c>workspace_id</c>. v0.3.8.97.
    ///
    /// This read is what lets promotion answer "which tree is this set FOR": the workspace id names
    /// the mission workspace row, and that row's SourceRoot is the checkout the set was diffed
    /// against. `PatchTargetResolver` is the one consumer; nothing else should re-derive a target
    /// from this row, or two answers to one question come back.
    /// </summary>
    public Dictionary<string, object?>? GetPatchSetRow(string patchSetId) =>
        Query(@"SELECT id, mission_id, task_id, workspace_id, summary, proposal_count, base_fingerprint, created_at
                FROM patch_sets WHERE id = @id", ("@id", patchSetId ?? ""))
            .FirstOrDefault();

    /// <summary>Every patch set a mission produced, newest last. v0.3.8.97 — the mission report
    /// compiles its patch-set section from these rows rather than from event counts.</summary>
    public List<Dictionary<string, object?>> GetPatchSetsForMission(string missionId) =>
        Query(@"SELECT id, mission_id, task_id, workspace_id, summary, proposal_count, base_fingerprint, created_at
                FROM patch_sets WHERE mission_id = @m ORDER BY created_at ASC, id ASC",
            ("@m", missionId ?? ""));

    public List<Dictionary<string, object?>> GetPatchProposalsForSet(string patchSetId)
    {
        var rows = Query(@"SELECT id, patch_set_id, mission_id, task_id, file_path, change_type, reason, risk,
                  old_content, new_content, base_hash, destination_path, requires_approval, status,
                  created_at, applied_at, backup_path, applied_hash, last_error
                FROM patch_proposals WHERE patch_set_id = @sid ORDER BY id ASC",
            ("@sid", patchSetId ?? ""));

        // UNSEAL, exactly as GetPatchProposal does. The bodies are encrypted at rest, and a caller
        // that received the sealed text would hand ciphertext to `PatchApply.Compute` — which would
        // compare it against the live file, find no match, and refuse with "stale base". A perfectly
        // correct-looking refusal for a reason that has nothing to do with the tree.
        foreach (var row in rows)
        {
            if (row.TryGetValue("old_content", out var oc)) row["old_content"] = _cipher.Unprotect(oc as string);
            if (row.TryGetValue("new_content", out var nc)) row["new_content"] = _cipher.Unprotect(nc as string);
        }

        return rows;
    }

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

    /// <summary>
    /// The PLANNING read (Strategist context). Legacy trails — whose strength and success evidence
    /// were accumulated under the pre-v2.19 completion rule — are excluded until they record a
    /// success under the corrected rule (success_count restarts at 0 at the reset, so any positive
    /// count here is post-reset, verified-success evidence). Reporting reads are unfiltered.
    /// </summary>
    // v2.26.0: PLANNING reads only learning-bearing categories. Operational telemetry and
    // heuristic source quality never steer strategy. Uncategorized rows predate the column and
    // re-enter only through the existing requalification rule (post-reset success), after which
    // any further touch stamps their real category.
    /// <summary>
    /// v3.8.26 Stage E: a trail must be OBSERVED ENOUGH before it is allowed to steer planning.
    ///
    /// Three observations, not one. A single mission is an anecdote, and a trail written once sits
    /// at whatever that one run produced — so before this, the first mission a newly-enabled role
    /// appeared in decided how the colony felt about it. That matters most in exactly the release
    /// that gives all twelve roles a trigger: six specialists are about to run for the first time,
    /// and their first outcome must not become their reputation.
    ///
    /// Deliberately LOW. This is a floor against anecdote, not a statistical confidence threshold —
    /// setting it high would mean the colony learns nothing from its first dozen missions, which is
    /// its own failure. It can rise once there is enough history to say what "enough" is.
    /// </summary>
    public const int MinObservationsToSteerPlanning = 3;

    /// <summary>
    /// A role's or worker's standing, DERIVED from its trail. Stage E, v3.8.29.
    ///
    /// Not a stored column. The plan has asked for a worker score since it was written, and adding
    /// one to the `workers` table would create a second source of truth that drifts from the trails
    /// it was computed from — the trails are already durable, already decayed toward neutral, and
    /// already attributed correctly. Computed on read, so there is exactly one place the answer
    /// lives.
    ///
    /// An absent trail is NEUTRAL and not established. "We have never seen this role work" and "this
    /// role works badly" are different facts, and conflating them is precisely how a specialist
    /// enabled for the first time would be routed away from before it ran once.
    /// </summary>
    public Pheromones.Reputation ReputationOf(string kind, string subject)
    {
        if (!Pheromones.TrailKind.IsReputation(kind)) return Pheromones.Reputation.Unknown(subject);

        var row = Query(@"SELECT strength, success_count, failure_count FROM pheromone_trails
                          WHERE trail_key = @key AND trail_type = @type LIMIT 1",
                ("@key", $"{kind}:{subject}"), ("@type", kind)).FirstOrDefault();

        if (row is null) return Pheromones.Reputation.Unknown(subject);

        return Pheromones.Reputation.From(
            subject,
            Convert.ToDouble(row.GetValueOrDefault("strength") ?? 0.5),
            Convert.ToInt32(row.GetValueOrDefault("success_count") ?? 0),
            Convert.ToInt32(row.GetValueOrDefault("failure_count") ?? 0),
            MinObservationsToSteerPlanning);
    }

    public List<Dictionary<string, object?>> GetTopPheromoneTrails(int limit = 10) =>
        CacheRead($"top_pheromones::{limit}", () =>
            Query(@"SELECT trail_key, trail_type, strength, success_count, failure_count, last_updated
                    FROM pheromone_trails
                    WHERE (legacy = 0 OR success_count > 0)
                      AND (signal_category IN ('procedural_learning', 'routing_preference')
                           OR (signal_category = '' AND success_count > 0))
                      AND (success_count + failure_count) >= @minobs
                    ORDER BY strength DESC, success_count DESC LIMIT @lim",
                ("@lim", limit), ("@minobs", MinObservationsToSteerPlanning)));

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
        var resetNote = LearningResetDate() is { } reset
            ? $"[learning reset {reset}: pre-reset trails are legacy_unverified and re-enter planning only after a post-reset success]\n"
            : "";
        if (trails.Count == 0)
        {
            // Distinguish "never had memory" from "memory exists but is all pre-boundary". After
            // the reset every trail is legacy with a restarted success count, so planning memory
            // reads as empty — and an operator seeing "no memory found" beside a full trail table
            // would reasonably conclude the subsystem is broken.
            var held = (int)AsLong(Scalar("SELECT COUNT(*) FROM pheromone_trails WHERE legacy = 1"));
            return resetNote + (held > 0
                ? $"No trails have earned post-reset standing yet ({held} held as legacy_unverified; "
                  + "each re-enters planning after one completed_verified mission)."
                : "No pheromone trail memory found.");
        }
        return resetNote + string.Join("\n", trails.Select(t =>
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
