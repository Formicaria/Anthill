using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Homelab.Actions;

// NOTE: the module's standing Task-alias convention applies (see ActionExecutor's header):
// non-generic Task statics are written fully qualified.

/// <summary>
/// THE SPINE'S DOOR INTO THE APPROVAL PIPELINE. v0.3.8.102, PLAN.md §2b — "existing homelab
/// workers reached through the mission spine, not beside it."
///
/// Two tools over the REAL <see cref="ActionExecutor"/> — the same object, with every gate it
/// ships: catalog refusal, blast-radius scoring, the WaitingForApproval lifecycle, the TOCTOU
/// re-read at execution, the mandatory rollback note, the kill switch. Nothing here re-implements
/// or bypasses any of it; these tools are how a mission REACHES it.
///
/// THE BOUNDARY BETWEEN THE TWO TOOLS IS THE RELEASE'S WHOLE POINT. Propose writes a
/// colony-database row (the LocalActionRunner precedent — zero network) and captures the
/// before-state via the runner's own dry-run; any worker holding the capability may call it.
/// Execute is listed in <see cref="SystemActionToolNames"/> as side-effecting, so the dispatch
/// chokepoint demands a conversation-scoped operator decision BEFORE the tool body runs — and the
/// body then reads that same decision to stamp WHO approved, because the approval identity in the
/// record must be the escalation lane's, never the proposing ant's.
/// </summary>
public static class SystemActionTools
{
    /// <summary>The execute tool's name, forwarded from the shared vocabulary — tests and the
    /// escalation set read one spelling.</summary>
    public const string ExecuteToolName = SystemActionToolNames.Execute;

    /// <summary>
    /// The operator's decision for the execute action, or null when nobody has decided.
    /// A DELEGATE rather than a type, deliberately: this module references only the SDK — the
    /// seam its own header calls the whole point — and the escalation lane lives in the core. The
    /// COMPOSITION supplies the bridge, which keeps the module ignorant of conversations while
    /// the identity in the record stays the lane's own.
    ///
    /// It takes the MISSION ID because a mission does not run inside the conversation's ambient
    /// scope — the answers the operator gave at mission start are SAVED as escalation decisions
    /// (the v0.3.8.46 rule: an answer given IS an operator decision, recorded whether or not the
    /// work ends up needing it), and the bridge reads that durable record. The permission is the
    /// record, so the record is where the tool looks for it.
    /// </summary>
    public delegate (bool Allowed, string DecisionId, string Reason)? OperatorDecisionSource(string missionId);

    public static ITool[] For(ActionExecutor executor, OperatorDecisionSource operatorDecision) =>
        new ITool[] { new ProposeTool(executor), new ExecuteTool(executor, operatorDecision) };

    private sealed class ProposeTool : ITool
    {
        private readonly ActionExecutor _executor;
        public ProposeTool(ActionExecutor executor) => _executor = executor;

        public string Name => SystemActionToolNames.Propose;
        public string Description =>
            "Propose an allowlisted homelab action (restart_container, restart_vm, …) into the "
          + "approval pipeline with a mandatory rollback note; returns the proposal id and the "
          + "captured before-state. Never executes anything.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var actionType = (args.GetValueOrDefault("action_type")?.ToString() ?? "").Trim();
            var targetKind = (args.GetValueOrDefault("target_kind")?.ToString() ?? "").Trim();
            var targetId = (args.GetValueOrDefault("target_id")?.ToString() ?? "").Trim();
            var summary = (args.GetValueOrDefault("summary")?.ToString() ?? "").Trim();
            var rollbackNote = (args.GetValueOrDefault("rollback_note")?.ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(rollbackNote))
                return new ToolResult(Name, false, "",
                    "A rollback note is required at PROPOSAL time — reversibility is a "
                  + "precondition of this class, not something added before execution.",
                    FailureClass.ValidationFailure);

            var (proposal, error) = _executor.Propose(new ActionExecutor.ProposeRequest(
                    actionType, targetKind, targetId,
                    Title: $"{actionType} → {targetId}", Summary: summary,
                    RollbackNote: rollbackNote, Payload: "",
                    ServiceCriticality: "normal", BackupCovered: false, InternetExposed: false),
                // The audit label the pipeline's rows carry: the tester's operation lane is the
                // requester — the role that dispatched this tool, not a role of its own.
                requestedBy: "tester");
            if (proposal is null)
                return new ToolResult(Name, false, "", error ?? "proposal refused", FailureClass.ValidationFailure);

            // THE BEFORE-STATE, from the runner's own dry-run — the pipeline's account of what is
            // and what would change, captured while nothing has. A dry-run failure fails the
            // proposal honestly: a before-state that could not be captured is not one to invent.
            var dryRun = _executor.DryRunAsync(proposal.ApprovableId, "tester").GetAwaiter().GetResult();
            if (!dryRun.Ok)
                return new ToolResult(Name, false, "",
                    $"proposed as {proposal.ApprovableId}, but the before-state could not be captured: {dryRun.Message}",
                    FailureClass.DependencyFailure);

            return new ToolResult(Name, true, Anthill.SDK.Common.Json.Dumps(new
            {
                proposal_id = proposal.ApprovableId,
                action_type = proposal.ActionType,
                target_kind = proposal.TargetKind,
                target_id = proposal.TargetId,
                rollback_note = proposal.RollbackNote,
                before_state = dryRun.Message,
            }, indented: true));
        }
    }

    private sealed class ExecuteTool : ITool
    {
        private readonly ActionExecutor _executor;
        private readonly OperatorDecisionSource _operatorDecision;
        public ExecuteTool(ActionExecutor executor, OperatorDecisionSource operatorDecision)
        { _executor = executor; _operatorDecision = operatorDecision; }

        public string Name => SystemActionToolNames.Execute;
        public string Description =>
            "Approve and execute a pending system-action proposal under the operator's recorded "
          + "escalation decision, then verify. Refused without that decision — absence is not consent.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var proposalId = (args.GetValueOrDefault("proposal_id")?.ToString() ?? "").Trim();
            if (proposalId.Length == 0)
                return new ToolResult(Name, false, "", "Missing required argument: proposal_id", FailureClass.ValidationFailure);
            var missionId = (args.GetValueOrDefault("mission_id")?.ToString() ?? "").Trim();

            // THE HUMAN STEP, read from the escalation lane through the injected bridge. This is
            // the identity source, not a convenience: the record must carry WHO approved, and
            // that identity is the lane's recorded decision — never this tool's caller. The
            // bridge resolves it from the mission's own conversation record (the operator's
            // answers are saved as escalation decisions at mission start), so a mission with no
            // recorded decision is refused in so many words — absence is not consent.
            var decision = _operatorDecision(missionId);
            if (decision is null)
                return new ToolResult(Name, false, "",
                    "Execution requires a recorded operator decision, and the mission's "
                  + "conversation holds none for this action — nobody has approved this.",
                    FailureClass.AuthorizationFailure);
            if (!decision.Value.Allowed)
                return new ToolResult(Name, false, "",
                    $"Execution refused: {decision.Value.Reason}", FailureClass.AuthorizationFailure);

            var approver = $"operator:{decision.Value.DecisionId}";
            var (approved, approveMessage) = _executor.Approve(proposalId, approver);
            if (!approved)
                return new ToolResult(Name, false, "", approveMessage, FailureClass.ValidationFailure);

            var (ok, message) = _executor.ExecuteAsync(proposalId, approver).GetAwaiter().GetResult();
            if (!ok)
                return new ToolResult(Name, false, "", message, FailureClass.DependencyFailure);

            // The AFTER-STATE is the runner's verify half, which ExecuteAsync already performed
            // and folded into the result — surfaced as its own field so the record's noun is a
            // field, not a substring hunt.
            return new ToolResult(Name, true, Anthill.SDK.Common.Json.Dumps(new
            {
                proposal_id = proposalId,
                approved_by = approver,
                receipt = message,
                after_state = message.Contains("verify:", StringComparison.OrdinalIgnoreCase)
                    ? message[message.IndexOf("verify:", StringComparison.OrdinalIgnoreCase)..]
                    : message,
            }, indented: true));
        }
    }
}
