using System.Text.Json.Serialization;
using Anthill.Core.Common;
using Anthill.Core.Health;

namespace Anthill.Core.Homelab;

/// <summary>One node in the operational dependency graph.</summary>
public sealed class GraphNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";           // host | service
    [JsonPropertyName("subsystem")] public string Subsystem { get; set; } = ""; // compute | health | storage | security | incident | memory
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown"; // healthy | degraded | failed | unknown
    [JsonPropertyName("open_incident")] public bool OpenIncident { get; set; }
    [JsonPropertyName("internet_exposed")] public bool InternetExposed { get; set; }
}

/// <summary>One edge: service→host (runs_on) or an explicit dependency-map entry.</summary>
public sealed class GraphEdge
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "runs_on";
    /// <summary>True when either endpoint is failed/degraded — the broken-path highlight.</summary>
    [JsonPropertyName("impacted")] public bool Impacted { get; set; }
}

/// <summary>Everything the Command Center dashboard needs, assembled in ONE call. No fabricated
/// values: unknown/unavailable data is 0/empty/"" and the UI must label it as such.</summary>
public sealed class HomelabDashboard
{
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("hosts")] public int Hosts { get; set; }
    [JsonPropertyName("services")] public int Services { get; set; }
    [JsonPropertyName("vms")] public int Vms { get; set; }
    [JsonPropertyName("containers")] public int Containers { get; set; }
    [JsonPropertyName("devices")] public int Devices { get; set; }
    [JsonPropertyName("health")] public HealthSummary Health { get; set; } = new();
    [JsonPropertyName("active_incidents")] public List<IncidentRecord> ActiveIncidents { get; set; } = new();
    [JsonPropertyName("open_risk_errors")] public int OpenRiskErrors { get; set; }
    [JsonPropertyName("open_risk_warnings")] public int OpenRiskWarnings { get; set; }
    [JsonPropertyName("top_risks")] public List<RiskRecord> TopRisks { get; set; } = new();
    [JsonPropertyName("storage_total_bytes")] public long StorageTotalBytes { get; set; }
    [JsonPropertyName("storage_used_bytes")] public long StorageUsedBytes { get; set; }
    [JsonPropertyName("backup_capable_pools")] public int BackupCapablePools { get; set; }
    [JsonPropertyName("last_health_run")] public string LastHealthRun { get; set; } = "";
    [JsonPropertyName("last_proxmox_sync")] public string LastProxmoxSync { get; set; } = "";
    [JsonPropertyName("last_risk_analysis")] public string LastRiskAnalysis { get; set; } = "";
    /// <summary>-1 = not available in this context (never fabricated).</summary>
    [JsonPropertyName("pending_approvals")] public int PendingApprovals { get; set; } = -1;
    [JsonPropertyName("failed_checks")] public List<HealthCheckResult> FailedChecks { get; set; } = new();
    [JsonPropertyName("recent_changes")] public List<ChangeRecord> RecentChanges { get; set; } = new();
    [JsonPropertyName("graph_nodes")] public List<GraphNode> GraphNodes { get; set; } = new();
    [JsonPropertyName("graph_edges")] public List<GraphEdge> GraphEdges { get; set; } = new();
    /// <summary>Deterministic "what should I do next" — derived only from real data.</summary>
    [JsonPropertyName("next_checks")] public List<string> NextChecks { get; set; } = new();
}

/// <summary>
/// V2.0.0 Command Center assembly (NORTH_STAR Phase 11). Pure read-model builder over the
/// repository — deterministic, repo-only, testable without the API host. Answers the eight
/// NORTH_STAR questions: what is broken, where does it run, what does it depend on, what changed
/// recently, what should I do next, what is not backed up, what is exposed, what is unknown.
/// </summary>
public static class CommandCenter
{
    public static HomelabDashboard Build(IHomelabRepository repository, HealthCheckRunner health, int pendingApprovals = -1)
    {
        var hosts = repository.ListNodes();
        var services = repository.ListServices();
        var deps = repository.ListDependencies();
        var devices = repository.ListNetworkDevices();
        var pools = repository.ListStoragePools();
        var incidents = repository.ListIncidents();
        var risks = repository.ListRiskRecords().Where(r => r.Status == "open").ToList();
        var schedules = repository.ListHealthSchedules();
        var summary = health.Summarize();
        var active = incidents.Where(i => i.Status is "open" or "investigating").ToList();

        // ---- Per-entity status from the latest health results ------------------------------------
        var latestByTarget = summary.FailingTargets
            .Concat(repository.RecentHealthResults(200)
                .GroupBy(r => $"{r.CheckKind}|{r.Target}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.CheckedAt).First()))
            .GroupBy(r => $"{r.CheckKind}|{r.Target}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        string ServiceStatus(ServiceRecord svc)
        {
            var mine = schedules.Where(s => s.ServiceId == svc.Id)
                .Select(s => latestByTarget.FirstOrDefault(r =>
                    r.Target.Equals(s.Target, StringComparison.OrdinalIgnoreCase) && r.CheckKind == s.CheckKind))
                .Where(r => r is not null).Select(r => r!.Status).ToList();
            if (mine.Count == 0) return "unknown";
            if (mine.Contains(HealthStatus.Failed)) return "failed";
            if (mine.Contains(HealthStatus.Degraded)) return "degraded";
            return mine.Contains(HealthStatus.Healthy) ? "healthy" : "unknown";
        }
        static string Worst(IEnumerable<string> statuses)
        {
            var list = statuses.ToList();
            if (list.Contains("failed")) return "failed";
            if (list.Contains("degraded")) return "degraded";
            if (list.Contains("healthy")) return "healthy";
            return "unknown";
        }

        // ---- Graph -----------------------------------------------------------------------------------
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var serviceStatus = services.ToDictionary(s => s.Id, ServiceStatus);
        bool HasOpenIncident(string subjectId) => active.Any(i =>
            i.SubjectId.Contains(subjectId, StringComparison.OrdinalIgnoreCase)
            || subjectId.Contains(i.SubjectId, StringComparison.OrdinalIgnoreCase));

        foreach (var svc in services)
            nodes.Add(new GraphNode
            {
                Id = svc.Id, Label = svc.Name, Kind = "service", Subsystem = "health",
                Status = serviceStatus[svc.Id], InternetExposed = svc.InternetExposed,
                OpenIncident = HasOpenIncident(svc.Id) || HasOpenIncident(svc.Name) || (svc.Url.Length > 0 && HasOpenIncident(svc.Url)),
            });
        foreach (var host in hosts)
        {
            var mine = services.Where(s => s.NodeId == host.Id).Select(s => serviceStatus[s.Id]).ToList();
            nodes.Add(new GraphNode
            {
                Id = host.Id, Label = host.Name, Kind = "host", Subsystem = "compute",
                Status = mine.Count > 0 ? Worst(mine) : "unknown",
                OpenIncident = HasOpenIncident(host.Id) || HasOpenIncident(host.Name) || (host.Address.Length > 0 && HasOpenIncident(host.Address)),
            });
        }
        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statusById = nodes.ToDictionary(n => n.Id, n => n.Status, StringComparer.OrdinalIgnoreCase);
        bool Bad(string id) => statusById.TryGetValue(id, out var s) && s is "failed" or "degraded";

        foreach (var svc in services.Where(s => s.NodeId.Length > 0 && nodeIds.Contains(s.NodeId)))
            edges.Add(new GraphEdge { From = svc.Id, To = svc.NodeId, Kind = "runs_on", Impacted = Bad(svc.Id) || Bad(svc.NodeId) });
        foreach (var dep in deps.Where(d => nodeIds.Contains(d.FromId) && nodeIds.Contains(d.ToId)))
            if (!edges.Any(e => e.From.Equals(dep.FromId, StringComparison.OrdinalIgnoreCase) && e.To.Equals(dep.ToId, StringComparison.OrdinalIgnoreCase)))
                edges.Add(new GraphEdge { From = dep.FromId, To = dep.ToId, Kind = dep.DependencyKind, Impacted = Bad(dep.FromId) || Bad(dep.ToId) });

        // ---- Next checks (deterministic recommendations — only from real data) -----------------------
        var next = new List<string>();
        foreach (var failing in summary.FailingTargets.Take(3))
            next.Add($"Investigate {failing.CheckKind} failure on {failing.Target}: {failing.Detail}");
        foreach (var incident in active.Where(i => i.Severity == "error").Take(2))
            next.Add($"Incident '{incident.Title}' is {incident.Status} — open its timeline and check the SUSPECT changes");
        foreach (var risk in risks.Where(r => r.Severity == "error").Take(2))
            next.Add($"Review risk finding: {risk.Summary}");
        if (pendingApprovals > 0)
            next.Add($"Review {pendingApprovals} pending approval(s) in the unified queue");
        if (schedules.Count == 0 && services.Count > 0)
            next.Add("No health checks are scheduled — add checks so failures are noticed");
        if (services.Count > 0 && deps.Count == 0 && services.All(s => s.NodeId.Length == 0))
            next.Add("Map services to hosts (runs_on) so the dependency graph can answer \"what depends on this?\"");

        return new HomelabDashboard
        {
            GeneratedAt = AnthillTime.NowUtc().ToIso(),
            Hosts = hosts.Count, Services = services.Count,
            Vms = repository.ListVms().Count, Containers = repository.ListContainers().Count,
            Devices = devices.Count,
            Health = summary,
            ActiveIncidents = active,
            OpenRiskErrors = risks.Count(r => r.Severity == "error"),
            OpenRiskWarnings = risks.Count(r => r.Severity == "warning"),
            TopRisks = risks.OrderBy(r => r.Severity == "error" ? 0 : r.Severity == "warning" ? 1 : 2).Take(5).ToList(),
            StorageTotalBytes = pools.Sum(p => p.TotalBytes),
            StorageUsedBytes = pools.Sum(p => p.UsedBytes),
            BackupCapablePools = pools.Count(p => p.Kind.Contains("backup", StringComparison.OrdinalIgnoreCase)),
            LastHealthRun = repository.GetJobState("health-checks")?.LastRun ?? "",
            LastProxmoxSync = repository.GetJobState("proxmox-sync")?.LastRun ?? "",
            LastRiskAnalysis = repository.GetJobState("risk-analysis")?.LastRun ?? "",
            PendingApprovals = pendingApprovals,
            FailedChecks = summary.FailingTargets.Take(10).ToList(),
            RecentChanges = repository.RecentChanges(10).ToList(),
            GraphNodes = nodes, GraphEdges = edges,
            NextChecks = next.Take(6).ToList(),
        };
    }

    /// <summary>"What depends on this?" — transitive dependents of a node, for the detail drawers.</summary>
    public static IReadOnlyList<string> Dependents(string nodeId, IEnumerable<GraphEdge> edges)
    {
        var incoming = edges.GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.From).ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var queue = new Queue<string>(new[] { nodeId });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nodeId };
        while (queue.Count > 0)
        {
            if (!incoming.TryGetValue(queue.Dequeue(), out var parents)) continue;
            foreach (var parent in parents.Where(seen.Add))
            {
                result.Add(parent);
                queue.Enqueue(parent);
            }
        }
        return result;
    }
}
