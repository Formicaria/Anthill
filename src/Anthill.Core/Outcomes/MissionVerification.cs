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
        //
        // v0.3.8.101 — EXCEPT A NON-CRITICAL CHECK THAT RAN AND FAILED. A failed check DID run to
        // completion: its exit status is its verdict, and for the troubleshooting class that
        // failing verdict is the reproduced symptom the mission exists to produce. `Critical=false`
        // is the marker plan admission stamps on exactly that class's check tasks — nothing else
        // demotes a verification step — so the exemption is scoped by construction: a failed
        // critical tester still fails this gate (and already failed the line above), and a skipped
        // or blocked step of any kind still means the check never ran. The verifier's own verdict
        // rule below is untouched; it is what actually decides the pass.
        if (!verifications.All(t => t.Status == TaskStatus.Complete
                || (t.Status == TaskStatus.Failed && !t.Critical
                    && string.Equals(t.AssignedAnt, "tester", StringComparison.OrdinalIgnoreCase))))
            return false;

        // v2.19.0 Stage 6: completion is necessary but NOT sufficient. A verifier that ran to
        // completion and reported "Verification Failed" used to satisfy this gate, because the
        // gate only asked whether the task finished. Its verdict must actually be a pass.
        //
        // The verdict rule applies to the verifier role ONLY. Tester and soldier are verification
        // steps too, but they report evidence and findings rather than the verifier's verdict
        // vocabulary, so parsing their output for a verdict would return Unknown and fail every
        // mission they touch. Their completion remains the signal, as before.
        if (!tasks.Where(IsVerdictBearing).All(t => VerificationVerdict.TextIsPass(t.Result))) return false;

        // Structural repair §4 — FRESH EVIDENCE FOR THE LATEST REVISION, fail closed.
        //
        // PatchSet A → Tester A → FAIL → Medic → Coder produces PatchSet B: B must receive its own
        // tester run before anything about the mission may be called verified, and A's evidence —
        // however green — is not evidence about B. The pairing is structural: the coder task that
        // produced the CURRENT revision carries ProducedRevisionId, and a check task that executed
        // inside that revision's tree carries the matching RanRevisionId. A tester that ran against
        // an earlier revision, or against the unpatched mission workspace (null), does not satisfy
        // the candidate. Missions that never materialized a revision are unaffected.
        return HasFreshEvidenceForLatestRevision(tasks);
    }

    /// <summary>
    /// The PROMOTION rule: the interim checks above, plus the evidence store's own testimony.
    /// v0.3.8.66 (PLAN.md §2 item 2) — auto-apply already refused a set whose evidence judged a
    /// different revision, but the canonical evaluator did not ask the store at all, so correct
    /// test results about the WRONG TREE could still reach completed_verified outside the
    /// auto-apply path. This overload closes that: it is what the canonical evaluator calls, and
    /// task-pairing alone no longer promotes a mission that materialized a patch.
    /// </summary>
    /// <param name="evidence">The mission's evidence rows. NULL means the store could not be
    /// read — and for a mission holding a materialized revision that fails CLOSED (§1b S3's
    /// direction: an outage is never permission), while a mission with no revision needs no
    /// evidence identity and is unaffected.</param>
    public static bool IsSatisfied(IReadOnlyList<Task>? tasks,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence) =>
        IsSatisfied(tasks) && EvidenceIdentitySatisfied(tasks!, evidence);

    /// <summary>
    /// Does the STORE hold deterministic, passing evidence for the mission's FINAL revision and
    /// tree? Distinct from <see cref="HasFreshEvidenceForLatestRevision"/> by design — that one
    /// pairs on task stamps, which vanish with the task objects; this one asks the rows that
    /// survive the mission, and it is the stronger question promotion requires.
    ///
    /// What cannot satisfy it: evidence for an earlier repair generation (only the latest revision
    /// counts); rows with no identity (legacy and unpatched-workspace evidence stay readable for
    /// history and cannot promote new work); non-deterministic rows (a model review naming the
    /// right tree is still not grounds to promote); and an unreadable store.
    /// </summary>
    public static bool EvidenceIdentitySatisfied(IReadOnlyList<Task> tasks,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence)
    {
        var latest = LatestProducedRevision(tasks);
        if (latest is null) return true;    // no materialized patch: nothing to identify

        if (evidence is null) return false; // the store could not answer, and unverifiable is not verified

        var forLatest = evidence
            .Where(e => e.IdentifiesARevision
                     && string.Equals(e.RevisionId, latest, StringComparison.Ordinal))
            .ToList();
        if (forLatest.Count == 0) return false;

        return EvidenceJudgesRevision(forLatest, latest, forLatest[0].TreeHash);
    }

    /// <summary>True when the mission has no materialized revision, or its latest revision has at
    /// least one COMPLETED tester run stamped with that exact revision.</summary>
    internal static bool HasFreshEvidenceForLatestRevision(IReadOnlyList<Task> tasks)
    {
        var latest = LatestProducedRevision(tasks);
        if (latest is null) return true;   // nothing revision-shaped to pair
        return tasks.Any(t =>
            string.Equals(t.AssignedAnt, "tester", StringComparison.OrdinalIgnoreCase)
            && t.Status == TaskStatus.Complete
            && string.Equals(t.RanRevisionId, latest, StringComparison.Ordinal));
    }

    /// <summary>
    /// Is there DETERMINISTIC, PASSING evidence about this exact revision and tree? v0.3.8.57.
    ///
    /// The companion to <see cref="HasFreshEvidenceForLatestRevision"/>, and deliberately a different
    /// question. That one pairs on the TASK — "a tester ran inside revision B" — which is a true and
    /// useful statement about scheduling. This one asks the evidence itself, which is what survives
    /// the mission, what a replay reads, and what can be checked long after the task objects are gone.
    ///
    /// Both are needed because they fail differently. A task can be stamped with a revision and
    /// produce no evidence at all; an evidence row can name a revision whose task record was pruned.
    /// Neither implies the other, and promotion should require the stronger of the two.
    ///
    /// NON-DETERMINISTIC EVIDENCE CANNOT SATISFY THIS. A model review is recorded and never promotes
    /// — v3.8.22's rule — so a `model_review` naming the right tree is still not grounds to apply
    /// anything. That distinction is the whole reason `Evidence.Deterministic` exists.
    /// </summary>
    public static bool EvidenceJudgesRevision(
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence, string? revisionId, string? treeHash)
    {
        if (evidence is null || string.IsNullOrWhiteSpace(revisionId) || string.IsNullOrWhiteSpace(treeHash))
            return false;

        return evidence.Any(e => e.Deterministic && e.Passed && e.Judges(revisionId!, treeHash!));
    }

    internal static string? LatestProducedRevision(IReadOnlyList<Task> tasks) =>
        tasks.Where(t => !string.IsNullOrEmpty(t.ProducedRevisionId))
             .OrderBy(t => t.FinishedAt ?? t.CreatedAt)
             .LastOrDefault()?.ProducedRevisionId;

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
        if (badVerdict is not null) return VerificationVerdict.Explain(badVerdict);
        if (!HasFreshEvidenceForLatestRevision(tasks))
            return $"stale evidence: the latest revision ({LatestProducedRevision(tasks)}) has no completed "
                 + "tester run of its own — evidence from an earlier revision or the unpatched tree does not count";
        return "verification satisfied";
    }
}
