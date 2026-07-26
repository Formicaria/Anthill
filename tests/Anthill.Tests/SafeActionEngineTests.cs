using Anthill.Core.SafeAction;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 6 success criteria under test: one lifecycle for all state-changing systems; every action
/// has a recovery path or is explicitly non-recoverable; critical changes cannot be "low risk" by
/// line count; rollback failure suspends autonomy; multi-step recovery stops safely at checkpoints.
/// </summary>
public class SafeActionEngineTests
{
    // ---- Lifecycle -------------------------------------------------------------------------------

    [Fact]
    public void ApprovalCannotBeSkipped()
    {
        Assert.False(ActionLifecycle.CanTransition(ActionState.RiskScored, ActionState.Approved));
        Assert.False(ActionLifecycle.CanTransition(ActionState.RiskScored, ActionState.Executing));
        Assert.True(ActionLifecycle.CanTransition(ActionState.RiskScored, ActionState.WaitingForApproval));
    }

    [Fact]
    public void NothingExecutesFromDraft()
    {
        Assert.False(ActionLifecycle.CanTransition(ActionState.Draft, ActionState.Executing));
        var t = ActionLifecycle.Transition(ActionState.Draft, ActionState.Executing);
        Assert.False(t.Ok);
        Assert.Contains("illegal transition", t.Reason);
        Assert.Equal(ActionState.Draft, t.State); // state unchanged on refusal
    }

    [Fact]
    public void ExecutionAloneCannotCompleteAnAction_VerificationIsTheOnlyDoor()
    {
        Assert.False(ActionLifecycle.CanTransition(ActionState.Executing, ActionState.CompletedVerified));
        Assert.True(ActionLifecycle.CanTransition(ActionState.Executing, ActionState.Verifying));
        Assert.True(ActionLifecycle.CanTransition(ActionState.Verifying, ActionState.CompletedVerified));
    }

    [Fact]
    public void HappyPath_IsWalkable_EndToEnd()
    {
        var path = new[]
        {
            ActionState.Draft, ActionState.Validated, ActionState.RiskScored, ActionState.WaitingForApproval,
            ActionState.Approved, ActionState.Scheduled, ActionState.Executing, ActionState.Verifying,
            ActionState.CompletedVerified,
        };
        for (var i = 0; i < path.Length - 1; i++)
            Assert.True(ActionLifecycle.Transition(path[i], path[i + 1]).Ok, $"{path[i]} -> {path[i + 1]}");
    }

    [Fact]
    public void TerminalStatesAreTerminal()
    {
        foreach (var s in new[] { ActionState.CompletedVerified, ActionState.Compensated, ActionState.Escalated })
        {
            Assert.True(ActionLifecycle.IsTerminal(s));
            Assert.Contains("terminal", ActionLifecycle.Transition(s, ActionState.Executing).Reason);
        }
    }

    [Fact]
    public void RollbackFailure_OnlyLeadsToEscalation()
    {
        Assert.True(ActionLifecycle.CanTransition(ActionState.RollbackFailed, ActionState.Escalated));
        Assert.False(ActionLifecycle.CanTransition(ActionState.RollbackFailed, ActionState.Compensated));
        Assert.False(ActionLifecycle.CanTransition(ActionState.RollbackFailed, ActionState.Executing));
    }

    // ---- Risk engine -----------------------------------------------------------------------------

    [Fact]
    public void OneLineChangeToCriticalFileClass_IsNeverLowRisk()
    {
        var r = RiskEngine.Score(new RiskInputs(
            Operation: "edit", ChangedLines: 1, TargetCriticality: "low", Production: false,
            BackupAgeDays: 0, Novel: false, SkillConfidence: 1.0, StrongVerifiers: true,
            TouchedPaths: new[] { ".github/workflows/ci.yml" }));
        Assert.NotEqual("low", r.Level);
        Assert.True(r.RequiresApproval);
        Assert.Contains(r.Reasons, x => x.Contains("critical file class"));
    }

    [Fact]
    public void IrreversibleOperation_FloorsRisk_AndRequiresApproval()
    {
        var r = RiskEngine.Score(new RiskInputs(Operation: "edit", Reversible: false, TargetCriticality: "low",
            Production: false, BackupAgeDays: 0, Novel: false, SkillConfidence: 1.0));
        Assert.True(r.Level is "high" or "critical");
        Assert.True(r.RequiresApproval);
    }

    [Fact]
    public void HighRiskCategories_AlwaysRequireApproval()
    {
        foreach (var op in new[] { "delete_vm", "firewall_rule_write", "credential_rotate", "db_migration", "cluster_join" })
        {
            var r = RiskEngine.Score(new RiskInputs(Operation: op, TargetCriticality: "low", Production: false,
                BackupAgeDays: 0, Novel: false, SkillConfidence: 1.0, StrongVerifiers: true));
            Assert.True(r.RequiresApproval, $"{op} should require approval");
        }
    }

    [Fact]
    public void RoutineLabChange_WithProofAndBackups_CanBeLow()
    {
        var r = RiskEngine.Score(new RiskInputs(
            Operation: "update_docs", TargetCriticality: "low", Production: false, BackupAgeDays: 1,
            Novel: false, SkillConfidence: 1.0, StrongVerifiers: true, InMaintenanceWindow: true,
            ChangedLines: 12, TouchedPaths: new[] { "docs/HOMELAB.md" }));
        Assert.Equal("low", r.Level);
        Assert.False(r.RequiresApproval);
    }

    [Fact]
    public void UnknownCriticality_AndNoBackup_ScoreCautiously()
    {
        var r = RiskEngine.Score(new RiskInputs(Operation: "restart_service", BackupAgeDays: -1));
        Assert.Contains(r.Reasons, x => x.Contains("criticality unknown"));
        Assert.Contains(r.Reasons, x => x.Contains("no known backup"));
        Assert.True(r.Score >= 25);
    }

    [Fact]
    public void SkillConfidenceAndVerifiers_ReduceRisk_ButCannotUnfloorCriticalPaths()
    {
        var weak = RiskEngine.Score(new RiskInputs(Operation: "edit", TargetCriticality: "normal", ChangedLines: 50));
        var strong = RiskEngine.Score(new RiskInputs(Operation: "edit", TargetCriticality: "normal", ChangedLines: 50,
            SkillConfidence: 1.0, StrongVerifiers: true, Novel: false, BackupAgeDays: 0));
        Assert.True(strong.Score < weak.Score);

        var criticalPath = RiskEngine.Score(new RiskInputs(Operation: "edit", TargetCriticality: "normal",
            SkillConfidence: 1.0, StrongVerifiers: true, Novel: false, BackupAgeDays: 0,
            TouchedPaths: new[] { "deploy/lxc/setup.sh" }));
        Assert.True(criticalPath.RequiresApproval);
    }

    // ---- Recovery --------------------------------------------------------------------------------

    [Fact]
    public void RollbackFailure_SuspendsAutonomy_AndEscalates()
    {
        var d = RecoveryOrchestrator.Decide(new RecoveryContext(RollbackAvailable: true, RollbackAttemptedAndFailed: true));
        Assert.Equal(RecoveryAction.Escalate, d.Action);
        Assert.True(d.SuspendsAutonomy);
        Assert.Contains("rollback FAILED", d.Reason);
    }

    [Fact]
    public void RecoveryPreference_RollbackThenRetryThenFailoverThenRestoreThenEscalate()
    {
        Assert.Equal(RecoveryAction.ImmediateRollback,
            RecoveryOrchestrator.Decide(new RecoveryContext(RollbackAvailable: true)).Action);
        Assert.Equal(RecoveryAction.RetryAfterCooldown,
            RecoveryOrchestrator.Decide(new RecoveryContext(false, Retryable: true)).Action);
        Assert.Equal(RecoveryAction.Failover,
            RecoveryOrchestrator.Decide(new RecoveryContext(false, Retryable: true, PriorAttempts: 2, FailoverAvailable: true)).Action);
        Assert.Equal(RecoveryAction.RestoreFromBackup,
            RecoveryOrchestrator.Decide(new RecoveryContext(false, BackupAvailable: true)).Action);
        var none = RecoveryOrchestrator.Decide(new RecoveryContext(false));
        Assert.Equal(RecoveryAction.Escalate, none.Action);
        Assert.True(none.SuspendsAutonomy); // no recovery path is itself a suspension event
    }

    [Fact]
    public void SecurityImplication_Quarantines()
    {
        var d = RecoveryOrchestrator.Decide(new RecoveryContext(RollbackAvailable: true, SecurityImplication: true));
        Assert.Equal(RecoveryAction.Quarantine, d.Action);
        Assert.True(d.SuspendsAutonomy);
    }

    // ---- Circuit breaker -------------------------------------------------------------------------

    [Fact]
    public void Breaker_TripsAfterThreshold_AndStaysTrippedThroughSuccess()
    {
        var cb = new ActionCircuitBreaker(threshold: 2);
        var scope = ActionCircuitBreaker.Scope("target", "pve1/104");
        Assert.False(cb.RecordFailure(scope));
        Assert.True(cb.RecordFailure(scope));   // trip transition
        Assert.True(cb.IsTripped(scope));
        cb.RecordSuccess(scope);
        Assert.True(cb.IsTripped(scope));       // success does not silently re-arm
        cb.Reset(scope, "operator cleared");
        Assert.False(cb.IsTripped(scope));
    }

    // ---- Change-set transactions ------------------------------------------------------------------

    private static ChangeStep Step(string id, bool exec = true, bool verify = true, bool? compensate = true, List<string>? log = null)
        => new(id,
            Execute: () => { log?.Add($"exec:{id}"); return exec; },
            Verify: () => verify,
            Compensate: compensate is null ? null : () => { log?.Add($"comp:{id}"); return compensate.Value; });

    [Fact]
    public void AllStepsPass_TransactionSucceeds()
    {
        var r = ChangeSetTransaction.Run(new[] { Step("a"), Step("b"), Step("c") });
        Assert.True(r.Success);
        Assert.Equal(new[] { "a", "b", "c" }, r.Completed);
        Assert.False(r.AutonomySuspended);
    }

    [Fact]
    public void FailedStep_StopsRun_AndCompensatesInReverseOrder()
    {
        var log = new List<string>();
        var r = ChangeSetTransaction.Run(new[]
        {
            Step("a", log: log), Step("b", log: log), Step("c", exec: false, log: log), Step("d", log: log),
        });
        Assert.False(r.Success);
        Assert.DoesNotContain("exec:d", log);              // stopped, never ran the rest
        Assert.Equal(new[] { "comp:b", "comp:a" }, log.Where(x => x.StartsWith("comp:")).ToArray());
        Assert.Empty(r.CompensationFailures);
    }

    [Fact]
    public void CheckpointVerificationFailure_CompensatesTheExecutedStepToo()
    {
        var log = new List<string>();
        var r = ChangeSetTransaction.Run(new[] { Step("a", log: log), Step("b", verify: false, log: log) });
        Assert.False(r.Success);
        Assert.Contains("checkpoint 'b' verification failed", r.StopReason);
        Assert.Contains("comp:b", log); // b executed, so b must be undone
        Assert.Contains("comp:a", log);
    }

    [Fact]
    public void CompensationFailure_SuspendsAutonomy()
    {
        var r = ChangeSetTransaction.Run(new[]
        {
            Step("a", compensate: false), Step("b", exec: false),
        });
        Assert.False(r.Success);
        Assert.True(r.AutonomySuspended);
        Assert.Contains("ROLLBACK FAILURE", r.StopReason);
        Assert.Contains(r.CompensationFailures, f => f.StartsWith("a:"));
    }

    [Fact]
    public void MissingCompensation_IsRecordedAsFailure_NotIgnored()
    {
        var r = ChangeSetTransaction.Run(new[] { Step("a", compensate: null), Step("b", exec: false) });
        Assert.True(r.AutonomySuspended);
        Assert.Contains(r.CompensationFailures, f => f.Contains("no compensation defined"));
    }

    [Fact]
    public void PartialRetention_WhenExplicitlyAllowed_KeepsCompletedSteps()
    {
        var log = new List<string>();
        var r = ChangeSetTransaction.Run(new[] { Step("a", log: log), Step("b", exec: false, log: log) }, allowPartialRetention: true);
        Assert.False(r.Success);
        Assert.Contains("partial retention allowed", r.StopReason);
        Assert.DoesNotContain(log, x => x.StartsWith("comp:"));
        Assert.False(r.AutonomySuspended);
    }
}
