using System.Text.Json.Serialization;
using Anthill.Core.Common;

namespace Anthill.Core.Homelab.Backup;

/// <summary>
/// v2.4.0 — NORTH_STAR Phase 13: backup + restore intelligence. Know what is protected, what is
/// not, and what recovery looks like. Everything here is DETERMINISTIC arithmetic over the
/// repository's real inventory/backup/dependency data — no LLM, no network, no invented values.
/// Unknown always fails toward caution (uncovered / low confidence / high priority).
/// </summary>
public sealed class CoverageEntry
{
    [JsonPropertyName("target_kind")] public string TargetKind { get; set; } = "";
    [JsonPropertyName("target_id")] public string TargetId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("coverage")] public string Coverage { get; set; } = "none"; // ok | stale | failed | none
    [JsonPropertyName("last_success")] public string LastSuccess { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("restore_confidence")] public int RestoreConfidence { get; set; } // 0-100
    [JsonPropertyName("restore_priority")] public int RestorePriority { get; set; } // 1 = restore first
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

public sealed class NodeLossImpact
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("vms_lost")] public List<string> VmsLost { get; set; } = new();
    [JsonPropertyName("containers_lost")] public List<string> ContainersLost { get; set; } = new();
    [JsonPropertyName("services_lost")] public List<string> ServicesLost { get; set; } = new();
    [JsonPropertyName("critical_services_lost")] public int CriticalServicesLost { get; set; }
    [JsonPropertyName("unprotected_casualties")] public List<string> UnprotectedCasualties { get; set; } = new();
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
}

public static class BackupIntelligence
{
    /// <summary>A backup older than this is stale (Phase 13 validation: stale-backup warnings).</summary>
    public const int StaleAfterDays = 7;

    private static DateTime? Parse(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Classify one target's coverage from its (possibly missing) backup record.</summary>
    public static string Classify(BackupRecord? b, DateTime nowUtc)
    {
        if (b is null) return "none";
        var success = Parse(b.LastSuccess);
        var attempt = Parse(b.LastAttempt);
        if (string.Equals(b.Status, "failed", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (attempt is not null && success is not null && attempt > success
            && !string.Equals(b.Status, "ok", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (success is null) return "none"; // never succeeded → not covered, whatever status says
        return (nowUtc - success.Value).TotalDays > StaleAfterDays ? "stale" : "ok";
    }

    /// <summary>0–100. Recency + verified-ok status + a real artifact (size, location) = confidence.</summary>
    public static int Confidence(BackupRecord? b, DateTime nowUtc)
    {
        if (b is null) return 0;
        var score = 0;
        var success = Parse(b.LastSuccess);
        if (success is not null)
        {
            var age = (nowUtc - success.Value).TotalDays;
            score += age <= 1 ? 50 : age <= StaleAfterDays ? 40 : age <= 30 ? 20 : 5;
        }
        if (string.Equals(b.Status, "ok", StringComparison.OrdinalIgnoreCase)) score += 25;
        if (b.SizeBytes > 0) score += 15;
        if (!string.IsNullOrWhiteSpace(b.Location)) score += 10;
        return Math.Min(100, score);
    }

    /// <summary>Full coverage map for every VM and container in inventory (Phase 13 core view).</summary>
    public static List<CoverageEntry> CoverageMap(HomelabRepository repo, DateTime nowUtc)
    {
        var backups = repo.ListBackups();
        var services = repo.ListServices();
        var deps = repo.ListDependencies();
        var entries = new List<CoverageEntry>();

        // Criticality reaches a guest through runs_on dependencies: service --runs_on--> vm/container.
        int CritScore(string kind, string id)
        {
            var crits = deps.Where(d => d.DependencyKind == "runs_on" && d.ToKind == kind && d.ToId == id)
                .Select(d => services.FirstOrDefault(s => s.Id == d.FromId)?.Criticality ?? "")
                .ToList();
            if (crits.Any(c => c == "critical")) return 3;
            if (crits.Any(c => c == "high")) return 2;
            if (crits.Count > 0) return 1;
            return 0;
        }

        void Add(string kind, string id, string name, string node)
        {
            var b = backups.Where(x => x.TargetKind == kind && x.TargetId == id)
                .OrderByDescending(x => x.LastSuccess).FirstOrDefault();
            var cov = Classify(b, nowUtc);
            entries.Add(new CoverageEntry
            {
                TargetKind = kind, TargetId = id, Name = name, NodeId = node,
                Coverage = cov, LastSuccess = b?.LastSuccess ?? "", Location = b?.Location ?? "",
                RestoreConfidence = Confidence(b, nowUtc),
                RestorePriority = CritScore(kind, id),
                Detail = cov switch
                {
                    "none" => b is null ? "No backup record exists for this target." : "No successful backup has ever completed.",
                    "stale" => $"Last success {b!.LastSuccess} is older than {StaleAfterDays} days.",
                    "failed" => "Most recent backup attempt failed.",
                    _ => "Backed up within the stale window.",
                },
            });
        }

        foreach (var vm in repo.ListVms()) Add("vm", vm.VmId.Length > 0 ? vm.VmId : vm.Id, vm.Name, vm.NodeId);
        foreach (var c in repo.ListContainers()) Add("container", c.ContainerId.Length > 0 ? c.ContainerId : c.Id, c.Name, c.NodeId);

        // restore_priority: rank 1..N — higher criticality first, then lower confidence first
        // (the most important, least recoverable thing is what you fix first).
        var ranked = entries.OrderByDescending(e => e.RestorePriority)
            .ThenBy(e => e.RestoreConfidence).ThenBy(e => e.Name).ToList();
        for (var i = 0; i < ranked.Count; i++) ranked[i].RestorePriority = i + 1;
        return ranked;
    }

    /// <summary>"What dies if this node fails?" (Phase 13 blast-radius simulation, read-only).</summary>
    public static NodeLossImpact SimulateNodeLoss(HomelabRepository repo, string nodeId, DateTime nowUtc)
    {
        var impact = new NodeLossImpact { NodeId = nodeId };
        var coverage = CoverageMap(repo, nowUtc).Where(e => e.NodeId == nodeId).ToList();
        var services = repo.ListServices();
        var deps = repo.ListDependencies();

        foreach (var e in coverage)
        {
            (e.TargetKind == "vm" ? impact.VmsLost : impact.ContainersLost).Add(e.Name.Length > 0 ? e.Name : e.TargetId);
            if (e.Coverage is "none" or "failed")
                impact.UnprotectedCasualties.Add($"{e.TargetKind} {e.Name} ({e.Coverage})");
            foreach (var d in deps.Where(d => d.DependencyKind == "runs_on" && d.ToKind == e.TargetKind && d.ToId == e.TargetId))
            {
                var svc = services.FirstOrDefault(s => s.Id == d.FromId);
                if (svc is null) continue;
                impact.ServicesLost.Add(svc.Name);
                if (svc.Criticality is "critical" or "high") impact.CriticalServicesLost++;
            }
        }
        // Services hosted directly on the node also die with it.
        foreach (var svc in services.Where(s => s.NodeId == nodeId && !impact.ServicesLost.Contains(s.Name)))
        {
            impact.ServicesLost.Add(svc.Name);
            if (svc.Criticality is "critical" or "high") impact.CriticalServicesLost++;
        }
        impact.Summary = $"Losing node {nodeId}: {impact.VmsLost.Count} VM(s), {impact.ContainersLost.Count} container(s), "
            + $"{impact.ServicesLost.Count} service(s) ({impact.CriticalServicesLost} critical/high) go down; "
            + $"{impact.UnprotectedCasualties.Count} casualty(ies) have NO restorable backup.";
        return impact;
    }

    /// <summary>Deterministic restore runbook for one target (Phase 13: runbook generation).</summary>
    public static List<string> Runbook(HomelabRepository repo, string targetKind, string targetId, DateTime nowUtc)
    {
        var entry = CoverageMap(repo, nowUtc)
            .FirstOrDefault(e => e.TargetKind == targetKind && e.TargetId == targetId);
        var steps = new List<string>();
        if (entry is null)
        {
            steps.Add($"UNKNOWN TARGET: no {targetKind} with id '{targetId}' in inventory — verify the id before doing anything.");
            return steps;
        }
        steps.Add($"1. Confirm the outage: check {entry.Name} ({targetKind} {targetId}) on node {entry.NodeId} is actually down before restoring over it.");
        if (entry.Coverage == "none")
        {
            steps.Add($"2. STOP — no restorable backup exists for this target ({entry.Detail}) Recovery means rebuild, not restore.");
            steps.Add("3. After rebuild, create a backup job immediately so this runbook has a restore path next time.");
            return steps;
        }
        steps.Add($"2. Locate the artifact: latest successful backup {entry.LastSuccess} at '{entry.Location}' (restore confidence {entry.RestoreConfidence}/100).");
        if (entry.Coverage == "stale")
            steps.Add($"3. WARNING — backup is stale (>{StaleAfterDays} days old). Data written since {entry.LastSuccess} is gone. Decide whether that loss is acceptable before proceeding.");
        if (entry.Coverage == "failed")
            steps.Add("3. WARNING — the most recent attempt FAILED; the artifact above is from an earlier run. Verify its integrity before trusting it.");
        var n = entry.Coverage == "ok" ? 3 : 4;
        steps.Add($"{n}. Restore via the hypervisor's restore flow to node {entry.NodeId} (do not overwrite a healthy guest — restore beside, then swap).");
        steps.Add($"{n + 1}. Verify: guest boots, dependent services respond, and ANTHILL health checks for this target return healthy.");
        steps.Add($"{n + 2}. Record the incident + outcome in the incident log so restore confidence reflects reality.");
        return steps;
    }
}
