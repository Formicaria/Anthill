using Anthill.Core.SafeAction;
using Anthill.Core.Skills;
using Anthill.Core.Verification;

namespace Anthill.Core.Shadow;

/// <summary>
/// v2.17.0 (NORTH_STAR Phase 7 — Shadow Operations &amp; Operator Qualification, Stage 1).
///
/// Shadow mode is the qualification gate before V3.0 grants any real authority: ANTHILL observes a
/// real incident and produces a full recommendation — diagnosis, proposed action, chosen skill, risk
/// score, predicted outcome, verification plan, rollback plan — and then STOPS. It never executes.
/// The operator later records what was actually done and whether the recommendation was correct, and
/// <see cref="QualificationScoreboard"/> turns those pairs into reliability metrics.
///
/// This engine is deterministic C#: it assembles the bundle from the already-shipped subsystems
/// (<see cref="RiskEngine"/> v2.14, <see cref="VerificationPolicy"/> v2.12, the <see cref="SkillRegistry"/>
/// v2.13, and <see cref="RecoveryOrchestrator"/> v2.14) rather than inventing parallel judgment, so a
/// shadow recommendation is reproducible and never depends on a model at decision time.
/// </summary>
public static class ShadowPrediction
{
    public const string Success = "predicted_success";
    public const string Partial = "predicted_partial";
    public const string Failure = "predicted_failure";
    public const string NeedsApproval = "predicted_needs_approval";
}

/// <summary>An observed situation to reason about. Nothing here is executed.</summary>
public sealed record ShadowObservation(
    string IncidentId,
    string Diagnosis,             // what ANTHILL believes is wrong
    string ProposedOperation,     // e.g. "restart_service", "restore", "modify", "delete"
    string TargetKind,
    string TargetId,
    string TaskType = "",         // maps to VerificationPolicy (code_patch / config_change / ...); falls back to the operation
    string Environment = "",      // environment fingerprint for skill coverage (e.g. "dotnet-9")
    RiskInputs? Risk = null);     // optional full risk inputs; otherwise a minimal set is derived

/// <summary>The non-executing recommendation bundle mandated by the Shadow Operations phase.</summary>
public sealed record ShadowRecommendation(
    string IncidentId,
    string Diagnosis,
    string ProposedAction,
    string? ChosenSkillId,
    double ChosenSkillConfidence,
    RiskAssessment Risk,
    string PredictedOutcome,                 // one of ShadowPrediction.*
    IReadOnlyList<string> VerificationPlan,  // the required verifiers this action WOULD have to pass
    RecoveryAction RollbackPlan,
    string RollbackReason,
    bool WouldRecommendExecution);           // shadow never executes; this is only whether it would advise it

public static class ShadowOperator
{
    /// <summary>Produce a recommendation for an observation. Pure and side-effect free — the whole
    /// point of shadow mode is that this path cannot touch anything.</summary>
    public static ShadowRecommendation Recommend(ShadowObservation obs, SkillRegistry skills)
    {
        var operation = obs.ProposedOperation ?? "";
        var skill = skills.PreferredFor(operation, obs.Environment ?? "");
        var confidence = skill?.Confidence ?? 0;

        // Build risk inputs: honor a caller-supplied set, but always reflect the operation and the
        // skill confidence the registry actually reports (never let a caller assert confidence).
        var baseInputs = obs.Risk ?? new RiskInputs(Operation: operation, Novel: skill is null);
        var risk = baseInputs with
        {
            Operation = string.IsNullOrEmpty(baseInputs.Operation) ? operation : baseInputs.Operation,
            SkillConfidence = confidence,
        };
        var assessment = RiskEngine.Score(risk);

        var plan = VerificationPolicy.For(string.IsNullOrEmpty(obs.TaskType) ? operation : obs.TaskType);

        var securityImplication = IsHighRisk(operation) && !risk.Reversible;
        var recovery = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: risk.HasDeterministicRollback,
            Retryable: false,
            BackupAvailable: risk.BackupAgeDays >= 0,
            FailoverAvailable: false,
            SecurityImplication: securityImplication));

        var predicted = Predict(assessment, confidence, skill);
        var wouldRecommend = !assessment.RequiresApproval && predicted == ShadowPrediction.Success;

        return new ShadowRecommendation(
            obs.IncidentId,
            obs.Diagnosis ?? "",
            $"{operation} on {obs.TargetKind}:{obs.TargetId}",
            skill?.Id,
            confidence,
            assessment,
            predicted,
            plan,
            recovery.Action,
            recovery.Reason,
            wouldRecommend);
    }

    private static bool IsHighRisk(string operation)
    {
        var op = operation.ToLowerInvariant();
        return RiskEngine.HighRiskOperations.Any(h => op.Contains(h));
    }

    /// <summary>Deterministic outcome prediction. Approval-required always dominates; without a
    /// proven skill we predict failure (we have no evidence it works); otherwise confidence and the
    /// risk score set the expectation. A model is never consulted here.</summary>
    private static string Predict(RiskAssessment risk, double confidence, Skill? skill)
    {
        if (risk.RequiresApproval) return ShadowPrediction.NeedsApproval;
        if (skill is null) return ShadowPrediction.Failure;
        if (confidence >= 0.8 && risk.Score < 40) return ShadowPrediction.Success;
        if (confidence >= 0.5) return ShadowPrediction.Partial;
        return ShadowPrediction.Failure;
    }
}
