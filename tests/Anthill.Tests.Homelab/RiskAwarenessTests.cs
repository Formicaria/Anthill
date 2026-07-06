using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Security;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.13.0 network + security awareness (NORTH_STAR Phase 9 validation list: finding generation,
/// duplicate IP, exposure classification, allowlist/no-scanning, UI smoke via CI). The analyzer is
/// repo-only — these tests never open a socket, which is itself the no-scanning proof.
/// </summary>
public class RiskAwarenessTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly RiskAnalyzer _analyzer;

    public RiskAwarenessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_risk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "risk.db"));
        _analyzer = new RiskAnalyzer(_repo);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private IReadOnlyList<RiskRecord> Open() => _repo.ListRiskRecords().Where(r => r.Status == "open").ToList();

    // ---- Finding generation ------------------------------------------------------------------

    [Fact]
    public void EmptyInventory_ProducesNoFindings()
    {
        var (open, resolved) = _analyzer.Analyze();
        Assert.Equal(0, open);
        Assert.Equal(0, resolved);
        Assert.Empty(_repo.ListRiskRecords());
    }

    [Fact]
    public void RiskyPort_WarningInternally_ErrorWhenInternetExposed()
    {
        _repo.UpsertService(new ServiceRecord { Name = "samba", Owner = "op", Ports = { 445 } }, "t");
        _repo.UpsertService(new ServiceRecord { Name = "old-ftp", Owner = "op", Ports = { 21 }, InternetExposed = true }, "t");
        _analyzer.Analyze();

        var findings = Open().Where(r => r.FindingKind == "risky_open_port").ToList();
        Assert.Equal(2, findings.Count);
        Assert.Equal("warning", Assert.Single(findings, f => f.Summary.Contains("samba")).Severity);
        var ftp = Assert.Single(findings, f => f.Summary.Contains("old-ftp"));
        Assert.Equal("error", ftp.Severity); // exposure classification upgrades severity
        Assert.Contains("internet", ftp.Summary);
    }

    [Fact]
    public void ExposedDashboard_FlaggedAsError_InternalDashboardIsNot()
    {
        _repo.UpsertService(new ServiceRecord { Name = "grafana-dashboard", Owner = "op", Ports = { 3000 }, InternetExposed = true }, "t");
        _repo.UpsertService(new ServiceRecord { Name = "internal-dashboard", Owner = "op", Ports = { 3000 }, InternetExposed = false }, "t");
        _analyzer.Analyze();

        var exposed = Open().Where(r => r.FindingKind == "exposed_dashboard").ToList();
        var finding = Assert.Single(exposed);
        Assert.Equal("error", finding.Severity);
        Assert.Contains("grafana-dashboard", finding.Summary);
    }

    [Fact]
    public void DuplicateIp_AcrossHostsAndDevices_OneErrorFindingPerIp()
    {
        _repo.UpsertNode(new HomelabNode { Name = "nas", Address = "192.168.1.10" }, "t");
        _repo.UpsertNode(new HomelabNode { Name = "pve", Address = "192.168.1.11" }, "t");
        _repo.UpsertNetworkDevice(new NetworkDevice { Name = "printer", Ip = "192.168.1.10", Known = true }, "t");
        _analyzer.Analyze();

        var dupes = Open().Where(r => r.FindingKind == "duplicate_ip").ToList();
        var finding = Assert.Single(dupes);
        Assert.Equal("error", finding.Severity);
        Assert.Equal("192.168.1.10", finding.SubjectId);
        Assert.Contains("nas", finding.Summary);
        Assert.Contains("printer", finding.Summary);
    }

    [Fact]
    public void UnknownDevice_OwnerlessService_MissingDns_UnwatchedService_UnverifiedCredential()
    {
        _repo.UpsertNetworkDevice(new NetworkDevice { Name = "mystery", Ip = "192.168.1.66", Known = false }, "t");
        _repo.UpsertNode(new HomelabNode { Name = "nas", Address = "192.168.1.10" }, "t"); // bare IP, no dot in name
        _repo.UpsertService(new ServiceRecord { Name = "jellyfin", Owner = "", Ports = { 8096 } }, "t"); // no owner, no check
        var store = new HomelabCredentialStore(_repo);
        store.SaveCredential("prox", "proxmox_api_token", "192.168.1.5", "tok", "t"); // never verified
        _analyzer.Analyze();

        var kinds = Open().Select(r => r.FindingKind).ToHashSet();
        Assert.Contains("unknown_device", kinds);
        Assert.Contains("ownerless_service", kinds);
        Assert.Contains("missing_dns_name", kinds);
        Assert.Contains("service_without_health_check", kinds);
        Assert.Contains("credential_never_verified", kinds);
    }

    [Fact]
    public void WatchedService_NotFlaggedForMissingHealthCheck()
    {
        var svc = new ServiceRecord { Name = "jellyfin", Owner = "op", Url = "http://192.168.1.5:8096" };
        _repo.UpsertService(svc, "t");
        _repo.UpsertHealthSchedule(new HealthCheckSchedule { CheckKind = "http", Target = "http://192.168.1.5:8096", ServiceId = svc.Id }, "t");
        _analyzer.Analyze();
        Assert.DoesNotContain(Open(), r => r.FindingKind == "service_without_health_check");
    }

    [Fact]
    public void UnbackedUpHost_FlaggedOnlyWhenRunningWorkloadsWithoutBackupPool()
    {
        var host = new HomelabNode { Id = "h1", Name = "pve1", Address = "10.0.0.2" };
        _repo.UpsertNode(host, "t");
        _repo.UpsertVm(new VmRecord { Id = "vm1", Name = "vm", NodeId = "h1", Status = "running" });
        _analyzer.Analyze();
        Assert.Contains(Open(), r => r.FindingKind == "un_backed_up_host");

        // Add a backup-capable pool anywhere → next analysis auto-resolves the finding.
        _repo.UpsertStoragePool(new StoragePoolRecord { Id = "p1", Name = "pbs", NodeId = "h1", Kind = "pbs (backups)" });
        _analyzer.Analyze();
        Assert.DoesNotContain(Open(), r => r.FindingKind == "un_backed_up_host");
        Assert.Contains(_repo.ListRiskRecords(), r => r.FindingKind == "un_backed_up_host" && r.Status == "resolved");
    }

    // ---- Reconciliation ---------------------------------------------------------------------------

    [Fact]
    public void Reanalysis_IsStable_NoDuplicates_AcknowledgementsStick()
    {
        _repo.UpsertService(new ServiceRecord { Id = "s1", Name = "samba", Owner = "op", Ports = { 445 } }, "t");
        // Watch the service so the only finding in play is the risky port.
        _repo.UpsertHealthSchedule(new HealthCheckSchedule { CheckKind = "tcp", Target = "nas:445", ServiceId = "s1" }, "t");
        _analyzer.Analyze();
        var finding = Assert.Single(Open());

        _repo.SetRiskStatus(finding.Id, "acknowledged", "operator");
        _analyzer.Analyze(); // re-run must not resurrect it as open or duplicate it
        var all = _repo.ListRiskRecords();
        var again = Assert.Single(all);
        Assert.Equal("acknowledged", again.Status);

        // Fixing the underlying problem resolves even acknowledged findings.
        _repo.UpsertService(new ServiceRecord { Id = "s1", Name = "samba", Owner = "op", Ports = { 8445 } }, "t");
        _analyzer.Analyze();
        Assert.Equal("resolved", Assert.Single(_repo.ListRiskRecords()).Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task SchedulerAdapter_RunsAndRecordsAnalysisEvent()
    {
        var svc = new ServiceRecord { Name = "telnetd", Owner = "op", Ports = { 23 } };
        _repo.UpsertService(svc, "t");
        _repo.UpsertHealthSchedule(new HealthCheckSchedule { CheckKind = "tcp", Target = "x:23", ServiceId = svc.Id }, "t");
        var result = await _analyzer.RunAsync(CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(1, result.ItemCount);
        Assert.Contains(_repo.RecentEvents(10), e => e.EventType == "risk_analysis");
    }

    // ---- Devices in import/export ------------------------------------------------------------------

    [Fact]
    public void Devices_RoundTripThroughInventoryExport()
    {
        _repo.UpsertNetworkDevice(new NetworkDevice { Name = "switch", Kind = "switch", Ip = "10.0.0.3", Vlan = "10", Known = true }, "t");
        var bundle = _repo.ExportInventory();
        Assert.Single(bundle.Devices);

        using var target = new HomelabRepository(Path.Combine(_dir, "target.db"));
        target.ImportInventory(bundle, "importer");
        var device = Assert.Single(target.ListNetworkDevices());
        Assert.Equal("switch", device.Name);
        Assert.Equal("10", device.Vlan);
        target.ImportInventory(bundle, "importer"); // idempotent
        Assert.Single(target.ListNetworkDevices());
    }
}
