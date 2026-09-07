using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.138 — the review's item 7: the validated dispatch plan used to be persisted to the event
/// log and then DISCARDED, `PlanningService.CreatePlan` building the executed graph independently.
/// So the one case the pre-dispatch stage exists for — an operator actually requested a workflow —
/// executed whatever the planner invented instead of what was validated, while the record claimed
/// otherwise.
///
/// These tests pin the closure from the producer's side: every DispatchPlan below comes from
/// <see cref="DispatchPlanner.Plan"/> itself, never hand-built to match — the cross-boundary rule.
/// The materialized graph must BE the plan (same task ids, so the persisted record and the executed
/// graph join; same types, roles and declared edges), admitted through the SAME pipeline as
/// planner-authored tasks, with runtime verification policy still applied on top.
/// </summary>
[Collection("specialist-gates")]
public class DispatchPlanMaterializationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "anthill_dispmat_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteMemory _memory;
    // Composed for its side effect, like PlanVerificationPolicyTests: the executor catalog is
    // only populated when a Queen is composed, and EnsurePlanVerification fails CLOSED without it.
    private readonly Queen _queen;
    private readonly PlanningService _planning;

    public DispatchPlanMaterializationTests()
    {
        Anthill.Core.Configuration.AnthillRuntime.Initialize();
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "dispatch.db"));
        _queen = new Queen(_memory);
        _planning = new PlanningService(new Planner(useOllama: false, router: null), _memory,
            new ToolRegistry(_memory), () => _memory.LoadSkillRegistry());
    }

    public void Dispose()
    {
        _queen.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MissionContext Context(string goal, DispatchPlan? plan = null, Mission? mission = null) =>
        MissionContext.ForMission(mission ?? new Mission { Goal = goal }) with { DispatchPlan = plan };

    /// <summary>From the producer, with the producer's own verdict checked first.</summary>
    private static DispatchPlan PlanFor(RequestedWorkflow requested, string goal, string missionId = "msn_dispmat")
    {
        var result = DispatchPlanner.Plan(missionId, goal, requested, nowIso: "2026-09-07T00:00:00Z");
        Assert.True(result.Ok, result.Explanation);
        return result.Plan!;
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE HEADLINE FACT. An operator-requested plan IS the executed graph: same ids (the join),
    /// same types, same roles, the operator's declared edges and nothing invented — and every task
    /// still leaves planning with a resolved worker, because materialization goes through the same
    /// admission pipeline as planner-authored tasks.
    /// </summary>
    [Fact]
    public void AnOperatorRequestedPlan_IsTheExecutedGraph()
    {
        const string goal = "scan the sources, then write it up";
        var plan = PlanFor(new RequestedWorkflow(
            Tasks:
            [
                new RequestedTask("Scan the sources", TaskType: "research"),
                new RequestedTask("Write it up", TaskType: "synthesis", Role: "builder",
                    DependsOn: ["Scan the sources"]),
            ],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null), goal);
        Assert.Equal(DispatchPlanner.Strategies.OperatorRequested, plan.Strategy);

        var tasks = _planning.CreatePlan(Context(goal, plan));

        // Nothing informational is consequential, so no verifier is appended: the graph is the
        // plan's two steps and only them. No section analysis, no planner invention.
        Assert.Equal(2, tasks.Count);

        // The JOIN: executed task ids are the plan's task ids, exactly.
        Assert.Equal(
            plan.Tasks.Select(t => t.TaskId).OrderBy(x => x, StringComparer.Ordinal),
            tasks.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal));

        var scan = Assert.Single(tasks, t => t.Title == "Scan the sources");
        var write = Assert.Single(tasks, t => t.Title == "Write it up");
        Assert.Equal("research", scan.TaskType);
        Assert.Equal("researcher", scan.AssignedAnt);
        Assert.Equal("synthesis", write.TaskType);
        Assert.Equal("builder", write.AssignedAnt);

        // The operator's declared edge, carried by plan id — and no edges anyone else added.
        Assert.Equal(new[] { scan.Id }, write.DependsOn);
        Assert.Empty(scan.DependsOn);

        // Admission ran: the plan names roles, never workers, and every task leaves with one.
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.AssignedWorker)));
    }

    /// <summary>
    /// Runtime policy survives the operator's authorship. The dispatch planner refuses an
    /// operator-authored verifier step precisely because policy inserts one; a consequential
    /// operator plan must therefore still get the policy verifier appended here, wired to the work.
    /// </summary>
    [Fact]
    public void AConsequentialOperatorPlan_StillGetsThePolicyVerifier()
    {
        const string goal = "apply the parser fix";
        var plan = PlanFor(new RequestedWorkflow(
            Tasks: [new RequestedTask("Fix the parser", TaskType: "patch_proposal", Role: "coder")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null), goal);

        var tasks = _planning.CreatePlan(Context(goal, plan));

        var work = Assert.Single(tasks, t => t.AssignedAnt == "coder");
        var verifier = Assert.Single(tasks, t => t.AssignedAnt == "verifier");
        Assert.Equal("verification", verifier.TaskType);
        Assert.True(verifier.Critical);
        Assert.Contains(work.Id, verifier.DependsOn);
    }

    /// <summary>
    /// The ordinary case is untouched. A `planner_chosen` plan constrains nothing — carrying it on
    /// the context must produce exactly the graph the planner path produces without one.
    /// </summary>
    [Fact]
    public void APlannerChosenPlan_LeavesThePlannerPathUnchanged()
    {
        const string goal = "research the parser and summarise the findings";
        var chosen = PlanFor(new RequestedWorkflow(
            Tasks: [], RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null), goal);
        Assert.Equal(DispatchPlanner.Strategies.PlannerChosen, chosen.Strategy);

        var carried = _planning.CreatePlan(Context(goal, chosen));
        var bare = _planning.CreatePlan(Context(goal));

        Assert.Equal(bare.Select(t => t.AssignedAnt), carried.Select(t => t.AssignedAnt));
        Assert.Equal(bare.Select(t => t.TaskType), carried.Select(t => t.TaskType));
        Assert.Equal(bare.Select(t => t.AssignedWorker), carried.Select(t => t.AssignedWorker));
    }

    /// <summary>
    /// The materialization is RECORDED, in the mission's own history — the complement of
    /// `mission_plan_substituted`: that row says the requested plan was not used, this one says it
    /// was, naming the plan's task ids so the claim is checkable against the graph.
    /// </summary>
    [Fact]
    public void TheMaterialization_IsRecordedInTheMissionsHistory()
    {
        const string goal = "scan the sources";
        var mission = new Mission { Goal = goal };
        _memory.SaveMission(mission);

        var plan = PlanFor(new RequestedWorkflow(
            Tasks: [new RequestedTask("Scan the sources", TaskType: "research")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null),
            goal, mission.Id);

        _planning.CreatePlan(Context(goal, plan, mission));

        var rows = _memory.GetRecentEvents(10,
            Anthill.SDK.Events.EventTypes.MissionPlanFromDispatch, mission.Id);
        Assert.Single(rows);
    }

    /// <summary>
    /// And for a mission that does not exist — the preview path — the same call records nothing
    /// and throws nothing, exactly like the substitution event's rule and for the same FK reason.
    /// </summary>
    [Fact]
    public void AnUnpersistedMission_MaterializesQuietly_NoEventRow()
    {
        const string goal = "scan the sources";
        var plan = PlanFor(new RequestedWorkflow(
            Tasks: [new RequestedTask("Scan the sources", TaskType: "research")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null), goal);

        var tasks = _planning.CreatePlan(Context(goal, plan));   // transient mission, never saved

        Assert.Single(tasks);
        Assert.Empty(_memory.GetRecentEvents(10, Anthill.SDK.Events.EventTypes.MissionPlanFromDispatch));
    }
}
