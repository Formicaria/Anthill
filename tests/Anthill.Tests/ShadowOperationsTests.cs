using System.Collections.Generic;
using Anthill.SDK.Actions;
using Anthill.Core.Shadow;
using Anthill.Core.Skills;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.17.0 (NORTH_STAR Phase 7, Stage 1). Shadow mode produces a full recommendation and never
/// executes: a high-risk operation is flagged for approval and never recommended for execution; a
/// proven skill on a low-risk operation predicts success; an unproven operation predicts failure.
/// The qualification scoreboard turns recommendation/outcome pairs into deterministic rates.
/// </summary>
public class ShadowOperationsTests
{
    // ---- ShadowOperator -----------------------------------------------------------------------

    [Fact]
    public void HighRiskOperation_IsFlaggedForApproval_AndNeverRecommendedForExecution()
    {
        var rec = ShadowOperator.Recommend(
            new ShadowObservation("inc-del", "database volume nearly full", "delete", "volume", "db1"),
            new SkillRegistry());

        Assert.True(rec.Risk.RequiresApproval);
        Assert.Equal(ShadowPrediction.NeedsApproval, rec.PredictedOutcome);
        Assert.False(rec.WouldRecommendExecution);
        Assert.NotEmpty(rec.VerificationPlan); // a plan is always produced (at minimum the policy scan)
    }

    [Fact]
    public void ProvenSkill_OnLowRiskOperation_PredictsSuccess_AndWouldRecommend()
    {
        var skills = new SkillRegistry();
        var skill = skills.RegisterCandidate("svc-restart", "restart_service");
        skill.Status = SkillStatus.Certified;
        skill.SuccessCount = 9;
        skill.FailureCount = 1; // derived confidence 0.9

        var obs = new ShadowObservation("inc-crash", "web service crashed", "restart_service", "service", "web",
            Risk: new RiskInputs(Operation: "restart_service", Reversible: true, HasDeterministicRollback: true,
                TargetCriticality: "low", Production: false, BackupAgeDays: 0, Novel: false,
                StrongVerifiers: true, InMaintenanceWindow: true));

        var rec = ShadowOperator.Recommend(obs, skills);

        Assert.Equal("svc-restart", rec.ChosenSkillId);
        Assert.False(rec.Risk.RequiresApproval);
        Assert.Equal(ShadowPrediction.Success, rec.PredictedOutcome);
        Assert.True(rec.WouldRecommendExecution);
    }

    [Fact]
    public void NoProvenSkill_OnLowRiskOperation_PredictsFailure_NotExecution()
    {
        var obs = new ShadowObservation("inc-tidy", "stale log files", "tidy", "logs", "app",
            Risk: new RiskInputs(Operation: "tidy", Reversible: true, HasDeterministicRollback: true,
                TargetCriticality: "low", Production: false, BackupAgeDays: 0, Novel: false,
                StrongVerifiers: true, InMaintenanceWindow: true));

        var rec = ShadowOperator.Recommend(obs, new SkillRegistry());

        Assert.Null(rec.ChosenSkillId);
        Assert.False(rec.Risk.RequiresApproval);
        Assert.Equal(ShadowPrediction.Failure, rec.PredictedOutcome); // no evidence it works
        Assert.False(rec.WouldRecommendExecution);
    }

    // ---- QualificationScoreboard --------------------------------------------------------------

    private static ShadowRecommendation Rec(string id, string predicted, bool wouldRecommend, bool requiresApproval) =>
        new(id, "diag", "op on x:y", "skill", 0.9,
            new RiskAssessment(requiresApproval ? "high" : "low", requiresApproval ? 80 : 5, new List<string>(), requiresApproval),
            predicted, new[] { "build" }, RecoveryAction.ImmediateRollback, "rollback available", wouldRecommend);

    [Fact]
    public void Scoreboard_ComputesCoreRates_Deterministically()
    {
        var pairs = new List<(ShadowRecommendation, ShadowOutcome)>
        {
            (Rec("a", ShadowPrediction.Success, wouldRecommend: true, requiresApproval: false),
             new ShadowOutcome("a", DiagnosisCorrect: true,  ActionWasNeeded: true,  ActionMatched: true,  WouldHaveSucceeded: true)),
            (Rec("b", ShadowPrediction.NeedsApproval, wouldRecommend: false, requiresApproval: true),
             new ShadowOutcome("b", DiagnosisCorrect: false, ActionWasNeeded: true,  ActionMatched: false, WouldHaveSucceeded: false)),
            (Rec("c", ShadowPrediction.Success, wouldRecommend: true, requiresApproval: false),
             new ShadowOutcome("c", DiagnosisCorrect: true,  ActionWasNeeded: false, ActionMatched: false, WouldHaveSucceeded: false)),
        };

        var m = QualificationScoreboard.Compute(pairs);

        Assert.Equal(3, m.Sample);
        Assert.Equal(0.667, m.DiagnosisPrecision, 3);       // 2/3 diagnoses correct
        Assert.Equal(0.5, m.DiagnosisRecall, 3);            // 1 of 2 needed situations diagnosed
        Assert.Equal(0.5, m.ActionSelectionAccuracy, 3);    // 1 of 2 needed actions matched
        Assert.Equal(0.333, m.UnnecessaryActionRate, 3);    // 1 of 3 would-act-when-unneeded (c)
        Assert.Equal(0.5, m.PredictedSuccessAccuracy, 3);   // 1 of 2 predicted-success truly would succeed
        Assert.Equal(0, m.PolicyViolations);                // never recommend execution under approval
        Assert.Equal(0, m.UnverifiedSuccessClaims);         // predicted-success always has a plan
    }

    [Fact]
    public void Scoreboard_EmptySample_IsAllZero_NotDivideByZero()
    {
        var m = QualificationScoreboard.Compute(new List<(ShadowRecommendation, ShadowOutcome)>());
        Assert.Equal(0, m.Sample);
        Assert.Equal(0d, m.DiagnosisPrecision);
        Assert.Equal(0d, m.PredictedSuccessAccuracy);
    }
}
