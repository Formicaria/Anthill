namespace Anthill.Core.Outcomes;

/// <summary>
/// HAS THIS DEFECT ALREADY COME BACK? v0.3.8.105, PLAN.md §2b `.105`.
///
/// WHAT IT ANSWERS, and why it is one type rather than a line of code in two places. A failure's
/// SEMANTIC signature — `FailureContext.FailureSignature` — is stable across task-id regeneration,
/// so two distinct tasks carrying the same signature means the same defect reappeared with nothing
/// material changed. The medic has asked that question since `.57` and bounded its repair loop on
/// the answer. The ADAPTIVE CONTROLLER, which decides whether to SPEND a repair cycle at all, has
/// never asked it: it sees "a critical task failed", spends a repair, and the repair produces a
/// medic that then refuses on the ground the controller could have known one step earlier.
///
/// So the mission burned a bounded repair cycle to be told a thing that was already in the store,
/// and — the part that actually matters — the mission's recorded stop reason said the repair bound
/// was spent, which is true and is not WHY it stopped. `repeated_failure` is why.
///
/// ONE ANSWER, TWO CONSUMERS. Copying the query into the controller would have created a second
/// reading of the same rows, and two readings of one record eventually disagree — the defect
/// `MissionContract` exists to end for the operator's goal, arriving here for the failure store.
/// The medic's own loop control now calls this; the controller calls it before spending budget.
///
/// IT COUNTS DISTINCT TASKS, NOT ARTIFACTS. One failing task can record a context on every attempt,
/// and counting artifacts would escalate a single failure on its own retry — turning a bounded
/// repair into no repair at all. That rule was learned in `MedicAnt.HasSeenSignatureBefore` and is
/// preserved here verbatim rather than rediscovered.
/// </summary>
public static class FailureRecurrence
{
    /// <summary>
    /// A failure that has now been recorded for more than one task.
    /// </summary>
    /// <param name="Signature">The semantic signature, as a reader can quote it.</param>
    /// <param name="DistinctTasks">How many distinct tasks recorded it. Always at least two.</param>
    /// <param name="FailureClass">The wire-form class, so a consumer can say what came back.</param>
    public sealed record Recurrence(string Signature, int DistinctTasks, string FailureClass)
    {
        public string Explanation =>
            $"the failure '{FailureClass}' (signature {Signature}) has now been recorded for "
          + $"{DistinctTasks} distinct tasks in this mission with nothing material changed";
    }

    /// <summary>
    /// The recurring failures in a mission, strongest first. Empty when the store is absent or
    /// unreadable — the conservative direction for the caller that decides whether to SPEND a
    /// repair cycle, because the cost of missing a recurrence is one cycle and the cost of
    /// inventing one is a mission stopped that could have been repaired.
    ///
    /// A CALLER THAT NEEDS TO TELL "NONE" FROM "COULD NOT LOOK" MUST USE <see cref="Recurred"/>.
    /// The medic is that caller and the difference is not academic: for the layer deciding whether
    /// to PERFORM a repair, an unreadable store that reads as "no recurrence" is a REMOVED bound,
    /// and a removed bound is how a bounded repair loop becomes an unbounded one.
    /// </summary>
    public static IReadOnlyList<Recurrence> InMission(
        Anthill.SDK.Artifacts.IArtifactStore? artifacts, string missionId) =>
        TryRead(artifacts, missionId) ?? Array.Empty<Recurrence>();

    /// <summary>
    /// Whether ONE specific signature has recurred. NULL means the store could not be read, and
    /// the caller decides what an unknown is worth — that decision is genuinely different for the
    /// two consumers and must not be made here on their behalf.
    /// </summary>
    public static bool? Recurred(
        Anthill.SDK.Artifacts.IArtifactStore? artifacts, string missionId, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        var all = TryRead(artifacts, missionId);
        return all?.Any(r => string.Equals(r.Signature, signature, StringComparison.Ordinal));
    }

    /// <summary>Null when the store is absent or threw; a list (possibly empty) when it answered.</summary>
    private static IReadOnlyList<Recurrence>? TryRead(
        Anthill.SDK.Artifacts.IArtifactStore? artifacts, string missionId)
    {
        if (artifacts is null || string.IsNullOrWhiteSpace(missionId)) return null;

        try
        {
            return artifacts
                .ForMission(missionId, Anthill.SDK.Artifacts.ArtifactSchemas.FailureContext)
                .Select(a => (a.TaskId, Context: Anthill.SDK.Artifacts.FailureContext.FromJson(a.Payload)))
                .Where(x => x.Context is not null && !string.IsNullOrWhiteSpace(x.Context!.FailureSignature))
                .GroupBy(x => x.Context!.FailureSignature, StringComparer.Ordinal)
                .Select(g => new Recurrence(
                    g.Key,
                    g.Select(x => x.TaskId ?? "").Distinct(StringComparer.Ordinal).Count(),
                    g.First().Context!.FailureClass))
                .Where(r => r.DistinctTasks > 1)
                .OrderByDescending(r => r.DistinctTasks)
                .ThenBy(r => r.Signature, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error)
        {
            // NULL, not empty. "Nothing came back" and "I could not look" are different facts and
            // this is exactly the joint where collapsing them removes a bound — said out loud so a
            // silent store is visible in the operator's log rather than only in its consequences.
            Console.Error.WriteLine(
                $"[recurrence] could not read failure_context artifacts for {missionId}: {error.Message}");
            return null;
        }
    }
}
