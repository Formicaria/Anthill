using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.8.0 Durable Mission Runtime (NORTH_STAR V3-track Phase 1 required tests). Process death is
/// simulated the way it actually manifests: a NEW SqliteMemory instance opened over the same
/// database file, with whatever state the "dead" process left behind. Success criteria under test:
/// no accepted mission disappears, no double launch, expired work reclaimed, completed work never
/// repeated, replayed idempotency keys never duplicate, and recovery can explain every job.
/// </summary>
public class DurableMissionRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_dmr_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string DbPath()
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
    }

    // ---- Kill while queued: the mission survives restart ---------------------------------------

    [Fact]
    public void KilledWhileQueued_JobSurvivesRestart_AsResumable()
    {
        var db = DbPath();
        using (var mem = new SqliteMemory(db))
            mem.PersistNewJob("j1", "fix the flux capacitor", null);
        // "restart"
        using var mem2 = new SqliteMemory(db);
        var (resumable, retried, orphaned, cancelled) = mem2.ReconcileJobsAtStartup();
        Assert.Equal(1, resumable);
        Assert.Equal(0, orphaned);
        var job = mem2.GetMissionJob("j1");
        Assert.NotNull(job);
        Assert.Equal("queued", job!.Status); // still dispatchable — nothing lost
    }

    // ---- Kill while a worker owns the lease: retried with attempt history ----------------------

    [Fact]
    public void KilledMidRun_IsRetriedWithNewAttempt_AndExplains()
    {
        var db = DbPath();
        using (var mem = new SqliteMemory(db))
        {
            mem.PersistNewJob("j1", "goal", null);
            Assert.NotNull(mem.TryClaimJob("j1", "worker-0", leaseSeconds: 60)); // running when "killed"
        }
        using var mem2 = new SqliteMemory(db);
        var (_, retried, _, _) = mem2.ReconcileJobsAtStartup();
        Assert.Equal(1, retried);
        var job = mem2.GetMissionJob("j1")!;
        Assert.Equal("queued", job.Status);
        Assert.Equal(2, job.Attempt);                       // separate attempt, same mission identity
        Assert.Contains("recovered", job.Reason ?? "");     // runtime can explain the incomplete mission
    }

    [Fact]
    public void AttemptsExhausted_BecomesOrphaned_ForOperatorReview_NeverSilentlyLost()
    {
        var db = DbPath();
        using (var mem = new SqliteMemory(db))
        {
            mem.PersistNewJob("j1", "goal", null);
            mem.TryClaimJob("j1", "w", 60);
        }
        for (var i = 0; i < 2; i++) // die twice more
        {
            using var m = new SqliteMemory(db);
            m.ReconcileJobsAtStartup(maxAttempts: 3);
            var j = m.GetMissionJob("j1")!;
            if (j.Status == "queued") m.TryClaimJob("j1", "w", 60);
        }
        using var mem3 = new SqliteMemory(db);
        var (_, _, orphaned, _) = mem3.ReconcileJobsAtStartup(maxAttempts: 3);
        Assert.Equal(1, orphaned);
        var job = mem3.GetMissionJob("j1")!;
        Assert.Equal("failed", job.Status);
        Assert.Contains("operator review", job.Reason ?? "");
    }

    // ---- Atomic claims: two Directors cannot double-launch -------------------------------------

    [Fact]
    public void TwoClaimants_ExactlyOneWins()
    {
        var db = DbPath();
        using var mem = new SqliteMemory(db);
        mem.PersistNewJob("j1", "goal", null);
        var wins = new[] { "a", "b" }.AsParallel()
            .Select(w => mem.TryClaimJob("j1", w, 60)).Count(r => r is not null);
        Assert.Equal(1, wins);
    }

    [Fact]
    public void TwoDirectors_SeparateProcesses_SameDatabase_NoDoubleLaunch()
    {
        var db = DbPath();
        using (var seed = new SqliteMemory(db)) seed.PersistNewJob("j1", "goal", null);
        using var d1 = new SqliteMemory(db);
        using var d2 = new SqliteMemory(db);
        var first = d1.TryClaimJob("j1", "director-1", 60);
        var second = d2.TryClaimJob("j1", "director-2", 60);
        Assert.NotNull(first);
        Assert.Null(second); // already running — refused
    }

    // ---- Idempotency: replaying the same key never duplicates ----------------------------------

    [Fact]
    public void IdempotencyKeyReplay_ReturnsOriginal_CreatesNothing()
    {
        using var mem = new SqliteMemory(DbPath());
        var (first, r1) = mem.PersistNewJob("j1", "deploy the thing", "key-42");
        var (second, r2) = mem.PersistNewJob("j2", "deploy the thing", "key-42");
        Assert.False(r1);
        Assert.True(r2);
        Assert.Equal(first.Id, second.Id);           // the original job, not a twin
        Assert.Single(mem.ListMissionJobs(10));      // exactly one row exists
    }

    // ---- Completed work is never repeated -------------------------------------------------------

    [Fact]
    public void CompletedJob_UntouchedByRecovery_AndUnclaimable()
    {
        var db = DbPath();
        using (var mem = new SqliteMemory(db))
        {
            mem.PersistNewJob("j1", "goal", null);
            mem.TryClaimJob("j1", "w", 60);
            mem.UpdateJobState("j1", "complete", result: "done", outcome: "completed", finished: true);
        }
        using var mem2 = new SqliteMemory(db);
        var (resumable, retried, orphaned, _) = mem2.ReconcileJobsAtStartup();
        Assert.Equal((0, 0, 0), (resumable, retried, orphaned));
        Assert.Null(mem2.TryClaimJob("j1", "w2", 60)); // terminal work cannot be re-run
        Assert.Equal("complete", mem2.GetMissionJob("j1")!.Status);
    }

    // ---- Cancellation survives the crash --------------------------------------------------------

    [Fact]
    public void CancelRequestedBeforeCrash_HonoredByRecovery()
    {
        var db = DbPath();
        using (var mem = new SqliteMemory(db))
        {
            mem.PersistNewJob("j1", "goal", null);
            mem.UpdateJobState("j1", "queued", cancelRequested: true);
        }
        using var mem2 = new SqliteMemory(db);
        var (_, _, _, cancelled) = mem2.ReconcileJobsAtStartup();
        Assert.Equal(1, cancelled);
        Assert.Equal("cancelled", mem2.GetMissionJob("j1")!.Status);
        Assert.Null(mem2.TryClaimJob("j1", "w", 60));
    }

    // ---- Leases ---------------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_OnlyRenewsForTheOwningWorker()
    {
        using var mem = new SqliteMemory(DbPath());
        mem.PersistNewJob("j1", "goal", null);
        mem.TryClaimJob("j1", "worker-a", 60);
        Assert.True(mem.HeartbeatJob("j1", "worker-a", 60));
        Assert.False(mem.HeartbeatJob("j1", "worker-b", 60)); // not yours
        Assert.False(mem.HeartbeatJob("nope", "worker-a", 60));
    }
}
