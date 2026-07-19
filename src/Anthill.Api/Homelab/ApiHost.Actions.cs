using Anthill.Core.Configuration;
using Anthill.Core.Homelab.Actions;

namespace Anthill.Api;

/// <summary>
/// V2.3.0 approval-gated homelab action endpoints (NORTH_STAR Phase 12, bound by docs/APPROVALS.md).
/// Permission split is deliberate and tested: proposing requires manage_homelab_integrations,
/// deciding requires approve_homelab_actions, executing (and dry-running) requires
/// execute_homelab_actions — and BOTH action capability gates ship disabled (fail closed), so a
/// fresh install cannot execute anything until an operator turns the gates on. The kill switch:
/// engaging HOMELAB_STOP needs only approve_homelab_actions (halting must be easy); resuming
/// needs execute_homelab_actions (un-halting is an execution-grade decision).
/// </summary>
public static partial class ApiHost
{
    public static ActionExecutor HomelabActions { get; private set; } = null!;

    private sealed record ActionProposeRequest(
        string? ActionType, string? TargetKind, string? TargetId, string? Title, string? Summary,
        string? RollbackNote, string? Payload, string? ServiceCriticality, bool? BackupCovered, bool? InternetExposed);
    private sealed record ActionRollbackNoteRequest(string? RollbackNote);
    private sealed record KillSwitchRequest(string? Reason);

    private static void InitHomelabActions()
    {
        var runners = new List<IHomelabActionRunner> { new LocalActionRunner(Homelab) };
        // v2.3.1: the first real infrastructure runner. DOUBLE-gated — the Proxmox integration must
        // be enabled AND the operator must explicitly opt in to write actions (default off), so a
        // read-only Proxmox connection can never silently gain power/snapshot/backup capability.
        // Token comes from the credential store per client; the target allowlist is enforced inside
        // the client before any request (v2.3.1.1), exactly like the read-only sync client.
        if (AnthillRuntime.EnableHomelab && AnthillRuntime.EnableHomelabProxmox
            && AnthillRuntime.HomelabProxmoxWriteActionsEnabled
            && !string.IsNullOrWhiteSpace(AnthillRuntime.HomelabProxmoxHost))
            runners.Add(new ProxmoxActionRunner(() => new ProxmoxActionClient(
                AnthillRuntime.HomelabProxmoxHost, AnthillRuntime.HomelabProxmoxPort, HomelabTargets,
                () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabProxmoxCredentialId, usedBy: "ProxmoxActionRunner"),
                AnthillRuntime.HomelabProxmoxInsecureTls,
                protocol: AnthillRuntime.HomelabProxmoxProtocol)));
        // v2.3.1.1: the mock runner is registered LAST. It claims every catalog action, so with the
        // dev mock gate on it previously shadowed the real Proxmox runner (first CanRun match wins)
        // and reported real actions as executed without touching anything.
        if (AnthillRuntime.EnableHomelabMockProviders) runners.Add(new MockActionRunner());
        HomelabActions = new ActionExecutor(Homelab, runners);
    }

    private static void MapHomelabActionEndpoints(WebApplication app)
    {
        app.MapGet("/homelab/actions", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = Homelab.ListActionProposals(100),
                ["stopped"] = HomelabActionControl.IsStopped,
                ["allowed_actions"] = ActionCatalog.Allowed.OrderBy(a => a).ToList(),
                ["design"] = "docs/APPROVALS.md",
            });
        });

        app.MapPost("/homelab/actions/propose", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            ActionProposeRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ActionProposeRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null) return ApiJson.Error("Invalid request body.", "bad_request");
            var (proposal, error) = HomelabActions.Propose(new ActionExecutor.ProposeRequest(
                body.ActionType ?? "", body.TargetKind ?? "", body.TargetId ?? "", body.Title ?? "",
                body.Summary ?? "", body.RollbackNote ?? "", body.Payload ?? "",
                body.ServiceCriticality ?? "", body.BackupCovered ?? false, body.InternetExposed ?? false),
                CurrentUsername(ctx) ?? "operator");
            return error is not null
                ? ApiJson.Error(error, "refused")
                : ApiJson.Ok(proposal, $"Action proposed (blast radius {proposal!.BlastRadiusScore} = {proposal.RiskLevel}). It cannot run until approved" +
                    (string.IsNullOrWhiteSpace(proposal.RollbackNote) ? " and a rollback note is added." : "."));
        });

        app.MapPost("/homelab/actions/{id}/approve", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_homelab_actions"); if (auth is not null) return auth;
            var (ok, message) = HomelabActions.Approve(id, CurrentUsername(ctx) ?? "operator");
            return ok ? ApiJson.Ok(Homelab.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        app.MapPost("/homelab/actions/{id}/reject", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_homelab_actions"); if (auth is not null) return auth;
            var (ok, message) = HomelabActions.Reject(id, CurrentUsername(ctx) ?? "operator");
            return ok ? ApiJson.Ok(Homelab.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        // Rollback notes are mandatory before execution; this lets an approver add/refine one
        // on a pending or approved proposal without re-proposing.
        app.MapPost("/homelab/actions/{id}/rollback-note", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_homelab_actions"); if (auth is not null) return auth;
            ActionRollbackNoteRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ActionRollbackNoteRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.RollbackNote)) return ApiJson.Error("A rollback note is required.", "bad_request");
            var proposal = Homelab.GetActionProposal(id);
            if (proposal is null) return ApiJson.Error("Unknown action proposal.", "bad_request");
            if (proposal.State is not ("pending" or "approved"))
                return ApiJson.Error($"Rollback note can only be set while pending/approved — this proposal is '{proposal.State}'.", "refused");
            proposal.RollbackNote = body!.RollbackNote!.Trim();
            Anthill.Core.Homelab.Actions.BlastRadius.Apply(proposal); // note presence lowers the score — recompute honestly
            Homelab.UpdateActionProposal(proposal);
            return ApiJson.Ok(proposal, $"Rollback note saved (blast radius now {proposal.BlastRadiusScore} = {proposal.RiskLevel}).");
        });

        app.MapPost("/homelab/actions/{id}/dryrun", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "execute_homelab_actions"); if (auth is not null) return auth;
            var (ok, message) = await HomelabActions.DryRunAsync(id, CurrentUsername(ctx) ?? "operator", ctx.RequestAborted);
            return ok ? ApiJson.Ok(new Dictionary<string, object?> { ["dry_run"] = message }, "Dry run only — nothing was executed.")
                      : ApiJson.Error(message, "refused");
        });

        app.MapPost("/homelab/actions/{id}/execute", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "execute_homelab_actions"); if (auth is not null) return auth;
            var (ok, message) = await HomelabActions.ExecuteAsync(id, CurrentUsername(ctx) ?? "operator", ctx.RequestAborted);
            return ok ? ApiJson.Ok(Homelab.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        // ---- Kill switch (NORTH_STAR Phase 12: POST /homelab/actions/stop|resume) --------------

        app.MapPost("/homelab/actions/stop", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "approve_homelab_actions"); if (auth is not null) return auth;
            KillSwitchRequest? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<KillSwitchRequest>(); } catch { /* reason is optional */ }
            var by = CurrentUsername(ctx) ?? "operator";
            HomelabActionControl.Stop($"{by}: {(string.IsNullOrWhiteSpace(body?.Reason) ? "manual stop" : body!.Reason!.Trim())}");
            Homelab.RecordEvent(new Anthill.Core.Homelab.HomelabEvent
            {
                EventType = "homelab_stop_engaged", SubjectKind = "kill_switch", SubjectId = "HOMELAB_STOP",
                Severity = "warning", Message = $"[{by}] HOMELAB_STOP engaged — no homelab action may execute.",
            });
            return ApiJson.Ok(new Dictionary<string, object?> { ["stopped"] = true }, "HOMELAB_STOP engaged. No homelab action will execute until resumed.");
        });

        app.MapPost("/homelab/actions/resume", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "execute_homelab_actions"); if (auth is not null) return auth;
            var by = CurrentUsername(ctx) ?? "operator";
            HomelabActionControl.Resume();
            var still = HomelabActionControl.IsStopped; // file deletion can fail — report honestly
            Homelab.RecordEvent(new Anthill.Core.Homelab.HomelabEvent
            {
                EventType = still ? "homelab_resume_failed" : "homelab_resumed", SubjectKind = "kill_switch",
                SubjectId = "HOMELAB_STOP", Severity = still ? "error" : "info",
                Message = $"[{by}] " + (still ? "Resume attempted but the HOMELAB_STOP sentinel could not be cleared." : "HOMELAB_STOP cleared — approved actions may execute again."),
            });
            return still
                ? ApiJson.Error("The HOMELAB_STOP sentinel could not be cleared — remove .anthill/HOMELAB_STOP manually.", "refused")
                : ApiJson.Ok(new Dictionary<string, object?> { ["stopped"] = false }, "HOMELAB_STOP cleared.");
        });
    }
}
