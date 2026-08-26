using System.Text;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// The operator's report, COMPILED FROM RECORDS. v0.3.8.73 — the first live qualification run's
/// finding, and the largest of them.
///
/// WHAT THE FIELD RUN FOUND. A live mission's final report carried commands, exit codes, durations,
/// test totals, a role census, medic activity and a `Dispatched` column. None of it came from the
/// colony. `BuilderAnt` writes the operator answer by prompting a model with prior task context —
/// so when a prompt asks for a report with telemetry, a model supplies telemetry. There was no
/// reporting code to have a bug in.
///
/// THE TELL, and it is worth recording because it separates six findings from one: `Dispatched`
/// appears nowhere in this repository, and the statuses in it read `In Progress` and `complete`
/// while the persisted vocabulary is the lowercase <see cref="TaskStatus"/> enum. The column was
/// invented, and so was everything in it. Two of the eight reported defects dissolved on the same
/// evidence — the colony cannot finalize with a task in progress (<c>Queen.FinalizeMission</c> forces
/// any non-terminal task to `failed` with `internal_runtime_defect` and fails the mission closed),
/// and the verifier does not fail open (it is always handed the evidence store, and an empty
/// evidence list resolves to `Unknown`, never a pass).
///
/// SO THIS IS THE FIX: every number an operator reads is a projection of a row. The model's prose
/// survives as a NARRATIVE section that cannot contribute a figure — the same division the scribe
/// has had since v3.8.28, where release notes are assembled from the mission's own results and never
/// from a model answer. That role was already right; the operator report was the one that was not.
///
/// WHAT THIS DELIBERATELY DOES NOT DO: invent a value it cannot source. A field with no record is
/// absent or explicitly "not recorded", never a plausible default — a report that fills gaps is the
/// defect this class exists to remove, one layer in.
/// </summary>
public static class MissionReport
{
    public sealed record CheckRun(string CheckId, string ExitCode, bool Passed, string TaskId);

    public sealed record TaskLine(
        string Id, string Ant, string Worker, string Title, string Status,
        double? ElapsedSeconds, bool Critical, string? FailureReason);

    public sealed record Report(
        string MissionId,
        string Goal,
        string Status,
        string OutcomeCode,
        string VerificationBasis,
        DateTime? StartedAt,
        DateTime? FinishedAt,
        double? ElapsedSeconds,
        IReadOnlyList<TaskLine> Tasks,
        IReadOnlyList<CheckRun> Checks,
        IReadOnlyList<string> RolesRegistered,
        IReadOnlyList<string> RolesDispatched,
        int PatchSetCount,
        int ArtifactCount,
        int EvidenceCount);

    /// <summary>
    /// Compile from persisted state alone. Every argument is a store; nothing here takes text.
    ///
    /// The signature is the guarantee: there is no parameter a model answer could be passed through,
    /// so no future edit can quietly let one contribute. A helper that accepted "context" would be
    /// the door back.
    /// </summary>
    public static Report Compile(SqliteMemory memory, string missionId)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var mission = memory.GetMission(missionId);
        var rows = memory.GetTasksForMission(missionId, limit: 500);

        var tasks = rows.Select(r => new TaskLine(
            Id: Str(r, "id"),
            Ant: Str(r, "assigned_ant"),
            Worker: Str(r, "assigned_worker"),
            Title: Str(r, "title"),
            Status: Str(r, "status"),
            ElapsedSeconds: Num(r, "elapsed_seconds"),
            Critical: Str(r, "critical") is "1" or "True" or "true",
            FailureReason: Str(r, "failure_reason") is { Length: > 0 } f ? f : null)).ToList();

        // CHECKS COME FROM THE TESTER'S EVIDENCE ROWS, not from anyone's account of them. The
        // tester records one `check` evidence per check id with `exit_code=… success=…`; that is the
        // only place an exit code exists, so it is the only place this reads.
        var checks = new List<CheckRun>();
        foreach (var row in rows)
        {
            var taskId = Str(row, "id");
            var result = memory.LoadTaskResult(taskId);
            if (result is null) continue;
            foreach (var e in result.Evidence.Where(e => e.Kind == Agents.AntEvidenceKinds.Check))
            {
                var detail = e.Detail ?? "";
                var exit = System.Text.RegularExpressions.Regex.Match(detail, @"exit_code=(-?\d+|n/a)");
                checks.Add(new CheckRun(
                    e.Value,
                    exit.Success ? exit.Groups[1].Value : "not recorded",
                    detail.Contains("success=True", StringComparison.OrdinalIgnoreCase),
                    taskId));
            }
        }

        // THE ROLE CENSUS COMES FROM THE REGISTRY, which is the only thing that knows what exists.
        // The field report's census was short because a model listed the roles it had seen mentioned.
        var registered = Agents.AntRegistry.Roles
            .Select(r => r.RoleId).Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var dispatched = tasks.Select(t => t.Ant).Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

        var evaluation = memory.LoadMissionEvaluation(missionId);
        var evidence = SafeEvidence(memory, missionId);

        // TIMES ARE COMPUTED, never described. Absent stamps produce nulls, which render as "not
        // recorded" — a duration nobody measured must not appear as a number.
        // The missions table records `created_at` and `saved_at`, not start/finish — so the mission
        // window is the span of its TASKS, with the mission's own stamps as the outer bound. Neither
        // is invented: a mission with no timed task reports no elapsed time at all.
        var started = rows.Select(r => Time(r, "started_at")).Where(t => t is not null).Min()
                   ?? Time(mission, "created_at");
        var finished = rows.Select(r => Time(r, "finished_at")).Where(t => t is not null).Max()
                    ?? Time(mission, "saved_at");

        return new Report(
            MissionId: missionId,
            Goal: mission is null ? "" : Str(mission, "goal"),
            Status: mission is null ? "unknown" : Str(mission, "status"),
            OutcomeCode: evaluation?.OutcomeCode ?? "none persisted",
            VerificationBasis: evidence is null
                ? "evidence store unreadable — verification unavailable"
                : EvidenceVerdict.For(evidence).Explanation,
            StartedAt: started,
            FinishedAt: finished,
            ElapsedSeconds: started is { } s && finished is { } f2 ? (f2 - s).TotalSeconds : null,
            Tasks: tasks,
            Checks: checks,
            RolesRegistered: registered,
            RolesDispatched: dispatched,
            PatchSetCount: memory.GetRecentEvents(500, "patch_set_created", missionId).Count,
            ArtifactCount: SafeArtifactCount(memory, missionId),
            EvidenceCount: evidence?.Count ?? 0);
    }

    /// <summary>
    /// The report as text an operator reads. Rendering only — every value is already a fact by the
    /// time it arrives here, and nothing is computed in this method.
    /// </summary>
    public static string Render(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== MISSION RECORD (compiled from persisted state; no model wrote any value below) ===");
        sb.AppendLine($"mission_id: {r.MissionId}");
        sb.AppendLine($"status: {r.Status}");
        sb.AppendLine($"outcome_code: {r.OutcomeCode}");
        sb.AppendLine($"verification: {r.VerificationBasis}");
        sb.AppendLine($"started_at: {Stamp(r.StartedAt)}");
        sb.AppendLine($"finished_at: {Stamp(r.FinishedAt)}");
        sb.AppendLine($"elapsed_seconds: {(r.ElapsedSeconds is { } e ? e.ToString("F1") : "not recorded")}");
        sb.AppendLine();

        sb.AppendLine($"tasks ({r.Tasks.Count}):");
        foreach (var t in r.Tasks)
            sb.AppendLine($"  [{t.Status}] {t.Ant}/{t.Worker} — {t.Title}"
                + $" ({(t.ElapsedSeconds is { } es ? es.ToString("F2") + "s" : "duration not recorded")}"
                + $"{(t.Critical ? ", critical" : "")})"
                + (t.FailureReason is { } fr ? $"\n      failure: {fr}" : ""));
        sb.AppendLine();

        sb.AppendLine($"checks ({r.Checks.Count}):");
        if (r.Checks.Count == 0)
            sb.AppendLine("  none — no check evidence was recorded for this mission");
        foreach (var c in r.Checks)
            sb.AppendLine($"  {c.CheckId}: exit_code={c.ExitCode} {(c.Passed ? "PASS" : "FAIL")} (task {c.TaskId})");
        sb.AppendLine();

        sb.AppendLine($"roles registered ({r.RolesRegistered.Count}): {string.Join(", ", r.RolesRegistered)}");
        sb.AppendLine($"roles dispatched ({r.RolesDispatched.Count}): "
            + (r.RolesDispatched.Count == 0 ? "none" : string.Join(", ", r.RolesDispatched)));
        sb.AppendLine($"patch_sets: {r.PatchSetCount}   artifacts: {r.ArtifactCount}   evidence_rows: {r.EvidenceCount}");
        return sb.ToString();
    }

    private static string Str(Dictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key)?.ToString() ?? "";

    private static double? Num(Dictionary<string, object?> row, string key) =>
        double.TryParse(row.GetValueOrDefault(key)?.ToString(), out var v) ? v : null;

    private static DateTime? Time(Dictionary<string, object?>? row, string key) =>
        row is not null && DateTime.TryParse(row.GetValueOrDefault(key)?.ToString(), out var v)
            ? v.ToUniversalTime() : null;

    private static string Stamp(DateTime? t) => t?.ToString("u") ?? "not recorded";

    private static IReadOnlyList<Evidence>? SafeEvidence(SqliteMemory memory, string missionId)
    {
        try { return ((IEvidenceStore)memory).ForMission(missionId); }
        catch (Exception error)
        {
            // Unreadable is REPORTED as unreadable, not rendered as zero. "0 evidence rows" and
            // "the store would not answer" are different facts and only one of them is good news.
            Console.Error.WriteLine($"[mission-report] could not read evidence for {missionId}: {error.Message}");
            return null;
        }
    }

    private static int SafeArtifactCount(SqliteMemory memory, string missionId)
    {
        try { return ((IArtifactStore)memory).ForMission(missionId).Count; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[mission-report] could not read artifacts for {missionId}: {error.Message}");
            return 0;
        }
    }
}
