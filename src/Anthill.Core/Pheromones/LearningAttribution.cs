using Anthill.Core.Domain;
using Anthill.SDK.Contracts;

namespace Anthill.Core.Pheromones;

/// <summary>
/// WHO a task's outcome is evidence about. Stage E, v3.8.26.
///
/// Until this release the answer was "whoever was assigned", unconditionally:
///
/// <code>
///   var taskDelta = task.Status == Skipped ? -0.01 : taskSuccess &amp;&amp; success ? 0.03 : -0.04;
/// </code>
///
/// A SKIPPED task pushed its role's trail DOWN. A role was punished for not running — for being
/// gated off, for depending on something that failed, for arriving after a mission deadline. And
/// every non-Complete status fell into the -0.04 branch: Blocked (the role's own contract refused
/// the task), Cancelled (the operator stopped the mission), Pending (it never got a turn).
///
/// That was survivable while six of twelve roles never ran. It stops being survivable the moment
/// those gates open, which is the release this lands in: a specialist enabled for the first time
/// would accumulate negative reputation from missions it was blocked out of, and the colony would
/// learn to route away from roles it had never actually tried.
///
/// The rule this encodes: **a trail moves only when the outcome is evidence about the thing the
/// trail names.** Everything else is neutral — not positive, not negative, recorded as an episode
/// and left alone.
/// </summary>
public static class LearningAttribution
{
    /// <summary>What a task's outcome says about the role and worker that held it.</summary>
    public enum Attribution
    {
        /// <summary>The work was done and the mission verified it. Reinforce.</summary>
        Positive,

        /// <summary>The work was attempted and failed in a way attributable to the doer. Penalise.</summary>
        Negative,

        /// <summary>
        /// Nothing happened that this role can be credited or blamed for. NO trail movement.
        ///
        /// The largest and most important case, and the one that did not exist before: skipped,
        /// blocked, cancelled, never-reached, environment failure, provider outage.
        /// </summary>
        Neutral,
    }

    /// <summary>
    /// The failure classes that are NOT the worker's doing.
    ///
    /// A model provider that fell over, a rate limit, a mission deadline, a dependency that never
    /// produced its input — these say something real, and what they say is about the PROVIDER, the
    /// TOOL or the ENVIRONMENT. `ModelRouter` and `ToolRegistry` already record those trails
    /// separately and correctly, which is precisely why charging them to the worker as well is
    /// double-counting a fact against the wrong subject.
    ///
    /// AuthorizationFailure is here for a subtler reason: it means the colony's own policy refused
    /// the dispatch. A role denied a tool it is not allowed to call has not performed badly — it has
    /// been correctly constrained, and penalising that would teach the colony to avoid roles whose
    /// contracts are working.
    /// </summary>
    public static readonly IReadOnlySet<FailureClass> NotTheWorkersFault =
        new HashSet<FailureClass>
        {
            FailureClass.TransientProviderFailure,
            FailureClass.RateLimit,
            FailureClass.Timeout,
            FailureClass.DependencyFailure,
            FailureClass.AuthorizationFailure,
            FailureClass.Conflict,
        };

    /// <summary>
    /// Decide what a finished task's outcome is evidence of.
    /// </summary>
    /// <param name="task">The task, after the mission is terminal.</param>
    /// <param name="missionVerified">Whether the CANONICAL evaluation reached a verified outcome.
    /// Passed in rather than re-derived — the whole point of a single persisted evaluation is that
    /// nothing downstream computes its own answer.</param>
    public static Attribution For(Task task, bool missionVerified)
    {
        if (task is null) return Attribution.Neutral;

        switch (task.Status)
        {
            // Ran, finished, and the mission's own verification stood behind it. The ONLY positive.
            // A completed task in an unverified mission is neutral rather than positive: the work
            // happened, and whether it was the RIGHT work is exactly what verification failed to
            // establish.
            case TaskStatus.Complete:
                return missionVerified ? Attribution.Positive : Attribution.Neutral;

            // Did not run. Was never going to teach anyone anything.
            case TaskStatus.Skipped:
            case TaskStatus.Pending:
            case TaskStatus.Blocked:
            case TaskStatus.Cancelled:
                return Attribution.Neutral;

            case TaskStatus.Failed:
                // A failure is evidence about the doer only when the doer is what failed.
                return IsEnvironmental(task) ? Attribution.Neutral : Attribution.Negative;

            default:
                // Fail toward NEUTRAL for a status this function has not been taught. A new status
                // silently defaulting to "penalise" is how a role acquires a reputation for
                // something nobody decided it should be judged on.
                return Attribution.Neutral;
        }
    }

    /// <summary>
    /// True when the recorded failure belongs to the provider, a tool, or the environment.
    ///
    /// Reads <see cref="Task.FailureType"/>, which the task lifecycle sets from the typed
    /// <see cref="FailureClass"/> — never the failure PROSE. Parsing a message to decide whose fault
    /// something was is the same defect as parsing a verdict to decide whether it passed.
    /// </summary>
    /// <remarks>
    /// v3.8.32 — this method was BROKEN from the day it was written, and the test over it passed.
    ///
    /// It compared <c>task.FailureType</c>, which <c>TaskOutcomeMapper</c> fills with
    /// <c>transient_provider_failure</c>, against <c>cls.ToString()</c>, which is
    /// <c>TransientProviderFailure</c>, using <c>OrdinalIgnoreCase</c> — which normalises the casing
    /// and NOT the underscores. Nothing in the set ever matched. For six releases every provider
    /// outage, rate limit and dependency failure was charged as a negative trail against the ant that
    /// happened to be holding the task: the exact defect this class exists to prevent.
    ///
    /// The test fed <c>nameof(FailureClass.TransientProviderFailure)</c> — a value production never
    /// writes into this field — so it exercised a code path no mission could reach. The replacement
    /// test drives real <c>AntExecutionResult</c>s through the real mapper, so the two halves can
    /// never again be verified in isolation from each other.
    /// </remarks>
    public static bool IsEnvironmental(Task task)
    {
        // One parser, shared with the producer, normalising both historical string forms. A local
        // comparison here is what caused the original defect and must not come back.
        return FailureClassNames.TryParse(task.FailureType, out var cls)
               && NotTheWorkersFault.Contains(cls);
    }

    /// <summary>
    /// The trail delta for an attribution. Neutral is EXACTLY zero, not a small negative — the
    /// previous -0.01 for a skipped task looked like rounding and was a judgment.
    /// </summary>
    public static double DeltaFor(Attribution attribution) => attribution switch
    {
        Attribution.Positive => 0.03,
        Attribution.Negative => -0.04,
        _ => 0.0,
    };
}
