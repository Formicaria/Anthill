using Anthill.Core.Outcomes;

namespace Anthill.Core.Autonomy;

/// <summary>
/// v2.22.0 Phase C3: what an objective has actually ACHIEVED, derived from the evidence of its
/// runs rather than from how many times it ran.
///
/// The defect this closes: `RecordObjectiveRunOutcome` moves an objective to `Done` the moment
/// `RunCount >= MaxRuns`. An objective that failed every single attempt therefore finishes in
/// exactly the same state as one that succeeded on the first — "Done" meant *the budget ran out*,
/// not *the goal was met*. Reporting could not tell achievement from exhaustion, and neither could
/// the follow-up and lifecycle logic reading that status.
///
/// Progress is computed from `autonomy_runs`, which already records each run's mission_status.
/// Nothing new is stored: the evidence was always there, it was simply never asked.
/// </summary>
public static class ObjectiveProgress
{
    /// <summary>What an objective's run history proves.</summary>
    public sealed record Summary(int Runs, int VerifiedSuccesses, string? LastVerifiedAt)
    {
        /// <summary>True when at least one run reached a verified success — the only evidence that
        /// the objective's work was ever actually done.</summary>
        public bool Achieved => VerifiedSuccesses > 0;
    }

    /// <summary>
    /// Read the run history. A run counts as a verified success only when its recorded
    /// mission_status resolves to <see cref="MissionOutcome.CompletedVerified"/> — the same rule
    /// the Director, the pheromone engine, and skill credit all use. An objective must not be able
    /// to look achieved under a weaker standard than the one that graded its missions.
    /// </summary>
    public static Summary Assess(IReadOnlyList<Dictionary<string, object?>>? runs)
    {
        if (runs is null || runs.Count == 0) return new Summary(0, 0, null);

        var verified = 0;
        string? lastVerifiedAt = null;

        foreach (var run in runs)
        {
            var status = run.GetValueOrDefault("mission_status")?.ToString();

            // The Director stores the RESOLVED outcome here (ReadOutcome returns
            // MissionOutcome.ResolveFromStatusText), so this reads the same vocabulary the rest of
            // the system grades by — one standard, not a second weaker one for reporting.
            //
            // Rows written before v2.19.0 hold raw statuses like "complete" and fail this check.
            // That is correct and deliberate: a run whose verification cannot be confirmed must not
            // count as an achievement, which is the same fail-closed stance the v2.20.0 learning
            // reset took toward pre-boundary evidence.
            if (!MissionOutcome.IsPositiveSuccess(status)) continue;

            verified++;
            var finished = run.GetValueOrDefault("finished_at")?.ToString();
            if (!string.IsNullOrWhiteSpace(finished)
                && (lastVerifiedAt is null || string.CompareOrdinal(finished, lastVerifiedAt) > 0))
                lastVerifiedAt = finished;
        }

        return new Summary(runs.Count, verified, lastVerifiedAt);
    }

    /// <summary>
    /// The end reason for an objective that has exhausted its run budget. This is the distinction
    /// the old code could not draw: a budget reached WITH a verified success behind it is
    /// completion; a budget reached with none is exhaustion, and calling that "Completed" would
    /// report failure as achievement.
    /// </summary>
    public static string BudgetEndReason(Summary progress) =>
        progress.Achieved ? ObjectiveEndReason.CompletedSuccessfully : ObjectiveEndReason.ExhaustedWithoutSuccess;

    /// <summary>Operator-facing explanation of where an objective actually got to.</summary>
    public static string Explain(Summary progress) => progress switch
    {
        { Runs: 0 } => "never ran",
        { Achieved: true } p => $"{p.VerifiedSuccesses} verified success(es) across {p.Runs} run(s)"
                                + (p.LastVerifiedAt is null ? "" : $", last {p.LastVerifiedAt}"),
        var p => $"{p.Runs} run(s), none verified — the objective was attempted but never achieved",
    };
}
