using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Incidents;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.0.0 Command Center (NORTH_STAR Phase 11 validation list: dashboard endpoint, health
/// summary, dependency graph). The builder is a pure read-model over the repository — these tests
/// prove it aggregates real data faithfully and fabricates NOTHING when data is missing.
/// </summary>
public class CommandCenterTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly HealthCheckRunner _health;

    public CommandCenterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_cc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "cc.db"));
        _health = new HealthCheckRunner(_repo, new HomelabTargetGuard(_repo));
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>host pve1 ← service jellyfin (runs_on) ← service proxy (needs, via dependency map).</summary>
    private (HomelabNode Host, ServiceRecord Svc, ServiceRecord Proxy) Seed()
    {
        var host = new HomelabNode { Id = "h1", Name = "pve1", Address = "10.0.0.2" };
        _repo.UpsertNode(host, "t");
        var svc = new ServiceRecord { Id = "s1", Name = "jellyfin", NodeId = "h1", Ports = { 8096 }, Owner = "op" };
        _repo.UpsertService(svc, "t");
        var proxy = new ServiceRecord { Id = "s2", Name = "proxy", Owner = "op" };
        _repo.UpsertService(proxy, "t");
        _repo.UpsertDependency(new DependencyRecord { FromId = "s2", ToId = "s1", FromKind = "service", ToKind = "service", DependencyKind = "needs" }, "t");
        return (host, svc, proxy);
    }

    private void FailJellyfin()
    {
        _repo.UpsertHealthSchedule(new HealthCheckSchedule { CheckKind = "tcp", Target = "10.0.0.2:8096", ServiceId = "s1" }, "t");
        _repo.SaveHealthResult(new HealthCheckResult { CheckKind = "tcp", Target = "10.0.0.2:8096", Status = HealthStatus.Failed, Detail = "refused", CheckedAt = AnthillTime.NowUtc().ToIso() });
    }

    // ---- Empty state: nothing fabricated -----------------------------------------------------

    [Fact]
    public void EmptyRepo_ProducesZerosAndEmpties_NeverFabricates()
    {
        var d = CommandCenter.Build(_repo, _health);
        Assert.Equal(0, d.Hosts); Assert.Equal(0, d.Services);
        Assert.Equal(0, d.Health.Targets);
        Assert.Empty(d.ActiveIncidents); Assert.Empty(d.GraphNodes); Assert.Empty(d.GraphEdges);
        Assert.Empty(d.FailedChecks);
        Assert.Equal(0, d.StorageTotalBytes);
        Assert.Equal("", d.LastHealthRun);        // "no data yet", not a made-up stamp
        Assert.Equal("", d.LastProxmoxSync);      // "not configured", not a made-up stamp
        Assert.Equal(-1, d.PendingApprovals);     // unavailable stays -1
        Assert.NotEqual("", d.GeneratedAt);
    }

    // ---- Counts, health rollup, timestamps ----------------------------------------------------------

    [Fact]
    public void Dashboard_AggregatesCountsHealthAndJobStamps()
    {
        Seed();
        FailJellyfin();
        _repo.UpsertStoragePool(new StoragePoolRecord { Id = "p1", Name = "pbs", NodeId = "h1", Kind = "pbs (backups)", TotalBytes = 1000, UsedBytes = 400 });
        _repo.RecordJobRun("health-checks", true, "ok");
        _repo.RecordJobRun("proxmox-sync", true, "ok");

        var d = CommandCenter.Build(_repo, _health, pendingApprovals: 2);
        Assert.Equal(1, d.Hosts);
        Assert.Equal(2, d.Services);
        Assert.Equal(1, d.Health.Failed);
        Assert.Single(d.FailedChecks);
        Assert.Equal(1000, d.StorageTotalBytes);
        Assert.Equal(400, d.StorageUsedBytes);
        Assert.Equal(1, d.BackupCapablePools);
        Assert.NotEqual("", d.LastHealthRun);
        Assert.NotEqual("", d.LastProxmoxSync);
        Assert.Equal(2, d.PendingApprovals);
    }

    // ---- Dependency graph -----------------------------------------------------------------------------

    [Fact]
    public void Graph_BuildsNodesAndEdges_ImplicitRunsOnPlusDependencyMap()
    {
        Seed();
        var d = CommandCenter.Build(_repo, _health);
        Assert.Equal(3, d.GraphNodes.Count); // host + 2 services
        Assert.Equal(2, d.GraphEdges.Count); // s1→h1 (runs_on implicit) + s2→s1 (needs)
        Assert.Contains(d.GraphEdges, e => e.From == "s1" && e.To == "h1" && e.Kind == "runs_on");
        Assert.Contains(d.GraphEdges, e => e.From == "s2" && e.To == "s1" && e.Kind == "needs");
        Assert.All(d.GraphEdges, e => Assert.False(e.Impacted)); // nothing failing yet
        Assert.Equal("unknown", d.GraphNodes.First(n => n.Id == "s2").Status); // no checks = unknown, not healthy
    }

    [Fact]
    public void Graph_FailedServiceImpactsItsPaths_AndHostInheritsWorstStatus()
    {
        Seed();
        FailJellyfin();
        var d = CommandCenter.Build(_repo, _health);

        Assert.Equal("failed", d.GraphNodes.First(n => n.Id == "s1").Status);
        Assert.Equal("failed", d.GraphNodes.First(n => n.Id == "h1").Status); // worst-of-services
        Assert.True(d.GraphEdges.First(e => e.From == "s1" && e.To == "h1").Impacted);
        Assert.True(d.GraphEdges.First(e => e.From == "s2" && e.To == "s1").Impacted); // proxy's path is broken too
    }

    [Fact]
    public void Graph_ExposedServiceAndOpenIncident_AreFlaggedOnNodes()
    {
        var (_, svc, _) = Seed();
        svc.InternetExposed = true;
        _repo.UpsertService(svc, "t");
        new IncidentManager(_repo).Open("Jellyfin down", "health_check", "jellyfin", "warning", "t");

        var d = CommandCenter.Build(_repo, _health);
        var node = d.GraphNodes.First(n => n.Id == "s1");
        Assert.True(node.InternetExposed);
        Assert.True(node.OpenIncident); // incident subject 'jellyfin' matches the service name
    }

    [Fact]
    public void Dependents_AnswersWhatDependsOnThis_Transitively()
    {
        Seed();
        var d = CommandCenter.Build(_repo, _health);
        var dependents = CommandCenter.Dependents("h1", d.GraphEdges);
        Assert.Contains("s1", dependents);  // runs on the host
        Assert.Contains("s2", dependents);  // needs s1 which runs on the host — transitive
        Assert.Empty(CommandCenter.Dependents("s2", d.GraphEdges)); // nothing depends on the proxy
    }

    // ---- Next checks (deterministic, data-derived only) ------------------------------------------------

    [Fact]
    public void NextChecks_DeriveOnlyFromRealSignals()
    {
        Seed();
        var quiet = CommandCenter.Build(_repo, _health);
        // No failures/incidents/risks: the only suggestion allowed is the real gap — no health checks.
        var hint = Assert.Single(quiet.NextChecks);
        Assert.Contains("health checks", hint);

        FailJellyfin();
        _repo.UpsertRiskRecord(new RiskRecord { Id = "risk:x", FindingKind = "exposed_dashboard", Severity = "error", Summary = "Admin service exposed", Status = "open" });
        var busy = CommandCenter.Build(_repo, _health, pendingApprovals: 3);
        Assert.Contains(busy.NextChecks, n => n.Contains("Investigate tcp failure"));
        Assert.Contains(busy.NextChecks, n => n.Contains("Admin service exposed"));
        Assert.Contains(busy.NextChecks, n => n.Contains("3 pending approval"));
        Assert.True(busy.NextChecks.Count <= 6);
    }
}
