using Anthill.Core.Configuration;
using Anthill.Modules.Infrastructure.Actions;

namespace Anthill.Api;

/// <summary>
/// V2.3.0 approval-gated infrastructure action endpoints (NORTH_STAR Phase 12, bound by docs/APPROVALS.md).
/// Permission split is deliberate and tested: proposing requires manage_infrastructure_integrations,
/// deciding requires approve_infrastructure_actions, executing (and dry-running) requires
/// execute_infrastructure_actions — and BOTH action capability gates ship disabled (fail closed), so a
/// fresh install cannot execute anything until an operator turns the gates on. The kill switch:
/// engaging INFRASTRUCTURE_STOP needs only approve_infrastructure_actions (halting must be easy); resuming
/// needs execute_infrastructure_actions (un-halting is an execution-grade decision).
/// </summary>
public static partial class ApiHost
{
    public static ActionExecutor InfrastructureActions { get; private set; } = null!;

    private sealed record ActionProposeRequest(
        string? ActionType, string? TargetKind, string? TargetId, string? Title, string? Summary,
        string? RollbackNote, string? Payload, string? ServiceCriticality, bool? BackupCovered, bool? InternetExposed);
    private sealed record ActionRollbackNoteRequest(string? RollbackNote);
    private sealed record KillSwitchRequest(string? Reason);

    private static void InitInfrastructureActions()
    {
        var runners = new List<IInfrastructureActionRunner> { new LocalActionRunner(Infrastructure) };
        // v2.3.1: the first real infrastructure runner. DOUBLE-gated — the Proxmox integration must
        // be enabled AND the operator must explicitly opt in to write actions (default off), so a
        // read-only Proxmox connection can never silently gain power/snapshot/backup capability.
        // Token comes from the credential store per client; the target allowlist is enforced inside
        // the client before any request (v2.3.1.1), exactly like the read-only sync client.
        if (AnthillRuntime.EnableInfrastructure && AnthillRuntime.EnableInfrastructureProxmox
            && AnthillRuntime.InfrastructureProxmoxWriteActionsEnabled
            && !string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureProxmoxHost))
            runners.Add(new ProxmoxActionRunner(() => new ProxmoxActionClient(
                AnthillRuntime.InfrastructureProxmoxHost, AnthillRuntime.InfrastructureProxmoxPort, InfrastructureTargets,
                // CS8603 fix: GetSecret is nullable — fail fast with a clear operator message rather
                // than sending an empty Authorization header to Proxmox.
                () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureProxmoxCredentialId, usedBy: "ProxmoxActionRunner")
                    ?? throw new InvalidOperationException($"Proxmox credential '{AnthillRuntime.InfrastructureProxmoxCredentialId}' is not configured — save it under Infrastructure → + Add / Manage → Virtualization Connections."),
                AnthillRuntime.InfrastructureProxmoxInsecureTls,
                protocol: AnthillRuntime.InfrastructureProxmoxProtocol)));
        // v0.3.8.40: Docker container lifecycle, on a server/container deployment only.
        //
        // Registered UNCONDITIONALLY with respect to configuration, and gated inside the runner
        // instead. That is deliberate: a runner absent from the list makes DryRunAvailable false and
        // the operator sees an action that simply cannot be dry-run, with no reason given. Present
        // and refusing means they get the REASON — wrong deployment mode, or execution disabled —
        // which is the difference between a missing feature and an explained one.
        //
        // Before the mock runner, which claims every catalogued action and would otherwise shadow
        // this one; the ordering lesson v2.3.1.1 records about Proxmox applies identically here.
        // Gates are read through delegates, not captured: both are live settings, and a value
        // sampled at boot would keep answering with whatever was true then — an operator who turned
        // execution off would find it still on.
        runners.Add(new DockerActionRunner(
            isServerDeployment: () => AnthillRuntime.Deployment == DeploymentMode.Server,
            deploymentDescription: () =>
                $"{AnthillRuntime.Deployment.ToString().ToLowerInvariant()} ({AnthillRuntime.DeploymentReason})",
            executeEnabled: () => AnthillRuntime.DockerExecuteEnabled));
        // v2.3.1.1: the mock runner is registered LAST. It claims every catalog action, so with the
        // dev mock gate on it previously shadowed the real Proxmox runner (first CanRun match wins)
        // and reported real actions as executed without touching anything.
        if (AnthillRuntime.EnableInfrastructureMockProviders) runners.Add(new MockActionRunner());
        InfrastructureActions = new ActionExecutor(Infrastructure, runners);

        // v0.3.8.102 — THE SPINE'S DOOR, registered where the executor is built: the system-action
        // tools reach THIS executor, with THESE runners and gates, through the same adoption path
        // every module tool uses. The decision bridge is the composition's job (the module
        // references only the SDK): first the ambient scope for conversational flows, then the
        // SAVED escalation record for mission flows — a mission runs OUTSIDE the conversation's
        // ambient scope, and the operator's answers were recorded as decisions at mission start
        // (the v0.3.8.46 rule, consumed for the first time here). Either way the identity stamped
        // as the approver is the lane's decision, never the proposing ant's.
        Queen.AdoptModuleTools(Anthill.Modules.Infrastructure.Actions.SystemActionTools.For(InfrastructureActions,
            missionId =>
            {
                var live = Anthill.Core.Conversations.ConversationScope.Evaluate(
                    Anthill.SDK.Contracts.SystemActionToolNames.Execute);
                var decision = live ?? Anthill.Core.Conversations.OperatorDecisions.ForMission(
                    Queen.Memory, missionId, Anthill.SDK.Contracts.SystemActionToolNames.Execute);
                if (decision is null) return null;
                return (decision.Allowed, decision.Id, decision.Reason ?? "");
            }));
    }

    private static void MapInfrastructureActionEndpoints(WebApplication app)
    {
        app.MapGet("/infrastructure/actions", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = Infrastructure.ListActionProposals(100),
                ["stopped"] = InfrastructureActionControl.IsStopped,
                ["allowed_actions"] = ActionCatalog.Allowed.OrderBy(a => a).ToList(),
                ["design"] = "docs/APPROVALS.md",
            });
        });

        app.MapPost("/infrastructure/actions/propose", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            ActionProposeRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ActionProposeRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null) return ApiJson.Error("Invalid request body.", "bad_request");
            var (proposal, error) = InfrastructureActions.Propose(new ActionExecutor.ProposeRequest(
                body.ActionType ?? "", body.TargetKind ?? "", body.TargetId ?? "", body.Title ?? "",
                body.Summary ?? "", body.RollbackNote ?? "", body.Payload ?? "",
                body.ServiceCriticality ?? "", body.BackupCovered ?? false, body.InternetExposed ?? false),
                CurrentUsername(ctx) ?? "operator");
            return error is not null
                ? ApiJson.Error(error, "refused")
                : ApiJson.Ok(proposal, $"Action proposed (blast radius {proposal!.BlastRadiusScore} = {proposal.RiskLevel}). It cannot run until approved" +
                    (string.IsNullOrWhiteSpace(proposal.RollbackNote) ? " and a rollback note is added." : "."));
        });

        app.MapPost("/infrastructure/actions/{id}/approve", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_infrastructure_actions"); if (auth is not null) return auth;
            var (ok, message) = InfrastructureActions.Approve(id, CurrentUsername(ctx) ?? "operator");
            return ok ? ApiJson.Ok(Infrastructure.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        app.MapPost("/infrastructure/actions/{id}/reject", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_infrastructure_actions"); if (auth is not null) return auth;
            var (ok, message) = InfrastructureActions.Reject(id, CurrentUsername(ctx) ?? "operator");
            return ok ? ApiJson.Ok(Infrastructure.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        // Rollback notes are mandatory before execution; this lets an approver add/refine one
        // on a pending or approved proposal without re-proposing.
        app.MapPost("/infrastructure/actions/{id}/rollback-note", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve_infrastructure_actions"); if (auth is not null) return auth;
            ActionRollbackNoteRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ActionRollbackNoteRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.RollbackNote)) return ApiJson.Error("A rollback note is required.", "bad_request");
            var proposal = Infrastructure.GetActionProposal(id);
            if (proposal is null) return ApiJson.Error("Unknown action proposal.", "bad_request");
            if (proposal.State is not ("pending" or "approved"))
                return ApiJson.Error($"Rollback note can only be set while pending/approved — this proposal is '{proposal.State}'.", "refused");
            proposal.RollbackNote = body!.RollbackNote!.Trim();
            Anthill.Modules.Infrastructure.Actions.BlastRadius.Apply(proposal); // note presence lowers the score — recompute honestly
            Infrastructure.UpdateActionProposal(proposal);
            return ApiJson.Ok(proposal, $"Rollback note saved (blast radius now {proposal.BlastRadiusScore} = {proposal.RiskLevel}).");
        });

        app.MapPost("/infrastructure/actions/{id}/dryrun", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "execute_infrastructure_actions"); if (auth is not null) return auth;
            var (ok, message) = await InfrastructureActions.DryRunAsync(id, CurrentUsername(ctx) ?? "operator", ctx.RequestAborted);
            return ok ? ApiJson.Ok(new Dictionary<string, object?> { ["dry_run"] = message }, "Dry run only — nothing was executed.")
                      : ApiJson.Error(message, "refused");
        });

        app.MapPost("/infrastructure/actions/{id}/execute", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "execute_infrastructure_actions"); if (auth is not null) return auth;
            var (ok, message) = await InfrastructureActions.ExecuteAsync(id, CurrentUsername(ctx) ?? "operator", ctx.RequestAborted);
            return ok ? ApiJson.Ok(Infrastructure.GetActionProposal(id), message) : ApiJson.Error(message, "refused");
        });

        // ---- Kill switch (NORTH_STAR Phase 12: POST /infrastructure/actions/stop|resume) --------------

        app.MapPost("/infrastructure/actions/stop", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "approve_infrastructure_actions"); if (auth is not null) return auth;
            KillSwitchRequest? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<KillSwitchRequest>(); } catch { /* reason is optional */ }
            var by = CurrentUsername(ctx) ?? "operator";
            InfrastructureActionControl.Stop($"{by}: {(string.IsNullOrWhiteSpace(body?.Reason) ? "manual stop" : body!.Reason!.Trim())}");
            Infrastructure.RecordEvent(new Anthill.Modules.Infrastructure.InfrastructureEvent
            {
                EventType = "infrastructure_stop_engaged", SubjectKind = "kill_switch", SubjectId = "INFRASTRUCTURE_STOP",
                Severity = "warning", Message = $"[{by}] INFRASTRUCTURE_STOP engaged — no infrastructure action may execute.",
            });
            return ApiJson.Ok(new Dictionary<string, object?> { ["stopped"] = true }, "INFRASTRUCTURE_STOP engaged. No infrastructure action will execute until resumed.");
        });

        app.MapPost("/infrastructure/actions/resume", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "execute_infrastructure_actions"); if (auth is not null) return auth;
            var by = CurrentUsername(ctx) ?? "operator";
            InfrastructureActionControl.Resume();
            var still = InfrastructureActionControl.IsStopped; // file deletion can fail — report honestly
            Infrastructure.RecordEvent(new Anthill.Modules.Infrastructure.InfrastructureEvent
            {
                EventType = still ? "infrastructure_resume_failed" : "infrastructure_resumed", SubjectKind = "kill_switch",
                SubjectId = "INFRASTRUCTURE_STOP", Severity = still ? "error" : "info",
                Message = $"[{by}] " + (still ? "Resume attempted but the INFRASTRUCTURE_STOP sentinel could not be cleared." : "INFRASTRUCTURE_STOP cleared — approved actions may execute again."),
            });
            return still
                ? ApiJson.Error("The INFRASTRUCTURE_STOP sentinel could not be cleared — remove .anthill/INFRASTRUCTURE_STOP manually.", "refused")
                : ApiJson.Ok(new Dictionary<string, object?> { ["stopped"] = false }, "INFRASTRUCTURE_STOP cleared.");
        });
    }
}
