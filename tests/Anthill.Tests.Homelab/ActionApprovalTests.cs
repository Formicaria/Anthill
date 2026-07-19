using Anthill.Core.Configuration;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Actions;
using Anthill.Core.Homelab.Approvals;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// V2.3.0 approval-gated action tests (NORTH_STAR Phase 12 "Validation": approval gate, audit,
/// permission, dry-run, mock executor, kill-switch isolation, rollback note). The kill switch is
/// injected as a Func&lt;bool&gt; so tests never touch the real .anthill/HOMELAB_STOP sentinel.
/// </summary>
public class ActionApprovalTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public ActionApprovalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_actions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static ActionExecutor.ProposeRequest Request(
        string actionType = "restart_service", string targetId = "svc-1", string rollback = "restart it again / it restarts on boot",
        string criticality = "normal", bool backup = true, bool exposed = false, string payload = "")
        => new(actionType, "service", targetId, "", "test proposal", rollback, payload, criticality, backup, exposed);

    private static (ActionExecutor Executor, HomelabRepository Repo, MockActionRunner Mock) Harness(
        string dbPath, Func<bool>? stopped = null)
    {
        var repo = new HomelabRepository(dbPath);
        var mock = new MockActionRunner();
        var executor = new ActionExecutor(repo, new IHomelabActionRunner[] { new LocalActionRunner(repo), mock }, stopped ?? (() => false));
        return (executor, repo, mock);
    }

    // ---- The approval gate (APPROVALS.md requirement 2: TOCTOU re-check) -----------------------

    [Fact]
    public async Task Execute_RefusesPendingProposal()
    {
        var (executor, _, mock) = Harness(NewDbPath());
        var (proposal, error) = executor.Propose(Request(), "tester");
        Assert.Null(error);
        var (ok, message) = await executor.ExecuteAsync(proposal!.ApprovableId, "tester");
        Assert.False(ok);
        Assert.Contains("not 'approved'", message);
        Assert.Empty(mock.Executed);
    }

    [Fact]
    public async Task Execute_RefusesRejectedProposal()
    {
        var (executor, _, mock) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        Assert.True(executor.Reject(proposal!.ApprovableId, "approver").Ok);
        var (ok, _) = await executor.ExecuteAsync(proposal.ApprovableId, "tester");
        Assert.False(ok);
        Assert.Empty(mock.Executed);
    }

    [Fact]
    public async Task Propose_Approve_Execute_HappyPath_WithAudit()
    {
        var (executor, repo, mock) = Harness(NewDbPath());
        var (proposal, error) = executor.Propose(Request(), "tester");
        Assert.Null(error);
        Assert.Equal("pending", proposal!.State);

        Assert.True(executor.Approve(proposal.ApprovableId, "approver").Ok);
        var (ok, message) = await executor.ExecuteAsync(proposal.ApprovableId, "runner-op");
        Assert.True(ok, message);
        Assert.Single(mock.Executed);

        var stored = repo.GetActionProposal(proposal.ApprovableId)!;
        Assert.Equal("executed", stored.State);
        Assert.Equal("approver", stored.DecidedBy);
        Assert.Equal("runner-op", stored.ExecutedBy);
        Assert.Contains("verify: ok", stored.ExecutionResult);

        // Every transition is on the audit stream (APPROVALS.md requirement 4).
        var events = repo.RecentEvents(20).Select(e => e.EventType).ToList();
        Assert.Contains("action_proposed", events);
        Assert.Contains("action_approved", events);
        Assert.Contains("action_executed", events);
    }

    [Fact]
    public void Decide_RefusesNonPendingProposal()
    {
        var (executor, _, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        Assert.True(executor.Approve(proposal!.ApprovableId, "approver").Ok);
        Assert.False(executor.Approve(proposal.ApprovableId, "approver").Ok); // already approved
        Assert.False(executor.Reject(proposal.ApprovableId, "approver").Ok);  // can't reject approved
    }

    // ---- Forbidden actions are structural (APPROVALS.md requirement 5) -------------------------

    [Theory]
    [InlineData("delete_vm")]
    [InlineData("delete_container")]
    [InlineData("delete_firewall_rule")]
    [InlineData("factory_reset")]
    [InlineData("wipe_disk")]
    [InlineData("modify_secret")]
    [InlineData("disable_backup")]
    public void Propose_RefusesForbiddenActions(string actionType)
    {
        var (executor, _, _) = Harness(NewDbPath());
        var (proposal, error) = executor.Propose(Request(actionType), "tester");
        Assert.Null(proposal);
        Assert.Contains("structurally forbidden", error);
    }

    [Fact]
    public void Propose_RefusesUnknownActions()
    {
        var (executor, _, _) = Harness(NewDbPath());
        var (proposal, error) = executor.Propose(Request("reboot_the_universe"), "tester");
        Assert.Null(proposal);
        Assert.Contains("allowlisted", error);
    }

    [Fact]
    public async Task Execute_RefusesForbiddenAction_EvenIfRecordWasWrittenAroundTheApi()
    {
        // A forbidden record smuggled straight into the store must STILL never run.
        var (executor, repo, mock) = Harness(NewDbPath());
        var smuggled = new ActionProposal
        {
            Title = "smuggled", ActionType = "delete_vm", TargetKind = "vm", TargetId = "vm-9",
            State = "approved", RollbackNote = "n/a", DedupeKey = "delete_vm:vm-9",
        };
        repo.SaveActionProposal(smuggled);
        var (ok, message) = await executor.ExecuteAsync(smuggled.ApprovableId, "attacker");
        Assert.False(ok);
        Assert.Contains("structurally forbidden", message);
        Assert.Empty(mock.Executed);
        Assert.Contains(repo.RecentEvents(10), e => e.EventType == "execution_refused");
    }

    // ---- Kill switch (safety rule 12) -----------------------------------------------------------

    [Fact]
    public async Task Execute_RefusesWhileKillSwitchEngaged()
    {
        var stopped = true;
        var (executor, _, mock) = Harness(NewDbPath(), () => stopped);
        var (proposal, _) = executor.Propose(Request(), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");

        var (ok, message) = await executor.ExecuteAsync(proposal.ApprovableId, "tester");
        Assert.False(ok);
        Assert.Contains("HOMELAB_STOP", message);
        Assert.Empty(mock.Executed);

        stopped = false; // resume: the same approved proposal may now run
        var (okAfter, _) = await executor.ExecuteAsync(proposal.ApprovableId, "tester");
        Assert.True(okAfter);
        Assert.Single(mock.Executed);
    }

    [Fact]
    public void KillSwitch_DoesNotAffectAutonomyStopScope()
    {
        // Scoped correctly (NORTH_STAR gate 10): the homelab sentinel name is its own file,
        // distinct from the autonomy STOP sentinel.
        Assert.NotEqual(AnthillRuntime.AutonomyStopFileName, AnthillRuntime.HomelabStopFileName);
    }

    // ---- Rollback note is mandatory before execution -------------------------------------------

    [Fact]
    public async Task Execute_RefusesWithoutRollbackNote()
    {
        var (executor, _, mock) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(rollback: ""), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");
        var (ok, message) = await executor.ExecuteAsync(proposal.ApprovableId, "tester");
        Assert.False(ok);
        Assert.Contains("rollback", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mock.Executed);
    }

    // ---- Dry run --------------------------------------------------------------------------------

    [Fact]
    public async Task DryRun_DescribesWithoutExecuting_AndNeverChangesState()
    {
        var (executor, repo, mock) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        var (ok, message) = await executor.DryRunAsync(proposal!.ApprovableId, "tester");
        Assert.True(ok);
        Assert.Contains("would", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mock.Executed);
        Assert.Equal("pending", repo.GetActionProposal(proposal.ApprovableId)!.State);
    }

    // ---- Dedupe (the one rule, kind-agnostic) ---------------------------------------------------

    [Fact]
    public void Propose_SupersedesOlderPendingDuplicate()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (first, _) = executor.Propose(Request(), "tester");
        var (second, _) = executor.Propose(Request(), "tester");
        Assert.Equal("superseded", repo.GetActionProposal(first!.ApprovableId)!.State);
        Assert.Equal("pending", repo.GetActionProposal(second!.ApprovableId)!.State);
    }

    // ---- Blast radius is deterministic and fails toward caution --------------------------------

    [Fact]
    public void BlastRadius_IsDeterministic()
    {
        var p = new ActionProposal
        {
            ActionType = "restart_vm", ServiceCriticality = "critical", DependencyFanout = 2,
            BackupCovered = false, InternetExposed = true, RollbackNote = "",
        };
        var a = BlastRadius.Score(p);
        var b = BlastRadius.Score(p);
        Assert.Equal(a.Score, b.Score);
        // 2 fanout (+4) + critical (+4) + no backup (+3) + exposed (+3) + no rollback (+4) + power (+2) = 20
        Assert.Equal(20, a.Score);
        Assert.Equal("critical", a.Level);
    }

    [Fact]
    public void BlastRadius_UnknownCriticalityScoresAsHigh()
    {
        var unknown = BlastRadius.Score(new ActionProposal { ActionType = "run_backup", ServiceCriticality = "", RollbackNote = "x", BackupCovered = true });
        var low = BlastRadius.Score(new ActionProposal { ActionType = "run_backup", ServiceCriticality = "low", RollbackNote = "x", BackupCovered = true });
        Assert.True(unknown.Score > low.Score);
    }

    [Fact]
    public void BlastRadius_LocalActionWithRollbackAndBackupIsLow()
    {
        var result = BlastRadius.Score(new ActionProposal
        {
            ActionType = "resolve_incident", ServiceCriticality = "low",
            BackupCovered = true, InternetExposed = false, RollbackNote = "reopen the incident",
        });
        Assert.Equal("low", result.Level);
    }

    // ---- Local runner really does the local work ------------------------------------------------

    [Fact]
    public async Task LocalRunner_ResolvesIncident_AndVerifies()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        repo.OpenIncident(new IncidentRecord { Id = "inc-1", Title = "disk filling", Status = "open" }, "tester");
        var (proposal, _) = executor.Propose(Request("resolve_incident", "inc-1",
            rollback: "reopen the incident", payload: "{\"root_cause\":\"log rotation was off\"}"), "tester");
        executor.Approve(proposal!.ApprovableId, "approver");
        var (ok, message) = await executor.ExecuteAsync(proposal.ApprovableId, "tester");
        Assert.True(ok, message);
        Assert.Equal("resolved", repo.GetIncident("inc-1")!.Status);
        Assert.Contains("verify: ok", repo.GetActionProposal(proposal.ApprovableId)!.ExecutionResult);
    }

    // ---- Unified queue projection ---------------------------------------------------------------

    [Fact]
    public void UnifiedProjection_CarriesActionProposalIntoTheOneQueue()
    {
        var (executor, repo, _) = Harness(NewDbPath());
        var (proposal, _) = executor.Propose(Request(), "tester");
        var view = ApprovableProjections.FromActionProposal(repo.GetActionProposal(proposal!.ApprovableId)!);
        Assert.Equal("homelab_action", view.Kind);
        Assert.Equal("action_proposal", view.RendererHint);
        Assert.Equal("pending", view.State);
        Assert.Equal(proposal.ApprovableId, view.SourceId);
        Assert.StartsWith("homelab_action:", view.ApprovableId);
    }

    // ---- Fail-closed capability gates (permission split) ---------------------------------------

    [Fact]
    public void ActionCapabilityGates_ShipDisabled()
    {
        Assert.False(AnthillRuntime.ApiPermissions.GetValueOrDefault("approve_homelab_actions", true));
        Assert.False(AnthillRuntime.ApiPermissions.GetValueOrDefault("execute_homelab_actions", true));
    }
}
