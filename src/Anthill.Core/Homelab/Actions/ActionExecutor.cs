using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Homelab.Approvals;

namespace Anthill.Core.Homelab.Actions;

// NOTE: Anthill.Core's GlobalUsings.cs binds the bare identifier `Task` to the domain mission
// entity (Anthill.Core.Domain.Task). Generic Task<T> still resolves to the threading type
// (aliases are arity-specific), but non-generic statics like Task.FromResult must be written
// fully qualified — the same convention FakeProviders/IncidentManager/RiskAnalyzer follow.

/// <summary>Outcome of a runner call (execute, dry-run, or verify).</summary>
public sealed record ActionRunResult(bool Ok, string Message);

/// <summary>
/// A thing that can carry out an approved action. v2.3.0 shipped the framework with two runners:
/// <see cref="LocalActionRunner"/> (touches only ANTHILL's own database — zero network) and
/// <see cref="MockActionRunner"/> (the deterministic test/dev harness, mirroring the v1.9.1
/// mock-provider pattern). v2.3.1 added <see cref="ProxmoxActionRunner"/>, the first real
/// infrastructure runner (double-gated, structurally allowlisted, target-guard enforced).
/// </summary>
public interface IHomelabActionRunner
{
    string Name { get; }
    bool CanRun(ActionProposal proposal);
    Task<ActionRunResult> ExecuteAsync(ActionProposal proposal, CancellationToken ct = default);
    /// <summary>Describes exactly what Execute would do, with real names/ids — never performs it.</summary>
    Task<ActionRunResult> DryRunAsync(ActionProposal proposal, CancellationToken ct = default);
    /// <summary>Post-execution verification (NORTH_STAR safety rule 10: never pretend something was fixed).</summary>
    Task<ActionRunResult> VerifyAsync(ActionProposal proposal, CancellationToken ct = default);
}

/// <summary>
/// The V2.3.0 approval-gated action pipeline (NORTH_STAR Phase 12, bound by docs/APPROVALS.md):
/// propose → blast-radius score → approve/reject (human, separate permission) → execute
/// (separate permission) → verify → audit. Every safety requirement from APPROVALS.md §"V2.1
/// execution requirements" is enforced HERE, not in the UI:
///  1. approval and execution are separate permission checks (done by the API layer),
///  2. execution re-reads state and refuses anything not 'approved' (TOCTOU guard),
///  3. the HOMELAB_STOP kill switch halts every execution regardless of state,
///  4. every transition lands on the homelab_events audit stream (+ change_log),
///  5. the forbidden-action list is enforced in the executor, structurally.
/// A rollback note is mandatory before execution (APPROVALS.md: "RollbackNote (mandatory before
/// execution)").
/// </summary>
public sealed class ActionExecutor
{
    private readonly HomelabRepository _repo;
    private readonly IReadOnlyList<IHomelabActionRunner> _runners;
    private readonly Func<bool> _isStopped;

    public ActionExecutor(HomelabRepository repo, IReadOnlyList<IHomelabActionRunner> runners, Func<bool>? isStopped = null)
    {
        _repo = repo;
        _runners = runners;
        _isStopped = isStopped ?? (() => HomelabActionControl.IsStopped);
    }

    public sealed record ProposeRequest(
        string ActionType, string TargetKind, string TargetId, string Title, string Summary,
        string RollbackNote, string Payload, string ServiceCriticality, bool BackupCovered, bool InternetExposed);

    /// <summary>Validates against the catalog, scores blast radius, dedupes, persists, audits.</summary>
    public (ActionProposal? Proposal, string? Error) Propose(ProposeRequest request, string requestedBy)
    {
        var refusal = ActionCatalog.Refusal(request.ActionType);
        if (refusal is not null) return (null, refusal);
        if (string.IsNullOrWhiteSpace(request.TargetId)) return (null, "A target id is required.");

        var proposal = new ActionProposal
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? $"{request.ActionType} → {request.TargetId}" : request.Title.Trim(),
            Summary = request.Summary?.Trim() ?? "",
            ActionType = request.ActionType.Trim().ToLowerInvariant(),
            TargetKind = (request.TargetKind ?? "").Trim().ToLowerInvariant(),
            TargetId = request.TargetId.Trim(),
            RollbackNote = request.RollbackNote?.Trim() ?? "",
            Payload = request.Payload ?? "",
            ServiceCriticality = (request.ServiceCriticality ?? "").Trim().ToLowerInvariant(),
            BackupCovered = request.BackupCovered,
            InternetExposed = request.InternetExposed,
            RequestedBy = requestedBy,
            CreatedAt = AnthillTime.NowUtc().ToIso(),
        };
        proposal.DedupeKey = $"{proposal.ActionType}:{proposal.TargetId}".ToLowerInvariant();
        // v2.3.1.1: probe runners with the REAL proposal, not an action-type-only stub — the
        // Proxmox runner also validates the target form, so the stub always answered false and
        // every Proxmox-backed action reported dry_run_available = false.
        proposal.DryRunAvailable = _runners.Any(r => r.CanRun(proposal));

        // Dependency fan-out from the v1.10 dependency map: who depends on this target?
        proposal.DependencyFanout = _repo.ListDependencies().Count(d =>
            string.Equals(d.ToId, proposal.TargetId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.FromId, proposal.TargetId, StringComparison.OrdinalIgnoreCase));

        BlastRadius.Apply(proposal);

        // The one dedupe rule (APPROVALS.md): equal keys may not both be pending — newer supersedes.
        foreach (var older in _repo.ListActionProposals(200).Where(p => p.State == "pending" && p.DedupeKey == proposal.DedupeKey))
        {
            older.State = "superseded";
            _repo.UpdateActionProposal(older);
            Audit(older, "action_superseded", "info", $"Superseded by newer proposal {proposal.ApprovableId}.", requestedBy);
        }

        _repo.SaveActionProposal(proposal);
        Audit(proposal, "action_proposed", "info",
            $"{proposal.ActionType} on {proposal.TargetKind}/{proposal.TargetId} proposed (blast radius {proposal.BlastRadiusScore} = {proposal.RiskLevel}).", requestedBy);
        return (proposal, null);
    }

    public (bool Ok, string Message) Approve(string id, string decidedBy) => Decide(id, decidedBy, approve: true);
    public (bool Ok, string Message) Reject(string id, string decidedBy) => Decide(id, decidedBy, approve: false);

    private (bool Ok, string Message) Decide(string id, string decidedBy, bool approve)
    {
        var proposal = _repo.GetActionProposal(id);
        if (proposal is null) return (false, "Unknown action proposal.");
        if (proposal.State != "pending")
            return (false, $"Only pending proposals can be decided — this one is '{proposal.State}'.");
        proposal.State = approve ? "approved" : "rejected";
        proposal.DecidedBy = decidedBy;
        proposal.DecidedAt = AnthillTime.NowUtc().ToIso();
        _repo.UpdateActionProposal(proposal);
        Audit(proposal, approve ? "action_approved" : "action_rejected", "info",
            $"{proposal.ActionType} on {proposal.TargetKind}/{proposal.TargetId} {proposal.State} by {decidedBy}.", decidedBy);
        return (true, $"Proposal {proposal.State}." + (approve && string.IsNullOrWhiteSpace(proposal.RollbackNote)
            ? " A rollback note is still required before it can execute." : ""));
    }

    /// <summary>Describes what would happen without doing it. Allowed from pending or approved; never changes state.</summary>
    public async Task<(bool Ok, string Message)> DryRunAsync(string id, string requestedBy, CancellationToken ct = default)
    {
        var proposal = _repo.GetActionProposal(id);
        if (proposal is null) return (false, "Unknown action proposal.");
        var refusal = ActionCatalog.Refusal(proposal.ActionType);
        if (refusal is not null) return (false, refusal);
        var runner = _runners.FirstOrDefault(r => r.CanRun(proposal));
        if (runner is null) return (false, NoRunnerMessage(proposal));
        var result = await runner.DryRunAsync(proposal, ct);
        Audit(proposal, "action_dry_run", "info", $"Dry run by {requestedBy} via {runner.Name}: {result.Message}", requestedBy);
        return (result.Ok, result.Message);
    }

    /// <summary>The gated execution path. Every refusal here is a safety property with a test on it.</summary>
    public async Task<(bool Ok, string Message)> ExecuteAsync(string id, string executedBy, CancellationToken ct = default)
    {
        // Safety rule 12: nothing executes while the kill switch is engaged — checked FIRST.
        if (_isStopped())
            return (false, "HOMELAB_STOP is engaged — no homelab action may execute until an operator resumes (POST /homelab/actions/resume or delete .anthill/HOMELAB_STOP).");

        // TOCTOU guard: re-read state at execution time; only 'approved' may run.
        var proposal = _repo.GetActionProposal(id);
        if (proposal is null) return (false, "Unknown action proposal.");
        if (proposal.State != "approved")
            return (false, $"Execution refused: proposal state is '{proposal.State}', not 'approved'. Approval is a distinct human step.");

        // Forbidden/unknown actions are refused here too — the catalog is enforced in the
        // executor, not just at proposal time or in the UI (APPROVALS.md requirement 5).
        var refusal = ActionCatalog.Refusal(proposal.ActionType);
        if (refusal is not null)
        {
            Audit(proposal, "execution_refused", "warning", refusal, executedBy);
            return (false, refusal);
        }

        if (string.IsNullOrWhiteSpace(proposal.RollbackNote))
            return (false, "Execution refused: a rollback/recovery note is mandatory before any action executes.");

        var runner = _runners.FirstOrDefault(r => r.CanRun(proposal));
        if (runner is null) return (false, NoRunnerMessage(proposal));

        ActionRunResult result;
        try { result = await runner.ExecuteAsync(proposal, ct); }
        catch (Exception ex) { result = new ActionRunResult(false, "Runner threw: " + ex.GetBaseException().Message); }

        if (!result.Ok)
        {
            proposal.ExecutionResult = Truncate(result.Message);
            _repo.UpdateActionProposal(proposal); // state stays 'approved' — the failure is visible, retry is explicit
            Audit(proposal, "execution_failed", "error",
                $"{proposal.ActionType} on {proposal.TargetKind}/{proposal.TargetId} failed via {runner.Name}: {result.Message}", executedBy);
            return (false, result.Message);
        }

        // Post-execution verification — report honestly, never assume success (safety rule 10).
        ActionRunResult verify;
        try { verify = await runner.VerifyAsync(proposal, ct); }
        catch (Exception ex) { verify = new ActionRunResult(false, "Verification threw: " + ex.GetBaseException().Message); }

        proposal.State = "executed";
        proposal.ExecutedBy = executedBy;
        proposal.ExecutedAt = AnthillTime.NowUtc().ToIso();
        proposal.ExecutionResult = Truncate($"{result.Message} | verify: {(verify.Ok ? "ok" : "FAILED")} — {verify.Message}");
        _repo.UpdateActionProposal(proposal);
        Audit(proposal, "action_executed", verify.Ok ? "info" : "warning",
            $"{proposal.ActionType} on {proposal.TargetKind}/{proposal.TargetId} executed via {runner.Name}. {proposal.ExecutionResult}", executedBy);
        _repo.RecordChange(new ChangeRecord
        {
            SubjectKind = proposal.TargetKind.Length > 0 ? proposal.TargetKind : "homelab_action",
            SubjectId = proposal.TargetId, ChangeKind = "action_executed",
            Summary = $"{proposal.ActionType}: {proposal.ExecutionResult}", ChangedBy = executedBy,
        });
        return (true, proposal.ExecutionResult);
    }

    private static string NoRunnerMessage(ActionProposal p) =>
        $"No registered runner can execute '{p.ActionType}' on target '{p.TargetId}'. Infrastructure "
        + "actions need the Proxmox write runner: enable homelab_proxmox_write_actions_enabled (plus the "
        + "Proxmox integration) and use a node/vmid target like 'pve1/104'.";

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private void Audit(ActionProposal p, string eventType, string severity, string message, string by)
    {
        _repo.RecordEvent(new HomelabEvent
        {
            EventType = eventType, SubjectKind = "homelab_action", SubjectId = p.ApprovableId,
            Severity = severity, Message = $"[{by}] {message}",
        });
    }
}

/// <summary>
/// Executes the purely local action set — these touch only ANTHILL's own database and never any
/// network or infrastructure, so they are the safe first inhabitants of the execution pipeline.
/// </summary>
public sealed class LocalActionRunner : IHomelabActionRunner
{
    private readonly HomelabRepository _repo;
    public string Name => "local";

    public LocalActionRunner(HomelabRepository repo) => _repo = repo;

    public bool CanRun(ActionProposal p) => ActionCatalog.LocalActions.Contains(p.ActionType);

    public Task<ActionRunResult> ExecuteAsync(ActionProposal p, CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(p.ActionType switch
    {
        "resolve_incident" => ResolveIncident(p),
        "update_inventory" => UpdateInventory(p),
        "run_diagnostic" => Diagnostic(),
        _ => new ActionRunResult(false, $"LocalActionRunner cannot run '{p.ActionType}'."),
    });

    public Task<ActionRunResult> DryRunAsync(ActionProposal p, CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(p.ActionType switch
    {
        "resolve_incident" => new ActionRunResult(true, $"Would mark incident '{p.TargetId}' resolved" + (PayloadField(p, "root_cause") is { Length: > 0 } rc ? $" with root cause: {rc}" : "") + ". No infrastructure is touched."),
        "update_inventory" => new ActionRunResult(true, $"Would append an operator note to {p.TargetKind} '{p.TargetId}' in the inventory. No infrastructure is touched."),
        "run_diagnostic" => new ActionRunResult(true, "Would report table counts and the current health summary from the colony database. Read-only."),
        _ => new ActionRunResult(false, $"LocalActionRunner cannot dry-run '{p.ActionType}'."),
    });

    public Task<ActionRunResult> VerifyAsync(ActionProposal p, CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(p.ActionType switch
    {
        "resolve_incident" => _repo.GetIncident(p.TargetId) is { Status: "resolved" }
            ? new ActionRunResult(true, "incident is resolved")
            : new ActionRunResult(false, "incident is NOT resolved after execution"),
        _ => new ActionRunResult(true, "local action verified by its own write path"),
    });

    private ActionRunResult ResolveIncident(ActionProposal p)
    {
        if (_repo.GetIncident(p.TargetId) is null) return new ActionRunResult(false, $"Unknown incident '{p.TargetId}'.");
        _repo.SetIncidentStatus(p.TargetId, "resolved", PayloadField(p, "root_cause") ?? "", p.ExecutedBy is { Length: > 0 } by ? by : p.RequestedBy);
        return new ActionRunResult(true, $"Incident '{p.TargetId}' marked resolved.");
    }

    private ActionRunResult UpdateInventory(ActionProposal p)
    {
        var note = PayloadField(p, "note");
        if (string.IsNullOrWhiteSpace(note)) return new ActionRunResult(false, "update_inventory needs a payload note field, e.g. {\"note\":\"...\"}.");
        var by = p.ExecutedBy is { Length: > 0 } b ? b : p.RequestedBy;
        if (p.TargetKind == "service")
        {
            var svc = _repo.ListServices().FirstOrDefault(s => string.Equals(s.Id, p.TargetId, StringComparison.OrdinalIgnoreCase));
            if (svc is null) return new ActionRunResult(false, $"Unknown service '{p.TargetId}'.");
            svc.Notes = (svc.Notes.Length > 0 ? svc.Notes + "\n" : "") + note;
            _repo.UpsertService(svc, by);
            return new ActionRunResult(true, $"Note appended to service '{svc.Name}'.");
        }
        var node = _repo.ListNodes().FirstOrDefault(n => string.Equals(n.Id, p.TargetId, StringComparison.OrdinalIgnoreCase));
        if (node is null) return new ActionRunResult(false, $"Unknown {p.TargetKind} '{p.TargetId}'.");
        node.Notes = (node.Notes.Length > 0 ? node.Notes + "\n" : "") + note;
        _repo.UpsertNode(node, by);
        return new ActionRunResult(true, $"Note appended to node '{node.Name}'.");
    }

    private ActionRunResult Diagnostic()
    {
        var counts = _repo.TableCounts();
        return new ActionRunResult(true, "Diagnostic: " + string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private static string? PayloadField(ActionProposal p, string field)
    {
        if (string.IsNullOrWhiteSpace(p.Payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(p.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v)
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Deterministic, network-free harness runner (the v1.9.1 mock-provider pattern applied to
/// actions). Handles every allowlisted action, always succeeds, and records what it "did" in the
/// message — used by tests and by dev installs running with mocks enabled. Never registered
/// unless the mock gate is on.
/// </summary>
public sealed class MockActionRunner : IHomelabActionRunner
{
    public string Name => "mock";
    public List<string> Executed { get; } = new();

    public bool CanRun(ActionProposal p) => ActionCatalog.Allowed.Contains(p.ActionType);

    public Task<ActionRunResult> ExecuteAsync(ActionProposal p, CancellationToken ct = default)
    {
        Executed.Add($"{p.ActionType}:{p.TargetId}");
        return System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, $"[mock] {p.ActionType} performed on {p.TargetKind}/{p.TargetId}."));
    }

    public Task<ActionRunResult> DryRunAsync(ActionProposal p, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, $"[mock] would perform {p.ActionType} on {p.TargetKind}/{p.TargetId}."));

    public Task<ActionRunResult> VerifyAsync(ActionProposal p, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, $"[mock] {p.ActionType} verified."));
}
