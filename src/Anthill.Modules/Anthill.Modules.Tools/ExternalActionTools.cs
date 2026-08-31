using System.Collections.Concurrent;
using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Anthill.SDK.External;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Tools;

/// <summary>
/// THE SPINE'S DOOR TO THE OUTSIDE WORLD. v0.3.8.103.
///
/// Two tools with one boundary between them, and the boundary is where a human decides. PROPOSE
/// resolves the operator's words into a concrete destination and records the proposal; it touches
/// nothing outside the process, which is what makes it safe to run before anyone has approved
/// anything. EXECUTE delivers, and only under a recorded operator decision.
///
/// THE ORDER IS THE DESIGN, not a convenience. Resolution has to happen BEFORE approval is offered,
/// because an operator cannot consent to an alias — "the team's incident webhook" is a name, and
/// approving a name attaches a signature to whatever that name turns out to mean at send time. So
/// the proposal is what gets approved, and the proposal already knows the url.
///
/// THE MODULE REFERENCES ONLY THE SDK, so the core's escalation lane arrives as a delegate exactly
/// the way `.102` wired the homelab's — see <see cref="OperatorDecisionSource"/>. A module that
/// reached into the core to ask whether it may act would be a module that could answer the question
/// itself.
/// </summary>
public static class ExternalActionTools
{
    /// <summary>
    /// The core's escalation lane, injected. Returns null when NOBODY HAS DECIDED, which is a
    /// different fact from a decision of "no" and must stay different: absence of an answer is not
    /// consent, and a delegate that flattened the two would make silence into approval.
    /// </summary>
    public delegate (bool Allowed, string DecisionId, string Reason)? OperatorDecisionSource(string missionId);

    public static ITool[] For(IExternalActionAdapter adapter, OperatorDecisionSource operatorDecision)
    {
        var pending = new ConcurrentDictionary<string, Proposal>(StringComparer.Ordinal);
        return new ITool[]
        {
            new ProposeTool(adapter, pending),
            new ExecuteTool(adapter, operatorDecision, pending),
        };
    }

    /// <summary>What was resolved and is waiting for a human. Held in the process because it is
    /// meaningful only within the mission that made it — a proposal that outlived its mission would
    /// be an approval looking for something to authorize.</summary>
    private sealed record Proposal(string RequestedTarget, string ResolvedTarget, string Method, string Body);

    private sealed class ProposeTool : ITool
    {
        private readonly IExternalActionAdapter _adapter;
        private readonly ConcurrentDictionary<string, Proposal> _pending;

        public ProposeTool(IExternalActionAdapter adapter, ConcurrentDictionary<string, Proposal> pending)
        {
            _adapter = adapter;
            _pending = pending;
        }

        public string Name => ExternalActionToolNames.Propose;
        public string Description =>
            "Resolve a requested external destination to a concrete, allowlisted target and propose "
          + "the send for approval; returns the proposal id and the resolved destination. Sends "
          + "nothing.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var requested = (args.GetValueOrDefault("target")?.ToString() ?? "").Trim();
            var body = (args.GetValueOrDefault("body")?.ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(requested))
                return new ToolResult(Name, false, "",
                    "No destination was named, so there is nothing to resolve and nothing a human "
                  + "could approve.", FailureClass.ValidationFailure);

            var resolution = _adapter.Resolve(requested);
            if (!resolution.Ok)
                // Refused BEFORE approval is offered. Asking a human to approve a destination the
                // colony cannot name is how a signature gets attached to whatever it means later.
                return new ToolResult(Name, false, "",
                    $"the destination could not be resolved: {resolution.Reason}",
                    FailureClass.ValidationFailure);

            var id = Guid.NewGuid().ToString("N")[..12];
            _pending[id] = new Proposal(requested, resolution.Target, resolution.Method, body);

            return new ToolResult(Name, true, Json.Dumps(new
            {
                proposal_id = id,
                kind = _adapter.Kind,
                requested_target = requested,
                resolved_target = resolution.Target,
                method = resolution.Method,
                request_summary = $"{body.Length} character(s)",
            }, indented: true));
        }
    }

    private sealed class ExecuteTool : ITool
    {
        private readonly IExternalActionAdapter _adapter;
        private readonly OperatorDecisionSource _operatorDecision;
        private readonly ConcurrentDictionary<string, Proposal> _pending;

        public ExecuteTool(IExternalActionAdapter adapter, OperatorDecisionSource operatorDecision,
            ConcurrentDictionary<string, Proposal> pending)
        {
            _adapter = adapter;
            _operatorDecision = operatorDecision;
            _pending = pending;
        }

        public string Name => ExternalActionToolNames.Execute;
        public string Description =>
            "Deliver a proposed, operator-approved external send to its resolved destination and "
          + "return the receipt. Irreversible.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var proposalId = (args.GetValueOrDefault("proposal_id")?.ToString() ?? "").Trim();
            var missionId = (args.GetValueOrDefault("mission_id")?.ToString() ?? "").Trim();

            if (!_pending.TryGetValue(proposalId, out var proposal))
                return new ToolResult(Name, false, "",
                    $"no resolved proposal '{proposalId}' is pending — nothing was approved, so "
                  + "there is nothing to send.", FailureClass.ValidationFailure);

            var decision = _operatorDecision(missionId);
            if (decision is null)
                return new ToolResult(Name, false, "",
                    $"no operator decision was recorded for {ExternalActionToolNames.Execute}. "
                  + "Absence of an answer is not consent, and this action cannot be undone.",
                    FailureClass.AuthorizationFailure);

            if (!decision.Value.Allowed)
                return new ToolResult(Name, false, "",
                    $"the operator declined {ExternalActionToolNames.Execute}"
                  + (string.IsNullOrWhiteSpace(decision.Value.Reason) ? "." : $": {decision.Value.Reason}"),
                    FailureClass.AuthorizationFailure);

            var receipt = _adapter.Send(proposal.ResolvedTarget, proposal.Method, proposal.Body);
            if (!receipt.Ok)
                return new ToolResult(Name, false, "",
                    $"the destination refused the send: {receipt.Reason}", FailureClass.DependencyFailure);

            return new ToolResult(Name, true, Json.Dumps(new
            {
                proposal_id = proposalId,
                // The approver's identity is the DECISION's, never the ant's — the distinctness the
                // whole approval design exists to keep.
                approved_by = $"operator:{decision.Value.DecisionId}",
                // Reported by the adapter, not echoed from the proposal: a value the caller supplied
                // would be the caller agreeing with itself, which is exactly how `.99`'s fixture
                // defect stayed green.
                executed_target = receipt.Target,
                receipt = receipt.Receipt,
            }, indented: true));
        }
    }
}
