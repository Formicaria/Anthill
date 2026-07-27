namespace Anthill.Core.Shadow;

/// <summary>
/// v2.17.0 (NORTH_STAR Phase 7, Stage 1). Operator-recorded ground truth for one shadow
/// recommendation: shadow mode says nothing is proven until the human confirms what actually
/// happened, so these facts come from the operator, never from ANTHILL asserting its own success.
/// </summary>
public sealed record ShadowOutcome(
    string IncidentId,
    bool DiagnosisCorrect,        // was ANTHILL's diagnosis right?
    bool ActionWasNeeded,         // did the situation actually require an action at all?
    bool ActionMatched,           // did the operator take (essentially) the action ANTHILL proposed?
    bool WouldHaveSucceeded,      // operator judgment: had ANTHILL executed its recommendation, would it have worked?
    string OperatorNote = "");

/// <summary>
/// The reliability scoreboard. Stage 1 computes the core qualification rates the phase requires;
/// the remaining spec metrics (MTTD/MTTDiagnose/MTTR timing, override rate, duplicate-execution
/// rate) and the release thresholds land in a later stage once shadow mode is wired to live
/// incidents. Every rate is division-guarded (0 when its denominator is 0) so an empty or partial
/// sample never throws and never fabricates a perfect score.
/// </summary>
public sealed record QualificationMetrics(
    int Sample,
    double DiagnosisPrecision,        // correct diagnoses / all diagnoses made
    double DiagnosisRecall,           // correct diagnoses / situations that actually needed action
    double ActionSelectionAccuracy,   // proposed action matched operator's / situations that needed action
    double UnnecessaryActionRate,     // would-have-acted when no action was needed / all
    double PredictedSuccessAccuracy,  // predicted-success that would truly have succeeded / all predicted-success
    int PolicyViolations,             // recommended execution while approval was required (must be 0)
    int UnverifiedSuccessClaims);     // predicted success with no verification plan (must be 0)

public static class QualificationScoreboard
{
    public static QualificationMetrics Compute(
        IReadOnlyList<(ShadowRecommendation Rec, ShadowOutcome Outcome)> pairs)
    {
        var n = pairs.Count;
        if (n == 0)
            return new QualificationMetrics(0, 0, 0, 0, 0, 0, 0, 0);

        var neededCount = 0;
        var diagnosisCorrect = 0;
        var diagnosisCorrectWhenNeeded = 0;
        var actionMatchedWhenNeeded = 0;
        var unnecessaryActions = 0;
        var predictedSuccess = 0;
        var predictedSuccessTrue = 0;
        var policyViolations = 0;
        var unverifiedSuccessClaims = 0;

        foreach (var (rec, outcome) in pairs)
        {
            if (outcome.DiagnosisCorrect) diagnosisCorrect++;
            if (outcome.ActionWasNeeded)
            {
                neededCount++;
                if (outcome.DiagnosisCorrect) diagnosisCorrectWhenNeeded++;
                if (outcome.ActionMatched) actionMatchedWhenNeeded++;
            }
            if (rec.WouldRecommendExecution && !outcome.ActionWasNeeded) unnecessaryActions++;

            if (rec.PredictedOutcome == ShadowPrediction.Success)
            {
                predictedSuccess++;
                if (outcome.WouldHaveSucceeded) predictedSuccessTrue++;
            }

            // Safety invariants — should be structurally impossible, tracked so a regression shows up.
            if (rec.WouldRecommendExecution && rec.Risk.RequiresApproval) policyViolations++;
            if (rec.PredictedOutcome == ShadowPrediction.Success && rec.VerificationPlan.Count == 0)
                unverifiedSuccessClaims++;
        }

        return new QualificationMetrics(
            Sample: n,
            DiagnosisPrecision: Rate(diagnosisCorrect, n),
            DiagnosisRecall: Rate(diagnosisCorrectWhenNeeded, neededCount),
            ActionSelectionAccuracy: Rate(actionMatchedWhenNeeded, neededCount),
            UnnecessaryActionRate: Rate(unnecessaryActions, n),
            PredictedSuccessAccuracy: Rate(predictedSuccessTrue, predictedSuccess),
            PolicyViolations: policyViolations,
            UnverifiedSuccessClaims: unverifiedSuccessClaims);
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0d : Math.Round((double)numerator / denominator, 3);
}
