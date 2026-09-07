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

    /// <summary>
    /// WHY A HANDOFF WAS NOT ADMITTED, AND WHETHER THAT MEANS THE MISSION IS BROKEN. v0.3.8.141.
    ///
    /// THE EVIDENCE. Sixty-six real missions on the operator's colony: 37 escalated, and every one
    /// of the 39 `required_handoff_refused` events ended that way. The reasons —
    /// 18 `mission task budget exhausted (12/12)`, 15 `near-duplicate handoff suppressed`,
    /// 5 unsupported task type, 1 depth limit — are not one kind of thing, and this gate returned
    /// them as one bool.
    ///
    /// `v3.8.25` PREDICTED THIS IN WRITING and then exempted only half of it.
    /// `RecordRequiredHandoffRefusal` says: "treating a declined suggestion as a block would make
    /// every capped or deduplicated handoff a mission failure." It drew that exemption for OPTIONAL
    /// handoffs. For REQUIRED ones exactly what it describes is what happened — and required is 33
    /// of those 39.
    ///
    /// SO THE VERDICT SAYS WHICH KIND OF NO IT IS. A bound that stops the colony GROWING must not
    /// also declare the mission broken; only "nobody here can do this" is a claim about the work.
    /// </summary>
    public enum Refusal
    {
        /// <summary>Not a refusal — the handoff was admitted.</summary>
        None,

        /// <summary>
        /// THE REQUIREMENT IS ALREADY MET. Dedupe fires precisely BECAUSE a task carrying this
        /// handoff's key already exists, so a required handoff refused for it was refused on the
        /// grounds that the thing it demanded is already there. That is the requirement being
        /// satisfied, and reporting it as a denial is backwards.
        /// </summary>
        AlreadySatisfied,

        /// <summary>
        /// THE COLONY HAS GROWN AS FAR AS IT MAY — the task budget or the handoff depth. The
        /// mission is BOUNDED, not broken: the work that ran still ran, and nothing has established
        /// that anything is wrong with it. These bounds exist so recursive task creation is
        /// structurally impossible, which is a property of the runtime rather than a finding about
        /// the mission.
        /// </summary>
        Bounded,

        /// <summary>
        /// NOBODY HERE CAN DO THIS — the destination role is not runtime-eligible, or no contract
        /// declares the task type even after reconciliation. A real refusal, and the only kind that
        /// is a claim about the WORK rather than about the runtime's limits. A required handoff
        /// refused for this still blocks, exactly as `v3.8.25` intended.
        /// </summary>
        Unservable,
    }

    /// <param name="Why">Which kind of no. <see cref="Refusal.None"/> when accepted.</param>
    public sealed record Admission(bool Accepted, string Reason, Task? CreatedTask,
        Refusal Why = Refusal.None)
    {
        /// <summary>
        /// Whether a REQUIRED handoff refused for this reason means the mission cannot be verified.
        ///
        /// Only <see cref="Refusal.Unservable"/> does. The other two are the runtime saying "enough"
        /// or "already done", and neither is evidence about the work — which is the whole finding of
        /// v0.3.8.141.
        /// </summary>
        public bool BlocksTheMission => Why == Refusal.Unservable;
    }

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
        // BOUNDED, not broken. These two are the reason this type exists — "recursive unlimited task
        // creation is structurally impossible" — and a limit doing its job is a fact about the
        // runtime, never a finding about the mission that hit it. 19 of the operator's 39 refusals
        // were these, and all 19 killed their mission.
        if (handoff.Depth > MaxHandoffDepth)
            return new(false, $"handoff depth {handoff.Depth} exceeds limit {MaxHandoffDepth}",
                null, Refusal.Bounded);

        if (mission.Tasks.Count >= MaxMissionTasks)
            return new(false, $"mission task budget exhausted ({mission.Tasks.Count}/{MaxMissionTasks})",
                null, Refusal.Bounded);

        // UNSERVABLE — a claim about the work, and one of the two that still blocks. A role the
        // runtime cannot execute is not a bound being reached; it is a step nobody can take.
        if (!AntRegistry.ExecutableRoleIds.Contains(handoff.DestinationRole))
            return new(false, $"destination role '{handoff.DestinationRole}' is not runtime-eligible (gate closed or not executable)",
                null, Refusal.Unservable);

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
            return new(false, $"destination '{handoff.DestinationRole}' does not support task type '{handoff.RequiredTaskType}'",
                null, Refusal.Unservable);

        // ALREADY SATISFIED — and this is the one that reads as a refusal and is the opposite of
        // one. Dedupe fires precisely BECAUSE a task carrying this key already exists, so a required
        // handoff stopped here was stopped on the grounds that the thing it demanded is present.
        // Fifteen of the operator's missions escalated on this, the taco mission among them:
        // "required handoff refused: medic -> builder (build_answer) — near-duplicate handoff
        // suppressed". The medic asked for an answer to be written and one already was.
        //
        // Still `Accepted: false`, because no NEW task should be created — that half was always
        // right and is what keeps a handoff loop from growing. What changes is what the refusal
        // MEANS to the caller, which is the only thing that was ever wrong here.
        if (mission.Tasks.Any(t => t.Description.Contains(handoff.DedupeKey, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"near-duplicate handoff suppressed (dedupe '{handoff.DedupeKey}')",
                null, Refusal.AlreadySatisfied);

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
