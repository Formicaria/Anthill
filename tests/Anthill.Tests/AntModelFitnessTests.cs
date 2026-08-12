using Anthill.Core.Agents;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.4.2 (ADR-003) — a role's declared model needs, checked against the model it is routed to.
///
/// This closes the roadmap's oldest deferral: the core-ant contracts were held back because they
/// "need to declare required MODEL capabilities, which cannot be expressed until the capability
/// model exists". v3.3.0 built the capability model; this is the other half.
///
/// Why it is worth testing rather than eyeballing: every mismatch this detects fails SILENTLY at
/// runtime. A model that cannot call tools is never shown them and answers from priors. A model
/// without structured output returns prose where a schema was expected, and the parse yields an
/// empty result rather than an error. A context window too small truncates and answers confidently
/// about the part that fit. Nothing throws, nothing opens a breaker, and in a transcript all three
/// look like a weak model rather than a misconfiguration.
/// </summary>
public class AntModelFitnessTests
{
    private static ModelRequirement Needs(bool tools = false, bool structured = false,
        bool reasoning = false, int? context = null) =>
        new(ToolCalling: tools, StructuredOutput: structured, Reasoning: reasoning, MinContextTokens: context);

    // ---- the check itself ------------------------------------------------------------------------

    /// <summary>
    /// The headline case, and the one that already happened on real hardware: a tool-using role on a
    /// text-only model. It must be REPORTED, because at runtime it is invisible.
    /// </summary>
    [Fact]
    public void ARoleNeedingTools_OnATextOnlyModel_IsUnfit()
    {
        var unmet = AntModelFitness.Unmet(Needs(tools: true), ModelCapabilities.TextOnly);

        Assert.Contains(unmet, u => u.Contains("tool calling"));
    }

    [Fact]
    public void ARoleNeedingTools_OnAToolCapableModel_IsFit() =>
        Assert.Empty(AntModelFitness.Unmet(Needs(tools: true), ModelCapabilities.Standard));

    /// <summary>
    /// Fail-closed by construction: an unknown model resolves to TextOnly, so it reports as unfit
    /// rather than being assumed adequate. Under-claiming costs a dismissible warning;
    /// over-claiming costs a silently wrong mission.
    /// </summary>
    [Fact]
    public void AnUndescribedModel_ReportsUnfit_RatherThanBeingAssumedAdequate()
    {
        var unknown = ModelCapabilityCatalog.For("some-provider", "a-model-nobody-has-described");
        Assert.NotEmpty(AntModelFitness.Unmet(Needs(tools: true, structured: true), unknown));
    }

    /// <summary>Every unmet requirement is listed, not just the first — an operator fixes them together.</summary>
    [Fact]
    public void EveryUnmetRequirement_IsReported()
    {
        var unmet = AntModelFitness.Unmet(
            Needs(tools: true, structured: true, reasoning: true), ModelCapabilities.TextOnly);

        Assert.Equal(3, unmet.Count);
    }

    [Fact]
    public void ARoleThatNeedsNothing_IsAlwaysFit() =>
        Assert.Empty(AntModelFitness.Unmet(ModelRequirement.None, ModelCapabilities.TextOnly));

    // ---- context windows: absence is not a limit -------------------------------------------------

    [Fact]
    public void ATooSmallContextWindow_IsReported()
    {
        var small = ModelCapabilities.Standard with { ContextWindowTokens = 8_000 };
        var unmet = AntModelFitness.Unmet(Needs(structured: true, context: 32_000), small);

        Assert.Contains(unmet, u => u.Contains("context window"));
    }

    /// <summary>
    /// The judgement call worth pinning down. A model whose window nobody has published must NOT be
    /// reported as too small: absence of a fact is not the fact of a limit, and warning about every
    /// undescribed model would train an operator to ignore the report — the only way it can fail.
    /// </summary>
    [Fact]
    public void AnUnknownContextWindow_IsNotReportedAsTooSmall()
    {
        var unknown = ModelCapabilities.Standard with { ContextWindowTokens = null };
        Assert.Empty(AntModelFitness.Unmet(Needs(structured: true, context: 200_000), unknown));
    }

    [Fact]
    public void AnAmpleContextWindow_IsFit()
    {
        var big = ModelCapabilities.Standard with { ContextWindowTokens = 128_000 };
        Assert.Empty(AntModelFitness.Unmet(Needs(structured: true, context: 32_000), big));
    }

    // ---- the contracts themselves ----------------------------------------------------------------

    /// <summary>
    /// Every role that calls a model must say what it needs from one. A contract that declares
    /// nothing is not "flexible" — it is a role whose routing can never be checked, which is exactly
    /// the state this increment exists to end.
    /// </summary>
    [Fact]
    public void EveryModelCallingRole_DeclaresWhatItNeeds()
    {
        var silent = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.AllowsModelCalls && kv.Value.ModelNeeds.IsEmpty)
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(silent.Count == 0,
            "These roles call a model but declare no requirements, so their routing can never be "
          + "checked and a mismatch will present as a weak model: " + string.Join(", ", silent));
    }

    /// <summary>
    /// And a role that makes NO model calls must not declare model requirements — a requirement
    /// nothing will ever check is a claim that quietly stops being true.
    /// </summary>
    [Fact]
    public void ADeterministicRole_DeclaresNoModelNeeds()
    {
        var tester = AntExecutionCatalog.ContractFor("tester");

        Assert.NotNull(tester);
        Assert.False(tester!.AllowsModelCalls);
        Assert.True(tester.ModelNeeds.IsEmpty);
    }

    /// <summary>
    /// The ui_cartographer is the sharpest case in the catalog: its entire purpose is walking a
    /// repository with tools, so a route to a model that cannot call them produces a confidently
    /// fabricated map. If any contract declares tool calling, it must.
    /// </summary>
    [Fact]
    public void TheRepositoryWalkingRole_RequiresToolCalling()
    {
        var cartographer = AntExecutionCatalog.ContractFor("ui_cartographer");

        Assert.NotNull(cartographer);
        Assert.True(cartographer!.ModelNeeds.ToolCalling);
    }

    /// <summary>
    /// Roles whose result the colony BRANCHES on must require structured output. Prose parsed as a
    /// schema yields an empty result, and an empty result is read as "found nothing" rather than as
    /// a failure — the prose-derived control flow v3.2.0 spent a whole phase removing.
    /// </summary>
    [Theory]
    [InlineData("soldier")]
    [InlineData("medic")]
    [InlineData("archivist")]
    public void RolesWhoseResultDrivesControlFlow_RequireStructuredOutput(string role)
    {
        var contract = AntExecutionCatalog.ContractFor(role);

        Assert.NotNull(contract);
        Assert.True(contract!.ModelNeeds.StructuredOutput, $"'{role}' result is parsed, not read");
    }

    /// <summary>
    /// Adding the requirement must not have disturbed anything else in the contracts — it is an
    /// optional trailing parameter precisely so the six existing declarations keep their meaning.
    /// </summary>
    [Fact]
    public void AddingModelRequirements_DidNotDisturbTheExistingContracts()
    {
        var tester = AntExecutionCatalog.ContractFor("tester")!;

        Assert.Contains("run_allowlisted_check", tester.AllowedTools);
        Assert.Contains("apply_patch", tester.ForbiddenTools);
        Assert.False(tester.AllowsSideEffects);
        Assert.False(tester.ProducesPatchProposals);
    }

    /// <summary>
    /// v0.3.8.49 (§15) — the two roles whose work is inference from evidence that does not state its
    /// own conclusion: the coder holds a change consistent across several files, the medic infers a
    /// failure's cause from symptoms that never name it. Both declare Reasoning, and on a
    /// completion-only model — which is what a fresh install's sole local default (llama3.1:8b)
    /// reports as — both must be reported UNFIT for reasoning. That unfitness is exactly the signal
    /// the router's reasoning-aware reroute now acts on, instead of letting the role answer fluently
    /// from a model that cannot actually reason. If either role loses its reasoning requirement, the
    /// reroute silently stops protecting it — so this test guards the invariant, not just the value.
    /// </summary>
    [Theory]
    [InlineData("coder")]
    [InlineData("medic")]
    public void TheInferenceRoles_RequireReasoning_AndAreUnfitOnACompletionOnlyModel(string role)
    {
        var contract = AntExecutionCatalog.ContractFor(role);

        Assert.NotNull(contract);
        Assert.True(contract!.ModelNeeds.Reasoning, $"'{role}' infers from evidence and needs reasoning");

        var unmet = AntModelFitness.Unmet(contract.ModelNeeds, ModelCapabilities.TextOnly);
        Assert.Contains(unmet, u => u.Contains("reasoning"));
    }
}
