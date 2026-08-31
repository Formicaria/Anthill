using Anthill.Core.Missions;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// THE DETERMINISTIC GATE FOR SYSTEM OPERATIONS. v0.3.8.102, PLAN.md §2b — the exit line verbatim:
/// a reversible operation with before-state, receipt and after-state.
///
/// What it decides, from records alone: a system-action mission holds a `system_operation` record,
/// and every piece the exit line makes load-bearing is present — the proposal's identity, a
/// before-state captured before anything changed, a receipt of what the pipeline executed, an
/// after-state probed once it had, a rollback note (the executor's own precondition, visible in
/// the record), and the DISTINCT human approval the escalation lane recorded. Each absence is
/// refused by name: a mission whose operation was proposed but never approved has delivered a
/// proposal, and the explanation says exactly that.
///
/// KEYED ON THE SPECIFICATION, like the audit and diagnosis gates — intake derives the class
/// deterministically — and therefore fail-CLOSED on an unreadable store (S3: an outage is never
/// permission). The record-keyed gates' opposite asymmetry stands beside it, each matching its key.
///
/// WHAT IT DOES NOT DECIDE: whether the operation was WISE, whether the after-state is the state
/// the operator wanted, or whether the rollback note would actually work — semantic judgments,
/// the standing line. What is checkable is presence, identity, and agreement with the pipeline's
/// own lifecycle; `SystemActionMissionTests` checks the lifecycle agreement composedly, against
/// the executor's own rows.
/// </summary>
public static class OperationIntegrity
{
    /// <param name="Satisfied">Whether every check passed.</param>
    /// <param name="Reasons">Each failed check, named — the missing piece, the absent record.</param>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Reasons)
    {
        public string Explanation => Satisfied
            ? "operation integrity: satisfied"
            : "operation integrity NOT satisfied — " + string.Join("; ", Reasons);
    }

    private static readonly Result Ok = new(true, Array.Empty<string>());

    public static bool Applies(MissionSpecification? specification) =>
        specification is { MissionClass: MissionSpecification.SystemActionClass } && specification.IsActionable;

    /// <summary>
    /// Grade the operation. Every input is a record the mission left behind.
    /// </summary>
    /// <param name="artifacts">The mission's artifacts (the operation records live here), or null
    /// when the store could not be read — which fails CLOSED, per the class comment.</param>
    public static Result Evaluate(MissionSpecification specification, IReadOnlyList<Artifact>? artifacts)
    {
        if (!Applies(specification)) return Ok;

        if (artifacts is null)
            return new Result(false, new[]
                { "the artifact store could not be read, so no operation record can be shown" });

        var records = artifacts
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.SystemOperation, StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.Id, Operation: SystemOperation.FromJson(a.Payload)))
            .ToList();

        if (records.Count == 0)
            return new Result(false, new[]
            {
                "no operation record exists — the mission was asked to change a service and "
              + "nothing checkable was operated (the operation may never have been proposed, or "
              + "was proposed and never approved — the escalation lane's rule is that absence of "
              + "an answer is not consent)",
            });

        var reasons = new List<string>();
        foreach (var (id, operation) in records)
        {
            if (operation is null)
            {
                reasons.Add($"operation record '{id}' does not parse — a row whose type is a promise it does not keep");
                continue;
            }

            // The exit line's nouns, each load-bearing and each refused by name.
            if (string.IsNullOrWhiteSpace(operation.ProposalId))
                reasons.Add($"operation record '{id}' names no proposal — nothing ties it to the pipeline's own rows");
            if (string.IsNullOrWhiteSpace(operation.BeforeState))
                reasons.Add($"operation record '{id}' captured no before-state — what changed cannot be answered without what was");
            if (string.IsNullOrWhiteSpace(operation.Receipt))
                reasons.Add($"operation record '{id}' holds no execution receipt — an operation that is a description of itself");
            if (string.IsNullOrWhiteSpace(operation.AfterState))
                reasons.Add($"operation record '{id}' probed no after-state — 'command issued' is not 'desired state achieved'");

            // Reversibility as a precondition, visible in the record.
            if (string.IsNullOrWhiteSpace(operation.RollbackNote))
                reasons.Add($"operation record '{id}' carries no rollback note — the executor mandates one before execution, and the record must show it");

            // The DISTINCT human decision. The proposing model cannot be the approving authority;
            // the escalation lane's decision identity is what this field carries.
            if (string.IsNullOrWhiteSpace(operation.ApprovedBy))
                reasons.Add($"operation record '{id}' records no approval — execution without a distinct operator decision is the boundary this class exists to keep");
        }

        return reasons.Count == 0 ? Ok : new Result(false, reasons);
    }
}
