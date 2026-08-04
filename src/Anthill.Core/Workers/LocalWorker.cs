using Anthill.Core.Memory;

namespace Anthill.Core.Workers;

/// <summary>
/// v3.8.0 — the in-process worker, named and registered like any other.
///
/// The phase asks that "a future remote worker does not require scheduler redesign". That is only
/// true if the local worker is not special: the moment the scheduler can assume the executing worker
/// is in this process, adding a remote one becomes a rewrite. So the local worker registers a row,
/// heartbeats, and claims tasks through exactly the same surface a remote worker would use.
///
/// The id is stable across restarts within a machine+process-directory, so an operator reading the
/// attempt log sees continuity rather than a new stranger after every restart — but it is NOT
/// globally fixed, because two colonies sharing one database must not appear to be one worker.
/// </summary>
public static class LocalWorker
{
    /// <summary>
    /// Identity of the worker this process presents as. Derived from the machine and the database it
    /// serves, so the same deployment keeps one identity and two deployments cannot collide.
    /// </summary>
    public static string Id { get; private set; } = "local";

    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How stale a heartbeat may be before the worker is considered gone.
    ///
    /// Several beats of slack, not one. A single missed heartbeat is a busy process or a slow disk;
    /// declaring a worker dead on that basis would reclaim work that is still running, and doing the
    /// work twice is worse than noticing a crash a minute later.
    /// </summary>
    public static readonly TimeSpan AliveWithin = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Register this process as a worker and report it alive.
    ///
    /// Roles are the ones this build can actually execute. Declaring roles it cannot run would be
    /// worse than declaring none: the scheduler would hand it work it must then refuse, which reads
    /// as a failing task rather than a misdeclared worker.
    /// </summary>
    public static WorkerRegistration Register(SqliteMemory memory, string id,
        IReadOnlyList<string> roles, int maxConcurrent)
    {
        Id = string.IsNullOrWhiteSpace(id) ? "local" : id.Trim();

        var now = Common.AnthillTime.NowUtc();
        var worker = new WorkerRegistration
        {
            Id = Id,
            Roles = roles ?? Array.Empty<string>(),
            Kind = "local",
            // Never below one: a worker that may take no work is not a worker, and a zero here would
            // stall the colony in a way that looks like a scheduling bug rather than a setting.
            MaxConcurrent = Math.Max(1, maxConcurrent),
            // Registration IS a report of life. Leaving this null would make a freshly registered
            // worker unavailable by its own availability rule, which is correct for silence after a
            // crash and wrong for a process that is demonstrably running right now.
            LastHeartbeat = now,
            RegisteredAt = now,
        };

        memory.SaveWorker(worker);
        return worker;
    }
}
