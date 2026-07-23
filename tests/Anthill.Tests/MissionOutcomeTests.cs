using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.7.0: the plain-English mission outcome the console shows on every job. The executor's stop
/// reason is authoritative for cancel/timeout; the finalized mission/task state drives the
/// completed/partial/failed split. Pure function — no DB, no model calls.
/// </summary>
public class MissionOutcomeTests
{
    private static Mission MissionWith(MissionStatus status, params TaskStatus[] taskStatuses)
    {
        var m = new Mission { Goal = "outcome test", Status = status };
        foreach (var s in taskStatuses) m.Tasks.Add(new Task { Title = "t", Status = s });
        return m;
    }

    [Fact]
    public void Cancelled_ReasonMentionsOperatorAndProgress()
    {
        var m = MissionWith(MissionStatus.Partial, TaskStatus.Complete, TaskStatus.Skipped, TaskStatus.Skipped);
        var o = Queen.ComputeOutcome(m, "mission_cancelled");
        Assert.Equal("cancelled", o.Outcome);
        Assert.Contains("Cancelled by operator", o.Reason);
        Assert.Contains("1/3", o.Reason); // 1 of 3 tasks finished before stopping
    }

    [Fact]
    public void TimedOut_ReasonMentionsTheBudget()
    {
        var m = MissionWith(MissionStatus.Partial, TaskStatus.Complete, TaskStatus.Skipped);
        var o = Queen.ComputeOutcome(m, "mission_timeout");
        Assert.Equal("timed_out", o.Outcome);
        Assert.Contains("Timed out", o.Reason);
    }

    [Fact]
    public void Completed_AllTasksSucceeded()
    {
        var m = MissionWith(MissionStatus.Complete, TaskStatus.Complete, TaskStatus.Complete);
        var o = Queen.ComputeOutcome(m, null);
        Assert.Equal("completed", o.Outcome);
        Assert.Contains("2/2", o.Reason);
    }

    [Fact]
    public void Failed_SurfacesTheCriticalTaskFailureReason()
    {
        var m = MissionWith(MissionStatus.Failed, TaskStatus.Complete);
        m.Tasks.Add(new Task { Title = "boom", Status = TaskStatus.Failed, FailureReason = "compiler exploded" });
        var o = Queen.ComputeOutcome(m, null);
        Assert.Equal("failed", o.Outcome);
        Assert.Contains("compiler exploded", o.Reason);
    }

    [Fact]
    public void PartialWithTaskTimeout_NotesThePerTaskLimit()
    {
        var m = MissionWith(MissionStatus.Partial, TaskStatus.Complete);
        m.Tasks.Add(new Task { Title = "slow", Status = TaskStatus.Failed, FailureType = "timeout" });
        var o = Queen.ComputeOutcome(m, null);
        Assert.Equal("partial", o.Outcome);
        Assert.Contains("per-task limit", o.Reason);
    }
}
