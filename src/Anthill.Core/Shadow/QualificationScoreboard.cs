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

/// <summary>
/// v2.24.0: the timing half of the phase's reliability metrics. Kept separate from the accuracy
/// metrics because they answer a different question and come from a different source — accuracy is
/// judged by the operator, timing is measured from timestamps.
///
/// Reported as a MEDIAN rather than a mean: one incident left open over a weekend would drag an
/// average far enough to make the number meaningless, and the threshold this feeds is about
/// typical behaviour, not total elapsed time.
/// </summary>
public sealed record ShadowTimingMetrics(int Sample, double MedianResolutionSeconds, double P90ResolutionSeconds)
{
    public static readonly ShadowTimingMetrics Empty = new(0, 0, 0);

    /// <summary>
    /// Compute from observed→resolved spans. An empty sample reports zeros rather than throwing or
    /// fabricating — the same division-guard stance the accuracy metrics take, so a system with no
    /// history never reports a flattering number.
    /// </summary>
    public static ShadowTimingMetrics From(IReadOnlyList<double>? seconds)
    {
        if (seconds is null || seconds.Count == 0) return Empty;
        var sorted = seconds.Where(s => s >= 0).OrderBy(s => s).ToList();
        if (sorted.Count == 0) return Empty;
        return new ShadowTimingMetrics(sorted.Count, Percentile(sorted, 0.50), Percentile(sorted, 0.90));
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return Math.Round(sorted[0], 3);
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        var value = lo == hi ? sorted[lo] : sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
        return Math.Round(value, 3);
    }
}

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
