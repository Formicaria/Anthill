using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Autonomy;

/// <summary>Outcome of a budget/kill-switch check before launching an autonomous mission.</summary>
public sealed record BudgetDecision(bool Allowed, string Reason, string Code)
{
    public static BudgetDecision Allow() => new(true, "Within budget and kill switch clear.", "ok");
    public static BudgetDecision Deny(string reason, string code) => new(false, reason, code);
}

/// <summary>
/// Hard rate/safety limits for the autonomous Director (Phase 0 rail). Reads counts from the
/// <c>autonomy_runs</c> audit trail so budgets survive restarts, and consults
/// <see cref="AutonomyControl"/> for the kill switch. Stateless: the Director calls
/// <see cref="Evaluate"/> before every mission and only proceeds when <c>Allowed</c> is true.
/// </summary>
public sealed class BudgetGuard
{
    private readonly SqliteMemory _memory;

    public BudgetGuard(SqliteMemory memory) => _memory = memory;

    public BudgetDecision Evaluate()
    {
        if (!AnthillRuntime.EnableAutonomy)
            return BudgetDecision.Deny("Autonomy is disabled in config (autonomy_enabled=false).", "autonomy_disabled");

        if (AutonomyControl.IsStopped)
            return BudgetDecision.Deny("Kill switch engaged; autonomy is halted.", "kill_switch");

        var now = AnthillTime.NowUtc();

        var lastHour = _memory.CountAutonomyRunsSince(now.AddHours(-1));
        if (lastHour >= AnthillRuntime.AutonomyMaxMissionsPerHour)
            return BudgetDecision.Deny(
                $"Hourly mission budget reached ({lastHour}/{AnthillRuntime.AutonomyMaxMissionsPerHour}).", "hourly_budget");

        var lastDay = _memory.CountAutonomyRunsSince(now.AddDays(-1));
        if (lastDay >= AnthillRuntime.AutonomyMaxMissionsPerDay)
            return BudgetDecision.Deny(
                $"Daily mission budget reached ({lastDay}/{AnthillRuntime.AutonomyMaxMissionsPerDay}).", "daily_budget");

        return BudgetDecision.Allow();
    }
}
