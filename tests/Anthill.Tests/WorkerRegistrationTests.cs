using Anthill.Core.Memory;
using Anthill.Core.Workers;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.0 — the worker protocol, kept separate from the local worker on purpose.
///
/// The phase asks that "a future remote worker does not require scheduler redesign". The only way to
/// keep that promise is for the scheduler to know a worker by a RECORD rather than by an in-process
/// object it can call directly — the moment dispatch depends on holding the worker in memory, remote
/// workers become a rewrite rather than an addition.
/// </summary>
public class WorkerRegistrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    public WorkerRegistrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-workers-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WorkerRegistration Worker(string id = "worker-a", params string[] roles) => new()
    {
        Id = id,
        Roles = roles.Length > 0 ? roles : new[] { "coder" },
        MaxConcurrent = 2,
        RegisteredAt = Now,
    };

    // ---- availability ---------------------------------------------------------------------------

    /// <summary>
    /// A worker that has NEVER heartbeated is not available.
    ///
    /// This is the load-bearing case and the easy one to get wrong. Silence at registration and
    /// silence after a crash look identical from outside, so treating "no heartbeat yet" as healthy
    /// means the scheduler will hand work to a process that may not exist — which is precisely how a
    /// task is accepted and then silently lost.
    /// </summary>
    [Fact]
    public void AWorkerThatHasNeverReported_IsNotAvailable() =>
        Assert.False(Worker().IsAvailable(Now, TimeSpan.FromMinutes(1)));

    [Fact]
    public void AWorkerThatReportedRecently_IsAvailable() =>
        Assert.True((Worker() with { LastHeartbeat = Now.AddSeconds(-10) })
            .IsAvailable(Now, TimeSpan.FromMinutes(1)));

    [Fact]
    public void AWorkerThatWentQuiet_IsNotAvailable() =>
        Assert.False((Worker() with { LastHeartbeat = Now.AddMinutes(-5) })
            .IsAvailable(Now, TimeSpan.FromMinutes(1)));

    /// <summary>Exactly at the boundary counts as alive — a heartbeat that arrived on time is on time.</summary>
    [Fact]
    public void TheHeartbeatWindow_IsInclusive() =>
        Assert.True((Worker() with { LastHeartbeat = Now.AddMinutes(-1) })
            .IsAvailable(Now, TimeSpan.FromMinutes(1)));

    // ---- what it may pick up ---------------------------------------------------------------------

    [Fact]
    public void AWorkerRunsOnlyTheRolesItDeclares()
    {
        var worker = Worker("worker-a", "coder", "verifier");

        Assert.True(worker.CanRun("coder"));
        Assert.True(worker.CanRun("VERIFIER"));   // role names are compared case-insensitively
        Assert.False(worker.CanRun("archivist"));
    }

    /// <summary>Fail closed, as everywhere else: no declared roles means no work, and null is not a role.</summary>
    [Fact]
    public void AWorkerWithNoRoles_RunsNothing()
    {
        var worker = new WorkerRegistration { Id = "empty" };

        Assert.False(worker.CanRun("coder"));
        Assert.False(worker.CanRun(null));
    }

    // ---- persistence -----------------------------------------------------------------------------

    [Fact]
    public void AWorkerSurvivesARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-workers-restart-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "memory.db");
        try
        {
            using (var first = new SqliteMemory(db))
                first.SaveWorker(Worker("worker-a", "coder", "builder"));

            using var reopened = new SqliteMemory(db);
            var stored = Assert.Single(reopened.LoadWorkers());

            Assert.Equal("worker-a", stored.Id);
            Assert.Equal(new[] { "coder", "builder" }, stored.Roles);
            Assert.Equal(2, stored.MaxConcurrent);
            Assert.Equal("local", stored.Kind);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Re-registering updates rather than duplicating. A worker restarts and registers again; two
    /// rows for one worker would double its apparent capacity, which is the wrong direction for a
    /// mistake — the ceiling an operator set would be quietly exceeded.
    /// </summary>
    [Fact]
    public void ReRegistering_UpdatesTheSameWorker()
    {
        _memory.SaveWorker(Worker("worker-a", "coder"));
        _memory.SaveWorker(Worker("worker-a", "coder", "verifier") with { MaxConcurrent = 4 });

        var stored = Assert.Single(_memory.LoadWorkers());
        Assert.Equal(new[] { "coder", "verifier" }, stored.Roles);
        Assert.Equal(4, stored.MaxConcurrent);
    }

    /// <summary>
    /// Re-registration moves the registration time, because it describes a NEW process wearing the
    /// same identity.
    ///
    /// Found by reading the live table after a restart: the row still reported the previous
    /// process's start, so "how long has the worker holding this lease been up" — the question
    /// asked while diagnosing a crash — was answered about a process that no longer existed.
    /// </summary>
    [Fact]
    public void ReRegistering_MovesTheRegistrationTime()
    {
        _memory.SaveWorker(Worker("worker-a", "coder"));
        var restarted = Now.AddHours(3);

        _memory.SaveWorker(Worker("worker-a", "coder") with { RegisteredAt = restarted });

        Assert.Equal(restarted, Assert.Single(_memory.LoadWorkers()).RegisteredAt);
    }

    /// <summary>
    /// A heartbeat must not disturb anything else. It is the most frequent write in the system, and
    /// one that also rewrote roles or concurrency would let a stale heartbeat payload silently undo
    /// an operator's change.
    /// </summary>
    [Fact]
    public void AHeartbeat_TouchesOnlyTheHeartbeat()
    {
        _memory.SaveWorker(Worker("worker-a", "coder"));

        _memory.Heartbeat("worker-a", Now);

        var stored = Assert.Single(_memory.LoadWorkers());
        Assert.Equal(Now, stored.LastHeartbeat);
        Assert.Equal(new[] { "coder" }, stored.Roles);
        Assert.Equal(2, stored.MaxConcurrent);
    }

    [Fact]
    public void HeartbeatingAnUnknownWorker_IsHarmless()
    {
        _memory.Heartbeat("never-registered", Now);

        Assert.Empty(_memory.LoadWorkers());
    }

    [Fact]
    public void ARegistrationWithoutAnId_IsIgnored()
    {
        _memory.SaveWorker(new WorkerRegistration { Id = "  " });

        Assert.Empty(_memory.LoadWorkers());
    }
}
