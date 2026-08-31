using System.Text.Json;
using Anthill.Core.Tools;

namespace Anthill.Core.Agents;

/// <summary>
/// THE SYSTEM OPERATOR. v0.3.8.102, PLAN.md §2b — the ant that reaches the homelab's
/// approval-gated action pipeline through the mission spine, not beside it.
///
/// DETERMINISTIC BY DESIGN, like the tester: no model call anywhere in this class. The goal is
/// parsed into an allowlisted action by a fixed mapping; the proposal, the before-state, the
/// approval identity, the receipt and the after-state all come from the pipeline's own rows
/// through the two spine tools; and the record is stamped, never composed. A model that wanted to
/// operate infrastructure would have to convince the PLANNER to type a task into this contract —
/// and then everything downstream of that typing is deterministic gates and a human decision.
///
/// WHAT REFUSES WHERE, because the ladder matters: an unparseable goal refuses HERE, with the
/// vocabulary named. A forbidden action refuses in the CATALOG. A missing rollback note refuses in
/// the PROPOSE tool. An unapproved execution refuses at the DISPATCH CHOKEPOINT (absence is not
/// consent) — and this ant then completes WITH A WARNING and no artifact, because the mission must
/// run on to its verifier and be refused by the class gate for the honest reason: the operation
/// was proposed and never approved, which the operator reading the evaluation should learn in the
/// gate's words rather than from a task that died.
/// </summary>
public sealed class SystemOperatorAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    public SystemOperatorAnt(ToolRegistry tools) : base("system_operator") => _tools = tools;

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("system_operator")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the system operator execution contract");

        // ---- 1. THE GOAL, PARSED INTO THE CATALOG'S VOCABULARY ----------------------------------
        //
        // A fixed mapping, not a guess: the nouns pick the kind, the kind picks the allowlisted
        // action. A goal this cannot parse is refused with the vocabulary named — proposing
        // "something" against "somewhere" would put a fabricated target into a real pipeline.
        var text = $"{mission.Goal}\n{task.Title}\n{task.Description}";
        var (actionType, targetKind) = ClassifyAction(text);
        var targetId = ResolveTarget(text);
        if (actionType is null || targetId is null)
            return AntExecutionResult.Blocked(
                "the request does not name an allowlisted operation and target this operator can "
              + "propose (recognised: restart/reboot of a container, vm or service, with a named "
              + "target). Nothing was proposed.");

        // ---- 2. PROPOSE, WITH REVERSIBILITY AS A PRECONDITION -----------------------------------
        var rollbackNote =
            $"Reverse with the paired action ({RollbackPair(actionType)}) on {targetId}; "
          + "verify the state with the runner's probe afterwards.";
        var proposed = _tools.RunTool(Anthill.SDK.Contracts.SystemActionToolNames.Propose,
            mission.Id, task.Id, Name, new()
            {
                ["action_type"] = actionType,
                ["target_kind"] = targetKind,
                ["target_id"] = targetId,
                ["summary"] = TextUtil.Truncate($"Requested by mission: {mission.Goal}", 300),
                ["rollback_note"] = rollbackNote,
            });
        if (!proposed.Success)
            return AntExecutionResult.Failed(FailureClass.DependencyFailure,
                $"the proposal was refused by the pipeline: {proposed.Error ?? proposed.Output}");

        var proposal = Parse(proposed.Output);
        var proposalId = proposal.GetValueOrDefault("proposal_id") ?? "";
        var beforeState = proposal.GetValueOrDefault("before_state") ?? "";

        // ---- 3. EXECUTE, UNDER THE OPERATOR'S RECORDED DECISION ---------------------------------
        var executed = _tools.RunTool(Anthill.SDK.Contracts.SystemActionToolNames.Execute,
            mission.Id, task.Id, Name, new() { ["proposal_id"] = proposalId });
        if (!executed.Success)
            // Proposed, not approved — the mission runs on and the class gate refuses it with the
            // honest reason. A structured warning, never a silent pass and never a dead task.
            return AntExecutionResult.SucceededWithWarnings(
                $"Operation proposed as {proposalId} and NOT executed: {executed.Error ?? executed.Output}",
                new[] { "operation_not_executed: the proposal exists in the approval pipeline and "
                      + "no operator decision authorised execution — absence is not consent" },
                $"Proposed {actionType} on {targetKind}/{targetId} (proposal {proposalId}). "
              + "Execution awaits an operator decision.");

        var execution = Parse(executed.Output);

        // ---- 4. THE RECORD, STAMPED FROM THE PIPELINE'S OWN ROWS --------------------------------
        var operation = new Anthill.SDK.Artifacts.SystemOperation(
            ProposalId: proposalId,
            ActionType: actionType,
            TargetKind: targetKind ?? "",
            TargetId: targetId,
            RollbackNote: rollbackNote,
            BeforeState: beforeState,
            Receipt: execution.GetValueOrDefault("receipt") ?? "",
            AfterState: execution.GetValueOrDefault("after_state") ?? "",
            ApprovedBy: execution.GetValueOrDefault("approved_by") ?? "");

        var result = new AntExecutionResult
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = $"{actionType} on {targetKind}/{targetId}: executed and verified under {operation.ApprovedBy}.",
            Narrative = $"Operation {proposalId}: {operation.Receipt}\nBefore: {operation.BeforeState}\n"
                      + $"After: {operation.AfterState}\nRollback: {operation.RollbackNote}",
            Evidence = { new AntEvidence(AntEvidenceKinds.Tool, Anthill.SDK.Contracts.SystemActionToolNames.Execute,
                $"proposal {proposalId} approved by {operation.ApprovedBy}") },
        };
        result.Artifacts.Add(new AntArtifact("system_operation",
            $"{actionType} → {targetId}", operation.ToJson()));
        return result;
    }

    private static (string? ActionType, string? TargetKind) ClassifyAction(string text)
    {
        var lowered = text.ToLowerInvariant();
        var operational = lowered.Contains("restart") || lowered.Contains("reboot") || lowered.Contains("power-cycle")
            || lowered.Contains("power cycle");
        if (!operational) return (null, null);

        if (lowered.Contains("container") || lowered.Contains("docker")) return ("restart_container", "container");
        if (lowered.Contains("vm") || lowered.Contains("virtual machine")) return ("restart_vm", "vm");
        if (lowered.Contains("service") || lowered.Contains("daemon")) return ("restart_service", "service");
        return (null, null);
    }

    /// <summary>
    /// The target, from the goal's own words: "the X container/vm/service" names the subject,
    /// "on host/node Y" locates it, and both survive into the id as "Y/X" — the shape the
    /// Proxmox runner already reads. Either half alone is still a target; neither is a refusal.
    /// </summary>
    private static string? ResolveTarget(string text)
    {
        var subject = System.Text.RegularExpressions.Regex.Match(text,
            @"\bthe\s+([\w][\w.-]*)\s+(?:docker\s+)?(?:container|vm|virtual machine|service|daemon)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
        var host = System.Text.RegularExpressions.Regex.Match(text,
            @"\bon\s+(?:the\s+)?(?:host|node)\s+([\w][\w.-]*)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;

        if (subject.Length > 0 && host.Length > 0) return $"{host}/{subject}";
        if (subject.Length > 0) return subject;
        if (host.Length > 0) return host;
        return null;
    }

    private static string RollbackPair(string actionType) => actionType switch
    {
        "restart_container" => "restart_container again, or start_container if it stays down",
        "restart_vm" => "restart_vm again, or start_vm if it stays down",
        _ => "restart_service again",
    };

    /// <summary>The tool outputs are this slice's own JSON; a parse failure reads as empty fields,
    /// which the class gate then refuses by name — never invented values.</summary>
    private static Dictionary<string, string> Parse(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json!);
            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
        }
        catch (JsonException) { }
        return result;
    }
}
