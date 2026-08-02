using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Contracts;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.2.0 (phase) — the ant's own report, persisted whole.
///
/// An <see cref="AntExecutionResult"/> used to decide a task's status and then be discarded except
/// for its narrative. Artifacts, evidence, handoffs, warnings, metrics and the failure class all
/// ended at that boundary, so anything wanting them later — an operator asking why a task failed,
/// the learning path, a future replay — had to read the prose back and infer. That is exactly what
/// this phase exists to remove, and it was still true one layer below the code that removed it.
///
/// This records what the ANT SAID. The <c>tasks</c> row records what the SCHEDULER DID. They are
/// deliberately separate because they can disagree: a late result is ignored, a timeout replaces
/// the text with a one-line reason. Collapsing them would erase the evidence of precisely those
/// disagreements, which is the evidence worth keeping.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// Record an ant's structured result. Called once per execution, as soon as the ant returns and
    /// BEFORE the scheduler maps it to a task status — so the report survives even when the mapping
    /// discards it (a late result) or overwrites it (a timeout).
    /// </summary>
    public void SaveTaskResult(string missionId, string taskId, string antName, AntExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(taskId) || result is null) return;

        // The contract version is looked up rather than passed in: the role's contract is the
        // authority on its own version, and a caller free to supply one could record a version the
        // ant never ran under.
        var contractVersion = AntExecutionCatalog.ContractFor(antName ?? "")?.Version;

        // A diagnostic record must never be able to break the mission it is recording. The row has
        // a foreign key to missions(id) and foreign_keys is ON, so a result arriving before its
        // mission row exists — or after a delete — would otherwise throw inside the execution path
        // and fail a task whose ant had already succeeded. Losing the record is the smaller loss,
        // and it is reported rather than swallowed.
        try
        {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO task_results
                    (task_id, mission_id, ant_name, status_code, success, contract_version, summary,
                     failure_class, failure_reason, failure_retryable,
                     artifacts_json, evidence_json, handoffs_json, warnings_json, metrics_json, recorded_at)
                  VALUES (@tid, @mid, @ant, @status, @ok, @cv, @summary,
                          @fclass, @freason, @fretry,
                          @artifacts, @evidence, @handoffs, @warnings, @metrics, @at)
                  ON CONFLICT(task_id) DO UPDATE SET
                     status_code=@status, success=@ok, contract_version=@cv, summary=@summary,
                     failure_class=@fclass, failure_reason=@freason, failure_retryable=@fretry,
                     artifacts_json=@artifacts, evidence_json=@evidence, handoffs_json=@handoffs,
                     warnings_json=@warnings, metrics_json=@metrics, recorded_at=@at",
                ("@tid", taskId), ("@mid", missionId), ("@ant", antName ?? ""),
                ("@status", result.StatusCode), ("@ok", result.Success ? 1 : 0),
                ("@cv", (object?)contractVersion ?? DBNull.Value),
                ("@summary", result.Summary ?? ""),
                ("@fclass", (object?)result.Failure?.Class.ToString() ?? DBNull.Value),
                ("@freason", (object?)result.Failure?.Reason ?? DBNull.Value),
                ("@fretry", result.Failure?.Retryable == true ? 1 : 0),
                ("@artifacts", Json.SafeDumps(result.Artifacts)),
                ("@evidence", Json.SafeDumps(result.Evidence)),
                ("@handoffs", Json.SafeDumps(result.Handoffs)),
                ("@warnings", Json.SafeDumps(result.Warnings)),
                ("@metrics", Json.SafeDumps(result.Metrics)),
                ("@at", AnthillTime.NowUtc().ToIso()));
        }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not record the ant result for task {taskId}: {error.Message}");
        }
    }

    /// <summary>
    /// The ant's report for one task, reconstructed from columns and JSON — never from the
    /// narrative. Null when the task predates this table, which callers must treat as "not
    /// recorded" rather than re-deriving an answer from the prose.
    /// </summary>
    public AntExecutionResult? LoadTaskResult(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        var row = Query(
            @"SELECT status_code, success, summary, failure_class, failure_reason, failure_retryable,
                     artifacts_json, evidence_json, handoffs_json, warnings_json, metrics_json
              FROM task_results WHERE task_id=@id", ("@id", taskId)).FirstOrDefault();
        if (row is null) return null;

        AntFailure? failure = null;
        var failureClass = row.GetValueOrDefault("failure_class")?.ToString();
        if (!string.IsNullOrWhiteSpace(failureClass) && Enum.TryParse<FailureClass>(failureClass, out var cls))
            failure = new AntFailure(cls, row.GetValueOrDefault("failure_reason")?.ToString() ?? "",
                Convert.ToInt64(row.GetValueOrDefault("failure_retryable") ?? 0L) != 0);

        return new AntExecutionResult
        {
            Success = Convert.ToInt64(row.GetValueOrDefault("success") ?? 0L) != 0,
            StatusCode = row.GetValueOrDefault("status_code")?.ToString() ?? "",
            Summary = row.GetValueOrDefault("summary")?.ToString() ?? "",
            Failure = failure,
            Artifacts = Json.TryParseList<AntArtifact>(row.GetValueOrDefault("artifacts_json") as string),
            Evidence = Json.TryParseList<AntEvidence>(row.GetValueOrDefault("evidence_json") as string),
            Handoffs = Json.TryParseList<AntHandoff>(row.GetValueOrDefault("handoffs_json") as string),
            Warnings = Json.TryParseStringList(row.GetValueOrDefault("warnings_json") as string).ToList(),
            Metrics = Json.TryParseTyped<AntMetrics>(row.GetValueOrDefault("metrics_json") as string) ?? new AntMetrics(),
        };
    }

    /// <summary>Every recorded ant report for a mission, oldest first.</summary>
    public IReadOnlyList<(string TaskId, AntExecutionResult Result)> LoadMissionTaskResults(string missionId)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return Array.Empty<(string, AntExecutionResult)>();
        var ids = Query(@"SELECT task_id FROM task_results WHERE mission_id=@m ORDER BY recorded_at",
                        ("@m", missionId))
            .Select(r => r.GetValueOrDefault("task_id")?.ToString() ?? "")
            .Where(id => id.Length > 0).ToList();

        var results = new List<(string, AntExecutionResult)>();
        foreach (var id in ids)
        {
            var loaded = LoadTaskResult(id);
            if (loaded is not null) results.Add((id, loaded));
        }
        return results;
    }
}
