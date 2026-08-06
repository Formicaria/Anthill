namespace Anthill.SDK.Actions;

/// <summary>
/// NORTH_STAR Phase 6 — recovery orchestration and circuit breakers. The sharpest safety property
/// here: a ROLLBACK FAILURE automatically suspends the related autonomy scope. Recovery never
/// "tries harder" on its own — it escalates, and escalation is sticky until an operator clears it.
/// </summary>
public enum RecoveryAction
{
    ImmediateRollback, CompensatingAction, RetryAfterCooldown, Failover,
    RestoreFromBackup, Quarantine, DisableAutomation, RevokeCapability, Escalate,
}

public sealed record RecoveryDecision(RecoveryAction Action, string Reason, bool SuspendsAutonomy);

public sealed record RecoveryContext(
    bool RollbackAvailable,
    bool RollbackAttemptedAndFailed = false,
    bool Retryable = false,
    int PriorAttempts = 0,
    int MaxAttempts = 2,
    bool BackupAvailable = false,
    bool FailoverAvailable = false,
    bool SecurityImplication = false);

public static class RecoveryOrchestrator
{
    public static RecoveryDecision Decide(RecoveryContext c)
    {
        // Rollback failure is the one-way door: suspend autonomy for this scope, escalate, stop.
        if (c.RollbackAttemptedAndFailed)
            return new(RecoveryAction.Escalate, "rollback FAILED — autonomy suspended for this scope pending operator review", SuspendsAutonomy: true);

        if (c.SecurityImplication)
            return new(RecoveryAction.Quarantine, "security implication — target quarantined, automation disabled", SuspendsAutonomy: true);

        if (c.RollbackAvailable)
            return new(RecoveryAction.ImmediateRollback, "deterministic rollback available", false);

        if (c.Retryable && c.PriorAttempts < c.MaxAttempts)
            return new(RecoveryAction.RetryAfterCooldown, $"transient failure, attempt {c.PriorAttempts + 1}/{c.MaxAttempts} after cooldown", false);

        if (c.FailoverAvailable)
            return new(RecoveryAction.Failover, "no rollback, but failover target exists", false);

        if (c.BackupAvailable)
            return new(RecoveryAction.RestoreFromBackup, "no rollback or failover — restore from backup (operator-gated)", true);

        return new(RecoveryAction.Escalate, "no recovery path available — escalate to operator", true);
    }
}

/// <summary>
/// Per-scope circuit breaker: pause an action type, target, provider, skill, or automation rule
/// after repeated failures. Trips are sticky (no auto-reset by time here — an operator or an
/// explicit reset clears them), so a flapping target cannot re-arm itself between attempts.
/// </summary>
public sealed class ActionCircuitBreaker
{
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tripped = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _threshold;

    public ActionCircuitBreaker(int threshold = 3) => _threshold = Math.Max(1, threshold);

    public static string Scope(string kind, string id) => $"{kind}:{id}";

    public bool IsTripped(string scope) => _tripped.Contains(scope);

    /// <summary>Returns true when this failure TRIPPED the breaker (transition, not steady state).</summary>
    public bool RecordFailure(string scope)
    {
        _failures[scope] = _failures.GetValueOrDefault(scope) + 1;
        if (_failures[scope] >= _threshold && _tripped.Add(scope)) return true;
        return false;
    }

    public void RecordSuccess(string scope)
    {
        // Success clears the count but NOT a trip — a tripped scope stays open until reset.
        _failures[scope] = 0;
    }

    /// <summary>Immediate trip regardless of count (used for rollback failure / security events).</summary>
    public void Trip(string scope) => _tripped.Add(scope);

    public void Reset(string scope, string _operatorReason)
    {
        _tripped.Remove(scope);
        _failures[scope] = 0;
    }

    public IReadOnlyCollection<string> TrippedScopes => _tripped.ToList();
}
