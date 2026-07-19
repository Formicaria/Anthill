using Anthill.Core.Configuration;
using Anthill.Core.Homelab.Automation;
using Anthill.Core.Homelab.Notifications;
using Anthill.Core.Homelab.Scheduling;

namespace Anthill.Api;

/// <summary>
/// v2.5.0 automation rule endpoints + evaluation job (NORTH_STAR Phase 14). The whole subsystem
/// is behind homelab_automation_enabled (default OFF), every rule additionally ships disabled, and
/// risky actions only ever become approval-gated proposals — never direct execution. Managing
/// rules needs manage_homelab_integrations; reading needs read_homelab.
/// </summary>
public static partial class ApiHost
{
    public static AutomationEngine? HomelabAutomation { get; private set; }

    private sealed record AutomationRuleRequest(
        string? Id, string? Name, string? TriggerKind, string? Target, int? Threshold,
        string? ActionKind, bool? Enabled, int? CooldownMinutes, int? MaxRunsPerDay);

    private static readonly string[] AutomationTriggers =
        { "service_down", "backup_failed_twice", "disk_above_percent", "repeated_health_failure", "unknown_device" };
    private static readonly string[] AutomationActions =
        { "propose_restart", "alert", "warn_event", "open_incident", "flag_risk" };

    private static void InitHomelabAutomation()
    {
        if (!AnthillRuntime.EnableHomelab || !AnthillRuntime.EnableHomelabAutomation) return;
        HomelabAutomation = new AutomationEngine(Homelab, HomelabActions, new NotificationService(Homelab));
        // One evaluation loop on the shared scheduler — NORTH_STAR §6 rule 2: no private timers.
        HomelabJobs.Register(new HomelabScheduledJob("automation-eval", TimeSpan.FromMinutes(2), _ =>
        {
            var fired = HomelabAutomation!.EvaluateAll();
            return System.Threading.Tasks.Task.FromResult(
                Anthill.Core.Homelab.HomelabProviderResult.Success($"automation evaluated ({fired.Count} rule outcome(s))", fired.Count));
        }));
    }

    private static void MapHomelabAutomationEndpoints(WebApplication app)
    {
        app.MapGet("/homelab/automation/rules", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableHomelabAutomation,
                ["triggers"] = AutomationTriggers, ["actions"] = AutomationActions,
                ["rules"] = Homelab.ListAutomationRules(),
            });
        });

        app.MapPost("/homelab/automation/rules", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
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
            Homelab.UpsertAutomationRule(rule);
            return ApiJson.Ok(rule);
        });

        app.MapPost("/homelab/automation/rules/{id}/{op}", (HttpContext ctx, string id, string op) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            if (op is not ("enable" or "disable")) return ApiJson.Error("op must be enable or disable.", "invalid_request");
            var rule = Homelab.ListAutomationRules().FirstOrDefault(r => r.Id == id);
            if (rule is null) return ApiJson.Error("Unknown rule.", "not_found");
            rule.Enabled = op == "enable";
            Homelab.UpsertAutomationRule(rule);
            return ApiJson.Ok(rule);
        });

        app.MapGet("/homelab/automation/runs", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(Homelab.ListAutomationRuns(100));
        });

        // Manual evaluation for testing rules without waiting for the scheduler tick.
        app.MapPost("/homelab/automation/evaluate", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            if (HomelabAutomation is null) return ApiJson.Error("Automation is disabled (homelab_automation_enabled).", "disabled");
            return ApiJson.Ok(HomelabAutomation.EvaluateAll());
        });
    }
}
