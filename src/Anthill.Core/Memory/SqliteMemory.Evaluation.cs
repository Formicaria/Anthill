using Anthill.Core.Outcomes;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.26.0 pre-V3 hardening — the canonical mission evaluation, persisted.
///
/// The authoritative outcome must not live only in transient events or in whatever a caller
/// re-derives from task rows: restored state must produce the SAME answer as live state, because
/// the Director, auto-apply, and reporting all read missions back after the process that ran them
/// has exited. A missing evaluation (row predates v2.26.0) is explicitly "legacy" — never treated
/// as verified, never retroactively promoted.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Persist the canonical evaluation onto the mission row. Called exactly once per
    /// mission, BEFORE completion is published to any consumer.</summary>
    public void SaveMissionEvaluation(MissionEvaluation evaluation)
    {
        if (evaluation is null || string.IsNullOrWhiteSpace(evaluation.MissionId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE missions SET outcome_code=@code, stop_reason=@stop, verification_status=@ver,
                      deliverable_status=@del, evaluator_version=@ev, evaluated_at=@at
                  WHERE id=@id",
                ("@id", evaluation.MissionId), ("@code", evaluation.OutcomeCode),
                ("@stop", evaluation.StopReason), ("@ver", evaluation.VerificationStatus),
                ("@del", evaluation.DeliverableStatus), ("@ev", evaluation.EvaluatorVersion),
                ("@at", evaluation.EvaluatedAt));
        }
        InvalidateCache();
    }

    /// <summary>
    /// Persist ONLY the mission's pheromone score. v0.3.8.41.
    ///
    /// A narrow UPDATE exists for one reason: learning now runs AFTER the canonical evaluation is
    /// persisted, because the archivist has to write its memory candidates in between and
    /// <see cref="Orchestration.LearningRecorder"/> is the thing that consumes them. The score is the
    /// only mission field learning mutates, and <c>SaveMission</c> is an INSERT OR REPLACE that does
    /// not carry the evaluation columns — so calling it here would silently erase the evaluation
    /// three lines after it was written, which is the exact defect the ordering comment in
    /// <c>Queen.RunMission</c> has warned about since v2.26.0.
    ///
    /// Two columns, one row, nothing else touched. A wide write in this position cannot be made safe;
    /// a narrow one cannot be made unsafe.
    /// </summary>
    public void SaveMissionScore(string missionId, double? score)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE missions SET success_score=@score WHERE id=@id",
                ("@id", missionId), ("@score", score));
        }
        InvalidateCache();
    }

    /// <summary>
    /// Load the persisted evaluation. Null when the row predates persisted evaluation — callers
    /// must treat that as LEGACY (never verified), not re-derive their own answer from task rows.
    /// </summary>
    public MissionEvaluation? LoadMissionEvaluation(string missionId)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return null;
        var row = Query(
            @"SELECT outcome_code, stop_reason, verification_status, deliverable_status,
                     evaluator_version, evaluated_at, status
              FROM missions WHERE id=@id", ("@id", missionId)).FirstOrDefault();
        if (row is null) return null;

        var code = row.GetValueOrDefault("outcome_code")?.ToString() ?? "";
        if (code.Length == 0) return null;   // legacy row — no evaluation was ever persisted

        return new MissionEvaluation(
            MissionId: missionId,
            OutcomeCode: code,
            StructuralStatus: row.GetValueOrDefault("status")?.ToString() ?? "",
            VerificationStatus: row.GetValueOrDefault("verification_status")?.ToString() ?? "",
            DeliverableStatus: row.GetValueOrDefault("deliverable_status")?.ToString() ?? "",
            StopReason: row.GetValueOrDefault("stop_reason")?.ToString() is { Length: > 0 } sr ? sr : null,
            EvaluatorVersion: row.GetValueOrDefault("evaluator_version")?.ToString() ?? MissionEvaluator.LegacyVersion,
            EvaluatedAt: row.GetValueOrDefault("evaluated_at")?.ToString() ?? "",
            Explanation: "loaded from persisted evaluation");
    }
}
