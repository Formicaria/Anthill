using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Autonomy;

/// <summary>Why the learning loop retired an objective. Codes: <c>stale_low_success</c>, <c>looping_goals</c>.</summary>
public sealed record RetirementDecision(string Code, string Reason);

/// <summary>
/// Phase 4 learning loop: turns the mission outcomes the colony already records into selection
/// pressure. Three pure functions, all driven by the per-objective success EMA
/// (<see cref="Objective.SuccessEma"/>) that <c>RecordObjectiveRunOutcome</c> maintains:
///
/// - <see cref="UpdateEma"/>: fold a finished run's success score into the objective's EMA.
/// - <see cref="PriorityBias"/>: a bounded read-time bias (±<see cref="AnthillRuntime.AutonomyPriorityBiasMax"/>
///   effective priority points) added during selection — stored priorities never drift, same
///   philosophy as Phase 3's anti-starvation aging.
/// - <see cref="EvaluateRetirement"/>: auto-pause objectives that keep running without producing
///   value (low EMA over enough runs) or that loop on near-identical generated goals. Retirement
///   is a pause + <c>objective_retired</c> event, never a delete — a human reviews and resumes.
///
/// Everything is gated on <see cref="AnthillRuntime.AutonomyLearningEnabled"/>; disabled, the
/// Director behaves exactly as Phase 3 shipped (the EMA is still recorded, it just isn't used).
/// </summary>
public static class ObjectiveLearning
{
    /// <summary>
    /// Folds a run's success score into the objective's EMA. A null score (mission failed before
    /// scoring) counts as 0 — an unscored run is evidence of failure, not missing data. The first
    /// recorded run seeds the EMA directly.
    /// </summary>
    public static double UpdateEma(double? previousEma, double? successScore)
    {
        var score = Math.Clamp(successScore ?? 0.0, 0.0, 1.0);
        if (previousEma is not { } prev) return score;
        var alpha = AnthillRuntime.AutonomyScoreEmaAlpha;
        return Math.Clamp(alpha * score + (1.0 - alpha) * prev, 0.0, 1.0);
    }

    /// <summary>
    /// Read-time effective-priority bias from the success EMA, linear in [-max, +max]:
    /// EMA 1.0 → +max, EMA 0.5 → 0, EMA 0.0 → -max. Zero when learning is disabled, the
    /// objective has no recorded runs yet (null EMA — new objectives start unbiased), or the
    /// bias cap is 0.
    /// </summary>
    public static long PriorityBias(Objective o)
    {
        if (!AnthillRuntime.AutonomyLearningEnabled) return 0;
        if (o.SuccessEma is not { } ema || AnthillRuntime.AutonomyPriorityBiasMax <= 0) return 0;
        return (long)Math.Round((ema - 0.5) * 2.0 * AnthillRuntime.AutonomyPriorityBiasMax);
    }

    /// <summary>
    /// Decides whether an objective should be retired (auto-paused) after its latest run.
    /// <paramref name="recentGoals"/> is the newest-first list of generated goals for this
    /// objective, including the run that just finished. Returns null to keep it running.
    /// Only Active objectives are considered — Done/Paused/Failed are already handled by the
    /// run-budget and circuit-breaker rails.
    /// </summary>
    public static RetirementDecision? EvaluateRetirement(Objective o, IReadOnlyList<string> recentGoals)
    {
        if (!AnthillRuntime.AutonomyLearningEnabled) return null;
        if (o.Status != ObjectiveStatus.Active) return null;

        // Stale: enough runs to judge, and the EMA says they aren't producing value.
        if (o.RunCount >= AnthillRuntime.AutonomyRetireMinRuns &&
            o.SuccessEma is { } ema && ema < AnthillRuntime.AutonomyRetireScoreThreshold)
            return new RetirementDecision("stale_low_success",
                $"Success EMA {ema:0.00} fell below {AnthillRuntime.AutonomyRetireScoreThreshold:0.00} " +
                $"after {o.RunCount} runs — the objective keeps running without producing value.");

        // Looping: the last N generated goals are all near-duplicates of each other. The
        // Strategist's dedup already falls back to charter-as-goal on repeats, so a genuine loop
        // shows up as the same (charter) goal run after run.
        var window = AnthillRuntime.AutonomyLoopWindow;
        if (window >= 2 && recentGoals.Count >= window)
        {
            var newest = recentGoals[0];
            var allSimilar = recentGoals.Skip(1).Take(window - 1)
                .All(prior => GoalSimilarity(newest, prior) >= AnthillRuntime.AutonomyDedupeSimilarity);
            if (allSimilar)
                return new RetirementDecision("looping_goals",
                    $"The last {window} generated goals are near-identical " +
                    $"(≥{AnthillRuntime.AutonomyDedupeSimilarity:P0} keyword overlap) — the objective is looping " +
                    "without discovering new work.");
        }

        return null;
    }

    /// <summary>
    /// Keyword containment similarity between two goals — intersection over the smaller keyword
    /// set, the same metric the Strategist uses for dedup, so "looping" and "duplicate" mean the
    /// same thing everywhere. Returns 0 when either side has no keywords.
    /// </summary>
    public static double GoalSimilarity(string a, string b)
    {
        var ka = TextUtil.ExtractKeywords(a);
        var kb = TextUtil.ExtractKeywords(b);
        if (ka.Count == 0 || kb.Count == 0) return 0.0;
        return ka.Intersect(kb).Count() / (double)Math.Min(ka.Count, kb.Count);
    }
}
