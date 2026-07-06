using System.Text.Json.Serialization;

namespace Anthill.Core.Homelab;

/// <summary>
/// Homelab foundation models (v1.9.0, NORTH_STAR Phase 4). Read-only schemas only: these records
/// describe what exists in the homelab, what changed, and what is at risk. Nothing in this file can
/// control infrastructure — control actions arrive in V2.1 through IApprovable proposals.
/// All timestamps are ISO-8601 UTC strings, matching the rest of the memory schema.
/// </summary>
public sealed class HomelabNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "host"; // host | hypervisor | nas | router | iot | other
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("role_tags")] public List<string> RoleTags { get; set; } = new();
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class NetworkDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "unknown"; // switch | ap | firewall | printer | unknown
    [JsonPropertyName("mac")] public string Mac { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("vlan")] public string Vlan { get; set; } = "";
    [JsonPropertyName("known")] public bool Known { get; set; } = true;
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("first_seen")] public string FirstSeen { get; set; } = "";
    [JsonPropertyName("last_seen")] public string LastSeen { get; set; } = "";
}

public sealed class ServiceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("ports")] public List<int> Ports { get; set; } = new();
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("criticality")] public string Criticality { get; set; } = "normal"; // low | normal | high | critical
    [JsonPropertyName("internet_exposed")] public bool InternetExposed { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class VmRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("vm_id")] public string VmId { get; set; } = "";       // provider id (e.g. Proxmox VMID)
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("cpu_cores")] public int CpuCores { get; set; }
    [JsonPropertyName("memory_mb")] public long MemoryMb { get; set; }
    [JsonPropertyName("uptime_seconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class ContainerRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("container_id")] public string ContainerId { get; set; } = ""; // provider id (LXC CTID / docker id)
    [JsonPropertyName("kind")] public string Kind { get; set; } = "lxc"; // lxc | docker | other
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class StoragePoolRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = ""; // zfs | lvm | dir | nfs | smb | other
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("used_bytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class StorageDeviceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; set; } = ""; // /dev/sda, nvme0n1 ...
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("pool_id")] public string PoolId { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("smart_status")] public string SmartStatus { get; set; } = "unknown";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class BackupRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("target_kind")] public string TargetKind { get; set; } = ""; // vm | container | host | service | dataset
    [JsonPropertyName("target_id")] public string TargetId { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown"; // ok | failed | stale | unknown
    [JsonPropertyName("last_success")] public string LastSuccess { get; set; } = "";
    [JsonPropertyName("last_attempt")] public string LastAttempt { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class HealthCheckResult
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("check_kind")] public string CheckKind { get; set; } = ""; // ping | http | tcp | service_url | disk | uptime
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("service_id")] public string ServiceId { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown"; // healthy | degraded | failed | unknown
    [JsonPropertyName("latency_ms")] public double LatencyMs { get; set; }
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("checked_at")] public string CheckedAt { get; set; } = "";
}

public sealed class HomelabEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("event_type")] public string EventType { get; set; } = ""; // scheduler_job | credential_used | inventory_changed | ...
    [JsonPropertyName("subject_kind")] public string SubjectKind { get; set; } = "";
    [JsonPropertyName("subject_id")] public string SubjectId { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info"; // info | warning | error
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

public sealed class ChangeRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("subject_kind")] public string SubjectKind { get; set; } = ""; // host | service | vm | config | credential | allowlist
    [JsonPropertyName("subject_id")] public string SubjectId { get; set; } = "";
    [JsonPropertyName("change_kind")] public string ChangeKind { get; set; } = ""; // created | updated | removed | imported
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("changed_by")] public string ChangedBy { get; set; } = "";
    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

public sealed class IncidentRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "open"; // open | investigating | resolved
    [JsonPropertyName("severity")] public string Severity { get; set; } = "warning";
    [JsonPropertyName("subject_kind")] public string SubjectKind { get; set; } = "";
    [JsonPropertyName("subject_id")] public string SubjectId { get; set; } = "";
    [JsonPropertyName("root_cause")] public string RootCause { get; set; } = "";
    [JsonPropertyName("opened_at")] public string OpenedAt { get; set; } = "";
    [JsonPropertyName("resolved_at")] public string ResolvedAt { get; set; } = "";
}

public sealed class DependencyRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("from_kind")] public string FromKind { get; set; } = ""; // service | vm | container | host
    [JsonPropertyName("from_id")] public string FromId { get; set; } = "";
    [JsonPropertyName("to_kind")] public string ToKind { get; set; } = "";
    [JsonPropertyName("to_id")] public string ToId { get; set; } = "";
    [JsonPropertyName("dependency_kind")] public string DependencyKind { get; set; } = "runs_on"; // runs_on | needs | stores_on
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

public sealed class RiskRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("finding_kind")] public string FindingKind { get; set; } = ""; // risky_open_port | unknown_device | no_backup | exposed_service | ...
    [JsonPropertyName("subject_kind")] public string SubjectKind { get; set; } = "";
    [JsonPropertyName("subject_id")] public string SubjectId { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "warning";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "open"; // open | acknowledged | resolved
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// A stored homelab credential. The secret payload is encrypted at rest with the existing
/// FieldCipher and is NEVER exposed through the API — status responses carry only
/// configured/last-verified metadata. Every secret use writes an audit HomelabEvent.
/// </summary>
public sealed class CredentialRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";              // operator-chosen, e.g. "proxmox-main"
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";          // proxmox_api_token | ssh_key | webhook | basic_auth | other
    [JsonPropertyName("target_host")] public string TargetHost { get; set; } = "";
    [JsonPropertyName("configured")] public bool Configured { get; set; }
    [JsonPropertyName("last_verified")] public string LastVerified { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// One operator-maintained allowlist entry that lets deterministic homelab providers reach a
/// trusted private host. This list is consulted ONLY by IHomelabTargetGuard — the general SSRF
/// guard (UrlSafety) is untouched and keeps blocking private/loopback ranges for LLM-directed
/// tools. Entries are an exact hostname, an exact IP, or an IPv4 CIDR block.
/// </summary>
public sealed class TargetAllowlistRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("target")] public string Target { get; set; } = ""; // "nas.lan" | "192.168.1.10" | "10.0.0.0/24"
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("added_by")] public string AddedBy { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

/// <summary>Secret-free status of one integration/provider, for the summary endpoint and UI.</summary>
public sealed class IntegrationStatus
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "not_configured"; // not_configured | idle | ok | failing
    [JsonPropertyName("last_run")] public string LastRun { get; set; } = "";
    [JsonPropertyName("last_result")] public string LastResult { get; set; } = "";
}
