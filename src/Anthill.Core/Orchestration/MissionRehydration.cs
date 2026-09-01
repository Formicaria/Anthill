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
            Id = Str(row, "id", missionId!),
            Goal = Str(row, "goal"),
            ProjectId = Nullable(row, "project_id"),
            Status = ParseMissionStatus(Str(row, "status", "created")),
            UserResult = Nullable(row, "user_result"),
            DebugResult = Nullable(row, "debug_result"),
            FinalResult = Nullable(row, "final_result"),
            BestOutputTaskId = Nullable(row, "best_output_task_id"),
            SuccessScore = Double(row, "success_score"),
            CreatedAt = Utc(row, "created_at"),
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
        Id = Str(row, "id"),
        Title = Str(row, "title"),
        Description = Str(row, "description"),
        AssignedAnt = Str(row, "assigned_ant"),
        AssignedWorker = Nullable(row, "assigned_worker"),
        TaskType = Str(row, "task_type", "general"),
        ParentTaskId = Nullable(row, "parent_task_id"),
        ParentTaskIds = Json.TryParseStringList(Nullable(row, "parent_task_ids_json")),
        DependsOn = Json.TryParseStringList(Nullable(row, "depends_on_json")),
        InputArtifactIds = Json.TryParseStringList(Nullable(row, "input_artifact_ids_json")),
        Status = EnumExtensions.ParseTaskStatus(Str(row, "status", "pending")),
        Result = Nullable(row, "result"),
        ResultSummary = Nullable(row, "result_summary"),
        ResultChars = Int(row, "result_chars"),
        EstimatedTokens = Int(row, "estimated_tokens"),
        CreatedAt = Utc(row, "created_at"),
        StartedAt = NullableUtc(row, "started_at"),
        FinishedAt = NullableUtc(row, "finished_at"),
        CompletedAt = NullableUtc(row, "completed_at"),
        FailedAt = NullableUtc(row, "failed_at"),
        SkippedAt = NullableUtc(row, "skipped_at"),
        ElapsedSeconds = Double(row, "elapsed_seconds"),
        AttemptCount = Int(row, "attempt_count"),
        MaxAttempts = Math.Max(1, Int(row, "max_attempts")),
        FailureReason = Nullable(row, "failure_reason"),
        FailureType = Nullable(row, "failure_type"),
        SkippedReason = Nullable(row, "skipped_reason"),
        BlockedReason = Nullable(row, "blocked_reason"),
        SkillId = Nullable(row, "skill_id"),
        Critical = Int(row, "critical") != 0,
        CancellationReason = Nullable(row, "cancellation_reason"),
        DeterministicBlock = Nullable(row, "deterministic_block"),
    };

    // ---- readers -------------------------------------------------------------------------------
    //
    // SQLite hands back object? — strings, longs, doubles and DBNull — so every one of these is
    // written to survive any of them rather than to assume the column's declared type.

    private static string Str(Dictionary<string, object?> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var v) && v is not null and not DBNull
            ? v.ToString() ?? fallback : fallback;

    private static string? Nullable(Dictionary<string, object?> row, string key) =>
        Str(row, key) is { Length: > 0 } s ? s : null;

    private static int Int(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key) switch
    {
        long l => (int)l,
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    private static double? Double(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key) switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static DateTime Utc(Dictionary<string, object?> row, string key) =>
        NullableUtc(row, key) ?? AnthillTime.NowUtc();

    private static DateTime? NullableUtc(Dictionary<string, object?> row, string key)
    {
        var raw = Str(row, key);
        if (raw.Length == 0) return null;
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed) ? parsed : null;
    }
}
