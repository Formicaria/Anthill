using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Scheduling;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.21.0 Phase A: handoff ingestion. Specialists have emitted structured handoffs since v2.19.0
/// and the Queen recorded them without acting — HandoffGate.Evaluate had zero production call
/// sites, the codebase's recurring "tested code with no call site" defect. A handoff can now
/// create a real follow-up task, through the same gates a planned task passes.
/// </summary>
[Collection("specialist-gates")]
public class HandoffIngestionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_handoff_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    /// <summary>
    /// The SOURCE task uses a core role that is executable by default. AntRuntime.Resolve throws
    /// for a role whose rollout gate is closed, so a gated source (tester) would fail before
    /// ingestion ever ran — and the test would be measuring the harness, not the gate under test.
    /// The destination role is what these tests actually vary.
    /// </summary>
    private static DomainTask Planned(string ant = "researcher", string type = "research") =>
        new() { Title = "planned", Description = "run the checks", AssignedAnt = ant, TaskType = type };

    // ---- depth is derived from lineage, never self-reported ------------------------------------

    /// <summary>
    /// The defect this guards. EVERY specialist hardcodes `Depth: 1` when building a handoff. If
    /// the orchestrator trusted that number, a handoff from a dynamic task would also arrive at
    /// depth 1, MaxHandoffDepth would never be reached, and unbounded recursive task creation
    /// would be possible — while the gate appeared to be enforcing a limit.
    /// </summary>
    [Fact]
    public void ASpecialistSelfReportsAConstantDepth_WhateverItsContext()
    {
        // A real specialist, run against two different missions: the handoff it builds declares
        // Depth 1 both times. Nothing about the task's position in a handoff chain reaches the ant,
        // so its self-reported depth cannot be the thing that bounds recursion.
        static int DeclaredDepth(string goal)
        {
            var t = new DomainTask { Title = "Review", Description = "adds helper script tools/x.py", AssignedAnt = "soldier", TaskType = "security_review" };
            var m = new Mission { Goal = goal, Tasks = { t } };
            return Assert.Single(new SoldierAnt().Execute(t, m).Handoffs).Depth;
        }

        Assert.Equal(1, DeclaredDepth("first mission"));
        Assert.Equal(1, DeclaredDepth("a mission already three handoffs deep"));

        // Which is why the orchestrator derives depth from the source task's lineage instead.
        Assert.Equal(0, HandoffGate.DepthOf(Planned()));
        Assert.Equal(1, HandoffGate.NextDepthFrom(Planned()));
    }

    [Fact]
    public void ADynamicTasksDepth_IsReadBackFromItsDescription()
    {
        var handoff = new AntHandoff("tester", "medic", "check failed", "failure_diagnosis",
            new[] { "test_report" }, true, 1, "k1");
        var admission = WithMedicOpen(() => HandoffGate.Evaluate(handoff, new Mission()));

        Assert.True(admission.Accepted);
        Assert.Equal(1, HandoffGate.DepthOf(admission.CreatedTask));
        Assert.Equal(2, HandoffGate.NextDepthFrom(admission.CreatedTask));   // the chain advances
    }

    [Fact]
    public void DepthSurvivesPersistence_SoRestartCannotResetTheBound()
    {
        var mem = Memory();
        var mission = new Mission { Goal = "g" };
        mem.SaveMission(mission);
        var dynamic = new DomainTask
        {
            Title = "Handoff: tester -> medic",
            Description = "check failed [handoff dedupe:k1 depth:2]",
            AssignedAnt = "medic", TaskType = "failure_diagnosis",
        };
        mem.SaveTask(mission.Id, dynamic);

        var reloaded = mem.GetTasksForMission(mission.Id).Single();
        var asTask = new DomainTask { Description = reloaded["description"]?.ToString() ?? "" };
        Assert.Equal(2, HandoffGate.DepthOf(asTask));
    }

    [Fact]
    public void MalformedOrAbsentDepthMarkers_ReadAsZero_NotAsAnException()
    {
        Assert.Equal(0, HandoffGate.DepthOf(null));
        Assert.Equal(0, HandoffGate.DepthOf(new DomainTask { Description = "" }));
        Assert.Equal(0, HandoffGate.DepthOf(new DomainTask { Description = "no marker here" }));
        Assert.Equal(0, HandoffGate.DepthOf(new DomainTask { Description = "depth:abc" }));
    }

    // ---- the scheduler admits mid-run tasks safely ---------------------------------------------

    [Fact]
    public void TheSchedulerAcceptsATaskMidRun_AndEvaluatesIt()
    {
        var planned = Planned();
        var scheduler = new TaskScheduler(new List<DomainTask> { planned }, "m1");
        scheduler.Prepare();

        var added = new DomainTask { Title = "dynamic", AssignedAnt = "medic", TaskType = "failure_diagnosis" };
        Assert.True(scheduler.AddDynamicTask(added));
        Assert.Contains(added, scheduler.Tasks);
        Assert.Same(added, scheduler.TaskById[added.Id]);
        Assert.Contains(scheduler.ReadyTasks(), t => t.Id == added.Id);  // ready by the normal rules
    }

    /// <summary>
    /// TaskById deliberately omits duplicated ids so execution can never be ambiguous. Silently
    /// replacing an entry would resurrect exactly that ambiguity, so a duplicate is refused.
    /// </summary>
    [Fact]
    public void TheSchedulerRefusesADuplicateId_RatherThanOverwriting()
    {
        var planned = Planned();
        var scheduler = new TaskScheduler(new List<DomainTask> { planned }, "m1");
        scheduler.Prepare();

        var clash = new DomainTask { Id = planned.Id, Title = "impostor", AssignedAnt = "medic" };
        Assert.False(scheduler.AddDynamicTask(clash));
        Assert.Same(planned, scheduler.TaskById[planned.Id]);
        Assert.Single(scheduler.Tasks);
    }

    // ---- ingestion is gated, and off by default -------------------------------------------------

    [Fact]
    public void HandoffIngestion_IsOffByDefault()
    {
        Assert.False(AnthillRuntime.EnableHandoffIngestion);
        Assert.False(new AnthillConfig().HandoffIngestionEnabled);
    }

    // ---- the call site (the whole point of this phase) -------------------------------------------

    /// <summary>
    /// HandoffGate.Evaluate was fully implemented and fully tested with ZERO production call sites
    /// for two releases. Testing Evaluate again would prove nothing new; this asserts the Queen
    /// actually calls it, against comment-stripped source.
    /// </summary>
    [Fact]
    public void TheQueen_ActuallyIngestsHandoffs_OnTaskCompletion()
    {
        // v3.1.0: dispatch moved to ExecutionService, and the ingestion call site moved with it.
        // The Queen still owns WHEN a mission runs; this is about the task-completion path.
        var code = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));
        Assert.Contains("IngestHandoffs(mission, context, task, execution, runtimeSelection, scheduler)", code);
        Assert.Contains("HandoffGate.Evaluate(handoff, mission)", code);
        Assert.Contains("HandoffGate.NextDepthFrom(sourceTask)", code);
    }

    // ---- the admission path, actually driven ----------------------------------------------------

    /// <summary>
    /// Runs the real ingestion: a completed tester task whose result proposes a medic handoff, with
    /// the medic gate open. The follow-up task must reach the scheduler, the mission, and the
    /// database — and carry depth 1 derived from its parent.
    /// </summary>
    [Fact]
    public void AnAdmittedHandoff_BecomesARealScheduledAndPersistedTask()
    {
        var (queen, mission, source, scheduler) = Harness();
        var execution = ResultProposing(Medic("k-admit"));

        WithIngestion(() => WithMedicOpen(() =>
        {
            queen.Execution.IngestHandoffs(mission, Context(mission), source, execution, Selection(source), scheduler);
            return 0;
        }));

        var created = Assert.Single(scheduler.Tasks, t => t.AssignedAnt == "medic");
        Assert.Equal("failure_diagnosis", created.TaskType);
        Assert.Contains(source.Id, created.ParentTaskIds);
        Assert.Equal(1, HandoffGate.DepthOf(created));                       // derived, not self-reported
        // TaskScheduler copies the list it is constructed with, so scheduler admission alone
        // would leave this task invisible to mission grading, verification, and the dedupe check.
        Assert.Contains(mission.Tasks, t => t.Id == created.Id);
        Assert.Equal(scheduler.Tasks.Count, mission.Tasks.Count);
        Assert.Contains(queen.Memory.GetTasksForMission(mission.Id),         // survives restart
            r => r["id"]?.ToString() == created.Id);
        Assert.Contains(queen.Memory.GetRecentEvents(50, eventType: "handoff_admitted"),
            e => e["mission_id"]?.ToString() == mission.Id);
    }

    /// <summary>The gate is the whole safety story, so its absence must change the outcome.</summary>
    [Fact]
    public void WithTheFeatureGateOff_NothingIsAdmitted()
    {
        var (queen, mission, source, scheduler) = Harness();
        WithMedicOpen(() =>
        {
            queen.Execution.IngestHandoffs(mission, Context(mission), source, ResultProposing(Medic("k-off")), Selection(source), scheduler);
            return 0;
        });
        Assert.DoesNotContain(scheduler.Tasks, t => t.AssignedAnt == "medic");
    }

    /// <summary>
    /// ADR §6: "Every runtime-added task passes the SAME authorization, contract and permission
    /// gates as an initial-plan task." With the medic's rollout gate closed, the handoff proposes a
    /// role that is not runtime-eligible — and no task appears.
    /// </summary>
    [Fact]
    public void AHandoffToAGatedRole_IsRefused_AndTheRefusalIsRecorded()
    {
        var (queen, mission, source, scheduler) = Harness();
        WithIngestion(() =>
        {
            queen.Execution.IngestHandoffs(mission, Context(mission), source, ResultProposing(Medic("k-gated")), Selection(source), scheduler);
            return 0;
        });

        Assert.DoesNotContain(scheduler.Tasks, t => t.AssignedAnt == "medic");
        var rejection = Assert.Single(queen.Memory.GetRecentEvents(50, eventType: "handoff_rejected"));
        Assert.Contains("not runtime-eligible", rejection["message"]?.ToString());
    }

    /// <summary>A repeated handoff must not spawn the same follow-up twice.</summary>
    [Fact]
    public void TheSameHandoffTwice_AdmitsOnce()
    {
        var (queen, mission, source, scheduler) = Harness();
        WithIngestion(() => WithMedicOpen(() =>
        {
            var context = Context(mission);
            queen.Execution.IngestHandoffs(mission, context, source, ResultProposing(Medic("k-dupe")), Selection(source), scheduler);
            queen.Execution.IngestHandoffs(mission, context, source, ResultProposing(Medic("k-dupe")), Selection(source), scheduler);
            return 0;
        }));
        Assert.Single(scheduler.Tasks, t => t.AssignedAnt == "medic");
    }

    /// <summary>
    /// The recursion bound, end to end: a handoff FROM an admitted task is one level deeper, so the
    /// chain terminates at MaxHandoffDepth instead of running forever.
    /// </summary>
    [Fact]
    public void AHandoffChain_TerminatesAtTheDepthLimit()
    {
        var (queen, mission, source, scheduler) = Harness();

        WithIngestion(() => WithMedicOpen(() =>
        {
            var context = Context(mission);
            var current = source;
            for (var i = 0; i < HandoffGate.MaxHandoffDepth + 2; i++)
            {
                queen.Execution.IngestHandoffs(mission, context, current, ResultProposing(Medic($"k-chain-{i}")), Selection(current), scheduler);
                var next = scheduler.Tasks.LastOrDefault(t => t.AssignedAnt == "medic");
                if (next is null || next.Id == current.Id) break;
                current = next;
            }
            return 0;
        }));

        var depths = scheduler.Tasks.Where(t => t.AssignedAnt == "medic").Select(HandoffGate.DepthOf).ToList();
        Assert.NotEmpty(depths);
        Assert.All(depths, d => Assert.True(d <= HandoffGate.MaxHandoffDepth, $"depth {d} exceeded the bound"));
    }

    // ---- a handoff can never widen what a role may do --------------------------------------------

    /// <summary>
    /// ADR §6: a handoff may never grant a capability. It can only request a role that is ALREADY
    /// runtime-eligible, for a task type its contract ALREADY supports — so with gates closed,
    /// nothing a specialist proposes can execute.
    /// </summary>
    [Fact]
    public void WithGatesClosed_NoHandoffCanCreateAnExecutableTask()
    {
        var handoff = new AntHandoff("tester", "medic", "check failed", "failure_diagnosis",
            new[] { "test_report" }, true, 1, "k1");
        var admission = HandoffGate.Evaluate(handoff, new Mission());
        Assert.False(admission.Accepted);
        Assert.Contains("not runtime-eligible", admission.Reason);
    }

    [Fact]
    public void AHandoffCannotRequestATaskTypeOutsideTheDestinationContract()
    {
        var handoff = new AntHandoff("tester", "medic", "please apply this patch", "code_change",
            new[] { "test_report" }, true, 1, "k2");
        var admission = WithMedicOpen(() => HandoffGate.Evaluate(handoff, new Mission()));
        Assert.False(admission.Accepted);
        Assert.Contains("does not support task type", admission.Reason);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>A Queen with a real database, a saved mission, one completed planned task, and a
    /// scheduler holding it — the state the ingestion path expects at a task's completion.</summary>
    private (Queen Queen, Mission Mission, DomainTask Source, TaskScheduler Scheduler) Harness()
    {
        var queen = new Queen(Memory());
        var source = Planned();
        var mission = new Mission { Goal = "diagnose the failing checks", Tasks = { source } };
        queen.Memory.SaveMission(mission);
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        scheduler.Prepare();
        return (queen, mission, source, scheduler);
    }

    private static AntHandoff Medic(string dedupe) =>
        new("tester", "medic", "check failed", "failure_diagnosis", new[] { "test_report" }, true, 1, dedupe);

    private static AntExecutionResult ResultProposing(params AntHandoff[] handoffs) =>
        AntExecutionResult.Succeeded("checks ran") with { Handoffs = handoffs.ToList() };

    private static AntRuntimeSelection Selection(DomainTask task) =>
        AntRuntime.Resolve(task, MissionConstraints.Parse("diagnose the failing checks"));

    /// <summary>
    /// v3.1.0 (ADR-002): the mission's resolved context. Built INSIDE the gate wrappers on purpose
    /// — a context captures the runtime's capability set at intake, and "what was enabled when the
    /// mission was admitted" is precisely the behaviour these tests vary.
    /// </summary>
    private static MissionContext Context(Mission mission) => MissionContext.ForMission(mission);

    private static T WithIngestion<T>(Func<T> body)
    {
        try
        {
            AnthillRuntime.EnableHandoffIngestion = true;
            return body();
        }
        finally { AnthillRuntime.EnableHandoffIngestion = false; }
    }

    private static T WithMedicOpen<T>(Func<T> body)
    {
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableMedicAnt = true;
            return body();
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableMedicAnt = false;
        }
    }

    private static string CodeOnly(string src) => string.Join("\n", src.Split('\n')
        .Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
