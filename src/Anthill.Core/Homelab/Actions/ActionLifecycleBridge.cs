using Anthill.Core.Homelab.Approvals;
using Anthill.Core.SafeAction;

namespace Anthill.Core.Homelab.Actions;

/// <summary>
/// v2.25.0 — the Safe Action Engine executor migration (NORTH_STAR release map, V2.14.0 note).
///
/// `ActionLifecycle` shipped in v2.14.0 as "the ONE lifecycle every state-changing system shares"
/// — and the homelab `ActionExecutor`, the only production system that actually changes external
/// state, never consulted it. Its transitions were guarded by string comparisons that happened to
/// agree with the lifecycle, which is agreement by coincidence, not by structure.
///
/// This bridge maps the executor's persisted string states onto the canonical
/// <see cref="ActionState"/> machine and refuses any mutation the lifecycle would refuse. The
/// persisted strings are unchanged — every existing route, approval flow, and dashboard read keeps
/// working — but the transition RULES now come from one place.
///
/// The second half of the migration: verification becomes the door to completion. A proposal whose
/// post-execution verify failed no longer quietly remains "executed, with a warning in the text" —
/// it lands in lifecycle state <c>failed</c> and produces a <see cref="RecoveryOrchestrator"/>
/// decision on the audit stream. The decision is a RECOMMENDATION: nothing here executes recovery,
/// because recovery that runs itself is exactly the autonomy V3 has not yet earned.
/// </summary>
public static class ActionLifecycleBridge
{
    /// <summary>Canonical lifecycle states as persisted strings (snake_case, like every other column).</summary>
    public static class Persisted
    {
        public const string WaitingForApproval = "waiting_for_approval";
        public const string Approved = "approved";
        public const string Executing = "executing";
        public const string Verifying = "verifying";
        public const string CompletedVerified = "completed_verified";
        public const string Failed = "failed";
        public const string Escalated = "escalated";
    }

    /// <summary>
    /// Map a proposal's legacy string state to the canonical machine. Unknown strings map to
    /// <see cref="ActionState.Escalated"/> — a state the machine treats as terminal — so a corrupt
    /// or future value can never be transitioned OUT of by accident.
    /// </summary>
    public static ActionState ToCanonical(string? proposalState) => (proposalState ?? "").Trim().ToLowerInvariant() switch
    {
        // A freshly proposed action has already passed Draft -> Validated -> RiskScored inside
        // Propose (catalog validation, then blast radius); what persists is the approval wait.
        "pending" => ActionState.WaitingForApproval,
        "approved" => ActionState.Approved,
        // Operator rejection and supersession are both refusals of the proposal — Failed is the
        // lifecycle's terminal for "this will never run", and both come from WaitingForApproval.
        "rejected" or "superseded" => ActionState.Failed,
        "executed" => ActionState.CompletedVerified,   // legacy terminal; per-row truth is LifecycleState
        Persisted.WaitingForApproval => ActionState.WaitingForApproval,
        Persisted.Executing => ActionState.Executing,
        Persisted.Verifying => ActionState.Verifying,
        Persisted.CompletedVerified => ActionState.CompletedVerified,
        Persisted.Failed => ActionState.Failed,
        Persisted.Escalated => ActionState.Escalated,
        _ => ActionState.Escalated,
    };

    /// <summary>
    /// Refuse any state mutation the canonical lifecycle refuses. Returns (true, "") when legal.
    /// The executor calls this BEFORE persisting — the guard is structural, not advisory.
    /// </summary>
    public static (bool Ok, string Reason) Guard(string? fromProposalState, ActionState to)
    {
        var result = ActionLifecycle.Transition(ToCanonical(fromProposalState), to);
        return result.Ok ? (true, "") : (false, $"lifecycle refuses: {result.Reason}");
    }

    /// <summary>
    /// Guard specifically for an APPROVAL DECISION (approve or reject). Stricter than
    /// <see cref="Guard"/> on purpose: rejection maps to Failed, and Approved -> Failed is a legal
    /// lifecycle transition — but that edge exists for EXECUTION failure, not for operator
    /// rejection. A decision is the WaitingForApproval exit and nothing else; rejecting an
    /// already-approved proposal would be revoking an approval, an operation the executor
    /// deliberately does not have. (Caught by the pre-migration test suite: the plain transition
    /// guard was WEAKER than the string check it replaced for exactly this case.)
    /// </summary>
    public static (bool Ok, string Reason) GuardDecision(string? fromProposalState, bool approve)
    {
        var canonical = ToCanonical(fromProposalState);
        if (canonical != ActionState.WaitingForApproval)
            return (false, $"lifecycle refuses: a decision is the WaitingForApproval exit, and this proposal is in '{canonical}'");
        return Guard(fromProposalState, approve ? ActionState.Approved : ActionState.Failed);
    }

    /// <summary>
    /// Build the recovery context from what the proposal actually establishes — nothing inferred,
    /// nothing optimistic. A homelab rollback NOTE is prose for a human, not a deterministic
    /// rollback procedure, so <c>RollbackAvailable</c> is false: claiming otherwise would let the
    /// orchestrator recommend an "immediate rollback" no machinery can perform.
    /// </summary>
    public static RecoveryDecision RecoveryForFailedVerify(ActionProposal proposal) =>
        RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false,
            BackupAvailable: proposal.BackupCovered,
            // An internet-exposed target whose action did not verify is treated as a potential
            // security matter — the orchestrator quarantines rather than retries.
            SecurityImplication: proposal.InternetExposed));
}
