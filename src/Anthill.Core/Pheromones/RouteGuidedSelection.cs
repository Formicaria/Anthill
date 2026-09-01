namespace Anthill.Core.Pheromones;

/// <summary>
/// v0.3.8.107 — THE SECOND DECISION THE PHEROMONE LAYER MAKES, and the same shape as the first.
///
/// `.93` gave trails one deterministic consumer: which WORKER of a role takes a task when the task's
/// own text does not say. This is its sibling one layer up — which MODEL ROUTE serves a role when
/// the operator has not said. Everything below is `TrailGuidedSelection`'s rules restated for
/// routes, because the rules were right and the subject is the only thing that changed.
///
/// THREE BOUNDS, in rank order, and each answers one clause of the exit gate.
///
///   1. IT NEVER OVERRIDES AUTHORITY. An operator who routed a role explicitly has made a decision;
///      a trail may not revisit it, however strong. This applies ONLY where the role has no entry
///      of its own in `model_routes` and is therefore being served by the `fallback` entry or the
///      built-in default — a route nobody chose for this role in particular. That is the exact
///      analogue of `.93`'s rule that reputation may replace a tie-break and never a fact, and the
///      distinction is already in the data: `RoleRoute` reads the role's own entry first and falls
///      through when there is none.
///
///      A model-priority override is authority too, and a louder kind — see `IsLearnable`.
///
///   2. IT NEVER OVERRIDES COMPATIBILITY. A candidate must satisfy the role's declared
///      `ModelRouteRequirements`, and it must not be a route the circuit breaker currently holds
///      open. A strong trail on a model that cannot do structured output does not make it able to.
///      Compatibility is a fact about the model; reputation is a fact about its history.
///
///   3. IT NEVER OVERRIDES EVIDENCE — it IS the evidence, and only of one kind.
///      <see cref="TrailKind.VerifiedRoute"/> trails are written from one site, on missions
///      that reached `completed_verified`. The per-call `model_route` trail is NOT consulted here
///      and must not be: its positive delta is `result.Ok`, so a model that answers promptly,
///      fluently and wrongly carries a strong one. That distinction is the whole reason this
///      release added a second kind rather than reading the six releases of trails already there.
///
/// NO QUALIFYING TRAIL, OR A TIE, KEEPS THE CONFIGURED ROUTE. A guess is beaten only by evidence,
/// never by a different guess.
/// </summary>
public static class RouteGuidedSelection
{
    /// <summary>A new trail starts at 0.5 (see <c>UpdatePheromoneTrail</c>'s insert); only verified
    /// missions push one above it. Qualification is strictly-greater, so an unwritten trail — and a
    /// route whose only history is failure — can never win.</summary>
    public const double BaselineStrength = TrailGuidedSelection.BaselineStrength;

    /// <summary>
    /// The trail key a verified route lives under. Named here so the writer
    /// (<c>SqliteMemory.CreditVerifiedRoutes</c>) and this reader cannot drift into two spellings —
    /// the defect `TrailGuidedSelection.TrailKeyFor` exists to prevent for workers.
    ///
    /// Deliberately NOT the `model:` prefix `ModelRouter` writes its per-call trail under. Two
    /// different facts under one key would be one number meaning both, which is the ambiguity this
    /// release exists to remove.
    /// </summary>
    public static string TrailKeyFor(string role, string provider, string model) =>
        $"verified_route:{provider}:{model}:{role}";

    /// <summary>One candidate route, with whatever the colony knows about it.</summary>
    /// <param name="Compatible">Whether it satisfies the role's declared requirements AND is not
    /// currently held open by the breaker. Decided by the caller, which owns both facts.</param>
    public sealed record Candidate(string Provider, string Model, bool Compatible);

    /// <summary>
    /// Whether a routing decision for this role is one a trail may influence at all.
    /// </summary>
    /// <param name="roleHasExplicitRoute">True when `model_routes` carries an entry for this role
    /// specifically. An operator's choice is a fact and is never revisited here.</param>
    /// <param name="modelPriorityActive">True when a global model-priority override is set — the
    /// loudest instruction the routing surface has, and one a trail must not quietly undo.</param>
    public static bool IsLearnable(bool roleHasExplicitRoute, bool modelPriorityActive) =>
        !roleHasExplicitRoute && !modelPriorityActive;

    /// <summary>
    /// The compatible candidate with the strongest qualifying verified trail, or null to keep the
    /// caller's configured route.
    ///
    /// <paramref name="trail"/> is a lookup rather than a memory reference so the decision is a pure
    /// function of its inputs — replayable in a test from two trail states, which is how this rule
    /// is proven without running two colonies. The same property `.93` was built for.
    /// </summary>
    public static Candidate? Prefer(
        IReadOnlyList<Candidate> candidates, Func<string, TrailView?> trail, string role)
    {
        if (candidates is null || candidates.Count == 0 || string.IsNullOrWhiteSpace(role)) return null;

        Candidate? best = null;
        var bestStrength = BaselineStrength;   // strictly-greater ⇒ an unwritten trail never wins

        foreach (var candidate in candidates)
        {
            // Compatibility is checked FIRST and is not a tie-break. A route that cannot serve the
            // role is not a weaker option; it is not an option.
            if (!candidate.Compatible) continue;

            var view = trail(TrailKeyFor(role, candidate.Provider, candidate.Model));
            if (view is null) continue;
            if (view.SuccessCount <= view.FailureCount) continue;   // net evidence must be positive
            if (view.Strength <= bestStrength) continue;            // ties keep the earlier answer

            best = candidate;
            bestStrength = view.Strength;
        }

        return best;
    }
}
