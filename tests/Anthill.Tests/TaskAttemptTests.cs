using Anthill.Core.Memory;
using Anthill.Core.Workers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.0 — durable attempts, and the atomic claim.
///
/// The phase's gates are about what survives a crash: no accepted task silently lost, expired work
/// reclaimed without duplicating retained side effects, every retry a distinct attempt with a
/// durable reason, and two workers never claiming the same task.
///
/// The last of those is the one that cannot be written in application code. Read the row, check it,
/// write it back — and between the read and the write another worker does exactly the same thing,
/// and both see an unclaimed task. No amount of care closes that: the flaw is the gap, not the
/// carelessness. So the claim is one transaction with the precondition inside it, and these tests
/// exercise it through the same public surface a scheduler uses rather than reaching past it.
/// </summary>
public class TaskAttemptTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public TaskAttemptTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-attempts-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // ---- the claim ------------------------------------------------------------------------------

    [Fact]
    public void AClaimedTask_ProducesARunningAttempt()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", Lease);

        Assert.NotNull(attempt);
        Assert.Equal(AttemptState.Running, attempt!.State);
        Assert.Equal(1, attempt.Number);
        Assert.Equal("worker-a", attempt.WorkerId);
        Assert.False(attempt.IsTerminal);
        Assert.NotNull(attempt.LeaseUntil);
    }

    /// <summary>
    /// The exit gate. The second claimant is told NO — and that is a normal outcome under
    /// concurrency, not an error: a scheduler racing three workers at one task expects two refusals,
    /// and treating them as faults would make ordinary operation look like a problem.
    /// </summary>
    [Fact]
    public void ASecondWorker_CannotClaimALiveTask()
    {
        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-a", Lease));

        Assert.Null(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));
        Assert.Single(_memory.LoadAttempts("t1"));
    }

    /// <summary>
    /// And it must hold under actual contention, not just in sequence.
    ///
    /// Written this way deliberately: a sequential test passes on an implementation that reads,
    /// checks and writes with a gap, because nothing ever runs in that gap. Eight threads on one
    /// task is the shape of the bug, and exactly one of them may win.
    /// </summary>
    [Fact]
    public void UnderContention_ExactlyOneWorkerWins()
    {
        var claims = new System.Collections.Concurrent.ConcurrentBag<TaskAttempt?>();
        var ready = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            ready.Wait();
            claims.Add(_memory.TryClaimTask("t1", "m1", $"worker-{i}", Lease));
        })).ToList();

        foreach (var t in threads) t.Start();
        ready.Set();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(20));

        Assert.Equal(1, claims.Count(c => c is not null));
        Assert.Single(_memory.LoadAttempts("t1"));
    }

    [Fact]
    public void DifferentTasks_AreClaimedIndependently()
    {
        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-a", Lease));
        Assert.NotNull(_memory.TryClaimTask("t2", "m1", "worker-a", Lease));
    }

    // ---- every retry is a distinct attempt --------------------------------------------------------

    /// <summary>
    /// The gate says "every retry is a distinct attempt with a durable reason", and DISTINCT is the
    /// word doing the work. A counter on the task would say "three attempts"; it could not say that
    /// the first timed out, the second hit a provider fault, and the third succeeded. Those are three
    /// facts about three executions, and collapsing them into a number is how a task that
    /// half-succeeded becomes indistinguishable from one that never ran.
    /// </summary>
    [Fact]
    public void EachRetry_IsItsOwnRow_WithItsOwnReason()
    {
        var first = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        _memory.FinishAttempt(first.Id, AttemptState.Failed,
            provider: "ollama", model: "llama3.1:8b",
            failureClass: "TransientProviderFailure", failureReason: "timed out");

        var second = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        _memory.FinishAttempt(second.Id, AttemptState.Succeeded, provider: "ollama", model: "qwen2.5:14b");

        var attempts = _memory.LoadAttempts("t1");
        Assert.Equal(new[] { 1, 2 }, attempts.Select(a => a.Number));
        Assert.Equal("timed out", attempts[0].FailureReason);
        Assert.Equal("TransientProviderFailure", attempts[0].FailureClass);
        Assert.Equal(AttemptState.Succeeded, attempts[1].State);

        // The route that ACTUALLY served each attempt, not the configured one. Capability-aware
        // routing substitutes models, and an attempt reporting the configured route describes an
        // execution that did not happen.
        Assert.Equal("llama3.1:8b", attempts[0].Model);
        Assert.Equal("qwen2.5:14b", attempts[1].Model);
    }

    [Fact]
    public void FinishingAnAttempt_ReleasesTheTaskForRetry()
    {
        var first = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        Assert.Null(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));

        _memory.FinishAttempt(first.Id, AttemptState.Failed);

        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));
    }

    /// <summary>A terminal attempt is final: finishing it twice must not resurrect or rewrite it.</summary>
    [Fact]
    public void FinishingATerminalAttempt_ChangesNothing()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        _memory.FinishAttempt(attempt.Id, AttemptState.Succeeded);
        _memory.FinishAttempt(attempt.Id, AttemptState.Failed, failureReason: "should not stick");

        var stored = Assert.Single(_memory.LoadAttempts("t1"));
        Assert.Equal(AttemptState.Succeeded, stored.State);
        Assert.Null(stored.FailureReason);
    }

    // ---- leases and reclaim ------------------------------------------------------------------------

    /// <summary>
    /// A worker that dies must not hold a task forever. An expired lease makes the task claimable
    /// again, which is the whole mechanism behind "no accepted task is silently lost".
    /// </summary>
    [Fact]
    public void AnExpiredLease_MakesTheTaskClaimableAgain()
    {
        var dead = _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1))!;

        var taken = _memory.TryClaimTask("t1", "m1", "worker-b", Lease);

        Assert.NotNull(taken);
        Assert.Equal(2, taken!.Number);
        Assert.NotEqual(dead.Id, taken.Id);
    }

    [Fact]
    public void RenewingALease_KeepsTheTaskHeld()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1))!;

        _memory.RenewLease(attempt.Id, Lease);

        Assert.Null(_memory.TryClaimTask("t1", "m1", "worker-b", Lease));
    }

    [Fact]
    public void RenewingATerminalAttempt_DoesNotReviveIt()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        _memory.FinishAttempt(attempt.Id, AttemptState.Succeeded);

        _memory.RenewLease(attempt.Id, Lease);

        Assert.Equal(AttemptState.Succeeded, Assert.Single(_memory.LoadAttempts("t1")).State);
    }

    /// <summary>
    /// ABANDONED, not Failed — and the distinction is the one this whole record type exists to
    /// preserve. Nobody observed a failure. The attempt may well have SUCCEEDED and died before
    /// saying so, which is exactly why its side effects cannot be assumed absent. Calling it failed
    /// would invite a retry that duplicates work already done.
    /// </summary>
    [Fact]
    public void ReclaimingExpiredWork_MarksItAbandoned_NotFailed()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1));

        var reclaimed = Assert.Single(_memory.ReclaimExpiredAttempts());

        Assert.Equal(AttemptState.Abandoned, reclaimed.State);
        Assert.Equal(AttemptState.Abandoned, Assert.Single(_memory.LoadAttempts("t1")).State);
    }

    [Fact]
    public void ReclaimingLeavesLiveWorkAlone()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", Lease);

        Assert.Empty(_memory.ReclaimExpiredAttempts());
        Assert.Equal(AttemptState.Running, Assert.Single(_memory.LoadAttempts("t1")).State);
    }

    /// <summary>Reclaim runs at every startup, so running it twice must not re-report finished work.</summary>
    [Fact]
    public void ReclaimIsIdempotent()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1));

        Assert.Single(_memory.ReclaimExpiredAttempts());
        Assert.Empty(_memory.ReclaimExpiredAttempts());
    }

    // ---- redelivery, and the promise code cannot keep ----------------------------------------------

    /// <summary>
    /// "Expired work is reclaimed without duplicate retained side effects" is not a promise code can
    /// keep by trying harder. An attempt that died mid-write may have completed the write, and
    /// nothing observable distinguishes that from one that died before it. So the safe automatic
    /// answer is: read-only work is redelivered freely; work that MAY have left effects is not, and
    /// waits for an operator who can look.
    /// </summary>
    [Fact]
    public void ReadOnlyWork_IsSafeToRedeliver()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1));

        Assert.True(Assert.Single(_memory.ReclaimExpiredAttempts()).SafeToRedeliver);
    }

    [Fact]
    public void WorkThatMayHaveLeftEffects_IsNotSafeToRedeliver()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMilliseconds(-1))!;
        _memory.MarkAttemptSideEffecting(attempt.Id);

        Assert.False(Assert.Single(_memory.ReclaimExpiredAttempts()).SafeToRedeliver);
    }

    /// <summary>
    /// The flag is set BEFORE the side effect, never after. An attempt that dies mid-write is the
    /// entire case it exists for, and a dead attempt records nothing — so a flag written on
    /// completion would be absent in precisely the situation it is meant to describe.
    /// </summary>
    [Fact]
    public void TheSideEffectFlag_SurvivesAnAttemptThatNeverFinished()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;
        _memory.MarkAttemptSideEffecting(attempt.Id);

        Assert.True(Assert.Single(_memory.LoadAttempts("t1")).MayHaveSideEffects);
    }

    // ---- persistence ---------------------------------------------------------------------------

    /// <summary>
    /// The gate is about surviving a crash, so the attempt has to survive the process. Reopened from
    /// disk rather than read back from the same instance — a cache would answer correctly either way.
    /// </summary>
    [Fact]
    public void AttemptsSurviveARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-attempts-restart-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "memory.db");
        try
        {
            string id;
            using (var first = new SqliteMemory(db))
            {
                var attempt = first.TryClaimTask("t1", "m1", "worker-a", Lease)!;
                id = attempt.Id;
                first.MarkAttemptSideEffecting(id);
                first.FinishAttempt(id, AttemptState.Failed,
                    provider: "ollama", model: "llama3.1:8b",
                    failureClass: "ToolFailure", failureReason: "patch rejected");
            }

            using var reopened = new SqliteMemory(db);
            var stored = Assert.Single(reopened.LoadAttempts("t1"));

            Assert.Equal(id, stored.Id);
            Assert.Equal(AttemptState.Failed, stored.State);
            Assert.Equal("patch rejected", stored.FailureReason);
            Assert.True(stored.MayHaveSideEffects);
            Assert.Null(stored.LeaseUntil);      // terminal attempts hold no lease
            Assert.NotNull(stored.FinishedAt);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AttemptsAreReadableByMission()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", Lease);
        _memory.TryClaimTask("t2", "m1", "worker-a", Lease);
        _memory.TryClaimTask("t3", "m2", "worker-a", Lease);

        Assert.Equal(2, _memory.LoadMissionAttempts("m1").Count);
        Assert.Single(_memory.LoadMissionAttempts("m2"));
    }

    /// <summary>
    /// An unreadable state reads as Abandoned. Fail closed: "we do not know how this ended" is much
    /// closer to abandoned than to done, and the cost of the two mistakes is not symmetric — calling
    /// an unknown ending "succeeded" silently drops work.
    /// </summary>
    [Fact]
    public void AnUnreadableState_ReadsAsAbandoned()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", Lease)!;

        // Written straight to the file, because there is no public way to store a state the enum
        // cannot express — which is the point. The row this defends against comes from a future
        // build, a partial migration or a hand-edit, never from this API.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = _memory.DbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE task_attempts SET state='nonsense' WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", attempt.Id);
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(AttemptState.Abandoned, Assert.Single(_memory.LoadAttempts("t1")).State);
    }
}
