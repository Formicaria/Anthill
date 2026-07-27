using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Scheduling;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.21.0 Phase B2: the Queen's loop obeys the adaptive controller. B1 shipped the decision layer
/// pure and unwired so the rules could be reviewed alone; this covers the wiring — that decisions
/// actually reach the mission, that adaptive tasks pass the same admission path as handoffs, and
/// that budgets survive a restart.
/// </summary>
[Collection("specialist-gates")]
public class AdaptiveWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_adaptive_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static T WithAdaptive<T>(Func<T> body)
    {
        try
        {
            AnthillRuntime.EnableAdaptiveMissionControl = true;
            return body();
        }
        finally { AnthillRuntime.EnableAdaptiveMissionControl = false; }
    }

    /// <summary>
    /// Budgets are derived by counting the mission's own audit events, so a restart cannot hand a
    /// mission a fresh allowance. This proves the counting is real: events written by one Queen are
    /// seen by a second one reading the same database.
    /// </summary>
    [Fact]
    public void AdaptiveBudgetsAreDerivedFromPersistedEvents_NotMemory()
    {
        var mem = Memory();
        var mission = new Mission { Goal = "ship it" };
        mem.SaveMission(mission);

        mem.LogEvent(mission.Id, "adaptive_repair", "cycle 1");
        mem.LogEvent(mission.Id, "adaptive_repair", "cycle 2");
        mem.LogEvent(mission.Id, "adaptive_delta_plan", "generation 1");

        // A fresh process reading the same database sees the same spend.
        var reopened = new SqliteMemory(mem.DbPath);
        Assert.Equal(2, reopened.GetRecentEvents(200, "adaptive_repair", mission.Id).Count);
        Assert.Single(reopened.GetRecentEvents(200, "adaptive_delta_plan", mission.Id));

        var budget = new AdaptiveBudget(
            ReplansUsed: reopened.GetRecentEvents(200, "adaptive_delta_plan", mission.Id).Count,
            RepairCyclesUsed: reopened.GetRecentEvents(200, "adaptive_repair", mission.Id).Count);

        Assert.False(budget.CanRepair);   // two cycles is the cap
        Assert.True(budget.CanReplan);    // and it did not borrow from the replan allowance
    }

    /// <summary>Events for one mission must never count against another's budget.</summary>
    [Fact]
    public void BudgetEventsAreScopedToTheirMission()
    {
        var mem = Memory();
        var a = new Mission { Goal = "a" };
        var b = new Mission { Goal = "b" };
        mem.SaveMission(a);
        mem.SaveMission(b);
        mem.LogEvent(a.Id, "adaptive_repair", "a's repair");

        Assert.Single(mem.GetRecentEvents(200, "adaptive_repair", a.Id));
        Assert.Empty(mem.GetRecentEvents(200, "adaptive_repair", b.Id));
    }

    [Fact]
    public void AdaptiveMissionControl_IsOffByDefault()
    {
        Assert.False(AnthillRuntime.EnableAdaptiveMissionControl);
        Assert.False(new AnthillConfig().AdaptiveMissionControlEnabled);
    }

    // ---- the wiring itself -----------------------------------------------------------------------

    /// <summary>
    /// Both execution loops must consult the controller — a mission that adapts under sequential
    /// execution but not under parallel would be a difference nobody could explain.
    /// </summary>
    [Fact]
    public void BothExecutionLoops_ConsultTheController()
    {
        var code = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        var sequential = Between(code, "private string? ExecuteTasksSequential", "private string? ExecuteTasksParallel");
        var parallel = Between(code, "private string? ExecuteTasksParallel", "private void RunSingleTask");

        Assert.Contains("ApplyAdaptiveDecision", sequential);
        Assert.Contains("ApplyAdaptiveDecision", parallel);
    }

    /// <summary>
    /// ADR §6: there is no admission path that skips the gates. Every runtime-created task —
    /// handoff, repair, delta — goes through the one helper, so that claim stays checkable.
    /// </summary>
    [Fact]
    public void EveryRuntimeCreatedTask_GoesThroughTheSingleAdmissionPath()
    {
        var code = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        // Exactly one place calls the scheduler's dynamic-admission API...
        Assert.Equal(1, Occurrences(code, "scheduler.AddDynamicTask("));
        // ...and it is inside the shared helper, which always runs the authorization gate.
        var helper = Between(code, "private string? TryAdmitDynamicTask", "private bool ApplyAdaptiveDecision");
        Assert.Contains("scheduler.AddDynamicTask(created)", helper);
        Assert.Contains("AntRegistry.ValidateTask(created, constraints)", helper);
        Assert.Contains("mission.Tasks.Add(created)", helper);
        Assert.Contains("Memory.SaveTask(mission.Id, created)", helper);
    }

    /// <summary>
    /// A repair task must not be critical: if it were, a failed repair attempt would itself become
    /// a new critical failure, which would request another repair — the loop the bounds exist to
    /// prevent, arriving through the back door.
    /// </summary>
    [Fact]
    public void ARepairTaskIsNotItselfCritical()
    {
        var apply = Between(
            CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"))),
            "if (decision.Action == AdaptiveAction.Repair)", "if (decision.Action == AdaptiveAction.DeltaPlan)");
        Assert.Contains("Critical = false", apply);
    }

    /// <summary>A refused adaptive task stops the mission rather than being skipped: the controller
    /// said the work was required and the mission cannot supply it.</summary>
    [Fact]
    public void ARefusedAdaptiveTask_StopsTheMission()
    {
        var record = Between(
            CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"))),
            "private bool RecordAdaptiveAdmission", "private void LogAdaptiveStop");
        Assert.Contains("adaptive task refused", record);
        Assert.Contains("return false", record);
    }

    // ---- controller behaviour that the wiring depends on -----------------------------------------

    /// <summary>
    /// The delta plan adds the missing verification and nothing else. A second delta for a mission
    /// that already has a verification step would duplicate it, so the wiring stops instead — a
    /// verifier that already ran and failed will not pass by being run again.
    /// </summary>
    [Fact]
    public void DeltaPlanning_AddsVerification_AndRefusesToDuplicateIt()
    {
        var controller = new AdaptiveMissionController();
        var unverified = new Mission { Goal = "g" };
        unverified.Tasks.Add(new DomainTask { Title = "build", AssignedAnt = "builder", Status = TaskStatus.Complete });

        Assert.Equal(AdaptiveAction.DeltaPlan, controller.Assess(unverified, new AdaptiveBudget()).Action);

        // Once a verification step exists and passed, there is nothing left to plan.
        unverified.Tasks.Add(new DomainTask
        {
            Title = "verify", AssignedAnt = "verifier", TaskType = "verify", Status = TaskStatus.Complete,
            Result = "Verification Passed\nReasoning: fine.",
        });
        Assert.Equal(AdaptiveAction.Finish, controller.Assess(unverified, new AdaptiveBudget()).Action);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"anchor not found: {start}");
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b > a ? text[a..b] : text[a..];
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
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
