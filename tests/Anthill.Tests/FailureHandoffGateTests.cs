using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.Core.Scheduling;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// The repair path fires. v3.8.32.
///
/// v3.8.25 moved handoff ingestion onto the failure path and its comment claimed the tester→medic
/// route now worked. It did not. The gate read:
///
/// <code>
///   if (decision.Action == TaskOutcomeAction.Fail &amp;&amp; !decision.Retryable)
/// </code>
///
/// <c>decision.Retryable</c> is derived from the ant's STATUS CODE, not from anything the scheduler
/// decided. The tester emits <c>failed_retryable</c> on every failed check, so that flag was true on
/// every attempt INCLUDING the one that exhausted the attempt budget — and the tester→medic handoff,
/// declared <c>required: true</c>, was dropped every single time. The colony's repair loop still
/// could not fire, one release after a release that said it could.
///
/// The reasoning in the comment was right: do not dispatch a medic mid-retry. The variable was
/// wrong. <c>TaskScheduler.MarkFailed</c> already returns "true when terminally failed; false when a
/// bounded retry was scheduled", and the code threw that return value away.
///
/// These tests drive the REAL <c>TaskOutcomeMapper</c> and the REAL scheduler into the REAL
/// <c>ApplyNonCompletingOutcome</c>. Nothing here constructs a decision by hand — constructing the
/// input is exactly how the original bug stayed invisible.
/// </summary>
[Collection("specialist-gates")]
public class FailureHandoffGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_hgate_" + Guid.NewGuid().ToString("N"));

    private readonly bool _ingestionWas = AnthillRuntime.EnableHandoffIngestion;
    private readonly bool _specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly bool _medicWas = AnthillRuntime.EnableMedicAnt;
    private readonly bool _testerWas = AnthillRuntime.EnableTesterAnt;

    public FailureHandoffGateTests()
    {
        AnthillRuntime.EnableHandoffIngestion = true;
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableTesterAnt = true;
    }

    public void Dispose()
    {
        AnthillRuntime.EnableHandoffIngestion = _ingestionWas;
        AnthillRuntime.EnableSpecialistAntExecution = _specialistsWas;
        AnthillRuntime.EnableMedicAnt = _medicWas;
        AnthillRuntime.EnableTesterAnt = _testerWas;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    /// <summary>
    /// EXACTLY what <c>TesterAnt</c> returns when a check fails — status code, failure class,
    /// retryable flag and the required medic handoff, copied from the producer rather than invented.
    /// If the tester's shape changes, <see cref="TheTesterStillEmitsTheShapeTheseTestsAssume"/>
    /// fails and these tests stop being a fiction about it.
    /// </summary>
    private static AntExecutionResult TesterFailure() => new()
    {
        Success = false,
        StatusCode = "failed_retryable",
        Summary = "2 check(s): 1 passed, 1 failed.",
        Failure = new AntFailure(FailureClass.VerificationFailure, "one or more checks failed", Retryable: true),
        Handoffs =
        {
            new AntHandoff("tester", "medic", "check failure needs diagnosis", "failure_diagnosis",
                new[] { "test_report" }, true, 1, "tester-fail:m1:t1"),
        },
    };

    private (ExecutionService Service, Mission Mission, DomainTask Source, TaskScheduler Scheduler, MissionContext Context)
        Harness(int maxAttempts)
    {
        var memory = Memory();
        var source = new DomainTask
        {
            Title = "run the checks", Description = "run the checks",
            AssignedAnt = "researcher", TaskType = "research",
            MaxAttempts = maxAttempts,
        };
        var mission = new Mission { Goal = "diagnose the failing checks", Tasks = { source } };
        memory.SaveMission(mission);

        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        scheduler.Prepare();

        var service = new ExecutionService(memory, new Dictionary<string, BaseAnt>());
        return (service, mission, source, scheduler, MissionContext.ForMission(mission));
    }

    private static int MedicTasks(Mission mission) =>
        mission.Tasks.Count(t => t.AssignedAnt.Equals("medic", StringComparison.OrdinalIgnoreCase));

    private void Apply(ExecutionService service, Mission mission, MissionContext context, DomainTask task,
        TaskScheduler scheduler, AntExecutionResult execution)
    {
        // The REAL producer decides. This is the line whose output the old gate misread.
        var decision = TaskOutcomeMapper.Map(execution);
        var selection = AntRuntime.Resolve(task, MissionConstraints.Parse(mission.Goal));

        service.ApplyNonCompletingOutcome(mission, context, task, selection, execution, decision,
            DateTime.UtcNow, 1.0, scheduler);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The producer's shape, pinned. Everything below assumes the tester reports a RETRYABLE failure
    /// with a required medic handoff; if that ever stops being true these tests would silently start
    /// proving nothing.
    /// </summary>
    [Fact]
    public void TheTesterStillEmitsTheShapeTheseTestsAssume()
    {
        var decision = TaskOutcomeMapper.Map(TesterFailure());

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.True(decision.Retryable, "the tester reports a retryable failure — the gate must not key on this");

        var handoff = Assert.Single(TesterFailure().Handoffs);
        Assert.Equal("medic", handoff.DestinationRole);
        Assert.True(handoff.Required);
    }

    /// <summary>
    /// THE regression. On the LAST attempt the medic task must be created.
    ///
    /// Before v3.8.32 this produced zero medic tasks, because `decision.Retryable` was true here just
    /// as it is on every other attempt.
    /// </summary>
    [Fact]
    public void WhenTheAttemptBudgetIsExhausted_TheMedicTaskIsCreated()
    {
        var (service, mission, source, scheduler, context) = Harness(maxAttempts: 1);
        source.AttemptCount = 1;   // this attempt is the last one

        Apply(service, mission, context, source, scheduler, TesterFailure());

        Assert.Equal(TaskStatus.Failed, source.Status);
        Assert.Equal(1, MedicTasks(mission));
    }

    /// <summary>
    /// ...and while a retry remains, it must NOT be — which is the rule the original comment stated
    /// and the reason the fix cannot simply be "always ingest". A medic dispatched mid-retry would
    /// diagnose a task the colony has not finished attempting, once per attempt.
    /// </summary>
    [Fact]
    public void WhileARetryRemains_NoMedicIsDispatched()
    {
        var (service, mission, source, scheduler, context) = Harness(maxAttempts: 3);
        source.AttemptCount = 1;   // two attempts still available

        Apply(service, mission, context, source, scheduler, TesterFailure());

        Assert.Equal(0, MedicTasks(mission));
        Assert.NotEqual(TaskStatus.Failed, source.Status);   // the scheduler re-queued it
    }

    /// <summary>
    /// The whole retry sequence: two attempts that re-queue, then one that exhausts the budget and
    /// dispatches EXACTLY ONE medic. This is the test that distinguishes the fix from both failure
    /// modes — never dispatching, and dispatching once per attempt.
    /// </summary>
    [Fact]
    public void AcrossAWholeRetrySequence_ExactlyOneMedicIsDispatched()
    {
        var (service, mission, source, scheduler, context) = Harness(maxAttempts: 3);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            source.AttemptCount = attempt;
            Apply(service, mission, context, source, scheduler, TesterFailure());
        }

        Assert.Equal(TaskStatus.Failed, source.Status);
        Assert.Equal(1, MedicTasks(mission));
    }

    /// <summary>
    /// A PERMANENT failure dispatches immediately — there is no retry to wait for. This guards the
    /// path that did work before, so the fix cannot have regressed it.
    /// </summary>
    [Fact]
    public void APermanentFailure_DispatchesTheMedicOnTheFirstAttempt()
    {
        var (service, mission, source, scheduler, context) = Harness(maxAttempts: 3);

        var permanent = TesterFailure() with
        {
            StatusCode = "failed_permanent",
            Failure = new AntFailure(FailureClass.VerificationFailure, "checks failed permanently", Retryable: false),
        };

        Apply(service, mission, context, source, scheduler, permanent);

        Assert.Equal(1, MedicTasks(mission));
    }

    /// <summary>
    /// A SKIPPED task never hands off. Its declared handoffs are proposals about work that did not
    /// happen, and acting on them would invent a diagnosis for an event with no evidence.
    /// </summary>
    [Fact]
    public void ASkippedTask_HandsOffNothing()
    {
        var (service, mission, source, scheduler, context) = Harness(maxAttempts: 1);

        var skipped = AntExecutionResult.Skipped("nothing to check") with { Handoffs = TesterFailure().Handoffs };

        Apply(service, mission, context, source, scheduler, skipped);

        Assert.Equal(0, MedicTasks(mission));
    }

    /// <summary>
    /// The scheduler's contract, stated directly — the value the gate now depends on. If MarkFailed
    /// ever stopped distinguishing these two cases, the gate above would silently go back to being
    /// wrong, and this is the test that would say so.
    /// </summary>
    [Fact]
    public void MarkFailed_ReportsTerminalityRatherThanEligibility()
    {
        var task = new DomainTask { Title = "t", AssignedAnt = "researcher", TaskType = "research", MaxAttempts = 2 };
        var mission = new Mission { Goal = "g", Tasks = { task } };
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        scheduler.Prepare();

        task.AttemptCount = 1;
        Assert.False(scheduler.MarkFailed(task.Id, "first", "verification_failure", retryable: true),
            "a retry was available, so this failure is not terminal");

        task.AttemptCount = 2;
        Assert.True(scheduler.MarkFailed(task.Id, "second", "verification_failure", retryable: true),
            "the budget is exhausted, so this failure IS terminal even though the class is retryable");
    }
}
