using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.1.0 (ADR-001) — planning behind an interface.
///
/// The extraction is not cosmetic: planning was written twice, once in <c>Queen.RunMission</c> and
/// once in <c>Queen.PlanPreview</c>, and the copies had already diverged. The preview omitted the
/// authorization step entirely. These tests pin that both surfaces now run the SAME construction,
/// and that the one remaining difference is the deliberate, named one.
/// </summary>
[Collection("specialist-gates")]
public class PlanningServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_planning_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteMemory _memory;
    private readonly PlanningService _planning;

    public PlanningServiceTests()
    {
        AnthillRuntime.Initialize();
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "plan.db"));
        var tools = new ToolRegistry(_memory);
        // useOllama:false — the deterministic fallback plan. A planning test that needed a live
        // model would be testing the model, and would not run in CI at all.
        _planning = new PlanningService(new Planner(useOllama: false, router: null), _memory, tools,
            () => _memory.LoadSkillRegistry());
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MissionContext Context(string goal) =>
        MissionContext.ForMission(new Mission { Goal = goal });

    /// <summary>
    /// The whole point of the extraction. There is ONE plan construction, so preview and dispatch
    /// cannot describe different plans — a preview that shows something other than what would run
    /// is worse than no preview, because an operator approves the plan they were shown.
    ///
    /// This is a structural guard, not a behavioural one: it asserts the interface offers no way to
    /// ask for a plan with admission skipped. That capability existed and was the bug.
    /// </summary>
    [Fact]
    public void ThereIsOnlyOnePlanConstruction_WithNoWayToSkipAdmission()
    {
        var methods = typeof(IPlanningService).GetMethods().Select(m => m.Name).ToList();
        Assert.Equal(new[] { nameof(IPlanningService.CreatePlan) }, methods);

        // And no parameter can turn the authorization step off.
        var parameters = typeof(IPlanningService)
            .GetMethod(nameof(IPlanningService.CreatePlan))!
            .GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal(new[] { typeof(MissionContext) }, parameters);
    }

    /// <summary>
    /// Determinism of shape: planning the same goal twice yields the same steps. Preview and
    /// dispatch run this same construction, so agreement between them follows from it.
    /// </summary>
    [Fact]
    public void PlanningTheSameGoalTwice_ProducesTheSameTaskShape()
    {
        const string goal = "research the parser and summarise the findings";

        var first = _planning.CreatePlan(Context(goal));
        var second = _planning.CreatePlan(Context(goal));

        Assert.Equal(first.Select(t => t.AssignedAnt), second.Select(t => t.AssignedAnt));
        Assert.Equal(first.Select(t => t.TaskType), second.Select(t => t.TaskType));
        Assert.Equal(first.Select(t => t.AssignedWorker), second.Select(t => t.AssignedWorker));
        Assert.Equal(first.Select(t => t.DependsOn.Count), second.Select(t => t.DependsOn.Count));
    }

    /// <summary>
    /// Every planned task carries a resolved worker. A task without one reaches the runtime with no
    /// permission boundary to enforce, which is the condition AntRuntime.Resolve refuses — better
    /// to establish it at planning than to discover it at dispatch.
    /// </summary>
    [Fact]
    public void EveryPlannedTask_HasAResolvedWorker()
    {
        var tasks = _planning.CreatePlan(Context("research the parser and summarise the findings"));

        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.AssignedWorker)));
    }

    /// <summary>
    /// A read-only mission must never be planned work it is forbidden to do. The planner enforces
    /// this, and the admission gate is the second, independent check — so this holds even if a
    /// future planner (or a model) proposes a coder task anyway.
    /// </summary>
    [Fact]
    public void AReadOnlyGoal_IsNeverPlannedAnAdmittedCoderTask()
    {
        var tasks = _planning.CreatePlan(
            Context("verify the parser only; no patches and do not modify files"));

        Assert.DoesNotContain(tasks, t => t.AssignedAnt == "coder" && t.Status != TaskStatus.Failed);
        Assert.DoesNotContain(tasks, t => t.TaskType == "patch_proposal" && t.Status != TaskStatus.Failed);
    }

    /// <summary>
    /// A refused task carries the reason, and the plan can report it without anyone re-running the
    /// gate. The plan-preview endpoint used to rebuild this list by calling ValidateTask again over
    /// tasks it had just received — a second reading of one plan, free to disagree with the first.
    /// </summary>
    [Fact]
    public void ThePlanReportsItsOwnRefusals_WithoutReRunningTheGate()
    {
        var context = Context("audit only: review the scheduler and report");
        var plan = new MissionPlan(_planning.CreatePlan(context), context.Constraints, SpecIngestion: false);

        // Whatever the planner produced, the plan's own account of what was refused must match the
        // tasks it is carrying — the endpoint renders THIS, not a recomputation.
        Assert.Equal(
            plan.Tasks.Where(t => t.FailureType == PlanningService.AdmissionRefusedFailureType).ToList(),
            plan.Refused);
        Assert.All(plan.Refused, t => Assert.False(string.IsNullOrWhiteSpace(t.FailureReason)));
        Assert.Equal(plan.Refused.Select(t => t.FailureReason).Distinct(), plan.RefusalReasons);

        // And the gate the plan applied is the real one: a coder patch task is refused here.
        var coderPatch = new DomainTask
        {
            AssignedAnt = "coder", AssignedWorker = "coder.backend_coder", TaskType = "patch_proposal",
            Title = "propose a change",
        };
        Assert.False(AntRegistry.ValidateTask(coderPatch, context.Constraints).Allowed);
    }

    /// <summary>
    /// Dependency wiring belongs to the plan, not to the mission that later runs it: a verifier
    /// must depend on the work it verifies whether the plan came from dispatch or preview.
    /// </summary>
    [Fact]
    public void AutoWiring_MakesTheVerifierDependOnUpstreamWork()
    {
        var mission = new Mission { Goal = "g" };
        var research = new DomainTask { Title = "r", AssignedAnt = "researcher" };
        var build = new DomainTask { Title = "b", AssignedAnt = "builder" };
        var verify = new DomainTask { Title = "v", AssignedAnt = "verifier" };
        mission.Tasks.AddRange(new[] { research, build, verify });

        PlanningService.AutoWireDependencies(mission);

        Assert.Empty(research.DependsOn);                 // sources have no upstream
        Assert.Contains(research.Id, build.DependsOn);
        Assert.Contains(research.Id, verify.DependsOn);
        Assert.Contains(build.Id, verify.DependsOn);
    }

    /// <summary>An explicit dependency from the planner is never overwritten by the defaults.</summary>
    [Fact]
    public void AutoWiring_RespectsExplicitDependencies()
    {
        var mission = new Mission { Goal = "g" };
        var research = new DomainTask { Title = "r", AssignedAnt = "researcher" };
        var verify = new DomainTask { Title = "v", AssignedAnt = "verifier", DependsOn = { "explicit-id" } };
        mission.Tasks.AddRange(new[] { research, verify });

        PlanningService.AutoWireDependencies(mission);

        Assert.Equal(new[] { "explicit-id" }, verify.DependsOn);
    }
}
