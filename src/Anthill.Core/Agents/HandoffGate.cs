using Anthill.Core.Domain;

namespace Anthill.Core.Agents;

/// <summary>
/// Execution framework Stage E — bounded handoff admission (spec §10). A specialist's handoff may
/// create a dynamic task ONLY when every gate passes: the destination is runtime-eligible, its
/// contract supports the required task type, handoff depth is under the limit, the mission task
/// budget holds, and no near-duplicate exists (dedupe key). Rejections carry the reason — nothing
/// is dropped silently, and recursive unlimited task creation is structurally impossible.
///
/// v0.3.8.135 — the required task type is RECONCILED against the destination's contract before that
/// check, by the same <see cref="Planning.TaskTypeVocabulary"/> the planner uses. See the comment at
/// the check itself: `.133` fixed the synonym problem at the planner's door and this was the other
/// one, where a refused REQUIRED handoff is a deterministic block and a mission died over a word.
/// </summary>
public static class HandoffGate
{
    public const int MaxHandoffDepth = 2;
    public const int MaxMissionTasks = 12;

    /// <summary>
    /// Marker written into a dynamic task's description so its handoff depth survives persistence
    /// and a restart. The tasks table has no depth column; the description does round-trip.
    /// </summary>
    private const string DepthMarker = "depth:";

    public sealed record Admission(bool Accepted, string Reason, Task? CreatedTask);

    /// <summary>
    /// The handoff depth a task sits at: 0 for anything in the original plan, N for a task created
    /// by a depth-N handoff.
    ///
    /// v2.21.0: this exists because EVERY specialist hardcodes `Depth: 1` when it builds a handoff.
    /// Trusting that number would mean a handoff from a dynamic task also arrived at depth 1, the
    /// limit would never be reached, and "recursive unlimited task creation is structurally
    /// impossible" would be false. Depth is therefore derived from the SOURCE TASK's lineage by
    /// the orchestrator, never taken from the ant's self-report.
    /// </summary>
    public static int DepthOf(Task? task)
    {
        var description = task?.Description ?? "";
        var at = description.IndexOf(DepthMarker, StringComparison.Ordinal);
        if (at < 0) return 0;

        var digits = description[(at + DepthMarker.Length)..].TakeWhile(char.IsDigit).ToArray();
        return digits.Length > 0 && int.TryParse(new string(digits), out var depth) ? depth : 0;
    }

    /// <summary>The depth a handoff FROM <paramref name="sourceTask"/> would create a task at.</summary>
    public static int NextDepthFrom(Task? sourceTask) => DepthOf(sourceTask) + 1;

    public static Admission Evaluate(AntHandoff handoff, Mission mission)
    {
        if (handoff.Depth > MaxHandoffDepth)
            return new(false, $"handoff depth {handoff.Depth} exceeds limit {MaxHandoffDepth}", null);

        if (mission.Tasks.Count >= MaxMissionTasks)
            return new(false, $"mission task budget exhausted ({mission.Tasks.Count}/{MaxMissionTasks})", null);

        if (!AntRegistry.ExecutableRoleIds.Contains(handoff.DestinationRole))
            return new(false, $"destination role '{handoff.DestinationRole}' is not runtime-eligible (gate closed or not executable)", null);

        // v0.3.8.135 — THE SECOND DOOR, and it had the same hole the first one did.
        //
        // `.133` built `TaskTypeVocabulary.Reconcile` for exactly this problem — a role's contract
        // is the authority, a model writes the plain-English word, and the two are not compared
        // until dispatch — and wired it into `AssignDefaultWorkers`, the funnel every PLANNER path
        // goes through. A HANDOFF goes through this method instead. So a medic asking a builder to
        // `summarize` was refused here for a spelling the builder's contract has a declared word
        // for, and a refused REQUIRED handoff is a deterministic block: the mission fails, over a
        // synonym, one door over from where that was fixed.
        //
        // RECONCILED BEFORE THE CHECK, NOT INSTEAD OF IT. The authority does not move — a type
        // nothing resolves is still refused below, by name, exactly as before. What changes is that
        // the refusal now means "no role here can do this", which is a real defect worth being loud
        // about, rather than "you spelled it the way people spell it".
        var contract = AntExecutionCatalog.ContractFor(handoff.DestinationRole);
        var requiredType = Planning.TaskTypeVocabulary.Reconcile(handoff.DestinationRole, handoff.RequiredTaskType);
        if (contract is not null && !contract.SupportsTaskType(requiredType))
            return new(false, $"destination '{handoff.DestinationRole}' does not support task type '{handoff.RequiredTaskType}'", null);

        if (mission.Tasks.Any(t => t.Description.Contains(handoff.DedupeKey, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"near-duplicate handoff suppressed (dedupe '{handoff.DedupeKey}')", null);

        var created = new Task
        {
            Title = $"Handoff: {handoff.SourceRole} -> {handoff.DestinationRole}",
            Description = $"{handoff.Reason} [handoff dedupe:{handoff.DedupeKey} depth:{handoff.Depth}]",
            AssignedAnt = handoff.DestinationRole,
            // The RECONCILED type, not the requested one — the created task is what dispatch will
            // read, and handing it the unreconciled spelling would make the check above ceremonial.
            TaskType = requiredType,
            Critical = handoff.Required,
        };
        return new(true, "", created);
    }
}
