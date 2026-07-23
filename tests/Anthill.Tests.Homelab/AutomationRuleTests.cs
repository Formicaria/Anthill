using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Actions;
using Anthill.Core.Homelab.Automation;
using Anthill.Core.Health;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.5.0 Phase 14 validation (NORTH_STAR: rule trigger; cooldown; approval-required; loop
/// prevention; HOMELAB_STOP). Fixed injected clock — nothing can flake.
/// </summary>
public class AutomationRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_auto_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private HomelabRepository Repo()
    {
        Directory.CreateDirectory(_dir);
        return new HomelabRepository(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static AutomationRule DiskRule(int threshold = 90) => new()
    {
        Id = "r1", Name = "disk-warn", TriggerKind = "disk_above_percent",
        Threshold = threshold, ActionKind = "warn_event", Enabled = true, CooldownMinutes = 60, MaxRunsPerDay = 3,
    };

    private void FillDisk(HomelabRepository repo, int pct)
        => repo.UpsertStoragePool(new StoragePoolRecord { Id = "s1", Name = "tank", TotalBytes = 100, UsedBytes = pct });

    // ---- Rule trigger --------------------------------------------------------------------------

    [Fact]
    public void DiskRule_FiresAboveThreshold_QuietBelow()
    {
        var repo = Repo();
        var engine = new AutomationEngine(repo, now: () => _now);
        FillDisk(repo, 95);
        repo.UpsertAutomationRule(DiskRule());
        var runs = engine.EvaluateAll();
        Assert.Single(runs);
        Assert.Equal("fired", runs[0].Outcome);

        var repo2 = Repo();
        var engine2 = new AutomationEngine(repo2, now: () => _now);
        FillDisk(repo2, 50);
        repo2.UpsertAutomationRule(DiskRule());
        Assert.Empty(engine2.EvaluateAll()); // quiet rules record nothing
    }

    [Fact]
    public void DisabledByDefault_NeverEvaluated()
    {
        var repo = Repo();
        var engine = new AutomationEngine(repo, now: () => _now);
        FillDisk(repo, 99);
        var rule = DiskRule(); rule.Enabled = false; // as shipped
        repo.UpsertAutomationRule(rule);
        Assert.Empty(engine.EvaluateAll());
    }

    // ---- Cooldown + daily cap (loop prevention) ------------------------------------------------

    [Fact]
    public void Cooldown_BlocksRefire_UntilWindowPasses()
    {
        var repo = Repo();
        var engine = new AutomationEngine(repo, now: () => _now);
        FillDisk(repo, 95);
        repo.UpsertAutomationRule(DiskRule());
        Assert.Equal("fired", engine.EvaluateAll()[0].Outcome);
        Assert.Equal("skipped_cooldown", engine.EvaluateAll()[0].Outcome); // immediate re-eval blocked
        _now = _now.AddMinutes(61);
        Assert.Equal("fired", engine.EvaluateAll()[0].Outcome); // window passed
    }

    [Fact]
    public void DailyCap_StopsRunawayRule()
    {
        var repo = Repo();
        var engine = new AutomationEngine(repo, now: () => _now);
        FillDisk(repo, 95);
        var rule = DiskRule(); rule.CooldownMinutes = 1; rule.MaxRunsPerDay = 2;
        repo.UpsertAutomationRule(rule);
        Assert.Equal("fired", engine.EvaluateAll()[0].Outcome);
        _now = _now.AddMinutes(2);
        Assert.Equal("fired", engine.EvaluateAll()[0].Outcome);
        _now = _now.AddMinutes(2);
        Assert.Equal("skipped_cap", engine.EvaluateAll()[0].Outcome); // third fire within 24h refused
    }

    // ---- Approval-required: automation proposes, never executes --------------------------------

    [Fact]
    public void ProposeRestart_CreatesPendingProposal_NeverExecutes()
    {
        var repo = Repo();
        var executor = new ActionExecutor(repo, new List<IHomelabActionRunner> { new MockActionRunner() }, isStopped: () => false);
        var engine = new AutomationEngine(repo, executor, now: () => _now);
        repo.SaveHealthResult(new HealthCheckResult { Target = "svc-1", ServiceId = "svc-1", Status = "failed", Detail = "timeout" });
        repo.UpsertAutomationRule(new AutomationRule
        {
            Id = "r2", Name = "restart-once", TriggerKind = "service_down", Target = "svc-1",
            ActionKind = "propose_restart", Enabled = true, CooldownMinutes = 60, MaxRunsPerDay = 3,
        });
        var runs = engine.EvaluateAll();
        Assert.Equal("fired", runs[0].Outcome);
        Assert.Contains("PROPOSED", runs[0].ActionTaken);
        var proposal = repo.ListActionProposals(10).FirstOrDefault(p => p.RequestedBy == "automation:r2");
        Assert.NotNull(proposal);
        Assert.Equal("pending", proposal!.State); // awaiting HUMAN approval — automation cannot execute
        Assert.Equal("", proposal.ExecutedAt);
    }

    [Fact]
    public void ProposeRestart_DoesNotStackProposals_WhilePriorPending()
    {
        var repo = Repo();
        var executor = new ActionExecutor(repo, new List<IHomelabActionRunner> { new MockActionRunner() }, isStopped: () => false);
        var engine = new AutomationEngine(repo, executor, now: () => _now);
        repo.SaveHealthResult(new HealthCheckResult { Target = "svc-1", ServiceId = "svc-1", Status = "failed", Detail = "timeout" });
        var rule = new AutomationRule
        {
            Id = "r3", Name = "restart-once", TriggerKind = "service_down", Target = "svc-1",
            ActionKind = "propose_restart", Enabled = true, CooldownMinutes = 1, MaxRunsPerDay = 10,
        };
        repo.UpsertAutomationRule(rule);
        Assert.Equal("fired", engine.EvaluateAll()[0].Outcome);
        _now = _now.AddMinutes(2);
        var second = engine.EvaluateAll()[0];
        Assert.Equal("skipped_pending", second.Outcome); // loop prevention 3
        Assert.Single(repo.ListActionProposals(10), p => p.RequestedBy == "automation:r3" && p.State == "pending");
    }
}
