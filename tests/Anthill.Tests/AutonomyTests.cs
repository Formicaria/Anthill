using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 0 autonomy rails: objective backlog persistence + priority ordering, the run-outcome
/// circuit breaker, the durable kill switch, and the rate-budget guard. No execution loop yet.
/// </summary>
public class AutonomyTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public AutonomyTests()
    {
        AnthillRuntime.Initialize();
        AnthillRuntime.EnableAutonomy = true;
        _dir = Path.Combine(Path.GetTempPath(), "anthill_autonomy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
        AutonomyControl.Resume(); // ensure a clean kill-switch state
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Objective NewObjective(string title, int priority, int maxRuns = 0) =>
        new() { Title = title, Charter = $"charter for {title}", Priority = priority, MaxRuns = maxRuns };

    [Fact]
    public void Objective_RoundTripsThroughStore()
    {
        var o = NewObjective("research papers", 5, maxRuns: 3);
        o.Metadata["topic"] = "swarm";
        _memory.SaveObjective(o);

        var loaded = _memory.GetObjective(o.Id);
        Assert.NotNull(loaded);
        Assert.Equal("research papers", loaded!.Title);
        Assert.Equal(5, loaded.Priority);
        Assert.Equal(3, loaded.MaxRuns);
        Assert.Equal(ObjectiveStatus.Pending, loaded.Status);
        Assert.Equal("swarm", loaded.Metadata.GetValueOrDefault("topic")?.ToString());
    }

    [Fact]
    public void NextReadyObjective_PicksHighestPriorityThenOldest()
    {
        var low = NewObjective("low", 1);
        var highOld = NewObjective("high-old", 10);
        var highNew = NewObjective("high-new", 10);
        _memory.SaveObjective(low);
        _memory.SaveObjective(highOld);
        System.Threading.Thread.Sleep(5);
        _memory.SaveObjective(highNew);

        var next = _memory.NextReadyObjective();
        Assert.NotNull(next);
        Assert.Equal(highOld.Id, next!.Id); // priority 10 beats 1; older of the two wins the tie
    }

    [Fact]
    public void NextReadyObjective_SkipsPausedDoneAndBudgetExhausted()
    {
        var paused = NewObjective("paused", 9);
        paused.Status = ObjectiveStatus.Paused;
        var exhausted = NewObjective("exhausted", 8, maxRuns: 1);
        exhausted.RunCount = 1;
        _memory.SaveObjective(paused);
        _memory.SaveObjective(exhausted);

        Assert.Null(_memory.NextReadyObjective());
    }

    [Fact]
    public void RunOutcome_TripsBreakerAfterConsecutiveFailures()
    {
        AnthillRuntime.AutonomyMaxConsecutiveFailures = 3;
        var o = NewObjective("flaky", 5);
        _memory.SaveObjective(o);

        _memory.RecordObjectiveRunOutcome(o.Id, success: false);
        _memory.RecordObjectiveRunOutcome(o.Id, success: false);
        var afterTwo = _memory.GetObjective(o.Id)!;
        Assert.Equal(ObjectiveStatus.Active, afterTwo.Status);
        Assert.Equal(2, afterTwo.ConsecutiveFailures);

        var afterThree = _memory.RecordObjectiveRunOutcome(o.Id, success: false)!;
        Assert.Equal(ObjectiveStatus.Paused, afterThree.Status); // breaker trips
        Assert.Equal(3, afterThree.RunCount);
    }

    [Fact]
    public void RunOutcome_SuccessResetsBreakerAndMaxRunsCompletes()
    {
        var o = NewObjective("bounded", 5, maxRuns: 2);
        _memory.SaveObjective(o);

        var afterFail = _memory.RecordObjectiveRunOutcome(o.Id, success: false)!;
        Assert.Equal(1, afterFail.ConsecutiveFailures);

        var afterSuccess = _memory.RecordObjectiveRunOutcome(o.Id, success: true)!;
        Assert.Equal(0, afterSuccess.ConsecutiveFailures);
        Assert.Equal(ObjectiveStatus.Done, afterSuccess.Status); // run budget (2) exhausted
    }

    [Fact]
    public void KillSwitch_StopsAndResumesDurably()
    {
        AutonomyControl.Resume();
        Assert.False(AutonomyControl.IsStopped);

        AutonomyControl.Stop("test halt");
        Assert.True(AutonomyControl.IsStopped);
        Assert.True(File.Exists(AutonomyControl.StopFilePath())); // durable across restarts

        AutonomyControl.Resume();
        Assert.False(AutonomyControl.IsStopped);
        Assert.False(File.Exists(AutonomyControl.StopFilePath()));
    }

    [Fact]
    public void BudgetGuard_DeniesWhenKillSwitchEngaged()
    {
        var guard = new BudgetGuard(_memory);
        AutonomyControl.Resume();
        Assert.True(guard.Evaluate().Allowed);

        AutonomyControl.Stop("test");
        var denied = guard.Evaluate();
        Assert.False(denied.Allowed);
        Assert.Equal("kill_switch", denied.Code);
        AutonomyControl.Resume();
    }

    [Fact]
    public void BudgetGuard_DeniesWhenHourlyBudgetReached()
    {
        AnthillRuntime.AutonomyMaxMissionsPerHour = 2;
        AnthillRuntime.AutonomyMaxMissionsPerDay = 100;
        var o = NewObjective("busy", 5);
        _memory.SaveObjective(o);
        for (var i = 0; i < 2; i++)
            _memory.SaveAutonomyRun(new AutonomyRun { ObjectiveId = o.Id, GeneratedGoal = "g", MissionStatus = "complete" });

        var guard = new BudgetGuard(_memory);
        var decision = guard.Evaluate();
        Assert.False(decision.Allowed);
        Assert.Equal("hourly_budget", decision.Code);
    }

    [Fact]
    public void BudgetGuard_DeniesWhenAutonomyDisabled()
    {
        AnthillRuntime.EnableAutonomy = false;
        var decision = new BudgetGuard(_memory).Evaluate();
        Assert.False(decision.Allowed);
        Assert.Equal("autonomy_disabled", decision.Code);
        AnthillRuntime.EnableAutonomy = true;
    }
}
