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

    // ---- the execution record ------------------------------------------------------------------
    //
    // v0.3.8.139 — WHAT THE MISSION DID, KEPT AFTER THE PROCESS THAT DID IT IS GONE.
    //
    // `docs/PLAN.md` §2e has said for eleven releases that items 3 through 8 — authoritative
    // execution records, artifact and evidence handoff, verification that reads execution rather
    // than a narrative, closure ENFORCEMENT, unsourced-claim rejection — "all consume the same
    // missing row", and that `.122` did not add it. This is that row, and the surprise is that it
    // already existed: `task_attempts` has been live and load-bearing since `v3.8.0`, because the
    // atomic claim runs on it. What it did not carry was any fact about what the attempt DID.
    //
    // SO THE FIELDS BELOW ARE NOT NEW INFORMATION. Every one of them is already computed, already
    // correct, and already on `Domain.Task` — where four of them are marked TRANSIENT in their own
    // doc comments, meaning the object holds them and the `tasks` row does not. A restart therefore
    // forgot WHY a worker was chosen, WHICH deliverable a task served, WHAT capability it was
    // required to have, and WHICH TREE a check actually ran in. Those are exactly the facts a later
    // stage needs in order to say a mission genuinely closed, and every one of them died with the
    // process.
    //
    // AND A SECOND TABLE WOULD HAVE BEEN THE WRONG ANSWER. The plan describes "one record per task
    // attempt, written where the scheduler already writes the terminal state" — which describes
    // this table exactly, and building a parallel one beside it would be two records of one thing:
    // defect #5 in this repository's own list, shipped deliberately. Extending the row that the
    // claim already writes keeps one answer to "what happened on attempt N".
    //
    // NULL IS "NOT RECORDED", NEVER "NO". Legacy rows predate the columns and are migrated in place
    // with no values, so nothing here may be read as a negative claim — the same non-retroactive
    // rule `evidence.revision_id` and `patch_sets.base_fingerprint` already follow. A gate reading
    // these to enforce closure must treat absence as unmeasurable, or it will refuse every mission
    // that ran before this release.

    /// <summary>The role and task type this attempt executed, so the row can be read without
    /// joining back to a `tasks` row that a later plan may have re-typed.</summary>
    public string? AssignedAnt { get; init; }
    public string? TaskType { get; init; }

    /// <summary>
    /// HOW the worker was chosen — `WorkerDecisionBasis` as a string, kept as text because the row
    /// outlives the enum. `.126` made this decision appealable or final depending on its basis, and
    /// the basis was the first thing a restart forgot.
    /// </summary>
    public string? WorkerBasis { get; init; }

    /// <summary>The deliverable ids this task served, as the plan declared them. The DECLARED claim
    /// is what `AssembledAnswer` grades a section on, and it is a property of the plan that a
    /// restored task cannot reconstruct.</summary>
    public IReadOnlyList<string> DeliverableIds { get; init; } = Array.Empty<string>();

    /// <summary>The capability the plan required of whoever served this. Transient on the task.</summary>
    public string? RequiredCapability { get; init; }

    /// <summary>
    /// Whether the model's generation was degraded on this attempt — truncated, retried down a
    /// fallback route, or otherwise not what was asked for. A completed task whose generation was
    /// degraded is a different fact from a completed task, and the evaluator has consumed it live
    /// since it existed while nothing could read it afterwards.
    /// </summary>
    public bool GenerationDegraded { get; init; }

    /// <summary>
    /// REVISION LINEAGE. `ProducedRevisionId` is stamped on the task whose patch set was
    /// materialized into a mission revision; `RanRevisionId` on a deterministic check task, naming
    /// the tree it actually judged. Both null means the unpatched mission workspace, which is
    /// exactly the "true statement about the wrong tree" case `v3.8.22` shipped — and until now the
    /// only record of which tree an attempt judged lived on an object that did not survive.
    /// </summary>
    public string? ProducedRevisionId { get; init; }
    public string? RanRevisionId { get; init; }

    /// <summary>
    /// When this attempt's claim expires. A worker keeps it alive by heartbeat; past this instant
    /// the attempt is reclaimable. Null once the attempt is terminal.
    /// </summary>
    public DateTime? LeaseUntil { get; init; }

    public DateTime StartedAt { get; init; } = AnthillTime.NowUtc();
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
    public DateTime RegisteredAt { get; init; } = AnthillTime.NowUtc();

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

/// <summary>
/// WHAT AN ATTEMPT DID, HANDED TO THE STORE AS ONE THING. v0.3.8.139.
///
/// A parameter object rather than eight more optional arguments on <c>FinishAttempt</c>, and that
/// is not tidiness. The facts arrive together, from one <c>Domain.Task</c>, at one call site; eight
/// nullable positional arguments is a signature where transposing two strings compiles, produces a
/// row whose worker basis is a capability id, and is discovered by a human reading a report months
/// later. `docs/GUARDS.md`' standing rule is that a wrong value must be hard to write, not merely
/// visible once written.
///
/// IT IS BUILT FROM THE TASK, by <see cref="From"/>, and never assembled field by field at a call
/// site. One constructor means "which facts belong in the execution record" is answered in one
/// place — the property this whole slice exists to establish, applied to itself.
/// </summary>
public sealed record AttemptExecutionRecord
{
    public string? AssignedAnt { get; init; }
    public string? TaskType { get; init; }
    public string? WorkerBasis { get; init; }
    public IReadOnlyList<string> DeliverableIds { get; init; } = Array.Empty<string>();
    public string? RequiredCapability { get; init; }
    public bool GenerationDegraded { get; init; }
    public string? ProducedRevisionId { get; init; }
    public string? RanRevisionId { get; init; }

    /// <summary>
    /// The record a finished task leaves.
    ///
    /// `WorkerBasis` is written as its enum NAME rather than its ordinal, because the row outlives
    /// the enum: an ordinal is correct until somebody inserts a member in the middle, at which point
    /// every historical row silently means something else. That has happened to this project's
    /// status codes before and is why they are text too.
    ///
    /// `Unset` is written as null rather than as the string "Unset": the basis was never decided,
    /// and "not recorded" is the honest reading of a decision nobody made — which keeps the column's
    /// null-means-unmeasurable rule true for the one value that would otherwise break it.
    /// </summary>
    public static AttemptExecutionRecord From(Domain.Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new AttemptExecutionRecord
        {
            AssignedAnt = task.AssignedAnt,
            TaskType = task.TaskType,
            WorkerBasis = task.WorkerBasis == Domain.WorkerDecisionBasis.Unset
                ? null
                : task.WorkerBasis.ToString(),
            DeliverableIds = task.DeliverableIds.ToList(),
            RequiredCapability = task.RequiredCapability,
            GenerationDegraded = task.GenerationDegraded,
            ProducedRevisionId = task.ProducedRevisionId,
            RanRevisionId = task.RanRevisionId,
        };
    }
}
