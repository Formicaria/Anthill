using Anthill.Core.Models;

namespace Anthill.Core.Agents;

/// <summary>One role's contract measured against the model it is actually routed to.</summary>
public sealed record ModelFitness(
    string RoleId,
    string Provider,
    string Model,
    IReadOnlyList<string> Unmet)
{
    public bool Fit => Unmet.Count == 0;
}

/// <summary>
/// v3.4.2 (ADR-003) — does the model this role is routed to actually do what the role needs?
///
/// This is the join the capability model was built for. v3.3.0 learned what each model CAN do;
/// <see cref="ModelRequirement"/> says what each role NEEDS; until something compared them, both
/// were facts nobody acted on.
///
/// The reason it has to be a startup check rather than a runtime one: EVERY mismatch here fails
/// silently at runtime. A model that cannot call tools is never shown them and answers from priors.
/// A model without structured output returns prose where a schema was expected, and the parse
/// produces an empty result rather than an error. A context window too small truncates the input and
/// answers confidently about the part that fit. None of these throw, none open a circuit breaker,
/// and none look like configuration problems in a transcript — they look like a weak model. An
/// operator can only act on this if they are told BEFORE the mission, which is what this is for.
///
/// Reports, never substitutes. The router owns routing; a second component quietly redirecting a
/// role would be a competing routing policy, and two policies that disagree are worse than one that
/// is wrong.
/// </summary>
public static class AntModelFitness
{
    /// <summary>
    /// Check one contract against one model's capabilities.
    ///
    /// An UNKNOWN capability set is fail-closed by construction: <see cref="ModelCapabilities"/>
    /// hands back <c>TextOnly</c> for a model nothing has described, so an undiscovered model
    /// reports as unfit rather than being assumed adequate. Under-claiming costs an operator a
    /// warning they can dismiss; over-claiming costs them a silently wrong mission.
    /// </summary>
    public static IReadOnlyList<string> Unmet(ModelRequirement requirement, ModelCapabilities capabilities)
    {
        if (requirement is null || requirement.IsEmpty) return Array.Empty<string>();
        var unmet = new List<string>();

        if (requirement.ToolCalling && !capabilities.ToolCalling)
            unmet.Add("tool calling — the model will never be shown the role's tools and will answer from priors");

        if (requirement.StructuredOutput && !capabilities.StructuredOutput)
            unmet.Add("structured output — the role's result is parsed as a schema, and prose parses to an empty result");

        if (requirement.Reasoning && !capabilities.Reasoning)
            unmet.Add("reasoning — the role infers from evidence that does not state its own conclusion");

        // Unknown context is NOT reported as too small. Absence of a fact is not the fact of a
        // limit, and warning about every model whose window nobody has published would train an
        // operator to ignore this report — which is the only way it can fail.
        if (requirement.MinContextTokens is { } needed
            && capabilities.ContextWindowTokens is { } actual && actual < needed)
            unmet.Add($"context window — needs at least {needed:N0} tokens, this model reports {actual:N0}");

        return unmet;
    }

    /// <summary>
    /// Every contracted role checked against its live route.
    ///
    /// Ordered by role for determinism: a startup report whose lines move between runs cannot be
    /// diffed, and a report nobody can diff is a report nobody reads.
    /// </summary>
    public static IReadOnlyList<ModelFitness> CheckAll(
        ModelRouter router,
        IReadOnlyDictionary<string, AntExecutionContract> contracts)
    {
        var report = new List<ModelFitness>();
        if (router is null || contracts is null) return report;

        foreach (var (roleId, contract) in contracts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // A role that makes no model calls has no route worth reporting on. Including it would
            // pad the report with rows that can never be actionable.
            if (!contract.AllowsModelCalls || contract.ModelNeeds.IsEmpty) continue;

            var (provider, model, _) = router.ResolveRoute(roleId);
            var unmet = Unmet(contract.ModelNeeds, ModelRouter.CapabilitiesFor(provider, model));
            report.Add(new ModelFitness(roleId, provider, model, unmet));
        }

        return report;
    }
}
