using Anthill.Core.Agents;
using Anthill.Core.Contracts;

namespace Anthill.Core.Outcomes;

/// <summary>What the scheduler should be told to do with a finished task.</summary>
public enum TaskOutcomeAction
{
    /// <summary>MarkComplete.</summary>
    Complete,
    /// <summary>MarkFailed — the scheduler itself decides whether a retry is scheduled.</summary>
    Fail,
    /// <summary>MarkSkipped — not attempted, or deliberately abandoned. Never a success.</summary>
    Skip,
}

/// <summary>
/// The decision, separated from the act of applying it so the rule is testable without a
/// scheduler, a mission, or a database.
/// </summary>
public sealed record TaskOutcomeDecision(
    TaskOutcomeAction Action,
    bool Retryable,
    string FailureType,
    string Reason,
    IReadOnlyList<string> Warnings);

/// <summary>
/// v2.19.0 Stage 3 — the ONE mapping from a structured ant result to a task outcome.
///
/// WHY THIS EXISTS
/// ---------------
/// <c>Queen.RunSingleTask</c> did this:
///
///     result = ant.Run(taskSnapshot, missionSnapshot);   // a string
///     task.Result = result;
///     scheduler.MarkComplete(task.Id, result, ...);
///
/// (<c>Run</c> no longer exists: v3.2.0 deleted it together with the last status-from-prose test.
/// The snippet is left as written, because it is what the defect actually looked like.)
///
/// The returned value was never inspected. The only routes to a non-complete status were a thrown
/// exception, a wall-clock timeout, or a pre-execution runtime denial — so an ant reporting
/// <c>failed_retryable</c> was recorded as a completed task. Combined with the Director treating
/// partial missions as success, a failing agent could drive an automatic code change
/// (docs/ADR-ADAPTIVE-MISSION-RUNTIME.md §2.4).
///
/// The governing rule here: a task is completed ONLY when the ant said it succeeded. Absence of an
/// exception proves nothing, and an unrecognised status fails closed rather than passing through.
/// </summary>
public static class TaskOutcomeMapper
{
    /// <summary>Status codes that may complete a task. Nothing else may, ever.</summary>
    private static readonly HashSet<string> Completing =
        new(StringComparer.Ordinal) { "succeeded", "succeeded_with_warnings" };

    public static TaskOutcomeDecision Map(AntExecutionResult? result)
    {
        // A null result is a defect in the ant, not a success.
        if (result is null)
            return new(TaskOutcomeAction.Fail, false, "missing_result",
                "The ant returned no structured result.", Array.Empty<string>());

        var warnings = (IReadOnlyList<string>)(result.Warnings ?? new List<string>());
        var reason = result.Failure?.Reason
                     ?? (string.IsNullOrWhiteSpace(result.Summary) ? result.StatusCode : result.Summary);

        switch (result.StatusCode)
        {
            case "succeeded":
                return new(TaskOutcomeAction.Complete, false, "", "", Array.Empty<string>());

            case "succeeded_with_warnings":
                // Still a success — the warnings are recorded, not escalated.
                return new(TaskOutcomeAction.Complete, false, "", "", warnings);

            case "failed_retryable":
                // The scheduler decides whether a retry is actually available (attempt budget).
                return new(TaskOutcomeAction.Fail, true, FailureType(result), reason, warnings);

            case "failed_permanent":
                return new(TaskOutcomeAction.Fail, false, FailureType(result), reason, warnings);

            case "blocked":
                // A policy/authorization refusal. Retrying cannot change the answer, and a handoff
                // must never be able to widen authority to get around it.
                return new(TaskOutcomeAction.Fail, false, "blocked", reason, warnings);

            case "skipped":
                return new(TaskOutcomeAction.Skip, false, "skipped", reason, warnings);

            case "cancelled":
                return new(TaskOutcomeAction.Skip, false, "cancelled", reason, warnings);

            case "timed_out":
                // Timeout is in FailureClassify's retryable set: the next attempt may be quicker.
                return new(TaskOutcomeAction.Fail, true, "timeout", reason, warnings);

            default:
                // Fail closed. An unrecognised code must never complete a task by default.
                return new(TaskOutcomeAction.Fail, false, "unknown_status",
                    $"Ant returned an unrecognised status code '{result.StatusCode}'.", warnings);
        }
    }

    /// <summary>
    /// Whether this result completed the task successfully. Deliberately derived from the SAME set
    /// the mapper uses, so the two can never disagree about what success means.
    /// </summary>
    public static bool IsCompleting(AntExecutionResult? result) =>
        result is not null && Completing.Contains(result.StatusCode);

    /// <summary>
    /// Typed failure classification for the task record, from the ant's own report.
    ///
    /// v3.8.32: goes through <see cref="FailureClassNames.Wire"/> rather than a private ToSnake that
    /// lived here. The private copy is what made this the PRODUCER half of a disagreement no test
    /// could see: the consumer, <c>LearningAttribution</c>, compared against the enum name instead,
    /// and neither side ever received a value from the other.
    /// </summary>
    private static string FailureType(AntExecutionResult result) =>
        result.Failure is { } f ? FailureClassNames.Wire(f.Class) : "execution_error";
}
