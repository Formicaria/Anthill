using Anthill.Core.Domain;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.19.0 Stage 2 — the INTERIM verification gate.
///
/// Stage 6 replaces this with <c>VerificationPolicy</c>-driven evidence requirements per mission
/// type, evaluated inside <c>FinalizeMission</c>. Until then this answers one narrower question:
/// did this mission actually run a verification step that finished successfully?
///
/// Deliberately conservative and one-directional. Relative to the previous rule — where any
/// mission with status complete OR partial reinforced learning and could auto-apply — this can
/// only ever reduce what counts as success. It never promotes something that did not already
/// qualify. A mission with no verification step at all is <c>completed_unverified</c>.
///
/// This is NOT the deterministic proof the ADR requires (§3.3): a verifier task completing is a
/// weaker signal than a required evidence bundle. It is an honest interim tightening, and is
/// named so nobody mistakes it for the real gate.
/// </summary>
public static class MissionVerification
{
    /// <summary>Roles and task types that constitute a verification step.</summary>
    private static readonly HashSet<string> VerificationRoles =
        new(StringComparer.OrdinalIgnoreCase) { "verifier", "tester", "soldier" };

    private static readonly HashSet<string> VerificationTaskTypes =
        new(StringComparer.OrdinalIgnoreCase) { "verify", "verification", "test", "check", "security_review" };

    /// <summary>True when a task is a verification step, by role or by declared task type.</summary>
    public static bool IsVerificationTask(Task task) =>
        task is not null &&
        (VerificationRoles.Contains(task.AssignedAnt) || VerificationTaskTypes.Contains(task.TaskType));

    /// <summary>
    /// The interim rule: at least one verification task ran and completed, and no CRITICAL task
    /// failed. Absence of verification is not verification.
    /// </summary>
    public static bool IsSatisfied(IReadOnlyList<Task>? tasks)
    {
        if (tasks is null || tasks.Count == 0) return false;

        // A critical failure disqualifies regardless of what else passed.
        if (tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical)) return false;

        var verifications = tasks.Where(IsVerificationTask).ToList();
        if (verifications.Count == 0) return false;                       // nothing verified anything

        // Every verification step that exists must have completed. A skipped or failed verifier
        // means the mission's own check did not run to completion.
        if (!verifications.All(t => t.Status == TaskStatus.Complete)) return false;

        // v2.19.0 Stage 6: completion is necessary but NOT sufficient. A verifier that ran to
        // completion and reported "Verification Failed" used to satisfy this gate, because the
        // gate only asked whether the task finished. Its verdict must actually be a pass.
        //
        // The verdict rule applies to the verifier role ONLY. Tester and soldier are verification
        // steps too, but they report evidence and findings rather than the verifier's verdict
        // vocabulary, so parsing their output for a verdict would return Unknown and fail every
        // mission they touch. Their completion remains the signal, as before.
        return tasks.Where(IsVerdictBearing).All(t => VerificationVerdict.TextIsPass(t.Result));
    }

    /// <summary>The verifier is the only role that emits the verdict vocabulary.</summary>
    private static bool IsVerdictBearing(Task t) =>
        t is not null && string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Row-based overload, for callers reading persisted task rows rather than a live
    /// <see cref="Mission"/> (the Director does). Note the tasks table does NOT persist a
    /// `critical` column, so this cannot distinguish critical from non-critical failure and
    /// disqualifies on ANY failed task — stricter than the object overload, which is the safe
    /// direction for a gate that decides whether work may auto-apply.
    /// </summary>
    public static bool IsSatisfiedFromRows(IReadOnlyList<Dictionary<string, object?>>? rows)
    {
        if (rows is null || rows.Count == 0) return false;
        static string Field(Dictionary<string, object?> r, string k) => r.GetValueOrDefault(k)?.ToString() ?? "";

        if (rows.Any(r => string.Equals(Field(r, "status"), "failed", StringComparison.OrdinalIgnoreCase)))
            return false;

        var verifications = rows
            .Where(r => VerificationRoles.Contains(Field(r, "assigned_ant"))
                     || VerificationTaskTypes.Contains(Field(r, "task_type")))
            .ToList();
        if (verifications.Count == 0) return false;

        if (!verifications.All(r => string.Equals(Field(r, "status"), "complete", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Stage 6, row path: same rule as the object overload. `result` is on the row, so the
        // verdict survives persistence without a schema change.
        return rows.Where(r => string.Equals(Field(r, "assigned_ant"), "verifier", StringComparison.OrdinalIgnoreCase))
                   .All(r => VerificationVerdict.TextIsPass(Field(r, "result")));
    }

    /// <summary>Why the gate said no, for persisted rows.</summary>
    public static string ExplainRows(IReadOnlyList<Dictionary<string, object?>>? rows)
    {
        if (rows is null || rows.Count == 0) return "no tasks recorded";
        static string Field(Dictionary<string, object?> r, string k) => r.GetValueOrDefault(k)?.ToString() ?? "";
        if (rows.Any(r => string.Equals(Field(r, "status"), "failed", StringComparison.OrdinalIgnoreCase)))
            return "a task failed";
        var verifications = rows
            .Where(r => VerificationRoles.Contains(Field(r, "assigned_ant"))
                     || VerificationTaskTypes.Contains(Field(r, "task_type")))
            .ToList();
        if (verifications.Count == 0) return "the mission ran no verification step";
        // Order matters: a verifier that never finished did not produce a verdict to judge, so
        // reporting a verdict problem there would misdescribe the cause.
        var unfinished = verifications.Count(r => !string.Equals(Field(r, "status"), "complete", StringComparison.OrdinalIgnoreCase));
        if (unfinished > 0) return $"{unfinished} verification step(s) did not complete";
        var badVerdict = rows.Where(r => string.Equals(Field(r, "assigned_ant"), "verifier", StringComparison.OrdinalIgnoreCase))
            .Select(r => VerificationVerdict.Parse(Field(r, "result")))
            .FirstOrDefault(v => !VerificationVerdict.IsPass(v));
        return badVerdict is not null ? VerificationVerdict.Explain(badVerdict) : "verification satisfied";
    }

    /// <summary>Why the gate said no — surfaced to the operator rather than failing silently.</summary>
    public static string Explain(IReadOnlyList<Task>? tasks)
    {
        if (tasks is null || tasks.Count == 0) return "no tasks recorded";
        if (tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical)) return "a critical task failed";
        var verifications = tasks.Where(IsVerificationTask).ToList();
        if (verifications.Count == 0) return "the mission ran no verification step";
        var unfinished = verifications.Where(t => t.Status != TaskStatus.Complete).ToList();
        if (unfinished.Count > 0) return $"{unfinished.Count} verification step(s) did not complete";
        var badVerdict = tasks.Where(IsVerdictBearing)
            .Select(t => VerificationVerdict.Parse(t.Result))
            .FirstOrDefault(v => !VerificationVerdict.IsPass(v));
        return badVerdict is not null ? VerificationVerdict.Explain(badVerdict) : "verification satisfied";
    }
}
