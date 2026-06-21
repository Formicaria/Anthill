using Anthill.Core.Domain;
using Anthill.Core.Scheduling;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Scheduler regressions ported from the Python v1.7.1 suite: linear dependencies,
/// fan-out/fan-in, blocked→ready transitions, failed-dependency skipping, bounded retries,
/// retry exhaustion, duplicate-id safety, cycle detection, and metadata-first graph export.
/// </summary>
public class SchedulerTests
{
    private static Task T(string id, string ant = "researcher", params string[] deps) =>
        new() { Id = id, Title = id, Description = id, AssignedAnt = ant, DependsOn = deps.ToList() };

    [Fact]
    public void LinearDependencies_BlockDownstreamUntilUpstreamCompletes()
    {
        var a = T("A");
        var b = T("B", "builder", "A");
        var s = new TaskScheduler(new[] { a, b }, "m");
        s.Prepare();

        Assert.Equal(TaskStatus.Ready, a.Status);
        Assert.Equal(TaskStatus.Blocked, b.Status);

        var next = s.NextReadyTask();
        Assert.Equal("A", next!.Id);
        s.MarkRunning("A");
        s.MarkComplete("A", "done");
        Assert.Equal(TaskStatus.Ready, b.Status);
    }

    [Fact]
    public void FanOutFanIn_JoinWaitsForAllBranches()
    {
        var root = T("R");
        var x = T("X", "file", "R");
        var y = T("Y", "coder", "R");
        var join = T("J", "builder", "X", "Y");
        var s = new TaskScheduler(new[] { root, x, y, join }, "m");
        s.Prepare();

        s.MarkRunning("R"); s.MarkComplete("R");
        Assert.Equal(TaskStatus.Ready, x.Status);
        Assert.Equal(TaskStatus.Ready, y.Status);
        Assert.Equal(TaskStatus.Blocked, join.Status);

        s.MarkRunning("X"); s.MarkComplete("X");
        Assert.Equal(TaskStatus.Blocked, join.Status);
        s.MarkRunning("Y"); s.MarkComplete("Y");
        Assert.Equal(TaskStatus.Ready, join.Status);
    }

    [Fact]
    public void FailedDependency_SkipsDownstream()
    {
        var a = T("A");
        var b = T("B", "builder", "A");
        var s = new TaskScheduler(new[] { a, b }, "m");
        s.Prepare();
        s.MarkRunning("A");
        var terminal = s.MarkFailed("A", "boom", retryable: false);
        Assert.True(terminal);
        Assert.Equal(TaskStatus.Failed, a.Status);
        Assert.Equal(TaskStatus.Skipped, b.Status);
    }

    [Fact]
    public void BoundedRetry_ReschedulesUntilExhausted()
    {
        var a = T("A");
        a.MaxAttempts = 2;
        var s = new TaskScheduler(new[] { a }, "m");
        s.Prepare();

        s.MarkRunning("A"); // attempt 1
        var terminal1 = s.MarkFailed("A", "fail1");
        Assert.False(terminal1); // retry scheduled
        Assert.Equal(TaskStatus.Ready, a.Status);

        s.MarkRunning("A"); // attempt 2
        var terminal2 = s.MarkFailed("A", "fail2");
        Assert.True(terminal2); // exhausted
        Assert.Equal(TaskStatus.Failed, a.Status);
        Assert.Equal(2, a.AttemptCount);
    }

    [Fact]
    public void DuplicateTaskIds_AreDetectedAndSkipped()
    {
        var a1 = T("DUP");
        var a2 = T("DUP");
        var s = new TaskScheduler(new[] { a1, a2 }, "m");
        var issues = s.ValidateGraph();
        Assert.Contains(issues, i => i.Code == "duplicate_task_id");
        s.Prepare();
        Assert.Equal(TaskStatus.Skipped, a1.Status);
        Assert.Equal(TaskStatus.Skipped, a2.Status);
        Assert.False(s.TaskById.ContainsKey("DUP"));
    }

    [Fact]
    public void MissingAndSelfDependency_AreDetected()
    {
        var a = T("A", "builder", "GHOST");
        var b = T("B", "builder", "B");
        var s = new TaskScheduler(new[] { a, b }, "m");
        var issues = s.ValidateGraph();
        Assert.Contains(issues, i => i.Code == "missing_dependency");
        Assert.Contains(issues, i => i.Code == "self_dependency");
    }

    [Fact]
    public void Cycle_IsDetectedAndParticipantsSkipped()
    {
        var a = T("A", "builder", "C");
        var b = T("B", "builder", "A");
        var c = T("C", "builder", "B");
        var s = new TaskScheduler(new[] { a, b, c }, "m");
        var issues = s.ValidateGraph();
        Assert.Contains(issues, i => i.Code == "cycle");
        s.Prepare();
        Assert.Equal(TaskStatus.Skipped, a.Status);
        Assert.Equal(TaskStatus.Skipped, b.Status);
        Assert.Equal(TaskStatus.Skipped, c.Status);
    }

    [Fact]
    public void ParallelReadyQueue_ExcludesRunningTasks()
    {
        var a = T("A");
        var b = T("B");
        var s = new TaskScheduler(new[] { a, b }, "m");
        s.Prepare();
        Assert.Equal(2, s.ReadyTasks().Count);
        s.MarkRunning("A");
        var ready = s.ReadyTasks();
        Assert.Single(ready);
        Assert.Equal("B", ready[0].Id);
    }

    [Fact]
    public void ExportGraph_IsMetadataFirstByDefault()
    {
        var a = T("A");
        a.ResultSummary = "secret token=abcdef should be redacted in preview";
        var s = new TaskScheduler(new[] { a }, "m");
        s.Prepare();
        var graph = s.ExportGraph();
        Assert.Equal("task-graph-v2", graph["schema_version"]);
        var nodes = (List<Dictionary<string, object?>>)graph["nodes"]!;
        Assert.False(nodes[0].ContainsKey("result_summary")); // omitted by default
        var preview = nodes[0].GetValueOrDefault("result_summary_preview")?.ToString() ?? "";
        Assert.Contains("[redacted]", preview);
    }

    [Fact]
    public void Cancelled_RemainsAPersistedPlaceholderStatus()
    {
        // v1.7.x documents cancelled as a real persisted status not emitted by scheduling flows.
        Assert.Contains(TaskStatus.Cancelled, TaskScheduler.TerminalStatuses);
        Assert.Equal("cancelled", TaskStatus.Cancelled.Value());
    }

    [Fact]
    public void NonCriticalFailedDependency_DoesNotSkipDownstream()
    {
        // Spec-ingestion contract: a failed non-critical section must not abort synthesis.
        var section = T("S");
        section.Critical = false;
        var synthesis = T("J", "builder", "S"); // critical by default
        var s = new TaskScheduler(new[] { section, synthesis }, "m");
        s.Prepare();

        s.MarkRunning("S");
        s.MarkFailed("S", "section timed out", retryable: false);

        Assert.Equal(TaskStatus.Failed, section.Status);
        Assert.Equal(TaskStatus.Ready, synthesis.Status); // proceeds despite the failed section
    }

    [Fact]
    public void NonCriticalDependency_StillGatesOrderingUntilTerminal()
    {
        var section = T("S");
        section.Critical = false;
        var synthesis = T("J", "builder", "S");
        var s = new TaskScheduler(new[] { section, synthesis }, "m");
        s.Prepare();

        s.MarkRunning("S");
        Assert.Equal(TaskStatus.Blocked, synthesis.Status); // waits while the section is still running
        s.MarkComplete("S", "done");
        Assert.Equal(TaskStatus.Ready, synthesis.Status);
    }

    [Fact]
    public void MixedCriticalAndNonCritical_OnlyCriticalFailurePropagates()
    {
        var goodSection = T("G");
        goodSection.Critical = false;
        var badSection = T("B");
        badSection.Critical = false;
        var criticalDep = T("C");           // critical by default
        var synthesis = T("J", "builder", "G", "B", "C");
        var s = new TaskScheduler(new[] { goodSection, badSection, criticalDep, synthesis }, "m");
        s.Prepare();

        s.MarkRunning("G"); s.MarkComplete("G");
        s.MarkRunning("B"); s.MarkFailed("B", "boom", retryable: false);
        s.MarkRunning("C"); s.MarkFailed("C", "critical boom", retryable: false);

        Assert.Equal(TaskStatus.Skipped, synthesis.Status); // the CRITICAL failure still aborts synthesis
    }
}
