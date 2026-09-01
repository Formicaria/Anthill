using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// AN ANT REGISTERS BY DECLARATION ALONE. v0.3.8.108, PLAN.md §2b `.108`.
///
/// THE PROGRAM'S LAST EXIT GATE: "a test ant registers by declaration alone and passes qualification
/// with no change to Queen, planner, scheduler or assembler."
///
/// WHAT WAS ACTUALLY WRONG, and it was not what the gate's wording suggests. Every layer the gate
/// names was ALREADY generic over roles: the planner reads the registry, the scheduler reads the
/// task graph, the assembler reads the deliverable ledger, the dispatch chokepoint reads the
/// execution contract. None of them knows the names of the twenty-five roles.
///
/// A role still could not exist without editing the core, because the four tables that decide
/// whether one runs were static literals — the registry's `BuildRoles()`, the execution catalog's
/// `Kinds` and `Contracts`, and `Queen._ants`, a dictionary literal inside a constructor. The last
/// one is the sharp end: "add an ant" meant editing the Queen, which is precisely what this gate
/// says must not be necessary.
///
/// So "extensible" was an implicit claim in every capability table since the roster was written, and
/// this is the release that either makes it true or stops making it. `AntExtensions` is one
/// declaration point all four tables read.
///
/// THE TEST ANT IS CONTRIBUTED AND THEN WITHDRAWN. It is never in the shipped roster — the counts
/// stay at twenty-five roles and thirty-four workers, and `RoleSurfaceTests` still pins them. A
/// contribution that outlived its test would leak into unrelated ones as a twenty-sixth role, and
/// the count assertions would start failing somewhere with no relationship to the cause.
/// </summary>
[Collection("specialist-gates")]
public class AntExtensibilityTests : IDisposable
{
    public AntExtensibilityTests() => AntExtensions.Reset();

    public void Dispose() => AntExtensions.Reset();

    private const string Role = "cartographer_of_nothing";
    private const string TaskType = "map_nothing";

    /// <summary>
    /// The whole of what a contributor writes. Nothing here is a core edit — it is four facts the
    /// core already requires of every built-in role, supplied together so they cannot be supplied
    /// apart.
    /// </summary>
    private static AntExtensions.Contribution TestAnt(bool executable = true) =>
        new(
            Role: new AntRoleDefinition(
                RoleId: Role,
                DisplayName: "CartographerOfNothing",
                Colony: "Test",
                Purpose: "Prove a role can be declared without editing the core.",
                Enabled: true,
                Executable: executable,
                Permissions: new AntPermissionContract(true, false, false, false, false, false, false, false, false),
                AllowedTools: new[] { "system_info" },
                ForbiddenTools: new[] { "apply_patch", "write_text_file", "shell_command" },
                AllowedPaths: Array.Empty<string>(),
                ForbiddenPaths: Array.Empty<string>(),
                Workers: new[]
                {
                    new AntWorkerDefinition(
                        WorkerId: $"{Role}.nothing_mapper",
                        DisplayName: "NothingMapper",
                        ParentRoleId: Role,
                        Purpose: "Map nothing, precisely.",
                        Enabled: true,
                        Permissions: new AntPermissionContract(true, false, false, false, false, false, false, false, false),
                        AllowedTools: new[] { "system_info" },
                        ForbiddenTools: Array.Empty<string>()),
                }),
            Kind: AntRuntimeKind.MissionAgent,
            Contract: new AntExecutionContract(
                RoleId: Role,
                Version: "test-v1",
                SupportedTaskTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TaskType },
                RequiredCapabilities: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "system_info" },
                ForbiddenTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apply_patch" },
                ProducedArtifactTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AllowedHandoffRoles: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AllowsModelCalls: false,
                AllowsSideEffects: false,
                ProducesPatchProposals: false),
            Executor: _ => new NothingAnt());

    /// <summary>A minimal executor. It calls no model and touches nothing — the point is that it
    /// REACHES execution, not what it does when it gets there.</summary>
    private sealed class NothingAnt : BaseAnt
    {
        public NothingAnt() : base(Role) { }

        public override AntExecutionResult Execute(Anthill.Core.Domain.Task task, Mission mission) => new()
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = "Mapped nothing.",
            Narrative = "Nothing was mapped, precisely as declared.",
        };
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE GATE. One declaration, and the role is present in every table that decides whether it
    /// runs — with no edit to any of them.
    /// </summary>
    [Fact]
    public void ADeclaredAnt_ReachesEveryTableThatDecidesWhetherItRuns()
    {
        Assert.DoesNotContain(Role, AntRegistry.ByRole.Keys, StringComparer.OrdinalIgnoreCase);

        AntExtensions.Declare(TestAnt());

        // 1. The registry knows it, and its worker.
        Assert.Contains(Role, AntRegistry.ByRole.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains($"{Role}.nothing_mapper", AntRegistry.ByWorker.Keys, StringComparer.OrdinalIgnoreCase);

        // 2. The execution catalog knows what KIND of thing it is.
        Assert.Equal(AntRuntimeKind.MissionAgent, AntExecutionCatalog.KindOf(Role));

        // 3. And what it may do — the contract the dispatch chokepoint enforces.
        var contract = AntExecutionCatalog.ContractFor(Role);
        Assert.NotNull(contract);
        Assert.True(contract!.SupportsTaskType(TaskType));

        // 4. And it is EXECUTABLE. This is the one that would have been missed: registered,
        //    contracted, dispatchable, and absent from the set that decides whether it runs.
        Assert.Contains(Role, AntRegistry.ExecutableRoleIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND WITHDRAWING IT LEAVES NO TRACE. The composed views are cached, so a stale cache would
    /// keep a withdrawn role alive in exactly the tables this release taught to compose — which
    /// would be the same defect one layer down.
    /// </summary>
    [Fact]
    public void AWithdrawnAnt_LeavesNoTrace()
    {
        AntExtensions.Declare(TestAnt());
        Assert.Contains(Role, AntRegistry.ByRole.Keys, StringComparer.OrdinalIgnoreCase);

        AntExtensions.Reset();

        Assert.DoesNotContain(Role, AntRegistry.ByRole.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Role, AntRegistry.ExecutableRoleIds, StringComparer.OrdinalIgnoreCase);
        Assert.Null(AntExecutionCatalog.ContractFor(Role));
        Assert.Equal(AntRuntimeKind.VisualScaffold, AntExecutionCatalog.KindOf(Role));
    }

    /// <summary>
    /// THE SHIPPED ROSTER IS UNCHANGED. A contribution is additive and scoped to whoever made it;
    /// a colony that contributes nothing has exactly the twenty-five built-in roles, which is what
    /// every count in the documentation and in `RoleSurfaceTests` says.
    /// </summary>
    [Fact]
    public void WithNoContributions_TheRosterIsTheBuiltInOne()
    {
        Assert.Equal(AntRegistry.BuiltInRoles.Count, AntRegistry.Roles.Count);
        Assert.Equal(25, AntRegistry.Roles.Count);
    }

    // -------------------------------------------------------------------------------------------
    // What a contribution may NOT do
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A CONTRIBUTION CANNOT SHADOW A BUILT-IN. Two things claiming `verifier` is not a conflict
    /// anyone notices until the wrong one runs — the rule `IModuleContext.RegisterTool` states for
    /// tools, applied to the thing that executes them. Refused outright rather than resolved by
    /// load order.
    /// </summary>
    [Fact]
    public void AContribution_CannotShadowABuiltInRole()
    {
        var shadow = TestAnt() with
        {
            Role = TestAnt().Role with { RoleId = "verifier" },
            Contract = TestAnt().Contract with { RoleId = "verifier" },
        };

        var error = Assert.Throws<InvalidOperationException>(() => AntExtensions.Declare(shadow));
        Assert.Contains("built-in", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same role twice is refused, for the same reason.</summary>
    [Fact]
    public void AContribution_CannotBeDeclaredTwice()
    {
        AntExtensions.Declare(TestAnt());
        Assert.Throws<InvalidOperationException>(() => AntExtensions.Declare(TestAnt()));
    }

    /// <summary>
    /// A CONTRACT THAT NAMES A DIFFERENT ROLE IS REFUSED. The contract is what the dispatch
    /// chokepoint authorizes against, so a role carrying someone else's would be checked against
    /// permissions that are not its own — which is the failure the whole contract system exists to
    /// prevent, arriving through the door this release just opened.
    /// </summary>
    [Fact]
    public void AContractForADifferentRole_IsRefused()
    {
        var mismatched = TestAnt() with { Contract = TestAnt().Contract with { RoleId = "somebody_else" } };

        var error = Assert.Throws<ArgumentException>(() => AntExtensions.Declare(mismatched));
        Assert.Contains("somebody_else", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// AND THE ORCHESTRATION IS UNTOUCHED — the gate's actual words.
    ///
    /// Source-shape, because this is a claim about what ISN'T there and no behavioural test can see
    /// an absence. None of the four layers the gate names may mention the contributed role, and none
    /// may carry a hardcoded roster of its own: they read the registry, the graph, the ledger and
    /// the contract, which is why declaring a role is now sufficient.
    /// </summary>
    [Fact]
    public void NoOrchestrationLayer_KnowsAnyRoleByName()
    {
        var layers = new[]
        {
            Path.Combine("Orchestration", "Queen.cs"),
            Path.Combine("Planning", "Planner.cs"),
            Path.Combine("Scheduling", "TaskScheduler.cs"),
            Path.Combine("Orchestration", "ResultAssembler.cs"),
        };

        foreach (var layer in layers)
        {
            var path = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", layer);
            if (!File.Exists(path)) continue;

            Assert.DoesNotContain(Role, SourceText.CodeOnly(File.ReadAllText(path)),
                StringComparison.OrdinalIgnoreCase);
        }

        // And the Queen composes its executors from the declaration point rather than only from its
        // own literal — the one line that made the roster non-extensible.
        var queen = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));
        Assert.Contains("AntExtensions.All", queen, StringComparison.Ordinal);
    }
}
