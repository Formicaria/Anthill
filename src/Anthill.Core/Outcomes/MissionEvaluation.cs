using Anthill.Core.Common;
using Anthill.Core.Configuration;
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
    string? StopReason,           // mission_timeout | mission_cancelled | adaptive_stop | null
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
    public const string Version = "evaluator-v1";
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
    public static MissionEvaluation Evaluate(Mission mission, string? stopReason, int patchProposalCount)
    {
        var structural = mission.Status.Value();

        // Verification layer — verdict-gated (v2.19); "not run" is distinct from "failed" for the
        // operator, but neither is a pass.
        var hasVerifier = mission.Tasks.Any(MissionVerification.IsVerificationTask);
        var verification = !hasVerifier ? MissionEvaluation.Verification.NotRun
            : MissionVerification.IsSatisfied(mission.Tasks) ? MissionEvaluation.Verification.Passed
            : MissionEvaluation.Verification.Failed;

        // Deliverable layer — "a patch proposal is a deliverable, not proof the patch is safe".
        string deliverable;
        if (!AnthillRuntime.EnableObjectiveVerification)
            deliverable = MissionEvaluation.Deliverable.NotChecked;
        else if (ObjectiveVerification.Required(mission.Goal, MissionConstraints.Parse(mission.Goal))
                 == ObjectiveVerification.Deliverable.Unknown)
            deliverable = MissionEvaluation.Deliverable.NotApplicable;
        else
            deliverable = ObjectiveVerification.IsSatisfied(mission, patchProposalCount)
                ? MissionEvaluation.Deliverable.Satisfied
                : MissionEvaluation.Deliverable.NotSatisfied;

        // Generation-integrity layer (v3.0.1): if the answer was produced by a DEGRADED (non-model)
        // fallback because the routed model was unavailable, the mission cannot be a verified
        // success. A fallback/ungrounded deliverable must not score as completed_verified — this is
        // what stopped an all-fallback (provider-down) run from reporting a perfect completion.
        var generationDegraded = mission.Tasks.Any(t => t.GenerationDegraded);

        var outcome = Resolve(mission.Status, stopReason, verification, deliverable, generationDegraded);
        return new MissionEvaluation(
            MissionId: mission.Id,
            OutcomeCode: outcome,
            StructuralStatus: structural,
            VerificationStatus: verification,
            DeliverableStatus: deliverable,
            StopReason: string.IsNullOrWhiteSpace(stopReason) ? null : stopReason,
            EvaluatorVersion: Version,
            EvaluatedAt: AnthillTime.NowUtc().ToIso(),
            Explanation: Explain(outcome, structural, verification, deliverable, stopReason, generationDegraded));
    }

    private static string Resolve(MissionStatus structuralStatus, string? stopReason,
        string verification, string deliverable, bool generationDegraded)
    {
        // An interrupted mission is never completed, whatever the tasks say.
        if (stopReason == "mission_cancelled") return MissionOutcome.Cancelled;
        if (stopReason == "mission_timeout") return MissionOutcome.TimedOut;
        if (stopReason == "adaptive_stop") return MissionOutcome.Escalated;

        if (structuralStatus == MissionStatus.Partial) return MissionOutcome.Partial;
        if (structuralStatus is not MissionStatus.Complete) return MissionOutcome.FailedPermanent;

        // Structural completion + verifier PASS + deliverable produced (where the layer is active
        // and applicable). NotSatisfied is the only deliverable state that demotes — a disabled
        // layer keeps pre-v2.26 behaviour, and is visible as "not_checked" rather than hidden.
        var verified = verification == MissionEvaluation.Verification.Passed
                       && deliverable != MissionEvaluation.Deliverable.NotSatisfied
                       && !generationDegraded;
        return verified ? MissionOutcome.CompletedVerified : MissionOutcome.CompletedUnverified;
    }

    private static string Explain(string outcome, string structural, string verification,
        string deliverable, string? stopReason, bool generationDegraded) =>
        $"outcome={outcome} (structural={structural}, verification={verification}, "
        + $"deliverable={deliverable}{(generationDegraded ? ", generation=degraded" : "")}"
        + $"{(stopReason is null ? "" : $", stop={stopReason}")})";
}
