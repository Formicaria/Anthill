using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;

namespace Anthill.Core.Orchestration;

/// <summary>What the controller decided to do after a wave of task execution.</summary>
public enum AdaptiveAction
{
    /// <summary>Work remains and the scheduler can make progress on its own.</summary>
    Continue,
    /// <summary>An unmet criterion needs tasks the static plan never contained.</summary>
    DeltaPlan,
    /// <summary>A deterministic check failed; route a bounded, focused repair.</summary>
    Repair,
    /// <summary>Bounds are exhausted or nothing is moving. Stop and tell the operator why.</summary>
    Escalate,
    /// <summary>Everything terminal, nothing unmet. The mission is done.</summary>
    Finish,
}

/// <summary>
/// The controller's typed answer. <see cref="Reason"/> is operator-facing: a decision to stop must
/// always be able to say what it was waiting for.
/// </summary>
public sealed record AdaptiveDecision(AdaptiveAction Action, string Reason, IReadOnlyList<string> UnmetCriteria)
{
    /// <summary>
    /// v0.3.8.105 — set only by the arm that stopped BECAUSE a failure recurred.
    ///
    /// Typed rather than inferred, and that is a correction of a mistake this file could easily have
    /// shipped: the caller's first draft read "escalating, and a recurrence exists" and labelled the
    /// stop `repeated_failure`. Those are different claims — a mission can escalate for no progress
    /// while a recurrence sits unrelated in the store — and a stop reason derived from a coincidence
    /// is exactly the kind of near-miss this repository keeps paying for. The arm that used the fact
    /// is the only thing that may report it.
    /// </summary>
    public Outcomes.FailureRecurrence.Recurrence? Recurrence { get; init; }

    public static AdaptiveDecision Of(AdaptiveAction action, string reason, IReadOnlyList<string>? unmet = null) =>
        new(action, reason, unmet ?? Array.Empty<string>());

    /// <summary>
    /// Value equality, including the criteria list.
    ///
    /// A record's generated equality compares members with <c>EqualityComparer&lt;T&gt;.Default</c>,
    /// which for a list means REFERENCE equality — so two assessments of the same unchanged mission
    /// would compare unequal simply because each built its own list. That is a quiet trap for any
    /// future "has the decision changed since the last wave?" check, which would always answer yes.
    /// </summary>
    public bool Equals(AdaptiveDecision? other) =>
        other is not null
        && Action == other.Action
        && Reason == other.Reason
        && UnmetCriteria.SequenceEqual(other.UnmetCriteria);

    public override int GetHashCode() =>
        UnmetCriteria.Aggregate(HashCode.Combine(Action, Reason), (acc, c) => HashCode.Combine(acc, c));
}

/// <summary>
/// Per-mission adaptive budgets. ADR §3.1 is explicit that these are DIFFERENT things with
/// SEPARATE counters: "A handoff is not a replan. A repair cycle is not a follow-up. Each has its
/// own counter, and exhausting one does not borrow budget from another."
///
/// Handoff depth is deliberately absent — it is bounded per-chain by HandoffGate, not per-mission,
/// so folding it in here would conflate two different bounds.
/// </summary>
public sealed record AdaptiveBudget(int ReplansUsed = 0, int RepairCyclesUsed = 0)
{
    public const int MaxReplans = 2;
    public const int MaxRepairCycles = 2;

    public bool CanReplan => ReplansUsed < MaxReplans;
    public bool CanRepair => RepairCyclesUsed < MaxRepairCycles;

    public AdaptiveBudget AfterReplan() => this with { ReplansUsed = ReplansUsed + 1 };
    public AdaptiveBudget AfterRepair() => this with { RepairCyclesUsed = RepairCyclesUsed + 1 };
}

/// <summary>
/// v2.22.0 Phase B: the adaptive loop's decision layer.
///
/// The Queen remains the authority for mission lifecycle, persistence and policy (ADR §3.2); this
/// type owns only the assessment and returns a typed decision. It is deliberately PURE — no
/// database, no model call, no scheduler mutation — so the same mission state always yields the
/// same decision, and a decision can be tested without running a mission.
///
/// Why bounded and delta-only: the ADR rejected letting the planner re-plan freely on each wave,
/// because that is unbounded recursive task creation wearing a different word. Replans are capped
/// by generation, repairs by cycle, and a wave that changed nothing escalates rather than looping.
/// </summary>
public sealed class AdaptiveMissionController
{
    /// <summary>
    /// A stable fingerprint of mission progress: every task's id paired with its status. Two waves
    /// with the same fingerprint moved nothing — which is the difference between "still working"
    /// and "stuck in a loop", and the only reliable way to tell them apart without a clock.
    ///
    /// Ordered by id so task ordering can never make a stalled mission look like it progressed.
    /// </summary>
    public static string Fingerprint(Mission? mission)
    {
        if (mission?.Tasks is null || mission.Tasks.Count == 0) return "empty";

        var canonical = string.Join("|", mission.Tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => $"{t.Id}:{t.Status.Value()}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>
    /// Decide what the mission should do next.
    ///
    /// <paramref name="previousFingerprint"/> is the fingerprint from the END of the previous wave;
    /// null on the first wave. Order of checks matters and is deliberate: terminal state first
    /// (a finished mission is never "stuck"), then real failures, then unmet criteria, then the
    /// stall check — so a mission that is genuinely progressing is never escalated, and one that
    /// has stopped moving is never left to spin.
    /// </summary>
    /// <param name="recurrence">v0.3.8.105: a failure already recorded for more than one task in
    /// this mission, from <see cref="Outcomes.FailureRecurrence"/>. Passed in rather than queried
    /// so this type stays PURE — the property its own remarks are built on. Null is what every
    /// caller before this release supplied and changes nothing.</param>
    public AdaptiveDecision Assess(Mission mission, AdaptiveBudget budget, string? previousFingerprint = null,
        string? missionClass = null, Outcomes.FailureRecurrence.Recurrence? recurrence = null)
    {
        if (mission?.Tasks is null || mission.Tasks.Count == 0)
            return AdaptiveDecision.Of(AdaptiveAction.Escalate, "mission has no tasks to assess");

        var terminal = mission.Tasks.All(IsTerminal);
        var unmet = UnmetCriteria(mission);

        // 1. Done is done. Assessed before anything else so a complete mission can never be
        //    diagnosed as stalled just because two waves look alike.
        if (terminal && unmet.Count == 0)
            return AdaptiveDecision.Of(AdaptiveAction.Finish, "all tasks terminal and every criterion met");

        // 2. A failed CRITICAL task is a repair candidate before it is a replan candidate: the
        //    plan was not wrong, a step of it broke. Repair is focused; delta planning is not.
        //
        // v0.3.8.101 — EXCEPT THE REPRODUCED SYMPTOM. In a troubleshooting mission the tester's
        // check task fails BY DESIGN: that failure is the symptom confirmed, the input to the
        // diagnosis, and the one outcome the mission was dispatched to produce. Reading it as a
        // broken critical task sent the controller through a repair cycle that could repair
        // nothing (the medic diagnoses and stops at this class's authority boundary), and then
        // escalated "the bound is spent" — stopping the mission before its builder and verifier
        // ever ran. A correctly reproduced symptom graded as an escalated failure, which is the
        // exact inversion of the class's purpose. A failed NON-tester task keeps the full repair
        // treatment: a dead researcher is a genuinely broken mission in any class.
        //
        // v0.3.8.104 — the class arrives from the mission's CONTRACT rather than being re-resolved
        // here. The previous comment said "intake is pure, so the class is read from the same
        // resolution every other layer uses", and that was true only while the rules never moved:
        // `.103` added a class and four verbs, so re-resolving would have graded a mission by rules
        // it was never admitted under. Null means a caller that has no contract to offer, and the
        // conservative reading — not troubleshooting — is the behaviour every release before `.101`
        // had.
        var troubleshooting = string.Equals(missionClass,
            Missions.MissionSpecification.TroubleshootingClass, StringComparison.OrdinalIgnoreCase);
        var brokenCritical = mission.Tasks
            .Where(t => t.Critical && t.Status == TaskStatus.Failed)
            .Where(t => !(troubleshooting && string.Equals(t.AssignedAnt, "tester", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (brokenCritical.Count > 0)
        {
            if (budget.CanRepair)
                return AdaptiveDecision.Of(AdaptiveAction.Repair,
                    $"{brokenCritical.Count} critical task(s) failed; routing a bounded repair",
                    brokenCritical.Select(t => $"critical task failed: {t.Title}").ToList());

            // v0.3.8.105 — THE SAME DEFECT, NAMED. The bound is spent either way; what changes is
            // whether the mission can say WHY it is spent.
            //
            // AND IT IS DELIBERATELY BELOW `CanRepair`, WHICH IS A CORRECTION. The first draft of
            // this release put the recurrence check ABOVE it, on the reasoning that a recurrence is
            // reproducible so the next cycle would end where the last one did. That reasoning is
            // wrong, and `CodePatchLifecycleTests.TheRepairLoop_MaterializesFreshEvidencePerGeneration`
            // is what said so: a repair GENERATION changes the artifact — the coder re-proposes, a
            // fresh patch set is materialised, a fresh tester judges it — so the same signature
            // appearing across two generations is the loop WORKING, not the loop spinning. Checking
            // first deleted the second generation outright and, with it, the medic's only route
            // into the mission. A recurrence may explain a stop; it must never cause one.
            //
            // The medic keeps the earlier bound, where it belongs: it fires after a repair was
            // actually attempted and the artifact still did not materially change. That is the
            // question this controller cannot answer and should not have tried to.
            if (recurrence is not null)
                return AdaptiveDecision.Of(AdaptiveAction.Escalate,
                    $"the repair bound is spent after {budget.RepairCyclesUsed} cycle(s), and the "
                  + $"reason is reproducible: {recurrence.Explanation}. Further repair generations "
                  + "would re-derive the same failure.",
                    brokenCritical.Select(t => $"critical task failed: {t.Title}")
                        .Append($"repeated failure: {recurrence.Signature}").ToList())
                    with { Recurrence = recurrence };

            return AdaptiveDecision.Of(AdaptiveAction.Escalate,
                $"critical failure persists after {budget.RepairCyclesUsed} repair cycle(s) — the bound is spent, not the problem",
                brokenCritical.Select(t => $"critical task failed: {t.Title}").ToList());
        }

        // 3. Unmet criteria on a mission whose tasks have all finished: the plan was incomplete.
        //    Only now is a delta plan the right instrument.
        if (terminal && unmet.Count > 0)
        {
            if (budget.CanReplan)
                return AdaptiveDecision.Of(AdaptiveAction.DeltaPlan,
                    $"tasks finished with {unmet.Count} unmet criterion(s); planning only what is missing", unmet);

            return AdaptiveDecision.Of(AdaptiveAction.Escalate,
                $"criteria still unmet after {budget.ReplansUsed} replan generation(s) — further replanning would not be bounded", unmet);
        }

        // 4. Work outstanding. If the previous wave produced an identical fingerprint, nothing
        //    moved: continuing would spin. This is the no-progress detector.
        var fingerprint = Fingerprint(mission);
        if (previousFingerprint is not null && previousFingerprint == fingerprint)
            return AdaptiveDecision.Of(AdaptiveAction.Escalate,
                "no task changed state during the last wave — the mission is not progressing", unmet);

        return AdaptiveDecision.Of(AdaptiveAction.Continue, "work remains and the mission is progressing", unmet);
    }

    /// <summary>
    /// What the mission still owes before it could be called verified. Deliberately the same
    /// standard MissionVerification applies — an assessment that used a weaker rule than the gate
    /// would keep proposing work the gate would never accept, or stop short of work it requires.
    /// </summary>
    public static IReadOnlyList<string> UnmetCriteria(Mission mission)
    {
        var unmet = new List<string>();
        if (mission?.Tasks is null) return unmet;

        if (!MissionVerification.IsSatisfied(mission.Tasks))
            unmet.Add($"verification: {MissionVerification.Explain(mission.Tasks)}");

        foreach (var t in mission.Tasks.Where(t => t.Critical && t.Status is TaskStatus.Skipped or TaskStatus.Blocked))
            unmet.Add($"critical task did not run: {t.Title}");

        return unmet;
    }

    private static bool IsTerminal(Task t) =>
        t.Status is TaskStatus.Complete or TaskStatus.Failed or TaskStatus.Skipped or TaskStatus.Cancelled;
}
