using Anthill.Core.Agents;

namespace Anthill.Core.Pheromones;

/// <summary>One trail's numbers, as a read model. Produced by the memory layer's
/// <c>GetPheromoneTrail</c>; null where no trail has ever been written.</summary>
public sealed record TrailView(double Strength, int SuccessCount, int FailureCount);

/// <summary>
/// v0.3.8.93 — THE FIRST DECISION THE PHEROMONE LAYER ACTUALLY MAKES.
///
/// Trails have been written on every mission since the layer existed — worker reputation, role
/// reputation, route reliability — and consumed by exactly nothing: their entire influence on the
/// colony was a formatted summary pasted into the planner's prompt, where a model may weigh it,
/// ignore it, or hallucinate around it. A learning system whose output is decoration is
/// indistinguishable from one that does not learn. This class is one deterministic consumer, scoped
/// to one decision: WHICH WORKER of a role takes a task when the task's own text does not say.
///
/// Two rules, in rank order, and the order is the point:
///
///   1. CAPABILITY COMPATIBILITY OUTRANKS STRENGTH. A keyword-decided selection
///      (<see cref="AntRegistry.ResolveWorker"/>) is never consulted here at all — the caller only
///      asks when the registry's capability map had no opinion. However strong the docs_coder's
///      trail, a UI task never reaches this class, so reputation can replace a tie-break and can
///      never replace a capability fact.
///
///   2. VERIFIED TRAILS ONLY. A worker trail qualifies when its strength is above the 0.5 baseline
///      AND its successes outnumber its failures. This is not a heuristic dressed as a threshold:
///      worker-kind trails receive a positive delta from exactly one site
///      (<c>SqliteMemory.UpdateMissionPheromones</c>), and that site pays it only for
///      <c>completed_verified</c> outcomes with positive attribution — unverified completions and
///      partials write nothing, failures subtract. A trail above its starting strength with a
///      positive success balance therefore carries net VERIFIED evidence, by construction of the
///      writer, and there is no other way for one to get there.
///
/// No qualifying trail, or a tie at the top, keeps the caller's default — a guess beaten only by
/// evidence, never by a different guess.
/// </summary>
public static class TrailGuidedSelection
{
    /// <summary>A new trail starts here (see <c>UpdatePheromoneTrail</c>'s insert); only verified
    /// successes push a worker trail above it. Qualification is strictly-greater.</summary>
    public const double BaselineStrength = 0.5;

    /// <summary>The trail key a worker's reputation lives under — the exact key
    /// <c>UpdateMissionPheromones</c> writes, named here so reader and writer cannot drift.</summary>
    public static string TrailKeyFor(AntWorkerDefinition worker) => $"worker:{worker.WorkerId}";

    /// <summary>
    /// The compatible worker with the strongest qualifying verified trail, or null to keep the
    /// caller's default. <paramref name="trail"/> is a lookup rather than a memory reference so the
    /// decision is a pure function of its inputs — replayable in a test from two trail states, which
    /// is how this rule is A/B-proven without running two colonies.
    /// </summary>
    public static AntWorkerDefinition? Prefer(
        IReadOnlyList<AntWorkerDefinition> compatible, Func<string, TrailView?> trail)
    {
        AntWorkerDefinition? best = null;
        var bestStrength = BaselineStrength;   // strictly-greater ⇒ an unwritten trail never wins

        foreach (var worker in compatible)
        {
            if (!worker.Enabled) continue;
            var view = trail(TrailKeyFor(worker));
            if (view is null) continue;
            if (view.SuccessCount <= view.FailureCount) continue;   // net evidence must be positive
            if (view.Strength <= bestStrength) continue;            // ties keep the earlier answer

            best = worker;
            bestStrength = view.Strength;
        }

        return best;
    }
}
