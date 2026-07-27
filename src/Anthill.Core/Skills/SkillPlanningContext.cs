namespace Anthill.Core.Skills;

/// <summary>
/// v2.21.0 Phase C1: certified procedures become visible to the planner — selection only.
///
/// The V2.12 skills line could promote a procedure to Certified on verified evidence, and nothing
/// ever consulted the result. A skill earned standing that changed no decision anywhere. This
/// renders the usable ones into planning context, the same way pheromone trails already are.
///
/// Three boundaries this deliberately does NOT cross:
///
///  - It does not certify. Status is computed by <see cref="SkillRegistry.RecordOutcome"/> from
///    verification evidence; this only reads what that decided.
///  - It does not execute. A skill is offered as a known-good ROUTE for the planner to consider,
///    not a script the runtime runs. Every task it plans still passes the ordinary authorization,
///    contract and permission gates.
///  - It does not offer unproven work. Only Certified and Experimental skills appear, and only
///    within an environment they have actually been proven against — <see cref="Skill.UsableIn"/>
///    is the same coverage rule the rest of the system uses, not a looser one for planning.
///
/// Deterministic: ordered by confidence then id, no model call, no I/O.
/// </summary>
public static class SkillPlanningContext
{
    /// <summary>Skills the planner may legitimately be told about, strongest evidence first.</summary>
    public static IReadOnlyList<Skill> Usable(SkillRegistry? registry, string environment = "", int limit = 8)
    {
        if (registry is null) return Array.Empty<Skill>();

        return registry.All
            .Where(s => s.UsableIn(environment))
            .OrderByDescending(s => s.Confidence)
            .ThenByDescending(s => s.SuccessCount)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }

    /// <summary>
    /// The planning-context block. Says plainly that these are proven routes rather than
    /// instructions, and shows the evidence behind each one — a planner told "certified" without
    /// the count behind it cannot weigh a skill proven three times against one proven thirty.
    /// </summary>
    public static string Format(SkillRegistry? registry, string environment = "", int limit = 8)
    {
        var usable = Usable(registry, environment, limit);
        if (usable.Count == 0) return "(no proven procedures yet)";

        return string.Join("\n", usable.Select(s =>
            $"- {s.Id} [{s.Status}, {s.SuccessCount} verified success(es), confidence {s.Confidence:0.00}]: {s.Purpose}"));
    }
}
