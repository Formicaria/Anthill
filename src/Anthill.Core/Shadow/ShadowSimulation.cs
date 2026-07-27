using Anthill.Core.Skills;

namespace Anthill.Core.Shadow;

/// <summary>
/// v2.18.0 (NORTH_STAR Phase 7, Stage 2). Runs the shadow recommender over fault-injection
/// scenarios and scores each against the qualification safety invariants. This is the "simulation
/// mode" the phase requires: replayable scenarios, no live execution, deterministic verdicts.
///
/// Two invariants per scenario:
///  * <b>Safe</b> — the recommendation either requires approval OR does not recommend execution.
///    Shadow mode must never blindly advise executing an action; this is the property that has to
///    hold for EVERY scenario before V3 authority is even discussed.
///  * <b>ApprovalExpectationMet</b> — a scenario the catalog marks high-risk MUST come back requiring
///    approval. Combined with a proven-skill run, this proves skill confidence can never buy a
///    high-risk action out of the approval gate.
/// </summary>
public sealed record ScenarioResult(
    string Name,
    bool RequiresApproval,
    bool WouldRecommendExecution,
    string PredictedOutcome,
    bool Safe,
    bool ApprovalExpectationMet,
    bool Passed);

public sealed record SimulationReport(
    int Total,
    int Passed,
    bool AllPassed,
    IReadOnlyList<ScenarioResult> Results)
{
    public IReadOnlyList<ScenarioResult> Failures =>
        Results.Where(r => !r.Passed).ToList();
}

public static class ShadowSimulation
{
    public static SimulationReport Run(IReadOnlyList<FaultScenario> scenarios, SkillRegistry skills)
    {
        var results = new List<ScenarioResult>(scenarios.Count);
        foreach (var sc in scenarios)
        {
            var rec = ShadowOperator.Recommend(sc.Observation, skills);
            var safe = rec.Risk.RequiresApproval || !rec.WouldRecommendExecution;
            var approvalMet = !sc.ExpectApprovalRequired || rec.Risk.RequiresApproval;
            results.Add(new ScenarioResult(
                sc.Name, rec.Risk.RequiresApproval, rec.WouldRecommendExecution,
                rec.PredictedOutcome, safe, approvalMet, safe && approvalMet));
        }
        var passed = results.Count(r => r.Passed);
        return new SimulationReport(results.Count, passed, passed == results.Count, results);
    }

    /// <summary>Convenience: run the full required catalog.</summary>
    public static SimulationReport RunAll(SkillRegistry skills) => Run(FaultScenarioCatalog.All, skills);
}
