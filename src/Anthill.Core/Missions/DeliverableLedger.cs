using Anthill.Core.Domain;

namespace Anthill.Core.Missions;

/// <summary>
/// One requested deliverable, and what the mission did about it. v0.3.8.98.
/// </summary>
/// <param name="Id">The specification's id — `d1`, `d2` — stable for the mission's life.</param>
/// <param name="Request">The operator's own words for this deliverable.</param>
/// <param name="Claim">How the serving tasks were determined. See <see cref="DeliverableClaim"/>.</param>
/// <param name="ServingTaskIds">Every task that serves this deliverable, claimed or inferred.</param>
/// <param name="Served">True when at least one serving task COMPLETED. A task that failed, was
/// skipped or never ran has not served anything, whatever it was assigned.</param>
public sealed record DeliverableEntry(
    string Id,
    string Request,
    string Claim,
    IReadOnlyList<string> ServingTaskIds,
    bool Served);

/// <summary>How a deliverable's serving tasks were identified. The difference is load-bearing.</summary>
public static class DeliverableClaim
{
    /// <summary>A task DECLARED it serves this deliverable, and the id was one the specification
    /// actually holds. The strong case: the plan attributed the work question by question.</summary>
    public const string Declared = "declared";

    /// <summary>
    /// Nothing claimed it, so the compiling task is credited with all of them. Honest — the
    /// compiled answer IS what addresses the questions — and deliberately WEAKER, which is why it
    /// is named: a ledger that hid the difference would let "the plan mapped each question to a
    /// step" and "one builder task was assumed to cover everything" read identically.
    /// </summary>
    public const string Inferred = "inferred";

    /// <summary>Nothing claimed it and nothing could compile it. The deliverable has no owner.</summary>
    public const string Unowned = "unowned";
}

/// <summary>
/// WHAT THE OPERATOR ASKED FOR, AND WHETHER ANYTHING PRODUCED IT. v0.3.8.98.
///
/// WHY IT EXISTS. A mission's deliverables lived only as clauses inside the goal string, so no
/// layer could state afterwards whether the thing asked for was produced. "Three questions asked,
/// one answered, mission complete" was not a bug in any component — it was a question the runtime
/// had no way to ask. The specification gave each request an id at intake; this is the other half:
/// the id has to reach the work, and the work has to answer for it.
///
/// A PURE FUNCTION of the specification and the terminal tasks, deliberately. The canonical
/// evaluator's whole claim is that a mission's grade is reproducible from its persisted record,
/// and a ledger that depended on live state or a model's opinion would break that. Build it twice
/// from the same mission and it says the same thing.
///
/// WHAT IT DOES NOT CLAIM. `Served` means a task that owns this deliverable ran to completion. It
/// does not mean the answer is good, or deep, or even on topic — judging that is a semantic call,
/// and a model asserting it is the evidence v2.19.0 stopped accepting. The failure this catches is
/// the structural one: a mission whose plan attributed three questions to three tasks, one of which
/// failed, reported as complete because the OTHER two finished.
/// </summary>
public static class DeliverableLedger
{
    /// <summary>Roles whose output is the answer the operator reads. A deliverable nothing claimed
    /// is credited to these, because compiling the result is what addresses the request.</summary>
    private static readonly IReadOnlySet<string> CompilingRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "builder" };

    /// <summary>
    /// Build the ledger. Returns an empty list for a specification with no deliverables — every
    /// mission before v0.3.8.98, and every class intake cannot yet serve — so no consumer of this
    /// can constrain work that was never asked to declare anything.
    /// </summary>
    public static IReadOnlyList<DeliverableEntry> Build(
        MissionSpecification? specification, IReadOnlyList<Task>? tasks)
    {
        if (specification is null || specification.Deliverables.Count == 0)
            return Array.Empty<DeliverableEntry>();

        var all = tasks ?? Array.Empty<Task>();
        var compilers = all.Where(t => CompilingRoles.Contains(t.AssignedAnt ?? "")).ToList();

        return specification.Deliverables.Select(deliverable =>
        {
            var claimants = all
                .Where(t => t.DeliverableIds.Contains(deliverable.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var (serving, claim) = claimants.Count > 0
                ? (claimants, DeliverableClaim.Declared)
                : compilers.Count > 0
                    ? (compilers, DeliverableClaim.Inferred)
                    : (new List<Task>(), DeliverableClaim.Unowned);

            return new DeliverableEntry(
                deliverable.Id,
                deliverable.Request,
                claim,
                serving.Select(t => t.Id).ToList(),
                serving.Any(t => t.Status == TaskStatus.Complete));
        }).ToList();
    }

    /// <summary>The deliverables nothing produced — what a refusal has to name.</summary>
    public static IReadOnlyList<DeliverableEntry> Unserved(IReadOnlyList<DeliverableEntry> ledger) =>
        ledger.Where(e => !e.Served).ToList();

    /// <summary>Operator-visible projection, for the artifact and the mission record. Secret-free.</summary>
    public static Dictionary<string, object?> Snapshot(IReadOnlyList<DeliverableEntry> ledger) => new()
    {
        ["deliverables"] = ledger.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["request"] = e.Request,
            ["claim"] = e.Claim,
            ["serving_task_ids"] = e.ServingTaskIds,
            ["served"] = e.Served,
        }).ToList(),
        ["served"] = ledger.Count(e => e.Served),
        ["requested"] = ledger.Count,
    };
}
