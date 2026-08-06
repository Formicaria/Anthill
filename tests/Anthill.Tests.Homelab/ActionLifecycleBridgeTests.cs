using Anthill.Modules.Homelab;
using Anthill.Modules.Homelab.Actions;
using Anthill.Modules.Homelab.Approvals;
using Anthill.SDK.Actions;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.25.0 — the Safe Action Engine executor migration.
///
/// `ActionLifecycle` shipped in v2.14.0 as "the ONE lifecycle every state-changing system shares",
/// and the homelab executor — the only production system that changes external state — never
/// consulted it. These tests pin the migration: the executor's refusals now COME FROM the canonical
/// machine, verification is the only door to completion, and a failed verify produces a recovery
/// recommendation (never a recovery execution).
/// </summary>
public class ActionLifecycleBridgeTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public ActionLifecycleBridgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static ActionExecutor.ProposeRequest Request(
        string actionType = "restart_service", string targetId = "svc-1",
        bool backup = true, bool exposed = false)
        => new(actionType, "service", targetId, "", "lifecycle test", "restart again / restarts on boot",
               "", "normal", backup, exposed);

    private static (ActionExecutor Executor, HomelabRepository Repo, MockActionRunner Mock) Harness(string dbPath)
    {
        var repo = new HomelabRepository(dbPath);
        var mock = new MockActionRunner();
        var executor = new ActionExecutor(repo, new IHomelabActionRunner[] { mock }, () => false);
        return (executor, repo, mock);
    }

    /// <summary>A runner whose execution succeeds but whose verification honestly reports failure.</summary>
    private sealed class VerifyFailsRunner : IHomelabActionRunner
    {
        public string Name => "verify-fails";
        public bool CanRun(ActionProposal p) => true;
        public System.Threading.Tasks.Task<ActionRunResult> ExecuteAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, "executed"));
        public System.Threading.Tasks.Task<ActionRunResult> DryRunAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, "would execute"));
        public System.Threading.Tasks.Task<ActionRunResult> VerifyAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(false, "target still unhealthy"));
    }

    // ---- the string states map onto the ONE lifecycle ------------------------------------------

    [Theory]
    [InlineData("pending", ActionState.WaitingForApproval)]
    [InlineData("approved", ActionState.Approved)]
    [InlineData("rejected", ActionState.Failed)]
    [InlineData("superseded", ActionState.Failed)]
    [InlineData("executed", ActionState.CompletedVerified)]
    public void LegacyStates_MapOntoTheCanonicalMachine(string legacy, ActionState expected) =>
        Assert.Equal(expected, ActionLifecycleBridge.ToCanonical(legacy));

    /// <summary>An unknown or corrupt state maps to a TERMINAL state, so nothing can transition
    /// out of it by accident — fail closed, like every other parser in this codebase.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("totally_new_state")]
    public void AnUnknownState_IsTerminal_NeverTransitionable(string? legacy)
    {
        var canonical = ActionLifecycleBridge.ToCanonical(legacy);
        Assert.True(ActionLifecycle.IsTerminal(canonical));
        Assert.False(ActionLifecycleBridge.Guard(legacy, ActionState.Executing).Ok);
        Assert.False(ActionLifecycleBridge.Guard(legacy, ActionState.Approved).Ok);
    }

    /// <summary>The property the lifecycle exists for: there is NO path to Executing that skips
    /// approval, from any state a proposal can actually be in.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("superseded")]
    [InlineData("executed")]
    public void NoState_ReachesExecuting_WithoutApproval(string legacy) =>
        Assert.False(ActionLifecycleBridge.Guard(legacy, ActionState.Executing).Ok);

    // ---- the executor's refusals are the lifecycle's -------------------------------------------

    [Fact]
    public async Task TheLifecycleColumn_RecordsTheFullTraversal()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        Assert.Equal(ActionLifecycleBridge.Persisted.WaitingForApproval,
            repo.GetActionProposal(proposal!.ApprovableId)!.LifecycleState);

        executor.Approve(proposal.ApprovableId, "approver");
        Assert.Equal(ActionLifecycleBridge.Persisted.Approved,
            repo.GetActionProposal(proposal.ApprovableId)!.LifecycleState);

        var (ok, _) = await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        Assert.True(ok);
        var final = repo.GetActionProposal(proposal.ApprovableId)!;
        Assert.Equal("executed", final.State);   // legacy state untouched — routes keep working
        Assert.Equal(ActionLifecycleBridge.Persisted.CompletedVerified, final.LifecycleState);
    }

    [Fact]
    public void ARejectedProposal_LandsOnTheCanonicalFailedState()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        executor.Reject(proposal!.ApprovableId, "approver");
        var read = repo.GetActionProposal(proposal.ApprovableId)!;
        Assert.Equal("rejected", read.State);
        Assert.Equal(ActionLifecycleBridge.Persisted.Failed, read.LifecycleState);
    }

    [Fact]
    public void ASupersededProposal_LandsOnTheCanonicalFailedState()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (older, _) = executor.Propose(Request(), "tester");
        executor.Propose(Request(), "tester");   // same dedupe key supersedes the first
        Assert.Equal(ActionLifecycleBridge.Persisted.Failed,
            repo.GetActionProposal(older!.ApprovableId)!.LifecycleState);
    }

    [Fact]
    public async Task DecidingTwice_IsRefusedByTheLifecycle_NotByAStringCompare()
    {
        var (executor, _, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        Assert.True(executor.Approve(proposal!.ApprovableId, "approver").Ok);

        var second = executor.Approve(proposal.ApprovableId, "approver");
        Assert.False(second.Ok);
        Assert.Contains("lifecycle refuses", second.Message);

        // The case the pre-migration suite caught: rejecting an ALREADY-APPROVED proposal must be
        // refused. Approved -> Failed is a legal lifecycle edge (execution failure), but a
        // decision is the WaitingForApproval exit — rejection cannot revoke an approval.
        var rejectApproved = executor.Reject(proposal.ApprovableId, "approver");
        Assert.False(rejectApproved.Ok);
        Assert.Contains("WaitingForApproval exit", rejectApproved.Message);
        Assert.False(ActionLifecycleBridge.GuardDecision("approved", approve: false).Ok);
        Assert.True(ActionLifecycleBridge.GuardDecision("pending", approve: false).Ok);

        // And an executed proposal is terminal — no re-execution, stated by the machine.
        await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        var again = await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        Assert.False(again.Ok);
        Assert.Contains("lifecycle refuses", again.Message);
    }

    // ---- verification is the only door to completion -------------------------------------------

    /// <summary>
    /// The heart of the migration. Before: execute-then-verify-failed remained "executed", with the
    /// failure buried in the result text — an unverified outcome counted as success, which is the
    /// exact defect the V3 thresholds forbid. The legacy string is preserved for compatibility;
    /// the canonical column tells the truth.
    /// </summary>
    [Fact]
    public async Task AFailedVerification_IsCanonicallyFailed_NotCompleted()
    {
        var repo = new HomelabRepository(NewDbPath());
        var executor = new ActionExecutor(repo, new IHomelabActionRunner[] { new VerifyFailsRunner() }, () => false);
        var (proposal, _) = executor.Propose(Request(), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");

        var (ok, message) = await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        // v2.26.0: the RETURN is now a failure too — "command issued" is not "desired state
        // achieved". (v2.25.0 made the lifecycle say failed but still returned Ok=true.)
        Assert.False(ok);
        Assert.Contains("FAILED", message);

        var read = repo.GetActionProposal(proposal.ApprovableId)!;
        Assert.Equal("executed", read.State);
        Assert.Equal(ActionLifecycleBridge.Persisted.Failed, read.LifecycleState);
    }

    [Fact]
    public async Task AFailedVerification_ProducesARecoveryRecommendation_OnTheAuditStream()
    {
        var repo = new HomelabRepository(NewDbPath());
        var executor = new ActionExecutor(repo, new IHomelabActionRunner[] { new VerifyFailsRunner() }, () => false);
        var (proposal, _) = executor.Propose(Request(backup: true), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");
        await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");

        var ev = Assert.Single(repo.RecentEvents(100), e => e.EventType == "recovery_recommended");
        Assert.Contains("Recovery recommendation", ev.Message);
        // BackupCovered + no deterministic rollback -> restore-from-backup, operator-gated.
        Assert.Contains(nameof(RecoveryAction.RestoreFromBackup), ev.Message);
    }

    /// <summary>A successful verification recommends nothing — recovery events only exist when
    /// there is something to recover from.</summary>
    [Fact]
    public async Task ASuccessfulVerification_RecommendsNoRecovery()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");
        await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        Assert.DoesNotContain(repo.RecentEvents(100), e => e.EventType == "recovery_recommended");
    }

    /// <summary>
    /// The recovery context is built from what the proposal establishes, never optimistically: a
    /// rollback NOTE is prose for a human, so RollbackAvailable is false and the orchestrator can
    /// never recommend an "immediate rollback" no machinery can perform. An internet-exposed
    /// target that failed verification is a potential security matter — quarantine, autonomy
    /// suspended, operator required.
    /// </summary>
    [Fact]
    public void RecoveryContext_NeverClaimsMachineryThatDoesNotExist()
    {
        var exposed = new ActionProposal { InternetExposed = true, BackupCovered = true };
        var quarantine = ActionLifecycleBridge.RecoveryForFailedVerify(exposed);
        Assert.Equal(RecoveryAction.Quarantine, quarantine.Action);
        Assert.True(quarantine.SuspendsAutonomy);

        var noBackup = new ActionProposal { InternetExposed = false, BackupCovered = false };
        Assert.Equal(RecoveryAction.Escalate, ActionLifecycleBridge.RecoveryForFailedVerify(noBackup).Action);
    }

    /// <summary>Runner failure (as opposed to verify failure) records the attempt as canonically
    /// failed while the legacy state stays 'approved' — retry remains an explicit operator step,
    /// exactly as before the migration.</summary>
    [Fact]
    public async Task ARunnerFailure_RecordsTheAttemptAsFailed_ButRetryStaysExplicit()
    {
        var repo = new HomelabRepository(NewDbPath());
        var failing = new FailingRunner();
        var executor = new ActionExecutor(repo, new IHomelabActionRunner[] { failing }, () => false);
        var (proposal, _) = executor.Propose(Request(), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");

        var (ok, _) = await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        Assert.False(ok);
        var read = repo.GetActionProposal(proposal.ApprovableId)!;
        Assert.Equal("approved", read.State);       // unchanged: retry is explicit
        Assert.Equal(ActionLifecycleBridge.Persisted.Failed, read.LifecycleState);

        // And the retry is still legal — approved -> executing again.
        failing.Succeed = true;
        Assert.True((await executor.ExecuteAsync(proposal.ApprovableId, "runner-op")).Ok);
    }

    private sealed class FailingRunner : IHomelabActionRunner
    {
        public bool Succeed;
        public string Name => "flaky";
        public bool CanRun(ActionProposal p) => true;
        public System.Threading.Tasks.Task<ActionRunResult> ExecuteAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(Succeed, Succeed ? "executed" : "runner exploded"));
        public System.Threading.Tasks.Task<ActionRunResult> DryRunAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, "would execute"));
        public System.Threading.Tasks.Task<ActionRunResult> VerifyAsync(ActionProposal p, CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new ActionRunResult(true, "verified"));
    }
}
