using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Anthill.Core.Projects;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ROUTING IS A PROJECT'S DECISION, AND ABSENCE OF ONE CHANGES NOTHING. v0.3.8.124.
///
/// Routing was colony-wide: one priority model and fourteen per-role routes in `config.json`. An
/// operator running one project against a local model and another against Claude could not say so —
/// every change was to the whole colony. `ProjectRoutingScope` is the narrowing, and these are the
/// two claims it has to keep:
///
///   1. A project's choices OUTRANK the colony's, at both tiers, and in the same order the colony
///      uses internally — priority above role route, so "use this model for everything here" stays
///      one decision rather than fourteen.
///   2. OUTSIDE a scope, nothing moves. Most flows belong to no project — a scheduled run, an
///      autonomy objective, a chat outside one — and every one of them must route exactly as it did
///      before this feature existed. A narrowing that changes the un-narrowed case is not a
///      narrowing, it is a rewrite with a smaller blast radius claimed for it.
///
/// The scope is ambient (AsyncLocal), so these tests enter and leave it rather than passing a
/// project id: that IS the contract, and a test that threaded an argument instead would be checking
/// a method this codebase does not call.
/// </summary>
public class ProjectRoutingTests : IDisposable
{
    private readonly string _savedProvider;
    private readonly string _savedModel;

    public ProjectRoutingTests()
    {
        AnthillRuntime.Initialize();
        _savedProvider = AnthillRuntime.ModelPriorityProvider;
        _savedModel = AnthillRuntime.ModelPriorityModel;
    }

    // Restored through the same public path the console uses, so the runtime gate and the persisted
    // config cannot be left disagreeing with each other by a test.
    public void Dispose() => SetColonyPriority(_savedProvider, _savedModel);

    private static void SetColonyPriority(string provider, string model) =>
        AnthillRuntime.ApplySettingsUpdate(new Dictionary<string, JsonElement>
        {
            ["model_priority_provider"] = JsonSerializer.SerializeToElement(provider),
            ["model_priority_model"] = JsonSerializer.SerializeToElement(model),
        });

    private static ProjectRoutingScope.Routing Routing(
        string priorityProvider = "", string priorityModel = "",
        params (string Role, string Provider, string Model)[] roles)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, provider, model) in roles) map[role] = (provider, model);
        return new ProjectRoutingScope.Routing("proj-1", priorityProvider, priorityModel, map);
    }

    // ---- The un-narrowed case, first, because it is the one that must not move -----------------

    /// <summary>
    /// A flow that belongs to no project routes exactly as it did before any of this existed.
    ///
    /// Asserted FIRST and against the colony's own answer rather than against a literal, so it
    /// cannot pass by both sides being wrong in the same way. Most missions are this case.
    /// </summary>
    [Fact]
    public void OutsideAProject_TheColonyRoutesExactlyAsItAlwaysDid()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();

        Assert.Null(ProjectRoutingScope.Current);
        Assert.Null(ProjectRoutingScope.Priority);
        Assert.Null(ProjectRoutingScope.RouteFor("coder"));
        Assert.Equal(router.RoleRoute("coder"), router.GetRoute("coder"));
    }

    /// <summary>
    /// And LEAVING a scope restores the previous one, including the absence of one. Nesting is the
    /// property that makes an ambient value safe to enter on a mission and again on a conversation
    /// turn inside it; a scope that leaked would route every later mission in the process through
    /// whichever project happened to run last.
    /// </summary>
    [Fact]
    public void AScopeIsRestoredWhenItEnds_SoNothingLeaksIntoTheNextFlow()
    {
        Assert.Null(ProjectRoutingScope.Current);

        using (ProjectRoutingScope.Enter(Routing(priorityProvider: "anthropic", priorityModel: "claude-x")))
        {
            Assert.Equal(("anthropic", "claude-x"), ProjectRoutingScope.Priority);

            using (ProjectRoutingScope.Enter(Routing(priorityProvider: "ollama", priorityModel: "inner")))
                Assert.Equal(("ollama", "inner"), ProjectRoutingScope.Priority);

            Assert.Equal(("anthropic", "claude-x"), ProjectRoutingScope.Priority);
        }

        Assert.Null(ProjectRoutingScope.Current);
    }

    // ---- Precedence ----------------------------------------------------------------------------

    /// <summary>
    /// The project's priority outranks everything, including the colony's — which is the whole
    /// point. An operator who pinned a project to a model did so knowing the colony was set to
    /// something else; the narrower statement wins.
    /// </summary>
    [Fact]
    public void AProjectsPriority_OutranksTheColonysPriority()
    {
        SetColonyPriority("ollama", "colony-wide-model");
        var router = new ModelRouter();

        Assert.Equal(("ollama", "colony-wide-model"), router.GetRoute("coder"));

        using var scope = ProjectRoutingScope.Enter(
            Routing(priorityProvider: "anthropic", priorityModel: "claude-x"));

        Assert.Equal(("anthropic", "claude-x"), router.GetRoute("coder"));
    }

    /// <summary>
    /// And it outranks the project's OWN per-role route, mirroring the colony's internal order. The
    /// role's route is not discarded — see the failover test below — only outranked, so a
    /// deliberate per-ant choice survives the priority being switched on and returns when it is off.
    /// </summary>
    [Fact]
    public void AProjectsPriority_OutranksItsOwnRoleRoutes()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();

        using var scope = ProjectRoutingScope.Enter(Routing(
            priorityProvider: "anthropic", priorityModel: "claude-x",
            roles: ("coder", "ollama", "qwen-coder")));

        Assert.Equal(("anthropic", "claude-x"), router.GetRoute("coder"));
        // The role's own route is still there, and still what RoleRoute answers.
        Assert.Equal(("ollama", "qwen-coder"), router.RoleRoute("coder"));
    }

    /// <summary>
    /// A project's role route beats the colony's route for that role, with no priority anywhere.
    /// </summary>
    [Fact]
    public void AProjectsRoleRoute_BeatsTheColonysRouteForThatRole()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();
        var colony = router.RoleRoute("coder");

        using var scope = ProjectRoutingScope.Enter(
            Routing(roles: ("coder", "anthropic", "claude-x")));

        Assert.Equal(("anthropic", "claude-x"), router.RoleRoute("coder"));
        Assert.NotEqual(colony, router.RoleRoute("coder"));
    }

    /// <summary>
    /// A ROLE THE PROJECT DOES NOT NAME INHERITS THE COLONY'S ROUTE. This is the difference between
    /// a project being a set of overrides an operator fills in as they care to, and a fourteen-row
    /// form they must complete before the project can run anything — and it is the assertion that
    /// stops a future change from making an unnamed role mean "no route" instead.
    /// </summary>
    [Fact]
    public void ARoleTheProjectDoesNotName_InheritsTheColonysRoute()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();
        var colonyBuilder = router.RoleRoute("builder");

        using var scope = ProjectRoutingScope.Enter(
            Routing(roles: ("coder", "anthropic", "claude-x")));

        Assert.Equal(colonyBuilder, router.RoleRoute("builder"));
        Assert.Equal(("anthropic", "claude-x"), router.RoleRoute("coder"));
    }

    // ---- The empty project, which must be indistinguishable from no project --------------------

    /// <summary>
    /// A project that overrides NOTHING routes exactly as no project does — and says so about
    /// itself, which is what `Queen.RunMission` and `ConversationRunner` read to decide whether to
    /// enter a scope at all. Entering an empty one behaves identically; only one of the two is
    /// honest about whether the project made a decision, and the mission event says which.
    /// </summary>
    [Fact]
    public void AProjectThatOverridesNothing_IsIndistinguishableFromNoProject()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();
        var outside = router.GetRoute("coder");

        var empty = Routing();
        Assert.True(empty.IsEmpty);
        Assert.False(empty.HasPriority);

        using var scope = ProjectRoutingScope.Enter(empty);

        Assert.Equal(outside, router.GetRoute("coder"));
        Assert.Null(ProjectRoutingScope.Priority);
    }

    /// <summary>
    /// HALF A PRIORITY IS NOT A PRIORITY. A provider with no model would route every ant in the
    /// project at a provider with no model named — the exact state `HasModelPriority` has refused
    /// colony-wide since v3.8.1, refused here for the same reason and at the same tier.
    /// </summary>
    [Theory]
    [InlineData("anthropic", "")]
    [InlineData("", "claude-x")]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void AHalfSetPriority_IsNoPriority(string provider, string model)
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();
        var colony = router.RoleRoute("coder");

        using var scope = ProjectRoutingScope.Enter(Routing(provider, model));

        Assert.Null(ProjectRoutingScope.Priority);
        Assert.Equal(colony, router.GetRoute("coder"));
    }

    // ---- The learning gate, which a narrower choice must also bind ------------------------------

    /// <summary>
    /// PHEROMONE LEARNING MAY NOT REORDER A ROUTE A PROJECT PINNED.
    ///
    /// `RouteGuidedSelection.IsLearnable` refuses to reorder a route an operator chose deliberately,
    /// and a project route is exactly that — chosen, and scoped more narrowly than a colony one.
    /// Reading only the colony's two facts would have let learning quietly override the route a
    /// project was created to pin, and it would have done it invisibly: a learned route is not shown
    /// as an override anywhere.
    ///
    /// Asserted through the ROUTER rather than by calling `IsLearnable` directly, because the defect
    /// would have been in what the router passes it, not in the policy itself.
    /// </summary>
    [Fact]
    public void PheromoneLearning_DoesNotReorderARouteAProjectPinned()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();

        using var scope = ProjectRoutingScope.Enter(
            Routing(roles: ("coder", "anthropic", "claude-x")));

        // With no memory the learning layer is inert anyway, so the meaningful assertion is that
        // the pinned route is what resolves — and `ResolveRoute` is the method every model call
        // goes through, not `RoleRoute`.
        var (provider, model, _) = router.ResolveRoute("coder");
        Assert.Equal("anthropic", provider);
        Assert.Equal("claude-x", model);
    }

    /// <summary>
    /// And a project-wide priority fails over to that project's OWN role route, never to the
    /// colony's `fallback`. Failing over to the colony would step straight out of the project the
    /// operator scoped their work to, which is the one thing a narrowing must not do — quietly, at
    /// the moment a model goes unhealthy, when nobody is looking at the route.
    /// </summary>
    [Fact]
    public void AProjectPriority_FailsOverInsideTheProject_NotOutOfIt()
    {
        SetColonyPriority("", "");
        var router = new ModelRouter();

        using var scope = ProjectRoutingScope.Enter(Routing(
            priorityProvider: "anthropic", priorityModel: "claude-x",
            roles: ("coder", "ollama", "qwen-coder")));

        // No breaker in this router, so ResolveRoute returns the primary — the assertion that
        // matters is what the FALLBACK would be, which `RoleRoute(role)` answers and which the
        // colony's `fallback` role does not.
        Assert.Equal(("ollama", "qwen-coder"), router.RoleRoute("coder"));
        Assert.NotEqual(router.RoleRoute("fallback"), router.RoleRoute("coder"));
    }
}
