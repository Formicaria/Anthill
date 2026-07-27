using Anthill.Core.Domain;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.19.0 Stage 2 — the canonical outcome vocabulary, and the single predicate that decides
/// whether an outcome may reinforce anything.
///
/// WHY THIS EXISTS
/// ---------------
/// Before this release <c>ColonyDirector.ReadOutcome</c> computed
/// <c>var success = status is "complete" or "partial";</c> and that one flag drove four separate
/// consequences: objective success EMA, autonomous follow-up creation, objective lifecycle
/// closure, and AUTO-APPLY of patches. A partially-failed mission therefore reinforced learning
/// and could automatically apply code.
///
/// Combined with the executor marking every non-throwing task complete (see
/// docs/ADR-ADAPTIVE-MISSION-RUNTIME.md §2.1), an agent that reported <c>failed_retryable</c>
/// could drive an automatic code change. That is the defect this predicate closes.
///
/// Every positive path must ask <see cref="IsPositiveSuccess"/>. There is deliberately no second
/// way to spell it.
/// </summary>
public static class MissionOutcome
{
    // ---- vocabulary ------------------------------------------------------------------------------
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingForApproval = "waiting_for_approval";
    public const string WaitingForVerification = "waiting_for_verification";
    /// <summary>Work finished AND the required evidence was produced. The only positive outcome.</summary>
    public const string CompletedVerified = "completed_verified";
    /// <summary>Work finished but the required evidence is absent. Not a success.</summary>
    public const string CompletedUnverified = "completed_unverified";
    public const string Partial = "partial";
    public const string FailedRetryable = "failed_retryable";
    public const string FailedPermanent = "failed_permanent";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    public const string Compensating = "compensating";
    public const string Compensated = "compensated";
    public const string RollbackFailed = "rollback_failed";

    /// <summary>
    /// THE predicate. Only <see cref="CompletedVerified"/> may drive positive reinforcement:
    /// objective success EMA, autonomous follow-up creation, auto-apply eligibility, positive
    /// pheromone reinforcement, skill promotion, or successful objective completion.
    ///
    /// Deliberately an exact match rather than a set membership test — a new outcome added later
    /// is non-reinforcing until someone consciously decides otherwise.
    /// </summary>
    public static bool IsPositiveSuccess(string? outcome) =>
        string.Equals(outcome, CompletedVerified, StringComparison.Ordinal);

    /// <summary>
    /// Resolve the structural mission status plus verification state into a canonical outcome.
    /// A mission that finished all its work is <see cref="CompletedUnverified"/> until the
    /// evidence requirement is actually met.
    /// </summary>
    public static string Resolve(MissionStatus status, bool verificationSatisfied) => status switch
    {
        MissionStatus.Complete => verificationSatisfied ? CompletedVerified : CompletedUnverified,
        MissionStatus.Partial => Partial,
        _ => FailedPermanent,
    };

    /// <summary>
    /// Same resolution from the persisted status text, for callers reading a mission row rather
    /// than a <see cref="Mission"/> object (the Director reads from memory as a dictionary).
    /// Unrecognised text fails closed.
    /// </summary>
    public static string ResolveFromStatusText(string? statusText, bool verificationSatisfied) =>
        statusText switch
        {
            "complete" => verificationSatisfied ? CompletedVerified : CompletedUnverified,
            "partial" => Partial,
            "cancelled" => Cancelled,
            _ => FailedPermanent,
        };
}
