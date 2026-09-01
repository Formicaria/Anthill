using Anthill.SDK.Contracts;

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

/// <param name="Class">v0.3.8.105 — the TYPED failure class, when the caller has one.
///
/// This type decided recovery from four booleans and knew nothing about the taxonomy the rest of
/// the colony classifies failures with. `FailureClass` has carried twenty-three members and three
/// predicates — <see cref="FailureClassify.IsRetryable"/>, <see cref="FailureClassify.IsKnown"/>,
/// <see cref="FailureClassify.MustEscalate"/> — since the structural-repair release, and the
/// component whose entire job is deciding what to do about a failure consulted none of them. So a
/// policy denial and a rate limit reached the same `Retryable` bool, and whichever the CALLER put
/// in it was the answer.
///
/// EXTENDED, NOT REPLACED. <see cref="RecoveryContext.Retryable"/> stays exactly as it was and
/// still decides on its own when no class is supplied — <see cref="FailureClass.None"/> means "this
/// caller has no typed class", and every existing caller keeps its behaviour to the letter. Where a
/// class IS supplied it can only make the decision MORE conservative: it can turn a retry into an
/// escalation and never the reverse.</param>
/// <param name="SignatureSeenBefore">v0.3.8.105 — this exact failure has already been recorded for
/// another task in the same scope. A defect that came back with nothing material changed is not
/// transient however transient its class, and retrying it is a loop wearing the word recovery.</param>
public sealed record RecoveryContext(
    bool RollbackAvailable,
    bool RollbackAttemptedAndFailed = false,
    bool Retryable = false,
    int PriorAttempts = 0,
    int MaxAttempts = 2,
    bool BackupAvailable = false,
    bool FailoverAvailable = false,
    bool SecurityImplication = false,
    FailureClass Class = FailureClass.None,
    bool SignatureSeenBefore = false);

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

        // v0.3.8.105 — THE TAXONOMY, CONSULTED. Everything below here is about whether to TRY
        // AGAIN, and these are the three answers the failure class already knows and this type
        // never asked for. Placed after rollback deliberately: undoing a denied action is not
        // routing around the denial, it is the correct response to it.

        // A POLICY, SECURITY OR AUTHORIZATION "NO" IS NEVER REPAIRED OR RETRIED. The medic has
        // refused to route around these since the structural-repair release
        // (`MedicAnt.SelectSpecialist` opens with the same check); recovery orchestration did not,
        // so the same denial reached two components and got two answers.
        if (FailureClassify.MustEscalate(c.Class))
            return new(RecoveryAction.Escalate,
                $"'{FailureClassNames.Wire(c.Class)}' is a deterministic denial — recovery escalates "
              + "to the operator and never routes around a policy or security refusal",
                SuspendsAutonomy: true);

        // THE SAME DEFECT, AGAIN. Checked before the retry branch because that is the only place it
        // can do any good: a recurrence is reproducible by definition, so the cooldown retry it
        // would otherwise take is a loop that spends the budget to arrive back here.
        if (c.SignatureSeenBefore)
            return new(RecoveryAction.Escalate,
                "this failure has already been recorded for another task in this scope with nothing "
              + "material changed — a reproducible defect is not a transient one, whatever its class",
                SuspendsAutonomy: false);

        // UNKNOWN STAYS UNKNOWN. `FailureClass.UnknownFailure` means the boundary could not
        // classify it, which is "insufficient evidence" and not "safe to try again" — the rule
        // `FailureClassify.IsKnown` exists to state and `MedicAnt` §1C already honours. `None` is
        // excluded: it means this caller supplied no class at all, which is not the same claim.
        if (c.Class != FailureClass.None && !FailureClassify.IsKnown(c.Class))
            return new(RecoveryAction.Escalate,
                "the failure is UNCLASSIFIED — escalating for evidence rather than retrying on a "
              + "guess about what went wrong",
                SuspendsAutonomy: false);

        // A TYPED CLASS NARROWS A CALLER'S OPTIMISM AND NEVER WIDENS IT. Conjunction, not
        // substitution, and the difference is the whole safety claim: a caller that passed
        // `Retryable: true` alongside a class the taxonomy calls permanent has made a claim the
        // taxonomy contradicts and loses; a caller that passed `Retryable: false` is not overruled
        // INTO a retry by a class that happens to be transient. Widen where a check comes from,
        // never what a refusal means.
        var retryable = c.Retryable && (c.Class == FailureClass.None || FailureClassify.IsRetryable(c.Class));

        if (retryable && c.PriorAttempts < c.MaxAttempts)
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
