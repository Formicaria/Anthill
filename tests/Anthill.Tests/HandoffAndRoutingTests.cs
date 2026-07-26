using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Planning;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage E validation gate (spec §10 + §7): handoffs are admitted only through the bounded gate
/// (eligibility, contract task-type, depth, budget, dedupe — all fail closed with reasons), and
/// planner routing inserts the UI cartographer before the coder ONLY for UI goals with gates open.
/// </summary>
[Collection("specialist-gates")]
public class HandoffAndRoutingTests
{
    private static AntHandoff TesterToMedic(int depth = 1, string dedupe = "k1") =>
        new("tester", "medic", "check failed", "failure_diagnosis", new[] { "test_report" }, true, depth, dedupe);

    private static void WithGates(Action body, params string[] roles)
    {
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            foreach (var r in roles)
            {
                if (r == "medic") AnthillRuntime.EnableMedicAnt = true;
                if (r == "ui_cartographer") AnthillRuntime.EnableUiCartographerAnt = true;
            }
            body();
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableMedicAnt = false;
            AnthillRuntime.EnableUiCartographerAnt = false;
        }
    }

    // ---- Handoff gate --------------------------------------------------------------------------

    [Fact]
    public void GateClosed_HandoffRejected_WithReason()
    {
        var a = HandoffGate.Evaluate(TesterToMedic(), new Mission());
        Assert.False(a.Accepted);
        Assert.Contains("not runtime-eligible", a.Reason);
    }

    [Fact]
    public void GateOpen_ValidHandoff_CreatesBoundedTask()
    {
        WithGates(() =>
        {
            var a = HandoffGate.Evaluate(TesterToMedic(), new Mission());
            Assert.True(a.Accepted, a.Reason);
            Assert.Equal("medic", a.CreatedTask!.AssignedAnt);
            Assert.Equal("failure_diagnosis", a.CreatedTask.TaskType);
            Assert.Contains("dedupe:k1", a.CreatedTask.Description);
        }, "medic");
    }

    [Fact]
    public void OverDepth_Rejected()
    {
        WithGates(() =>
        {
            var a = HandoffGate.Evaluate(TesterToMedic(depth: HandoffGate.MaxHandoffDepth + 1), new Mission());
            Assert.False(a.Accepted);
            Assert.Contains("depth", a.Reason);
        }, "medic");
    }

    [Fact]
    public void Duplicate_Suppressed()
    {
        WithGates(() =>
        {
            var m = new Mission();
            m.Tasks.Add(new DomainTask { Description = "earlier [handoff dedupe:k1 depth:1]" });
            var a = HandoffGate.Evaluate(TesterToMedic(dedupe: "k1"), m);
            Assert.False(a.Accepted);
            Assert.Contains("near-duplicate", a.Reason);
        }, "medic");
    }

    [Fact]
    public void BudgetExhausted_Rejected()
    {
        WithGates(() =>
        {
            var m = new Mission();
            for (var i = 0; i < HandoffGate.MaxMissionTasks; i++) m.Tasks.Add(new DomainTask { Title = $"t{i}" });
            var a = HandoffGate.Evaluate(TesterToMedic(), m);
            Assert.False(a.Accepted);
            Assert.Contains("budget", a.Reason);
        }, "medic");
    }

    [Fact]
    public void UnsupportedTaskType_Rejected()
    {
        WithGates(() =>
        {
            var bad = new AntHandoff("tester", "medic", "why", "ui_mapping", Array.Empty<string>(), true, 1, "k9");
            var a = HandoffGate.Evaluate(bad, new Mission());
            Assert.False(a.Accepted);
            Assert.Contains("does not support task type", a.Reason);
        }, "medic");
    }

    // ---- Planner specialist routing ------------------------------------------------------------

    private static List<DomainTask> CoderPlan() => new()
    {
        new() { Title = "research", AssignedAnt = "researcher", TaskType = "research", Description = "r" },
        new() { Title = "change ui", AssignedAnt = "coder", TaskType = "code_change", Description = "c" },
        new() { Title = "verify", AssignedAnt = "verifier", TaskType = "verification", Description = "v" },
    };

    [Fact]
    public void UiGoal_GatesOpen_InsertsCartographerBeforeCoder_WithDependency()
    {
        WithGates(() =>
        {
            var tasks = Planner.InjectSpecialistRouting(CoderPlan(), "improve the colony page UI layout");
            var mapIndex = tasks.FindIndex(t => t.AssignedAnt == "ui_cartographer");
            var coderIndex = tasks.FindIndex(t => t.AssignedAnt == "coder");
            Assert.True(mapIndex >= 0 && mapIndex < coderIndex);
            Assert.Contains(tasks[mapIndex].Id, tasks[coderIndex].DependsOn);
        }, "ui_cartographer");
    }

    [Fact]
    public void UiGoal_GatesClosed_NoCartographer()
    {
        var tasks = Planner.InjectSpecialistRouting(CoderPlan(), "improve the colony page UI layout");
        Assert.DoesNotContain(tasks, t => t.AssignedAnt == "ui_cartographer");
    }

    [Fact]
    public void BackendGoal_NeverGetsCartographer_EvenWithGatesOpen()
    {
        WithGates(() =>
        {
            var tasks = Planner.InjectSpecialistRouting(CoderPlan(), "refactor the sqlite repository locking");
            Assert.DoesNotContain(tasks, t => t.AssignedAnt == "ui_cartographer");
        }, "ui_cartographer");
    }
}
