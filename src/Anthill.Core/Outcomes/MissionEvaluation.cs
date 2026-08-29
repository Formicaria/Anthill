using Anthill.Core.Common;
using Anthill.Core.Domain;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.26.0 pre-V3 hardening — ONE mission outcome.
///
/// Before this, six call sites independently re-derived whether a mission succeeded
/// (Queen finalization ×2, the Director's row re-derivation, restored-mission listing, objective
/// verification, candidate promotion) — and they could disagree, because task rows lack fields the
/// live path uses, and one caller resolved the outcome MID-mission while status was still Running
/// (which is why v2.23's route registration never actually registered anything).
///
/// A mission is now evaluated exactly once, after every task is terminal, by
/// <see cref="MissionEvaluator.Evaluate"/>; the result is persisted BEFORE completion is published;
/// and every downstream positive path consumes the persisted record. The old helpers survive only
/// as internals of this evaluator (and as the adaptive controller's mid-mission *progress* probe,
/// which is explicitly not a mission-final authority).
/// </summary>
public sealed record MissionEvaluation(
    string MissionId,
    string OutcomeCode,           // MissionOutcome vocabulary — the closed set, never free text
    string StructuralStatus,      // MissionStatus.Value(): complete | partial | failed
    string VerificationStatus,    // MissionEvaluation.Verification.*
    string DeliverableStatus,     // MissionEvaluation.Deliverable.*
    string? StopReason,           // MissionStopReasons.* | null — see that type; adaptive_stop
                                  // and adaptive_stop_satisfied are NOT the same outcome
    string EvaluatorVersion,
    string EvaluatedAt,
    string Explanation)
{
    /// <summary>THE positive predicate. Everything that reinforces, credits, applies, or completes
    /// on success must ask this record — nothing may re-derive it.</summary>
    public bool IsPositive => OutcomeCode == MissionOutcome.CompletedVerified;

    public static class Verification
    {
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string NotRun = "not_run";
    }

    public static class Deliverable
    {
        public const string Satisfied = "satisfied";
        public const string NotSatisfied = "not_satisfied";
        /// <summary>The goal asks for no tangible deliverable (research/report missions).</summary>
        public const string NotApplicable = "not_applicable";
        /// <summary>Objective verification is disabled — the layer did not run. Distinct from
        /// NotApplicable so a disabled check can never masquerade as a passed one.</summary>
        public const string NotChecked = "not_checked";
    }
}

public static class MissionEvaluator
{
    /// <summary>Bumped whenever the evaluation rules change, so a persisted evaluation always says
    /// which rules produced it. "legacy" marks rows that predate persisted evaluation.</summary>
    /// <summary>
    /// v3.8.22 — bumped to v2. The deterministic-block layer can flip an outcome from
    /// completed_verified to completed_unverified, so an evaluation produced before it and one
    /// produced after it are not comparable, and a stored row must say which rules made it. That is
    /// the entire purpose of this constant and it had never been exercised: the generation-integrity
    /// layer in v3.0.1 was the same kind of change and left the version at v1, which means every row
    /// between v3.0.1 and here claims a rule set it was not evaluated under. Not retroactively
    /// fixable; noted so the next rules change does not repeat it.
    /// </summary>
    /// <summary>v3 (v0.3.8.66): the verification layer consumes the evidence store's identity
    /// testimony — a mission with a materialized patch requires deterministic evidence bound to
    /// its FINAL revision and tree, and task-pairing alone no longer promotes. A v2 row and a v3
    /// row are graded under different rules and must say so.</summary>
    public const string Version = "evaluator-v3";
    public const string LegacyVersion = "legacy";

    /// <summary>
    /// Evaluate a finished mission. Call exactly once, after every task is terminal and
    /// mission.Status is final. The three layers are computed independently and combined here —
    /// nowhere else:
    ///   structural (did the plan run) → verification (did a verifier PASS, verdict-gated) →
    ///   deliverable (did the goal's tangible ask actually get produced).
    /// `completed_verified` requires all three. A stop reason (timeout / cancel / adaptive
    /// escalation) overrides everything: an interrupted mission is never any flavour of completed.
    /// </summary>
    /// <param name="constraints">v3.1.0 (ADR-002): the mission's constraints, resolved once at
    /// intake. The evaluator must read a mission's own instructions from the same object the
    /// admission gate and the planner used, not from a ninth parse of the goal string.</param>
    /// <param name="objectiveVerificationEnabled">v3.1.0 (ADR-001): the run's verification policy,
    /// passed in rather than read from a mutable static. This keeps the evaluator a PURE function
    /// of its arguments — the property that makes "evaluated exactly once, and reproducibly" a
    /// checkable claim rather than an aspiration.</param>
    /// <param name="evidence">v0.3.8.66 (§2 item 2): the mission's evidence rows, so the
    /// verification layer can require identity for missions that materialized a patch. Null means
    /// the store could not be read; for a revision-bearing mission that fails closed.</param>
    /// <param name="specification">v0.3.8.98: what the operator ASKED FOR, resolved once at intake.
    /// The deliverable layer below could grade a file change and nothing else, so an assessment
    /// mission — which changes nothing by construction — collapsed onto a verifier model saying the
    /// right words. Null keeps the pre-v0.3.8.98 behaviour exactly.</param>
    /// <param name="consumptions">v0.3.8.98: the artifact consumption ledger, so "the verifier
    /// consumed nothing" is answerable from a record instead of assumed. Null fails the assessment
    /// objective closed, for the same reason a null evidence list does.</param>
    public static MissionEvaluation Evaluate(Mission mission, string? stopReason, int patchProposalCount,
        MissionConstraints constraints, bool objectiveVerificationEnabled,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence = null,
        Missions.MissionSpecification? specification = null,
        IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption>? consumptions = null)
    {
        var structural = mission.Status.Value();

        // Verification layer — verdict-gated (v2.19); "not run" is distinct from "failed" for the
        // operator, but neither is a pass. v0.3.8.66: and identity-gated — the evidence store's
        // own rows must judge the final revision and tree when the mission materialized a patch.
        var hasVerifier = mission.Tasks.Any(MissionVerification.IsVerificationTask);
        var verification = !hasVerifier ? MissionEvaluation.Verification.NotRun
            : MissionVerification.IsSatisfied(mission.Tasks, evidence) ? MissionEvaluation.Verification.Passed
            : MissionEvaluation.Verification.Failed;

        // Deliverable layer — "a patch proposal is a deliverable, not proof the patch is safe".
        //
        // v0.3.8.98 — AND AN ASSESSMENT'S DELIVERABLE IS ITS ANSWER. The branch below could read
        // exactly one intent, `FileChange`, so every mission that legitimately delivers an answer
        // rather than an edit resolved to `not_applicable` and was graded on the verifier alone.
        // That is the whole of what made mission 7afd85b2 gradeable as complete. The assessment
        // objective is asked FIRST, and only for the class it applies to; nothing else changes.
        var assessment = AssessmentObjective.Applies(specification)
            ? AssessmentObjective.Evaluate(specification!, evidence, consumptions, mission.FinalResult,
                // BUILT HERE, not passed in. The ledger is a pure function of the specification and
                // the terminal tasks, and the evaluator's whole claim is that a grade is
                // reproducible from the persisted record — so it derives what it can derive rather
                // than trusting a caller to have derived it the same way.
                Missions.DeliverableLedger.Build(specification, mission.Tasks))
            : null;

        string deliverable;
        if (!objectiveVerificationEnabled)
            deliverable = MissionEvaluation.Deliverable.NotChecked;
        else if (assessment is not null)
            deliverable = assessment.Satisfied
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;
        else if (ObjectiveVerification.Required(mission.Goal, constraints)
                 == ObjectiveVerification.Deliverable.Unknown)
            deliverable = MissionEvaluation.Deliverable.NotApplicable;
        else
            deliverable = ObjectiveVerification.IsSatisfied(mission, constraints, patchProposalCount)
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;

        // Generation-integrity layer (v3.0.1): if the answer was produced by a DEGRADED (non-model)
        // fallback because the routed model was unavailable, the mission cannot be a verified
        // success. A fallback/ungrounded deliverable must not score as completed_verified — this is
        // what stopped an all-fallback (provider-down) run from reporting a perfect completion.
        var generationDegraded = mission.Tasks.Any(t => t.GenerationDegraded);

        // Deterministic-block layer (v3.8.22): a reproducible check said no — the build verifier
        // failed, a patch fell outside its approved scope, or the soldier's policy engine matched a
        // blocking rule. Any one of those makes a verified outcome impossible, and none of them is a
        // judgment call that later evidence can outweigh.
        //
        // This layer exists because both signals were already computed and neither was read. The
        // verification layer above cannot cover it: it asks whether a VERIFIER TASK passed, and a
        // patch set's build failure is not a task — it is a verdict about a task's output.
        var deterministicBlock = mission.Tasks.FirstOrDefault(t => t.DeterministicBlock is not null)?.DeterministicBlock;

        var outcome = Resolve(mission.Status, stopReason, verification, deliverable, generationDegraded,
            deterministicBlock is not null);
        return new MissionEvaluation(
            MissionId: mission.Id,
            OutcomeCode: outcome,
            StructuralStatus: structural,
            VerificationStatus: verification,
            DeliverableStatus: deliverable,
            StopReason: string.IsNullOrWhiteSpace(stopReason) ? null : stopReason,
            EvaluatorVersion: Version,
            EvaluatedAt: AnthillTime.NowUtc().ToIso(),
            Explanation: Explain(outcome, structural, verification, deliverable, stopReason, generationDegraded)
                + (deterministicBlock is null ? "" : $" Deterministic block: {deterministicBlock}")
                // The gate that said no, named. A demotion an operator cannot locate is one they
                // cannot answer, and "deliverable=not_satisfied" alone names no gate.
                + (assessment is null || assessment.Satisfied ? "" : $" {assessment.Explanation}"));
    }

    private static string Resolve(MissionStatus structuralStatus, string? stopReason,
        string verification, string deliverable, bool generationDegraded, bool deterministicBlock = false)
    {
        // An interrupted mission is never completed, whatever the tasks say.
        if (stopReason == MissionStopReasons.Cancelled) return MissionOutcome.Cancelled;
        if (stopReason == MissionStopReasons.Timeout) return MissionOutcome.TimedOut;

        // v0.3.8.74 — ONLY AN ESCALATING STOP ESCALATES. This line used to read
        // `if (stopReason == "adaptive_stop")`, and `adaptive_stop` was returned for two opposite
        // situations: the repair bound spent with the failure persisting, and the controller
        // declining to add a verification step the mission already had. The second is success, and
        // grading it as an escalation made a clean, fully verified patch mission unable to reach
        // `completed_verified` — which auto-apply consumes, so it could never apply its own patch.
        //
        // `MissionStopReasons.AdaptiveStopSatisfied` falls through deliberately: the mission is then
        // graded on its tasks, its verification and its deliverable, exactly as if the controller
        // had never spoken. A controller that looked, found nothing to do and said so must not
        // change the grade.
        if (MissionStopReasons.IsEscalation(stopReason)) return MissionOutcome.Escalated;

        if (structuralStatus == MissionStatus.Partial) return MissionOutcome.Partial;
        if (structuralStatus is not MissionStatus.Complete) return MissionOutcome.FailedPermanent;

        // Structural completion + verifier PASS + deliverable produced (where the layer is active
        // and applicable). NotSatisfied is the only deliverable state that demotes — a disabled
        // layer keeps pre-v2.26 behaviour, and is visible as "not_checked" rather than hidden.
        var verified = verification == MissionEvaluation.Verification.Passed
                       && deliverable != MissionEvaluation.Deliverable.NotSatisfied
                       && !generationDegraded
                       && !deterministicBlock;   // v3.8.22 — a reproducible "no" is final
        return verified ? MissionOutcome.CompletedVerified : MissionOutcome.CompletedUnverified;
    }

    private static string Explain(string outcome, string structural, string verification,
        string deliverable, string? stopReason, bool generationDegraded) =>
        $"outcome={outcome} (structural={structural}, verification={verification}, "
        + $"deliverable={deliverable}{(generationDegraded ? ", generation=degraded" : "")}"
        + $"{(stopReason is null ? "" : $", stop={stopReason}")})";
}
