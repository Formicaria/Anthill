using Anthill.Core.Configuration;
using Anthill.Modules.Infrastructure.Automation;
using Anthill.Modules.Infrastructure.Notifications;
using Anthill.Modules.Infrastructure.Scheduling;

namespace Anthill.Api;

/// <summary>
/// v2.5.0 automation rule endpoints + evaluation job (NORTH_STAR Phase 14). The whole subsystem
/// is behind infrastructure_automation_enabled (default OFF), every rule additionally ships disabled, and
/// risky actions only ever become approval-gated proposals — never direct execution. Managing
/// rules needs manage_infrastructure_integrations; reading needs read_infrastructure.
/// </summary>
public static partial class ApiHost
{
    public static AutomationEngine? InfrastructureAutomation { get; private set; }

    private sealed record AutomationRuleRequest(
        string? Id, string? Name, string? TriggerKind, string? Target, int? Threshold,
        string? ActionKind, bool? Enabled, int? CooldownMinutes, int? MaxRunsPerDay);

    private static readonly string[] AutomationTriggers =
        { "service_down", "backup_failed_twice", "disk_above_percent", "repeated_health_failure", "unknown_device" };
    private static readonly string[] AutomationActions =
        { "propose_restart", "alert", "warn_event", "open_incident", "flag_risk" };

    private static void InitInfrastructureAutomation()
    {
        if (!AnthillRuntime.EnableInfrastructure || !AnthillRuntime.EnableInfrastructureAutomation) return;
        InfrastructureAutomation = new AutomationEngine(Infrastructure, InfrastructureActions, new NotificationService(Infrastructure));
        // One evaluation loop on the shared scheduler — NORTH_STAR §6 rule 2: no private timers.
        InfrastructureJobs.Register(new InfrastructureScheduledJob("automation-eval", TimeSpan.FromMinutes(2), _ =>
        {
            var fired = InfrastructureAutomation!.EvaluateAll();
            return System.Threading.Tasks.Task.FromResult(
                Anthill.Modules.Infrastructure.InfrastructureProviderResult.Success($"automation evaluated ({fired.Count} rule outcome(s))", fired.Count));
        }));
    }

    private static void MapInfrastructureAutomationEndpoints(WebApplication app)
    {
        app.MapGet("/infrastructure/automation/rules", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableInfrastructureAutomation,
                ["triggers"] = AutomationTriggers, ["actions"] = AutomationActions,
                ["rules"] = Infrastructure.ListAutomationRules(),
            });
        });

        app.MapPost("/infrastructure/automation/rules", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            AutomationRuleRequest? req = null;
            try { req = await ctx.Request.ReadFromJsonAsync<AutomationRuleRequest>(); }
            catch { return ApiJson.Error("Invalid JSON body.", "invalid_request"); }
            if (req is null || string.IsNullOrWhiteSpace(req.Name)) return ApiJson.Error("name is required.", "invalid_request");
            if (!AutomationTriggers.Contains(req.TriggerKind ?? "")) return ApiJson.Error("Unknown trigger_kind.", "invalid_request");
            if (!AutomationActions.Contains(req.ActionKind ?? "")) return ApiJson.Error("Unknown action_kind.", "invalid_request");
            var rule = new AutomationRule
            {
                Id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString() : req.Id!,
                Name = req.Name!, TriggerKind = req.TriggerKind!, Target = req.Target ?? "",
                Threshold = req.Threshold ?? 3, ActionKind = req.ActionKind!,
                Enabled = req.Enabled ?? false, // disabled unless explicitly enabled — Phase 14 rule
                CooldownMinutes = Math.Max(1, req.CooldownMinutes ?? 60),
                MaxRunsPerDay = Math.Max(1, req.MaxRunsPerDay ?? 3),
            };
            Infrastructure.UpsertAutomationRule(rule);
            return ApiJson.Ok(rule);
        });

        app.MapPost("/infrastructure/automation/rules/{id}/{op}", (HttpContext ctx, string id, string op) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            if (op is not ("enable" or "disable")) return ApiJson.Error("op must be enable or disable.", "invalid_request");
            var rule = Infrastructure.ListAutomationRules().FirstOrDefault(r => r.Id == id);
            if (rule is null) return ApiJson.Error("Unknown rule.", "not_found");
            rule.Enabled = op == "enable";
            Infrastructure.UpsertAutomationRule(rule);
            return ApiJson.Ok(rule);
        });

        app.MapGet("/infrastructure/automation/runs", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(Infrastructure.ListAutomationRuns(100));
        });

        // Manual evaluation for testing rules without waiting for the scheduler tick.
        app.MapPost("/infrastructure/automation/evaluate", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            if (InfrastructureAutomation is null) return ApiJson.Error("Automation is disabled (infrastructure_automation_enabled).", "disabled");
            return ApiJson.Ok(InfrastructureAutomation.EvaluateAll());
        });
    }
}
