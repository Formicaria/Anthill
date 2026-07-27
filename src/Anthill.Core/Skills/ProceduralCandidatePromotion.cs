using Anthill.Core.Outcomes;

namespace Anthill.Core.Skills;

/// <summary>
/// v2.23.0 Phase C4: the archivist's procedural candidates reach the skill evaluation pipeline.
///
/// v2.20.0 gave memory candidates a consumer — they became durable events with provenance — but
/// the *procedural* ones went no further. The archivist would observe "this route worked on a
/// verified mission", write it down, and the V2.12 evaluation model would never hear about it. The
/// two halves of learning were both present and not connected.
///
/// What this does, and just as importantly what it does NOT do:
///
///  - A procedural candidate becomes a skill **Candidate**: registered, named, with its route as
///    the procedure. Candidates are usable for nothing. They appear in no plan
///    (<see cref="SkillPlanningContext"/> offers only Certified and Experimental) and confer no
///    permission.
///  - It NEVER certifies, and never records an outcome. Standing is earned only through
///    <see cref="SkillRegistry.RecordOutcome"/> with a promotable verification bundle, exactly as
///    before. A route observed once is a hypothesis, not a proven procedure — treating an
///    observation as evidence is the precise mistake v2.19.0 existed to correct.
///  - Only candidates from a `completed_verified` mission are considered. The archivist already
///    enforces this (procedural candidates are emitted for no other outcome), and it is re-checked
///    here rather than assumed: a defence that lives in one place is a defence that moves.
///
/// So the loop is: observe a verified route → register it as a hypothesis → it earns standing only
/// by being followed and verified again. Nothing skips a step.
/// </summary>
public static class ProceduralCandidatePromotion
{
    public const string ProceduralClass = "procedural_candidate";

    /// <summary>Prefix for generated skill ids, so a promoted route is always identifiable.</summary>
    public const string IdPrefix = "route:";

    /// <summary>
    /// A stable id for a route. Derived from the route itself, so the same route observed on ten
    /// missions converges on ONE skill accumulating evidence, rather than ten single-observation
    /// skills that can never reach certification.
    /// </summary>
    public static string IdFor(string? summary)
    {
        var route = ExtractRoute(summary);
        return route.Length == 0 ? "" : IdPrefix + route.Replace(" -> ", ">").Replace(" ", "_").ToLowerInvariant();
    }

    /// <summary>
    /// The ant sequence out of the archivist's summary, which reads
    /// "Verified route for similar goals: researcher -> coder -> verifier".
    /// </summary>
    public static string ExtractRoute(string? summary)
    {
        var text = summary ?? "";
        var colon = text.LastIndexOf(':');
        var route = (colon >= 0 ? text[(colon + 1)..] : text).Trim();
        // A route must actually name a sequence; a bare sentence is not one.
        return route.Contains("->", StringComparison.Ordinal) ? route : "";
    }

    /// <summary>
    /// Register the mission's procedural candidates as skill candidates. Returns the ids
    /// registered (empty when the mission was not verified or proposed no usable route).
    ///
    /// Idempotent: <see cref="SkillRegistry.RegisterCandidate"/> returns the existing skill rather
    /// than resetting it, so re-observing a route never discards the standing it has earned.
    /// </summary>
    public static IReadOnlyList<string> Register(
        SkillRegistry? registry,
        IReadOnlyList<MemoryCandidateIngest.Candidate>? candidates,
        string missionOutcome)
    {
        if (registry is null || candidates is null || candidates.Count == 0) return Array.Empty<string>();

        // Re-checked here rather than trusted: only a verified mission may propose a route.
        if (!MissionOutcome.IsPositiveSuccess(missionOutcome)) return Array.Empty<string>();

        var registered = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.MemoryClass, ProceduralClass, StringComparison.OrdinalIgnoreCase)) continue;

            var id = IdFor(candidate.Summary);
            if (id.Length == 0) continue;                    // no parseable route — nothing to register

            var route = ExtractRoute(candidate.Summary);

            // RegisterCandidate creates at Candidate status, and returns an EXISTING skill
            // untouched — so a route already earning standing is neither reset nor re-graded by
            // being observed again.
            registry.RegisterCandidate(id,
                $"Observed route: {route}",
                route.Split("->", StringSplitOptions.RemoveEmptyEntries).Select(step => step.Trim()));

            registered.Add(id);
        }
        return registered;
    }
}
