using Anthill.Core.Memory;
using Anthill.Core.Workers;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A refused durable lease means the task is NOT executed here. v0.3.8.91.
///
/// WHAT WAS WRONG, and it was documented rather than hidden. `TryClaimTask` is genuinely atomic —
/// its guard and its insert are one transaction, with a comment saying exactly why — so
/// "another worker holds a live lease" was a trustworthy signal. The caller then discarded it:
///
///     var claim = _memory.TryClaimTask(...);
///     if (claim is not null) _liveAttempts[task.Id] = claim.Id;
///     else _memory.LogEvent(..., "Task ran without a durable attempt: ...");
///
/// No return. The lease was telemetry, not mutual exclusion.
///
/// The reason given was sound about the consequence: the in-process scheduler had ALREADY called
/// `MarkRunning`, so refusing there would strand the task in Running with nothing executing it.
/// Committing first is what created the trap. Claim first and there is nothing to strand — which is
/// the fix, and it is an ordering change rather than a new mechanism.
///
/// WHY IT MATTERS BEFORE ANY DISTRIBUTED WORK. On one process this is nearly unobservable. With two
/// processes against one colony database it is duplicate model calls, duplicate tool calls,
/// duplicate patch proposals, and two writers racing the same workspace. It is a prerequisite for a
/// multi-node feature, not a follow-up to it.
///
/// The storage layer was already covered by `TaskAttemptTests`. What had no test was the CALLER —
/// `attempt_claim_refused` appears nowhere in the suite before this file.
/// </summary>
public class AttemptLeaseExclusivityTests : IDisposable
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly string _path;
    private readonly SqliteMemory _memory;

    public AttemptLeaseExclusivityTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"anthill-lease-{Guid.NewGuid():N}.db");
        _memory = new SqliteMemory(_path);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_path); } catch { }
    }

    /// <summary>The storage guarantee this rests on, restated so the caller test cannot drift off it.</summary>
    [Fact]
    public void ASecondWorker_CannotClaimALiveLease()
    {
        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-a", Lease));
        Assert.Null(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));
    }

    /// <summary>
    /// And once the first worker finishes, the task is claimable again — otherwise "exclusive"
    /// would mean "claimable once ever", and a retry could never run.
    /// </summary>
    [Fact]
    public void AFinishedAttempt_ReleasesTheTaskForTheNextWorker()
    {
        var first = _memory.TryClaimTask("t1", "m1", "worker-a", Lease);
        Assert.NotNull(first);

        _memory.FinishAttempt(first!.Id, AttemptState.Abandoned, failureReason: "released");

        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));
    }

    /// <summary>
    /// THE CALLER YIELDS. The claim is taken BEFORE anything is committed, and a refusal returns.
    ///
    /// A source assertion, and the honest reason is that the behavioural version needs two processes
    /// against one database — which is a real test worth writing and is named in `PLAN.md` as part of
    /// the crash-and-concurrency work rather than faked here with one process pretending to be two.
    ///
    /// What it pins is the ORDER, because the order is the fix: `TryClaimTask` before `MarkRunning`,
    /// and a `return` on the refusal path. Either one alone leaves the defect — claiming first and
    /// continuing anyway still double-executes, and returning after `MarkRunning` still strands.
    /// </summary>
    [Fact]
    public void TheExecutor_ClaimsBeforeItCommits_AndYieldsWhenRefused()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var claimAt = code.IndexOf("_memory.TryClaimTask(", StringComparison.Ordinal);
        Assert.True(claimAt > 0, "the durable claim has moved; this guard reads it by name.");

        var markAt = code.IndexOf("scheduler.MarkRunning(", StringComparison.Ordinal);
        Assert.True(markAt > 0, "MarkRunning has moved.");

        Assert.True(claimAt < markAt,
            "the task is committed to Running before the durable claim is attempted. That ordering "
          + "is what forced the old code to ignore a refusal — refusing after the commit would "
          + "strand the task in Running with nothing executing it. Claim first and there is nothing "
          + "to strand.");

        // The refusal must END the invocation. A log line alone is what made the lease telemetry.
        var refusal = code.IndexOf("attempt_claim_refused", StringComparison.Ordinal);
        Assert.True(refusal > 0, "the refusal is no longer recorded at all.");

        var afterRefusal = code[refusal..Math.Min(code.Length, refusal + 500)];
        Assert.Contains("return;", afterRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CLAIM THIS INVOCATION WILL NOT USE IS RELEASED.
    ///
    /// The new ordering introduces a window the old one did not have: the claim succeeds and then
    /// the scheduler declines to start the task. Leaving the lease held would be worse than the bug
    /// being fixed — the task would carry a live lease no worker is honouring until it expired, so a
    /// scheduler decision would become a task nobody may claim.
    ///
    /// `Abandoned` rather than `Failed` on purpose: nothing was executed and nothing failed. The
    /// enum's own comment reserves `Abandoned` for an attempt whose outcome nobody observed, which
    /// is exactly this.
    /// </summary>
    [Fact]
    public void AClaimTheSchedulerDeclines_IsReleasedRatherThanHeld()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var markAt = code.IndexOf("scheduler.MarkRunning(", StringComparison.Ordinal);
        var window = code[markAt..Math.Min(code.Length, markAt + 900)];

        Assert.Contains("FinishAttempt(claim.Id, AttemptState.Abandoned", window, StringComparison.Ordinal);
    }
}
