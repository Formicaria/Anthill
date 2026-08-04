namespace Anthill.Core.Workers;

/// <summary>Where one attempt at one task got to.</summary>
public enum AttemptState
{
    /// <summary>Claimed by a worker, not yet finished. The only state a lease applies to.</summary>
    Running = 0,

    Succeeded,
    Failed,

    /// <summary>
    /// The worker holding it stopped reporting and its lease expired. Distinct from
    /// <see cref="Failed"/> because nobody observed a failure — the attempt may well have SUCCEEDED
    /// and died before saying so, which is precisely why its side effects cannot be assumed absent.
    /// </summary>
    Abandoned,
}

/// <summary>
/// v3.8.0 — one attempt at one task, recorded whole.
///
/// The phase's gate is "every retry is a distinct attempt with a durable reason", and the word doing
/// the work is DISTINCT. A retry counter on the task tells you something was tried three times; it
/// cannot tell you that the first failed on a timeout, the second on a provider fault, and the third
/// produced a change nobody has looked at. Those are three different facts about three different
/// executions, and collapsing them into a number is how a task that half-succeeded becomes
/// indistinguishable from one that never ran.
///
/// So each attempt is its own row, carrying what it was given, what it produced, which model served
/// it, and how it ended.
/// </summary>
public sealed record TaskAttempt
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string MissionId { get; init; }

    /// <summary>1-based. The Nth attempt at this task, not a global counter.</summary>
    public required int Number { get; init; }

    /// <summary>Who is (or was) executing it.</summary>
    public required string WorkerId { get; init; }

    public AttemptState State { get; init; } = AttemptState.Running;

    /// <summary>
    /// The route that ACTUALLY served this attempt, not the one configured. Capability-aware routing
    /// substitutes models, and an attempt that reports the configured route describes an execution
    /// that did not happen — the same reason conversation turns record their route per turn.
    /// </summary>
    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>
    /// Whether this attempt may have left effects outside the process. Set when the attempt STARTS
    /// something side-effecting, not when it finishes — an attempt that died mid-write is exactly
    /// the case this flag exists for, and it cannot record anything after it dies.
    /// </summary>
    public bool MayHaveSideEffects { get; init; }

    public string? FailureClass { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>
    /// When this attempt's claim expires. A worker keeps it alive by heartbeat; past this instant
    /// the attempt is reclaimable. Null once the attempt is terminal.
    /// </summary>
    public DateTime? LeaseUntil { get; init; }

    public DateTime StartedAt { get; init; } = Common.AnthillTime.NowUtc();
    public DateTime? FinishedAt { get; init; }

    public bool IsTerminal => State is not AttemptState.Running;

    /// <summary>Whether the lease has lapsed as of <paramref name="now"/>.</summary>
    public bool LeaseExpired(DateTime now) =>
        State == AttemptState.Running && LeaseUntil is { } until && until <= now;

    /// <summary>
    /// Whether reclaiming this is safe to do automatically.
    ///
    /// The exit gate says expired work must be reclaimed "without duplicate retained side effects",
    /// and that is not a promise code can keep by trying harder: an attempt that died mid-write may
    /// have completed the write. So an abandoned attempt that MAY have left effects is NOT
    /// automatically redeliverable — it is reclaimable only by an operator who can look.
    ///
    /// Read-only work has no such problem and is redelivered freely, which is the common case and
    /// the reason this distinction is worth drawing rather than blocking everything.
    /// </summary>
    public bool SafeToRedeliver => !MayHaveSideEffects;
}

/// <summary>
/// v3.8.0 — a worker, and what it is allowed to pick up.
///
/// Separated from the local implementation deliberately: the phase asks that "a future remote worker
/// does not require scheduler redesign", and the only way to keep that promise is for the scheduler
/// to know workers by this record rather than by any in-process object it can call directly.
/// </summary>
public sealed record WorkerRegistration
{
    public required string Id { get; init; }

    /// <summary>Roles this worker can execute. Empty means none — fail closed, as everywhere else.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>local | remote. Recorded now so the distinction exists before it is needed.</summary>
    public string Kind { get; init; } = "local";

    /// <summary>
    /// How many tasks it will take at once. An operator ceiling, never raised by the colony —
    /// Quartermaster may LOWER effective concurrency but must not exceed what a human allowed.
    /// </summary>
    public int MaxConcurrent { get; init; } = 1;

    public DateTime? LastHeartbeat { get; init; }
    public DateTime RegisteredAt { get; init; } = Common.AnthillTime.NowUtc();

    /// <summary>
    /// Whether this worker has reported recently enough to be given work.
    ///
    /// A worker that has never heartbeated is NOT available. Silence at registration and silence
    /// after a crash are indistinguishable from outside, and handing work to the second one is how a
    /// task is silently lost.
    /// </summary>
    public bool IsAvailable(DateTime now, TimeSpan within) =>
        LastHeartbeat is { } beat && now - beat <= within;

    public bool CanRun(string? role) =>
        role is not null && Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}
