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
    /// The whole point of the extraction. Preview and dispatch must agree about WHICH tasks a goal
    /// produces — a preview that shows a different plan than the one that would run is worse than
    /// no preview, because an operator approves the plan they were shown.
    /// </summary>
    [Fact]
    public void PreviewAndDispatch_ProduceTheSameTaskShape()
    {
        const string goal = "research the parser and summarise the findings";

        var dispatch = _planning.CreatePlan(Context(goal));
        var preview = _planning.PreviewPlan(Context(goal));

        Assert.Equal(dispatch.Select(t => t.AssignedAnt), preview.Select(t => t.AssignedAnt));
        Assert.Equal(dispatch.Select(t => t.TaskType), preview.Select(t => t.TaskType));
        Assert.Equal(dispatch.Select(t => t.AssignedWorker), preview.Select(t => t.AssignedWorker));
        Assert.Equal(dispatch.Select(t => t.DependsOn.Count), preview.Select(t => t.DependsOn.Count));
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
    /// The named difference: dispatch applies the registry's verdict, preview does not. Pinned so
    /// the discrepancy stays a decision with a reason rather than becoming folklore — v3.8.0's
    /// workflow templates are where it gets resolved.
    /// </summary>
    [Fact]
    public void OnlyDispatch_AppliesTheAdmissionVerdict()
    {
        var context = Context("audit only: review the scheduler and report");
        var rejected = new DomainTask
        {
            AssignedAnt = "coder", AssignedWorker = "coder.backend_coder", TaskType = "patch_proposal",
            Title = "propose a change",
        };

        // The gate the dispatch path applies, exercised directly on a task the planner would not
        // itself produce — this is what CreatePlan runs and PreviewPlan skips.
        Assert.False(AntRegistry.ValidateTask(rejected, context.Constraints).Allowed);
        Assert.All(_planning.PreviewPlan(context), t => Assert.False(t.FailureType == "ant_permission_denied"));
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
