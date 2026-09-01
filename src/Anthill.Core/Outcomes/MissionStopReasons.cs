namespace Anthill.Core.Outcomes;

/// <summary>
/// Why the executor stopped dispatching — the closed set, named. v0.3.8.74.
///
/// PLURAL because `ExecutionService` already has a private `MissionStopReason(context, token)`
/// method that ASKS whether the mission should stop. The collision was a compiler error and a useful
/// one: the method answers "should we stop, and why", and this type is the vocabulary that answer is
/// drawn from. Both now use it, so the timeout and cancellation strings have one definition rather
/// than being literals in the producer and literals again in the evaluator that grades them.
///
/// These were bare string literals scattered across `ExecutionService` and compared by literal in
/// `MissionEvaluation.Resolve`, which is how the defect this type exists for went unnoticed:
/// <see cref="AdaptiveStop"/> was returned from three call sites for TWO STRUCTURALLY OPPOSITE
/// situations, and the evaluator graded both as an escalation.
///
///   * the repair bound is spent and the critical failure persists — a genuine escalation;
///   * the controller wanted to add a verification step and found the mission already has one —
///     nothing is wrong, there is simply nothing to add.
///
/// A mission that passed every check and every review, whose plan happened to include a verifier,
/// was therefore graded `escalated` and could never reach `completed_verified`. That is not
/// cosmetic: auto-apply consumes the canonical evaluation, so the second case made a clean,
/// fully verified patch mission structurally incapable of applying its own patch.
///
/// It was found by qualification scenario 3 — the first test in the project's history to drive a
/// mission from a goal to applied bytes, and therefore the first that needed this outcome to be
/// reachable at all. Every earlier lifecycle test stopped at "materialized and reviewed", where the
/// difference between the two stops does not show.
/// </summary>
public static class MissionStopReasons
{
    /// <summary>The operator cancelled. Never a completion, whatever the tasks say.</summary>
    public const string Cancelled = "mission_cancelled";

    /// <summary>The mission ran out of time.</summary>
    public const string Timeout = "mission_timeout";

    /// <summary>
    /// The adaptive controller stopped because its bound is spent and the problem persists.
    /// An escalation: a person is needed.
    /// </summary>
    public const string AdaptiveStop = "adaptive_stop";

    /// <summary>
    /// The adaptive controller stopped because the work it would have added is ALREADY THERE.
    ///
    /// Distinct from <see cref="AdaptiveStop"/> and not a failure of any kind. The mission is graded
    /// on its tasks and its evidence exactly as if the controller had never spoken — which is the
    /// correct treatment of a controller that looked, found nothing to do, and said so.
    /// </summary>
    public const string AdaptiveStopSatisfied = "adaptive_stop_satisfied";

    /// <summary>
    /// v0.3.8.105 — the adaptive controller stopped because THE SAME DEFECT CAME BACK.
    ///
    /// An escalation, and a different one from <see cref="AdaptiveStop"/>. That reason says the
    /// repair bound is spent, which is true of a mission that tried two genuinely different things
    /// and of one that tried the same thing twice — and those want different words to an operator.
    /// A recurrence is reproducible: the identical signature under a new task id means nothing
    /// material changed, so the next cycle would spend the same budget to reach the same place.
    /// Naming it separately is what lets the mission stop TRUTHFULLY rather than merely stop.
    /// </summary>
    public const string RepeatedFailure = "repeated_failure";

    /// <summary>
    /// v0.3.8.105 — the mission is waiting on an operator decision that was never made.
    ///
    /// NOT an escalation and NOT a failure. Under `Ask`, a side-effecting action with no recorded
    /// answer is refused — absence is not consent — and until now that refusal was the whole story:
    /// the task failed and the mission was graded as one that broke. Nothing broke. A person was
    /// asked a question and has not answered it yet, and the difference decides what the operator
    /// does next: a failed mission invites a retry, and a waiting one invites an answer.
    /// </summary>
    public const string AwaitingDecision = "awaiting_operator_decision";

    /// <summary>Whether a stop reason means the mission needs a person. Null (ran to its natural
    /// end) and <see cref="AdaptiveStopSatisfied"/> do not.
    ///
    /// <see cref="AwaitingDecision"/> deliberately does NOT count. Both need a person, and they
    /// need different ones: an escalation needs a human to look at something that went wrong, and a
    /// pending decision needs a human to answer something that was asked. Folding the second into
    /// the first would grade a mission that behaved perfectly as one that escalated.</summary>
    public static bool IsEscalation(string? reason) =>
        string.Equals(reason, AdaptiveStop, StringComparison.Ordinal)
        || string.Equals(reason, RepeatedFailure, StringComparison.Ordinal);

    /// <summary>Whether the mission stopped to WAIT rather than to finish or to fail.</summary>
    public static bool IsPause(string? reason) =>
        string.Equals(reason, AwaitingDecision, StringComparison.Ordinal);
}
