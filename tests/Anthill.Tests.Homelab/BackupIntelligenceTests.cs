using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Backup;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.4.0 Phase 13 validation (NORTH_STAR: backup coverage; stale backup; blast-radius; runbook
/// generation). All time-dependent behavior uses an injected fixed clock — nothing here can flake.
/// </summary>
public class BackupIntelligenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_bi_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private HomelabRepository Repo()
    {
        Directory.CreateDirectory(_dir);
        return new HomelabRepository(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ---- Coverage classification ---------------------------------------------------------------

    [Fact]
    public void Classify_NoRecord_IsNone_FailsTowardCaution()
        => Assert.Equal("none", BackupIntelligence.Classify(null, Now));

    [Fact]
    public void Classify_RecentSuccess_IsOk()
        => Assert.Equal("ok", BackupIntelligence.Classify(
            new BackupRecord { Status = "ok", LastSuccess = Iso(Now.AddDays(-1)) }, Now));

    [Fact]
    public void Classify_OldSuccess_IsStale()
        => Assert.Equal("stale", BackupIntelligence.Classify(
            new BackupRecord { Status = "ok", LastSuccess = Iso(Now.AddDays(-(BackupIntelligence.StaleAfterDays + 1))) }, Now));

    [Fact]
    public void Classify_FailedStatus_IsFailed()
        => Assert.Equal("failed", BackupIntelligence.Classify(
            new BackupRecord { Status = "failed", LastSuccess = Iso(Now.AddDays(-1)), LastAttempt = Iso(Now) }, Now));

    [Fact]
    public void Classify_NeverSucceeded_IsNone_EvenIfStatusSaysOk()
        => Assert.Equal("none", BackupIntelligence.Classify(new BackupRecord { Status = "ok" }, Now));

    // ---- Coverage map + restore priority -------------------------------------------------------

    [Fact]
    public void CoverageMap_UncoveredCriticalGuest_RanksFirst()
    {
        var repo = Repo();
        repo.UpsertVm(new VmRecord { Id = "v1", VmId = "101", Name = "db", NodeId = "pve1" });
        repo.UpsertVm(new VmRecord { Id = "v2", VmId = "102", Name = "scratch", NodeId = "pve1" });
        var svc = new ServiceRecord { Id = "s1", Name = "postgres", NodeId = "pve1", Criticality = "critical" };
        repo.UpsertService(svc, "test");
        repo.UpsertDependency(new DependencyRecord { FromKind = "service", FromId = "s1", ToKind = "vm", ToId = "101", DependencyKind = "runs_on" }, "test");
        repo.UpsertBackup(new BackupRecord { Id = "b1", TargetKind = "vm", TargetId = "102", Status = "ok", LastSuccess = Iso(Now.AddDays(-1)) });

        var map = BackupIntelligence.CoverageMap(repo, Now);
        Assert.Equal(2, map.Count);
        var first = map[0];
        Assert.Equal("db", first.Name);                  // critical + uncovered restores first
        Assert.Equal("none", first.Coverage);
        Assert.Equal(1, first.RestorePriority);
        Assert.Equal("ok", map[1].Coverage);
        Assert.True(map[1].RestoreConfidence > first.RestoreConfidence);
    }

    // ---- Blast radius --------------------------------------------------------------------------

    [Fact]
    public void SimulateNodeLoss_CountsGuestsServicesAndUnprotectedCasualties()
    {
        var repo = Repo();
        repo.UpsertVm(new VmRecord { Id = "v1", VmId = "101", Name = "db", NodeId = "pve1" });
        repo.UpsertContainer(new ContainerRecord { Id = "c1", ContainerId = "200", Name = "web", NodeId = "pve1" });
        repo.UpsertVm(new VmRecord { Id = "v9", VmId = "900", Name = "elsewhere", NodeId = "pve2" });
        repo.UpsertService(new ServiceRecord { Id = "s1", Name = "postgres", NodeId = "pve1", Criticality = "critical" }, "test");
        repo.UpsertDependency(new DependencyRecord { FromKind = "service", FromId = "s1", ToKind = "vm", ToId = "101", DependencyKind = "runs_on" }, "test");
        repo.UpsertBackup(new BackupRecord { Id = "b1", TargetKind = "container", TargetId = "200", Status = "ok", LastSuccess = Iso(Now.AddDays(-1)) });

        var impact = BackupIntelligence.SimulateNodeLoss(repo, "pve1", Now);
        Assert.Single(impact.VmsLost);
        Assert.Single(impact.ContainersLost);
        Assert.DoesNotContain("elsewhere", impact.VmsLost);
        Assert.Contains("postgres", impact.ServicesLost);
        Assert.True(impact.CriticalServicesLost >= 1);
        Assert.Contains(impact.UnprotectedCasualties, c => c.Contains("db")); // vm 101 has no backup
        Assert.DoesNotContain(impact.UnprotectedCasualties, c => c.Contains("web")); // container is covered
    }

    // ---- Runbook generation --------------------------------------------------------------------

    [Fact]
    public void Runbook_CoveredTarget_ListsArtifactAndVerification()
    {
        var repo = Repo();
        repo.UpsertVm(new VmRecord { Id = "v1", VmId = "101", Name = "db", NodeId = "pve1" });
        repo.UpsertBackup(new BackupRecord { Id = "b1", TargetKind = "vm", TargetId = "101", Status = "ok", LastSuccess = Iso(Now.AddDays(-2)), Location = "pbs:backups/vm-101" });
        var steps = BackupIntelligence.Runbook(repo, "vm", "101", Now);
        Assert.Contains(steps, s => s.Contains("pbs:backups/vm-101"));
        Assert.Contains(steps, s => s.Contains("Verify"));
        Assert.DoesNotContain(steps, s => s.Contains("STOP"));
    }

    [Fact]
    public void Runbook_UncoveredTarget_SaysStopAndRebuild_NeverPretendsRestoreExists()
    {
        var repo = Repo();
        repo.UpsertVm(new VmRecord { Id = "v1", VmId = "101", Name = "db", NodeId = "pve1" });
        var steps = BackupIntelligence.Runbook(repo, "vm", "101", Now);
        Assert.Contains(steps, s => s.Contains("STOP"));
        Assert.Contains(steps, s => s.Contains("rebuild", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runbook_UnknownTarget_SaysUnknown()
    {
        var steps = BackupIntelligence.Runbook(Repo(), "vm", "999", Now);
        Assert.Contains(steps, s => s.Contains("UNKNOWN TARGET"));
    }

    // ---- Repository accessors ------------------------------------------------------------------

    [Fact]
    public void BackupUpsert_IsIdempotentByIdAndListable()
    {
        var repo = Repo();
        repo.UpsertBackup(new BackupRecord { Id = "b1", TargetKind = "vm", TargetId = "101", Status = "ok" });
        repo.UpsertBackup(new BackupRecord { Id = "b1", TargetKind = "vm", TargetId = "101", Status = "failed" });
        var all = repo.ListBackups();
        Assert.Single(all);
        Assert.Equal("failed", all[0].Status);
    }
}
