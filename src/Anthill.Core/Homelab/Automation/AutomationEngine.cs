using System.Text.Json.Serialization;
using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab.Actions;
using Anthill.Core.Homelab.Notifications;

namespace Anthill.Core.Homelab.Automation;

/// <summary>
/// v2.5.0 — NORTH_STAR Phase 14: automation rules. Low-risk self-healing and alerting ONLY.
/// Safety model, enforced here and tested:
///  - every rule ships DISABLED; nothing fires until an operator enables it,
///  - risky actions (restarts) are never executed directly — they become ActionProposals through
///    the v2.3 approval pipeline, which enforces catalog/approval/HOMELAB_STOP/rollback itself,
///  - cooldown per rule + max-runs-per-day + a pending-proposal check give triple loop prevention,
///  - every evaluation that fires lands in automation_runs and the homelab_events audit stream.
/// </summary>
public sealed class AutomationRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // service_down | backup_failed_twice | disk_above_percent | repeated_health_failure | unknown_device
    [JsonPropertyName("trigger_kind")] public string TriggerKind { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";   // service id / node/vmid / storage id ('' = any)
    [JsonPropertyName("threshold")] public int Threshold { get; set; } = 3; // percent for disk, consecutive fails for health
    // propose_restart | alert | warn_event | open_incident | flag_risk
    [JsonPropertyName("action_kind")] public string ActionKind { get; set; } = "alert";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false; // disabled by default — Phase 14 rule
    [JsonPropertyName("cooldown_minutes")] public int CooldownMinutes { get; set; } = 60;
    [JsonPropertyName("max_runs_per_day")] public int MaxRunsPerDay { get; set; } = 3;
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class AutomationRun
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("rule_id")] public string RuleId { get; set; } = "";
    [JsonPropertyName("rule_name")] public string RuleName { get; set; } = "";
    [JsonPropertyName("trigger_detail")] public string TriggerDetail { get; set; } = "";
    [JsonPropertyName("action_taken")] public string ActionTaken { get; set; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = ""; // fired | skipped_cooldown | skipped_cap | skipped_pending | error
    [JsonPropertyName("fired_at")] public string FiredAt { get; set; } = "";
}

public sealed class AutomationEngine
{
    private readonly HomelabRepository _repo;
    private readonly ActionExecutor? _actions;
    private readonly NotificationService? _notify;
    private readonly Func<DateTime> _now;

    public AutomationEngine(HomelabRepository repo, ActionExecutor? actions = null,
        NotificationService? notify = null, Func<DateTime>? now = null)
    {
        _repo = repo; _actions = actions; _notify = notify;
        _now = now ?? (() => AnthillTime.NowUtc());
    }

    /// <summary>Evaluate every ENABLED rule once. Returns run records (also persisted).</summary>
    public List<AutomationRun> EvaluateAll()
    {
        var results = new List<AutomationRun>();
        foreach (var rule in _repo.ListAutomationRules().Where(r => r.Enabled))
            results.Add(Evaluate(rule));
        return results.Where(r => r.Outcome.Length > 0).ToList();
    }

    public AutomationRun Evaluate(AutomationRule rule)
    {
        var run = new AutomationRun { RuleId = rule.Id, RuleName = rule.Name, FiredAt = _now().ToIso() };
        var (triggered, detail) = CheckTrigger(rule);
        if (!triggered) return run; // Outcome stays "" — nothing recorded for quiet rules
        run.TriggerDetail = detail;

        // Loop prevention 1: cooldown.
        var recent = _repo.ListAutomationRuns(200).Where(x => x.RuleId == rule.Id && x.Outcome == "fired").ToList();
        var last = recent.OrderByDescending(x => x.FiredAt).FirstOrDefault();
        if (last is not null && DateTime.TryParse(last.FiredAt, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var lastAt)
            && (_now() - lastAt).TotalMinutes < rule.CooldownMinutes)
        { run.Outcome = "skipped_cooldown"; Persist(rule, run); return run; }

        // Loop prevention 2: daily cap.
        var dayAgo = _now().AddDays(-1).ToIso();
        if (recent.Count(x => string.CompareOrdinal(x.FiredAt, dayAgo) >= 0) >= rule.MaxRunsPerDay)
        { run.Outcome = "skipped_cap"; Persist(rule, run); return run; }

        run.Outcome = "fired";
        run.ActionTaken = Act(rule, detail, run);
        Persist(rule, run);
        return run;
    }

    private void Persist(AutomationRun run) => _repo.RecordAutomationRun(run);
    private void Persist(AutomationRule rule, AutomationRun run)
    {
        _repo.RecordAutomationRun(run);
        _repo.RecordEvent(new HomelabEvent
        {
            EventType = "automation_" + run.Outcome, SubjectKind = "automation_rule", SubjectId = rule.Id,
            Severity = run.Outcome == "fired" ? "warning" : "info",
            Message = $"Rule '{rule.Name}' [{rule.TriggerKind}] {run.Outcome}: {run.TriggerDetail} {run.ActionTaken}".Trim(),
        });
    }

    private (bool, string) CheckTrigger(AutomationRule rule)
    {
        var now = _now();
        switch (rule.TriggerKind)
        {
            case "service_down":
            {
                var results = rule.Target.Length > 0
                    ? _repo.RecentHealthResultsForTarget(rule.Target, 1)
                    : _repo.RecentHealthResults(50);
                var down = results.FirstOrDefault(h => h.Status == "failed");
                return down is null ? (false, "") : (true, $"health check failed for {(down.ServiceId.Length > 0 ? down.ServiceId : down.Target)} ({down.Detail})");
            }
            case "repeated_health_failure":
            {
                if (rule.Target.Length == 0) return (false, "repeated_health_failure requires a target");
                var seq = _repo.RecentHealthResultsForTarget(rule.Target, Math.Max(1, rule.Threshold));
                return seq.Count >= rule.Threshold && seq.All(h => h.Status == "failed")
                    ? (true, $"{rule.Threshold} consecutive failures for {rule.Target}") : (false, "");
            }
            case "backup_failed_twice":
            {
                var failing = _repo.ListBackups()
                    .Where(b => (rule.Target.Length == 0 || b.TargetId == rule.Target)
                        && b.Status == "failed"
                        && b.LastAttempt.Length > 0 && b.LastSuccess.Length > 0
                        && string.CompareOrdinal(b.LastAttempt, b.LastSuccess) > 0)
                    .ToList();
                return failing.Count > 0
                    ? (true, "failing backups: " + string.Join(", ", failing.Select(b => $"{b.TargetKind}/{b.TargetId}"))) : (false, "");
            }
            case "disk_above_percent":
            {
                var hot = _repo.ListStoragePools()
                    .Where(s => s.TotalBytes > 0 && (double)s.UsedBytes / s.TotalBytes * 100 >= rule.Threshold)
                    .ToList();
                return hot.Count > 0
                    ? (true, "storage above " + rule.Threshold + "%: " + string.Join(", ", hot.Select(s => $"{s.Name} ({(double)s.UsedBytes / s.TotalBytes * 100:F0}%)"))) : (false, "");
            }
            case "unknown_device":
            {
                var unknown = _repo.ListNetworkDevices().Where(d => !d.Known).ToList();
                return unknown.Count > 0
                    ? (true, unknown.Count + " unknown device(s) on the network") : (false, "");
            }
            default:
                return (false, "");
        }
    }

    private string Act(AutomationRule rule, string detail, AutomationRun run)
    {
        switch (rule.ActionKind)
        {
            case "propose_restart":
            {
                if (_actions is null) return "no action executor available — nothing proposed";
                // Loop prevention 3: never stack proposals for the same target.
                if (_repo.ListActionProposals(100).Any(p => p.State == "pending" && p.TargetId == rule.Target && p.RequestedBy == "automation:" + rule.Id))
                { run.Outcome = "skipped_pending"; return "prior automation proposal still pending"; }
                var kind = rule.Target.Contains('/') ? "vm" : "service";
                var actionType = kind == "vm" ? "restart_vm" : "restart_service";
                var (proposal, err) = _actions.Propose(new ActionExecutor.ProposeRequest(
                    actionType, kind, rule.Target,
                    $"Automation: restart {rule.Target}", $"Rule '{rule.Name}' fired: {detail}",
                    "Automation restart-once; if it fails, do not retry — investigate the underlying failure.",
                    "", "unknown", false, false), "automation:" + rule.Id);
                return err is not null ? $"proposal refused: {err}"
                    : $"restart PROPOSED (awaiting human approval): {proposal!.ApprovableId}";
            }
            case "open_incident":
                _repo.OpenIncident(new IncidentRecord
                {
                    Title = $"Automation: {rule.Name}", Severity = "warning",
                    SubjectKind = "automation_rule", SubjectId = rule.Id, RootCause = detail,
                }, "automation:" + rule.Id);
                return "incident opened";
            case "flag_risk":
                return "risk flagged via event stream"; // the audit event from Persist IS the flag
            case "alert":
            {
                if (_notify is not null && NotificationService.Enabled)
                {
                    try
                    {
                        _notify.SendAsync(new AlertRecord { Kind = "automation", Severity = "warning", Message = $"{rule.Name}: {detail}", Target = rule.Target })
                            .GetAwaiter().GetResult();
                        return "alert sent to configured webhooks";
                    }
                    catch (Exception e) { return "alert send failed: " + e.Message; }
                }
                return "alert recorded (no webhooks configured)";
            }
            default: // warn_event
                return "warning recorded";
        }
    }
}
