using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Common;

namespace Anthill.Core.Orchestration;

/// <summary>
/// A FINISHED MISSION, READ BACK AS THE OBJECT GRAPH IT WAS. v0.3.8.110, PLAN.md §2b `.110`.
///
/// WHAT DID NOT EXIST, and it is the reason mission resumption was deferred from `.105` to `.106` to
/// `.110` rather than being hard. There is no typed loader anywhere in this tree.
/// <c>SqliteMemory.GetMission</c> returns a <c>Dictionary&lt;string, object?&gt;</c> and
/// <c>GetTasksForMission</c> returns a list of them; `new Mission` appears in exactly four places,
/// every one of them creating a mission rather than reading one. So the in-memory graph has always
/// died with <c>RunMission</c>, and every consumer that wanted a past mission read rows and rendered
/// them. Nothing could ever re-enter execution, because there was nothing to re-enter it WITH.
///
/// THIS IS A READER, NOT A REPAIRER. It reports what the store holds. A task row whose status the
/// vocabulary does not recognise becomes <see cref="TaskStatus.Pending"/> — the parser's own
/// documented fallback — and a mission with no row at all returns null rather than an empty mission,
/// because "this mission does not exist" and "this mission did nothing" are different facts and only
/// one of them is safe to act on.
///
/// WHAT IT DELIBERATELY DOES NOT CARRY. Several task fields are live-run state that the tasks table
/// does not persist: <c>WorkerBasis</c>, <c>GenerationDegraded</c>, <c>InputArtifactIds</c>,
/// <c>DeliverableIds</c>, <c>RequiredCapability</c>, <c>ProducedRevisionId</c>/<c>RanRevisionId</c>.
/// They are left at their defaults and that is stated rather than hidden, because a resumed
/// mission's evaluation reads two of them — <c>GenerationDegraded</c> and the revision pairing — and
/// a reader that silently defaulted them would produce a grade that looks computed and is not.
/// <see cref="MissionResumption"/> is the one consumer today and it re-evaluates only after running
/// a task that populates its own; anything else reading this must decide for itself what an unset
/// field means.
///
/// The mission's CONTRACT is not read here. It has its own loader — <c>MissionContracts.LoadOrCreate</c>
/// — which has been resume-safe since `.104` and is where a resumed run gets what it was admitted
/// under. Two readers of the same fact is the defect this repository names most often.
/// </summary>
public static class MissionRehydration
{
    /// <summary>
    /// The mission and its tasks, or null when the store holds no such mission.
    /// </summary>
    public static Mission? Load(SqliteMemory memory, string? missionId)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (string.IsNullOrWhiteSpace(missionId)) return null;

        var row = memory.GetMission(missionId!);
        if (row is null) return null;

        var mission = new Mission
        {
            Id = RowValues.Text(row, "id", missionId!),
            Goal = RowValues.Text(row, "goal"),
            ProjectId = RowValues.TextOrNull(row, "project_id"),
            Status = ParseMissionStatus(RowValues.Text(row, "status", "created")),
            UserResult = RowValues.TextOrNull(row, "user_result"),
            DebugResult = RowValues.TextOrNull(row, "debug_result"),
            FinalResult = RowValues.TextOrNull(row, "final_result"),
            BestOutputTaskId = RowValues.TextOrNull(row, "best_output_task_id"),
            SuccessScore = RowValues.Double(row, "success_score"),
            CreatedAt = RowValues.TimestampOrNow(row, "created_at"),
            Tasks = memory.GetTasksForMission(missionId!).Select(TaskFrom).ToList(),
        };

        return mission;
    }

    /// <summary>
    /// The mission-status vocabulary read back. Mirrors <c>EnumExtensions.Value(MissionStatus)</c>
    /// exactly; an unknown value is <see cref="MissionStatus.Created"/>, which is the same fallback
    /// that writer uses in the other direction.
    /// </summary>
    public static MissionStatus ParseMissionStatus(string? value) => value switch
    {
        "created" => MissionStatus.Created,
        "running" => MissionStatus.Running,
        "complete" => MissionStatus.Complete,
        "partial" => MissionStatus.Partial,
        "failed" => MissionStatus.Failed,
        _ => MissionStatus.Created,
    };

    private static Task TaskFrom(Dictionary<string, object?> row) => new()
    {
        Id = RowValues.Text(row, "id"),
        Title = RowValues.Text(row, "title"),
        Description = RowValues.Text(row, "description"),
        AssignedAnt = RowValues.Text(row, "assigned_ant"),
        AssignedWorker = RowValues.TextOrNull(row, "assigned_worker"),
        TaskType = RowValues.Text(row, "task_type", "general"),
        ParentTaskId = RowValues.TextOrNull(row, "parent_task_id"),
        ParentTaskIds = Json.TryParseStringList(RowValues.TextOrNull(row, "parent_task_ids_json")),
        DependsOn = Json.TryParseStringList(RowValues.TextOrNull(row, "depends_on_json")),
        InputArtifactIds = Json.TryParseStringList(RowValues.TextOrNull(row, "input_artifact_ids_json")),
        Status = EnumExtensions.ParseTaskStatus(RowValues.Text(row, "status", "pending")),
        Result = RowValues.TextOrNull(row, "result"),
        ResultSummary = RowValues.TextOrNull(row, "result_summary"),
        ResultChars = RowValues.Int(row, "result_chars"),
        EstimatedTokens = RowValues.Int(row, "estimated_tokens"),
        CreatedAt = RowValues.TimestampOrNow(row, "created_at"),
        StartedAt = RowValues.Timestamp(row, "started_at"),
        FinishedAt = RowValues.Timestamp(row, "finished_at"),
        CompletedAt = RowValues.Timestamp(row, "completed_at"),
        FailedAt = RowValues.Timestamp(row, "failed_at"),
        SkippedAt = RowValues.Timestamp(row, "skipped_at"),
        ElapsedSeconds = RowValues.Double(row, "elapsed_seconds"),
        AttemptCount = RowValues.Int(row, "attempt_count"),
        MaxAttempts = Math.Max(1, RowValues.Int(row, "max_attempts")),
        FailureReason = RowValues.TextOrNull(row, "failure_reason"),
        FailureType = RowValues.TextOrNull(row, "failure_type"),
        SkippedReason = RowValues.TextOrNull(row, "skipped_reason"),
        BlockedReason = RowValues.TextOrNull(row, "blocked_reason"),
        SkillId = RowValues.TextOrNull(row, "skill_id"),
        Critical = RowValues.Int(row, "critical") != 0,
        CancellationReason = RowValues.TextOrNull(row, "cancellation_reason"),
        DeterministicBlock = RowValues.TextOrNull(row, "deterministic_block"),
    };

    // v0.3.8.113 — THE PRIVATE ROW READERS ARE GONE, replaced by `Memory.RowValues`.
    //
    // This file introduced them at `.110` and the approvals slice needed the same six the moment it
    // started, which is the second implementation of one rule appearing within three releases — this
    // repository's most-named defect class, arriving in the middle of the release built to remove it.
    // One reader now, in the layer that owns rows.
}
