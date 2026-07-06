using Anthill.Core.Common;

namespace Anthill.Core.Homelab.Security;

/// <summary>
/// Deterministic risk analysis over the EXISTING homelab inventory (v1.13.0, NORTH_STAR Phase 9).
/// Awareness and reporting only: no firewall/DNS/DHCP writes, and — stronger — no network I/O of
/// any kind. Every rule reads only what the repository already knows (hosts, services, devices,
/// VMs, storage, health schedules, credential statuses) and reconciles into `risk_records` with
/// STABLE ids (finding kind + subject), so re-runs update findings in place, newly-fixed problems
/// auto-resolve, and operator acknowledgements survive re-analysis.
/// Active scanning does not exist in this phase; if it ever arrives it ships disabled-by-default
/// and routed through the Homelab Target Allowlist like every other prober.
/// </summary>
public sealed class RiskAnalyzer
{
    private readonly IHomelabRepository _repository;
    private readonly HomelabRepository? _credentialSource; // credential statuses live off-interface

    /// <summary>Ports that are risky to run at all (legacy/cleartext/unauthenticated-by-default).</summary>
    internal static readonly HashSet<int> RiskyPorts = new()
    { 21, 23, 69, 111, 135, 137, 138, 139, 161, 445, 512, 513, 514, 1433, 3306, 3389, 5432, 5900, 6379, 9200, 11211, 27017 };

    /// <summary>Admin/dashboard-ish ports whose exposure to the internet is an error, not a warning.</summary>
    internal static readonly HashSet<int> DashboardPorts = new() { 3000, 8006, 8080, 8443, 8713, 9090, 9443, 10000, 19999 };

    public RiskAnalyzer(IHomelabRepository repository)
    {
        _repository = repository;
        _credentialSource = repository as HomelabRepository;
    }

    /// <summary>Runs every rule and reconciles risk_records. Returns (open, newlyResolved).</summary>
    public (int Open, int Resolved) Analyze(string analyzedBy = "risk-analysis")
    {
        var findings = new Dictionary<string, RiskRecord>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string subjectKind, string subjectId, string severity, string summary)
        {
            var record = new RiskRecord
            {
                Id = $"risk:{kind}:{subjectId}".ToLowerInvariant(),
                FindingKind = kind, SubjectKind = subjectKind, SubjectId = subjectId,
                Severity = severity, Summary = summary, Status = "open",
            };
            findings[record.Id] = record;
        }

        var hosts = _repository.ListNodes();
        var services = _repository.ListServices();
        var devices = _repository.ListNetworkDevices();
        var vms = _repository.ListVms();
        var containers = _repository.ListContainers();
        var pools = _repository.ListStoragePools();
        var schedules = _repository.ListHealthSchedules();

        // 1. Risky open ports (worse when internet-exposed).
        foreach (var svc in services)
            foreach (var port in svc.Ports.Where(RiskyPorts.Contains))
                Add("risky_open_port", "service", $"{svc.Id}:{port}",
                    svc.InternetExposed ? "error" : "warning",
                    $"Service '{svc.Name}' exposes risky port {port}" + (svc.InternetExposed ? " to the internet" : ""));

        // 2. Unknown devices on the network (manually flagged or imported; no scanning).
        foreach (var device in devices.Where(d => !d.Known))
            Add("unknown_device", "network_device", device.Id, "warning",
                $"Unknown device '{(device.Name.Length > 0 ? device.Name : device.Mac)}' (ip={device.Ip}, vlan={device.Vlan}) — identify or remove it");

        // 3. Ownerless services.
        foreach (var svc in services.Where(s => string.IsNullOrWhiteSpace(s.Owner)))
            Add("ownerless_service", "service", svc.Id, "info",
                $"Service '{svc.Name}' has no owner — nobody gets asked before maintenance");

        // 4. Hosts running workloads with no backup evidence anywhere.
        var haveBackupPool = pools.Any(p => p.Kind.Contains("backup", StringComparison.OrdinalIgnoreCase));
        foreach (var host in hosts)
        {
            var workloads = vms.Count(v => v.NodeId == host.Id) + containers.Count(c => c.NodeId == host.Id);
            if (workloads > 0 && !haveBackupPool)
                Add("un_backed_up_host", "host", host.Id, "warning",
                    $"Host '{host.Name}' runs {workloads} workload(s) but no backup-capable storage is known anywhere");
        }

        // 5. Exposed dashboards/admin surfaces.
        foreach (var svc in services)
        {
            var looksAdmin = svc.Ports.Any(DashboardPorts.Contains)
                || svc.Name.Contains("admin", StringComparison.OrdinalIgnoreCase)
                || svc.Name.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
                || svc.Url.Contains("/admin", StringComparison.OrdinalIgnoreCase);
            if (looksAdmin && svc.InternetExposed)
                Add("exposed_dashboard", "service", svc.Id, "error",
                    $"Admin/dashboard service '{svc.Name}' is internet-exposed — put it behind a VPN or auth proxy");
        }

        // 6. Duplicate IPs across hosts and network devices.
        var byIp = hosts.Select(h => (Ip: h.Address.Trim(), Kind: "host", Name: h.Name))
            .Concat(devices.Select(d => (Ip: d.Ip.Trim(), Kind: "device", Name: d.Name)))
            .Where(x => x.Ip.Length > 0 && System.Net.IPAddress.TryParse(x.Ip, out _))
            .GroupBy(x => x.Ip)
            .Where(g => g.Count() > 1);
        foreach (var group in byIp)
            Add("duplicate_ip", "network", group.Key, "error",
                $"IP {group.Key} is claimed by {group.Count()} entries: " + string.Join(", ", group.Select(x => $"{x.Kind} '{x.Name}'")));

        // 7. Hosts addressed by bare IP with no DNS-style name.
        foreach (var host in hosts)
            if (System.Net.IPAddress.TryParse(host.Address.Trim(), out _) && !host.Name.Contains('.'))
                Add("missing_dns_name", "host", host.Id, "info",
                    $"Host '{host.Name}' ({host.Address}) has no DNS name recorded — IP-only references break when addresses change");

        // 8. Services with no health check watching them.
        foreach (var svc in services)
        {
            var watched = schedules.Any(s => s.ServiceId == svc.Id
                || (svc.Url.Length > 0 && s.Target.Contains(svc.Url, StringComparison.OrdinalIgnoreCase))
                || (s.Target.Length > 0 && svc.Url.Contains(s.Target, StringComparison.OrdinalIgnoreCase)));
            if (!watched)
                Add("service_without_health_check", "service", svc.Id, "info",
                    $"Service '{svc.Name}' has no health check — failures will go unnoticed");
        }

        // 9. Credentials configured but never verified.
        if (_credentialSource is not null)
            foreach (var cred in new HomelabCredentialStore(_credentialSource).ListStatuses())
                if (cred.Configured && string.IsNullOrWhiteSpace(cred.LastVerified))
                    Add("credential_never_verified", "credential", cred.Id, "info",
                        $"Credential '{cred.Id}' ({cred.Kind}) is configured but has never been verified");

        // Reconcile: upsert current findings (preserving acknowledged status), resolve vanished ones.
        var existing = _repository.ListRiskRecords();
        foreach (var finding in findings.Values)
        {
            var prior = existing.FirstOrDefault(r => r.Id == finding.Id);
            if (prior is not null && prior.Status == "acknowledged") finding.Status = "acknowledged";
            _repository.UpsertRiskRecord(finding);
        }
        var resolved = 0;
        foreach (var stale in existing.Where(r => r.Status != "resolved" && !findings.ContainsKey(r.Id)))
        {
            _repository.SetRiskStatus(stale.Id, "resolved", analyzedBy);
            resolved++;
        }

        var open = findings.Values.Count(f => f.Status == "open");
        _repository.RecordEvent(new HomelabEvent
        {
            EventType = "risk_analysis", SubjectKind = "risk", SubjectId = "analysis",
            Severity = findings.Values.Any(f => f.Severity == "error") ? "warning" : "info",
            Message = $"Risk analysis: {findings.Count} finding(s) ({open} open), {resolved} resolved",
        });
        return (open, resolved);
    }

    /// <summary>Scheduler adapter: deterministic, repo-only — safe on any cadence.</summary>
    public System.Threading.Tasks.Task<HomelabProviderResult> RunAsync(CancellationToken ct)
    {
        var (open, resolvedCount) = Analyze();
        return System.Threading.Tasks.Task.FromResult(
            HomelabProviderResult.Success($"risk analysis ok ({open} open, {resolvedCount} resolved)", open));
    }
}
