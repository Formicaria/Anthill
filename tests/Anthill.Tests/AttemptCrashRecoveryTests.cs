using Anthill.Core.Memory;
using Anthill.Core.Workers;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.0 — the six crash points the phase names, and what the next process may do about each.
///
/// The roadmap asks for fault coverage of a crash "before execution, during the model call, during a
/// tool call, after a change, during verification, and during cleanup". Those six are not six bugs;
/// they are one question asked at six moments — <em>had this attempt already touched anything
/// outside the process?</em> — and the honest answer differs at each.
///
/// A crash is simulated by leaving exactly what a killed process leaves: an attempt still marked
/// Running, with a lease that has lapsed because nobody is renewing it. That is the whole observable
/// residue of a crash. Nothing else survives, which is precisely why the recovery decision cannot
/// depend on anything else.
///
/// The load-bearing claim being tested is a NEGATIVE one: for the two moments where effects may
/// already exist, recovery must refuse to redeliver automatically. "Reclaimed without duplicate
/// retained side effects" is not a promise code can keep by trying harder — an attempt that died
/// mid-write may have completed the write, and nothing observable distinguishes that from one that
/// died just before it. So the only safe automatic answer is to stop and let a human look.
/// </summary>
public class AttemptCrashRecoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public AttemptCrashRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-crash-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// A process that died holding this task.
    ///
    /// The lease is taken already-expired rather than slept through, because the passage of time is
    /// not what this is testing — a test that waits for a real lease to lapse is slow and, worse,
    /// flaky on a loaded machine, which teaches people to rerun it rather than read it.
    /// </summary>
    private TaskAttempt Crashed(string taskId, bool afterTouchingSomething)
    {
        var attempt = _memory.TryClaimTask(taskId, "m1", "worker-that-died", TimeSpan.FromMilliseconds(-1))!;
        if (afterTouchingSomething) _memory.MarkAttemptSideEffecting(attempt.Id);
        return attempt;
    }

    // ---- the four moments where nothing outside the process was touched -------------------------

    [Theory]
    // Claimed, then the process died before the ant ran at all.
    [InlineData("crash_before_execution")]
    // Died waiting on the model. Tokens may have been spent; nothing in the world changed.
    [InlineData("crash_during_model_call")]
    // Died inside a READ-ONLY tool call — a search, a directory listing, an index lookup.
    [InlineData("crash_during_read_only_tool_call")]
    // Died while verifying. Verification observes; it does not modify what it observes.
    [InlineData("crash_during_verification")]
    public void ACrashThatTouchedNothing_IsSafeToRedeliver(string taskId)
    {
        Crashed(taskId, afterTouchingSomething: false);

        var reclaimed = Assert.Single(_memory.ReclaimExpiredAttempts());

        Assert.Equal(AttemptState.Abandoned, reclaimed.State);
        Assert.True(reclaimed.SafeToRedeliver);
    }

    // ---- the two where it may have -----------------------------------------------------------------

    [Theory]
    // Died after applying a change. The change may be on disk; re-running would apply it twice.
    [InlineData("crash_after_change")]
    // Died during cleanup. A half-removed workspace is the definition of state nobody can assume about.
    [InlineData("crash_during_cleanup")]
    public void ACrashThatMayHaveLeftEffects_IsNotRedeliveredAutomatically(string taskId)
    {
        Crashed(taskId, afterTouchingSomething: true);

        var reclaimed = Assert.Single(_memory.ReclaimExpiredAttempts());

        Assert.Equal(AttemptState.Abandoned, reclaimed.State);
        Assert.False(reclaimed.SafeToRedeliver);
    }

    // ---- what recovery must and must not conclude ---------------------------------------------------

    /// <summary>
    /// Abandoned, never Failed — and this is the distinction the whole record type exists to keep.
    ///
    /// Nobody observed a failure. The attempt may well have SUCCEEDED and died before saying so, so
    /// calling it failed would invite a retry of work that is already done. "We do not know how this
    /// ended" is a different fact from "this did not work", and only one of them is true here.
    /// </summary>
    [Fact]
    public void RecoveryNeverConcludesThatCrashedWorkFailed()
    {
        Crashed("t1", afterTouchingSomething: false);

        _memory.ReclaimExpiredAttempts();

        Assert.Equal(AttemptState.Abandoned, Assert.Single(_memory.LoadAttempts("t1")).State);
    }

    /// <summary>
    /// The gate: no accepted task is silently lost. After recovery the task must be claimable again,
    /// or a crash would strand it forever behind a lease held by a process that no longer exists.
    /// </summary>
    [Fact]
    public void AfterRecovery_TheTaskCanBePickedUpAgain()
    {
        Crashed("t1", afterTouchingSomething: false);
        _memory.ReclaimExpiredAttempts();

        var retry = _memory.TryClaimTask("t1", "m1", "worker-alive", TimeSpan.FromMinutes(5));

        Assert.NotNull(retry);
        Assert.Equal(2, retry!.Number);
    }

    /// <summary>
    /// And the crashed attempt is still THERE afterwards. The evidence of what happened is the point:
    /// an operator asking why a mission stopped halfway needs the abandoned row, not a clean slate
    /// that reads as though the work was never attempted.
    /// </summary>
    [Fact]
    public void TheCrashedAttempt_SurvivesAsEvidence()
    {
        Crashed("t1", afterTouchingSomething: true);
        _memory.ReclaimExpiredAttempts();
        _memory.TryClaimTask("t1", "m1", "worker-alive", TimeSpan.FromMinutes(5));

        var attempts = _memory.LoadAttempts("t1");
        Assert.Equal(2, attempts.Count);
        Assert.Equal(AttemptState.Abandoned, attempts[0].State);
        Assert.True(attempts[0].MayHaveSideEffects);
        Assert.Equal("worker-that-died", attempts[0].WorkerId);
    }

    /// <summary>
    /// A crash mid-mission must not take unrelated work with it. Only the attempts whose leases
    /// lapsed are reclaimed — a sweep that grabbed everything Running would abandon tasks that are
    /// executing perfectly well in another process.
    /// </summary>
    [Fact]
    public void RecoveryTouchesOnlyTheWorkThatWasActuallyLost()
    {
        Crashed("dead-task", afterTouchingSomething: false);
        var live = _memory.TryClaimTask("live-task", "m1", "worker-alive", TimeSpan.FromMinutes(30))!;

        var reclaimed = _memory.ReclaimExpiredAttempts();

        Assert.Equal("dead-task", Assert.Single(reclaimed).TaskId);
        Assert.Equal(AttemptState.Running, Assert.Single(_memory.LoadAttempts("live-task")).State);
        Assert.Equal(live.Id, _memory.LoadAttempts("live-task")[0].Id);
    }

    // ---- the gap the expiry sweep cannot close ------------------------------------------------------

    /// <summary>
    /// A crash does NOT expire the lease, and that is the case the expiry sweep structurally misses.
    ///
    /// Found by reasoning about a restart that printed no recovery line: a process killed mid-task
    /// leaves its attempt Running with almost the whole lease still on the clock — thirty minutes in
    /// this build — so the sweep at startup correctly finds nothing, and the task stays stranded for
    /// the remainder of a lease held by a process that no longer exists. "No accepted task is
    /// silently lost after crash or restart" was true only in the sense that it would come back
    /// half an hour later, which for an operator watching a stalled mission is not true at all.
    /// </summary>
    [Fact]
    public void AFreshlyCrashedAttempt_IsInvisibleToTheExpirySweep()
    {
        _memory.TryClaimTask("t1", "m1", "worker-that-died", TimeSpan.FromMinutes(30));

        Assert.Empty(_memory.ReclaimExpiredAttempts());
        Assert.Equal(AttemptState.Running, Assert.Single(_memory.LoadAttempts("t1")).State);
    }

    /// <summary>
    /// So a restarting worker reclaims its OWN orphans immediately, lease or no lease.
    ///
    /// The inference is sound only about itself: if this process is starting up wearing this id,
    /// any attempt still Running under that id belongs to a previous incarnation that is definitively
    /// gone. Nobody may make that inference about another worker, which is why the reclaim is scoped
    /// to an id rather than sweeping everything Running.
    /// </summary>
    [Fact]
    public void ARestartingWorker_ReclaimsItsOwnOrphansImmediately()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMinutes(30));

        var reclaimed = Assert.Single(_memory.ReclaimOwnAttempts("worker-a"));

        Assert.Equal(AttemptState.Abandoned, reclaimed.State);
        Assert.NotNull(_memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMinutes(30)));
    }

    /// <summary>
    /// And it must not touch anyone else's live work. A second colony sharing this database is
    /// running perfectly well, and a sweep that took every Running row would abandon its tasks
    /// mid-execution — turning one process's restart into another's data loss.
    /// </summary>
    [Fact]
    public void ARestartingWorker_LeavesOtherWorkersWorkAlone()
    {
        _memory.TryClaimTask("mine", "m1", "worker-a", TimeSpan.FromMinutes(30));
        _memory.TryClaimTask("theirs", "m1", "worker-b", TimeSpan.FromMinutes(30));

        var reclaimed = Assert.Single(_memory.ReclaimOwnAttempts("worker-a"));

        Assert.Equal("mine", reclaimed.TaskId);
        Assert.Equal(AttemptState.Running, Assert.Single(_memory.LoadAttempts("theirs")).State);
    }

    /// <summary>Reclaiming its own orphans still respects the side-effect boundary.</summary>
    [Fact]
    public void AnOwnOrphanThatTouchedSomething_StillWaitsForAPerson()
    {
        var attempt = _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMinutes(30))!;
        _memory.MarkAttemptSideEffecting(attempt.Id);

        Assert.False(Assert.Single(_memory.ReclaimOwnAttempts("worker-a")).SafeToRedeliver);
    }

    [Fact]
    public void ReclaimingOwnOrphans_IsIdempotentAndHarmlessWhenThereAreNone()
    {
        _memory.TryClaimTask("t1", "m1", "worker-a", TimeSpan.FromMinutes(30));

        Assert.Single(_memory.ReclaimOwnAttempts("worker-a"));
        Assert.Empty(_memory.ReclaimOwnAttempts("worker-a"));
        Assert.Empty(_memory.ReclaimOwnAttempts("never-existed"));
        Assert.Empty(_memory.ReclaimOwnAttempts(""));
    }

    /// <summary>
    /// The whole point of persisting this: recovery is performed by the NEXT process, not the one
    /// that died. Reopened from disk, because an in-memory sweep would prove nothing about a crash.
    /// </summary>
    [Fact]
    public void TheNextProcess_RecoversWhatThePreviousOneLeft()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-crash-restart-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "memory.db");
        try
        {
            using (var dying = new SqliteMemory(db))
            {
                var attempt = dying.TryClaimTask("t1", "m1", "worker-that-died", TimeSpan.FromMilliseconds(-1))!;
                dying.MarkAttemptSideEffecting(attempt.Id);
                // No FinishAttempt. That absence IS the crash.
            }

            using var restarted = new SqliteMemory(db);
            var recovered = Assert.Single(restarted.ReclaimExpiredAttempts());

            Assert.Equal(AttemptState.Abandoned, recovered.State);
            Assert.False(recovered.SafeToRedeliver);
            Assert.Equal("worker-that-died", recovered.WorkerId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
