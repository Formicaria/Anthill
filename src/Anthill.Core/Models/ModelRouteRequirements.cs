using Anthill.Core.Agents;
using Anthill.SDK.Reasoning;

namespace Anthill.Core.Models;

/// <summary>
/// What each ROUTE needs from the model it resolves to. v0.3.8.76 (PLAN.md §2 R1).
///
/// WHY A SECOND TABLE, when <see cref="AntExecutionContract"/> already declares model needs.
/// Because they are not the same set, and the difference was invisible while one table tried to be
/// both. A contract describes an ANT — what a role's handler is permitted and required to do inside
/// a mission. A route is a name the <see cref="ModelRouter"/> resolves to a provider and a model.
/// Most of the time those coincide. Three times they do not, and every one of those three was a
/// hole:
///
///   * `planner` and `strategist` call models on every mission and every autonomy cycle, and have
///     no contract at all — they are not mission roles. <c>AntModelFitness</c> iterated contracts,
///     so the two routes whose failure is most consequential were the two it never graded. The
///     planner turning JSON into prose does not error; it falls back to a static task plan, which
///     reads as a weak model rather than as a route pointed at a model that cannot emit JSON.
///
///   * `scribe` is a route AND an ant, and only the route calls a model:
///     <c>ResultAssembler.ComposeFinalAnswer</c> synthesises the operator's final answer under it,
///     while `ScribeAnt` holds no router. Grading the ant's contract graded the wrong thing under
///     the right name.
///
///   * five ants — soldier, medic, archivist, ui_cartographer, scribe — declared model requirements
///     and hold no router. They were graded, reported UNFIT, and sent operators to change models
///     for roles that ask a model nothing. That is where the colony's "seven roles need a capable
///     model" warning came from.
///
/// THE RULE THIS TABLE EXISTS TO MAKE CHECKABLE: a requirement is only meaningful where a call is
/// actually made. Every entry below names the caller, and <c>ModelRouteRequirementTests</c> asserts
/// both directions — every route string reaching the router appears here, and every route here is
/// reached by something. A requirement nobody consults and a call nobody described are the same
/// defect seen from opposite ends, and this repository has now shipped both.
///
/// NOT A ROUTING POLICY. This says what a route NEEDS; <see cref="ModelRouter"/> decides what it
/// GETS. <c>AntModelFitness</c> reports the gap and changes nothing, for the reason recorded there:
/// two components that both redirect are worse than one that is wrong.
/// </summary>
public static class ModelRouteRequirements
{
    /// <param name="RouteId">The string passed to <see cref="ModelRouter.GenerateTyped"/>.</param>
    /// <param name="Needs">What the model must do for this call to mean anything.</param>
    /// <param name="Caller">Where the call is made, so a reader can check the claim in one hop.</param>
    /// <param name="OnShortfall">
    /// What SILENTLY happens when the model cannot meet the requirement. Every entry has one, and
    /// writing it down is the point: each of these degrades into a plausible answer rather than an
    /// error, which is why an operator has to be told before the mission rather than after it.
    /// </param>
    public sealed record ModelRoute(
        string RouteId, ModelRequirement Needs, string Caller, string OnShortfall);

    public static readonly IReadOnlyDictionary<string, ModelRoute> Routes =
        new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- routes that are also mission ants -------------------------------------------

            ["researcher"] = new("researcher",
                new ModelRequirement(MinContextTokens: 8_000),
                "ResearcherAnt.Execute",
                "the brief is truncated and the mission proceeds on the part that fit"),

            ["web"] = new("web",
                new ModelRequirement(MinContextTokens: 8_000),
                "WebResearchAnt.Execute",
                "several sources and their snippets do not fit at once, and the summary silently "
              + "covers a subset of what was actually fetched"),

            ["coder"] = new("coder",
                new ModelRequirement(StructuredOutput: true, Reasoning: true),
                "CoderAnt.Execute",
                "prose where a patch set was expected — which parses to zero proposals, and a "
              + "zero-proposal result is a failed deliverable rather than an obvious malformation"),

            ["builder"] = new("builder",
                new ModelRequirement(MinContextTokens: 16_000),
                "BuilderAnt.Execute",
                "the assembled deliverable covers part of its inputs and says nothing about the rest"),

            // NO structured-output requirement, and PLAN.md §2 R1 asked for this one to be checked
            // rather than assumed. It was checked, and the requirement was describing a use of the
            // model that no longer exists.
            //
            // The verifier's verdict is DETERMINISTIC: it comes from the evidence store, and the
            // model's reading is recorded beside it as `model_verdict_overridden` when the two
            // disagree — explicitly subordinate, never promoting. What the model produces is the
            // explanation of WHY something looks wrong, which is prose, and which
            // `VerificationVerdict.Parse` reads as prose by design. Demanding a schema for a call
            // whose output is deliberately not parsed as one is the same defect this release is
            // about, and it was the only entry that arrived here already carrying it.
            //
            // The requirement that IS real is room to read the evidence it is explaining.
            ["verifier"] = new("verifier",
                new ModelRequirement(MinContextTokens: 8_000),
                "VerifierAnt.Execute",
                "the explanation covers part of the evidence. The VERDICT is unaffected — v3.8.22's "
              + "rule holds, only deterministic evidence promotes and a DeterministicBlock cannot be "
              + "argued away — so the cost here is an operator reading a partial account of a "
              + "correct decision, never a wrong decision"),

            // ---- routes that are NOT ants, and had no declaration at all until v0.3.8.76 ------

            ["planner"] = new("planner",
                new ModelRequirement(StructuredOutput: true, MinContextTokens: 8_000),
                "Planner.Plan",
                "`Json.ExtractJsonObject` throws, the planner logs and returns FallbackTasks, and "
              + "the mission runs a generic static plan. Nothing fails; the colony just quietly "
              + "stops planning and an operator sees a colony that ignores their goal"),

            ["strategist"] = new("strategist",
                new ModelRequirement(StructuredOutput: true),
                "Strategist.Propose",
                "the objective set falls back, so autonomy proposes nothing and looks idle rather "
              + "than blocked"),

            ["scribe"] = new("scribe",
                new ModelRequirement(MinContextTokens: 16_000),
                "ResultAssembler.ComposeFinalAnswer",
                "the final answer is synthesised from a truncated view of the mission. Presentation "
              + "only — SelectFinalAnswer falls back to the raw result — but it is the text the "
              + "operator reads. NOTE the requirement is context, NOT structured output: prose is "
              + "the deliverable here. The scribe ANT's contract used to demand a schema for a call "
              + "this route makes and that ant does not"),
        };

    public static ModelRoute? For(string? routeId) =>
        routeId is not null && Routes.TryGetValue(routeId, out var r) ? r : null;

    /// <summary>
    /// What a route needs, or <see cref="ModelRequirement.None"/> for a name nobody declared.
    ///
    /// None rather than a throw: <see cref="ModelRouter"/> resolves unknown role names by design
    /// (they fall back to the default route), and a fitness check is not the place to start
    /// rejecting them. The guard that no UNDECLARED route reaches the router is a test, where a new
    /// call site is a thing someone can fix, rather than a runtime failure in front of an operator.
    /// </summary>
    public static ModelRequirement NeedsOf(string? routeId) => For(routeId)?.Needs ?? ModelRequirement.None;
}
