using Anthill.Core.Agents;
using Anthill.Core.Domain;

namespace Anthill.Core.Orchestration;

/// <summary>
/// THE LAST PLACE TO CHANGE THE WORKER, AND THE ONLY ONE EVERY TASK PASSES. v0.3.8.105, PLAN.md §2b.
///
/// WHAT `.104` LEFT OPEN, in one sentence: `MissionPreflight` has exactly ONE call site — in
/// `Queen.RunMission`, over the compiled plan, before execution — and the plan does not stop
/// changing there. Every task admitted afterwards has never been checked by it:
///
/// <list type="bullet">
/// <item>handoff-created tasks (`ExecutionService.IngestHandoffs` → `TryAdmitDynamicTask`);</item>
/// <item>the adaptive controller's DELTA PLAN tasks and its REPAIR tasks;</item>
/// <item>the policy-review tasks inserted around a coder;</item>
/// <item>the verification steps `EnsureVerificationWaitsFor` adds behind a deliverable.</item>
/// </list>
///
/// So the release that made "a plan that could never deliver is refused before it runs" true made it
/// true of the FIRST plan. The tasks created while the mission is already running — which are
/// exactly the tasks created in response to something going wrong, and therefore exactly the ones
/// most likely to be mis-assigned — went straight to dispatch.
///
/// THIS IS NOT A SECOND PREFLIGHT. Preflight asks a question about a PLAN: is every deliverable
/// claimed, is every dependency real, is anything unreachable. Those are properties of a graph and
/// they cannot be asked of one task in isolation. This asks the one question that IS answerable per
/// task and that a mid-flight task can get wrong: <b>can the worker about to run this actually do
/// what the task requires?</b> It runs at the dispatch chokepoint, before the durable claim, before
/// the model call, and before any tool the task would reach.
///
/// WHY "BEFORE HARMFUL EXECUTION" IS THE POINT. A worker that cannot serve a capability does not
/// sit there politely: it runs, calls a model, calls tools, and produces a confident answer to a
/// question it was never equipped to answer. That is `.98`'s finding — a runtime-inspection step
/// served by the researcher that reads mission history — and every gate downstream of it grades the
/// output rather than the fitness. A reroute costs nothing; the alternative costs a wrong answer
/// that looks right.
///
/// IT REROUTES WITHIN THE ROLE AND NEVER ACROSS IT. `WorkerResolution.RepairIncompatible` set that
/// rule at `.98` and the reason has not changed: a wrong ROLE is a planning error, answered by the
/// admission gate and the authority ceiling; a wrong WORKER inside the right role is a resolution
/// error, and this is where resolution happens last. Widening it to roles would let a dispatch-time
/// mechanism move work across the authority boundaries three gates upstream just finished checking.
///
/// AND IT NEVER INVENTS A CAPABILITY. Only a task carrying its OWN <see cref="Domain.Task.RequiredCapability"/>
/// is judged — the same narrowing `WorkerResolution` and `MissionPreflight` both make, for the same
/// reason: a task measured against the mission-wide list is advisory, and refusing on it would break
/// every mission that legitimately plans a role outside its class's list.
/// </summary>
public static class TaskReroute
{
    public enum RerouteKind
    {
        /// <summary>Nothing to decide: no declared requirement, or the worker already serves it.</summary>
        Proceed,
        /// <summary>A compatible worker in the same role replaced an incompatible one.</summary>
        Rerouted,
        /// <summary>No worker in this role can serve what the task requires. Do not dispatch.</summary>
        Unserved,
    }

    /// <summary>
    /// The decision. <paramref name="Reason"/> is operator-facing and names the capability, the
    /// role and both workers — a reroute an operator cannot reconstruct is a silent reassignment,
    /// which is worse than the mis-assignment it fixed.
    /// </summary>
    public sealed record Decision(RerouteKind Kind, string? FromWorker, string? ToWorker,
        string Capability, string Reason)
    {
        public bool Blocks => Kind == RerouteKind.Unserved;
        public bool Changed => Kind == RerouteKind.Rerouted;
    }

    private static Decision Proceed(string reason) =>
        new(RerouteKind.Proceed, null, null, "", reason);

    /// <summary>
    /// Decide for one task about to be dispatched. PURE: no store, no model, no mutation — the
    /// caller applies the result, so the same task always yields the same decision and a test can
    /// ask it without standing up a mission.
    ///
    /// TAKES NO SPECIFICATION, and the absence is the decision rather than an omission. An earlier
    /// draft threaded <c>MissionSpecification</c> through so this could fall back to the
    /// mission-wide capability list — and that is precisely the advisory reading this type must not
    /// make. A parameter that decides nothing is a parameter that invites someone to make it decide
    /// something later, so it is not here.
    /// </summary>
    public static Decision Evaluate(Domain.Task task)
    {
        if (task is null) return Proceed("no task");

        // ONLY the task's own declared requirement. See the type's remarks — the mission-wide list
        // is advisory, and judging against it here would refuse legitimate plans at dispatch, which
        // is the most expensive possible place to be wrong about that.
        var capability = task.RequiredCapability;
        if (string.IsNullOrWhiteSpace(capability))
            return Proceed("the task declares no required capability, so there is nothing to serve");

        var role = task.AssignedAnt ?? "";
        if (string.IsNullOrWhiteSpace(role))
            // A task with no role is an ORPHAN, which preflight names and the runtime already
            // refuses at `AntRuntime.Resolve`. Not this gate's question, and answering it here
            // would put two layers in charge of one refusal.
            return Proceed("the task names no role; the worker runtime answers for that");

        var required = new[] { capability! };

        // Already fit: the assigned worker declares it. The commonest case and the cheapest.
        if (!string.IsNullOrWhiteSpace(task.AssignedWorker)
            && AntRegistry.ByWorker.TryGetValue(task.AssignedWorker!, out var assigned)
            && assigned.Capabilities.Contains(capability!, StringComparer.OrdinalIgnoreCase))
            return Proceed($"'{task.AssignedWorker}' declares '{capability}'");

        var (byCapability, decided) = AntRegistry.ResolveByCapability(role, required);

        if (decided && byCapability is not null
            && !string.Equals(byCapability.WorkerId, task.AssignedWorker, StringComparison.Ordinal))
            return new Decision(RerouteKind.Rerouted, task.AssignedWorker, byCapability.WorkerId, capability!,
                $"the task requires '{capability}' and '{task.AssignedWorker ?? "(unassigned)"}' does "
              + $"not declare it; '{byCapability.WorkerId}' is the one worker in the '{role}' role "
              + "that does. Rerouted before dispatch — an incompatible worker does not decline the "
              + "work, it answers the wrong question confidently.");

        // AMBIGUOUS IS NOT A BLOCK. More than one worker in the role declares the capability, so the
        // colony CAN do this and the choice among compatible candidates belongs to the trail, which
        // is the rule `.93` set and `.98` kept. Proceeding is correct: whoever holds the task is
        // compatible or the tie was already taken by a layer whose job that is.
        if (byCapability is not null)
            return Proceed(
                $"more than one worker in '{role}' declares '{capability}'; the assignment stands "
              + "and the choice among compatible candidates is not this gate's to make");

        // NOTHING IN THE ROLE SERVES IT. The colony cannot do this, and it will not be able to on a
        // retry — the distinction `MissionOutcome.BlockedMissingCapability` was added for.
        return new Decision(RerouteKind.Unserved, task.AssignedWorker, null, capability!,
            $"'{task.Title}' requires the capability '{capability}' and no worker in the '{role}' "
          + "role declares it. Dispatch refused: running it would produce a confident answer from a "
          + "worker that was never able to serve the request. This task was admitted after the "
          + "mission's plan was checked, so nothing upstream had the chance to say so.");
    }

    /// <summary>
    /// Apply a decision to the task. Returns true when the task was changed, so the caller can log
    /// exactly what happened rather than re-deriving it. Deliberately separate from
    /// <see cref="Evaluate"/>: a decision that mutated on the way to being read could not be
    /// inspected, and the reroute must be inspectable before it is applied.
    /// </summary>
    public static bool Apply(Domain.Task task, Decision decision)
    {
        if (!decision.Changed || decision.ToWorker is null) return false;
        task.AssignedWorker = decision.ToWorker;
        task.WorkerBasis = WorkerDecisionBasis.Specification;
        return true;
    }
}
