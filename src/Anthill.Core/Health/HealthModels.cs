using System.Text.Json.Serialization;
using Anthill.Core.Homelab;

namespace Anthill.Core.Health;

/// <summary>
/// Health-check models (v1.11.0, NORTH_STAR Phase 7). Awareness only: checks classify what is
/// alive, degraded, or broken — there is no auto-remediation anywhere in this subsystem.
/// </summary>
public static class HealthStatus
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>What to check, on the shared homelab scheduler cadence. Operator-managed.</summary>
public sealed class HealthCheckSchedule
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("check_kind")] public string CheckKind { get; set; } = "http"; // ping | http | tcp | service_url | disk | uptime
    [JsonPropertyName("target")] public string Target { get; set; } = "";            // host, host:port, or URL depending on kind
    [JsonPropertyName("service_id")] public string ServiceId { get; set; } = "";
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Per-check timeout override in ms; 0 = use the global homelab_health_timeout_ms.</summary>
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

/// <summary>Latest-state rollup across all checked targets, for the summary endpoint and UI.</summary>
public sealed class HealthSummary
{
    [JsonPropertyName("healthy")] public int Healthy { get; set; }
    [JsonPropertyName("degraded")] public int Degraded { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("unknown")] public int Unknown { get; set; }
    [JsonPropertyName("targets")] public int Targets { get; set; }
    [JsonPropertyName("failing_targets")] public List<HealthCheckResult> FailingTargets { get; set; } = new();
    [JsonPropertyName("computed_at")] public string ComputedAt { get; set; } = "";
}

/// <summary>
/// One alert worth telling the operator about (health-check failure, incident candidate; pending
/// approvals join in V2.1). Persisted as a homelab_events row and optionally pushed through the
/// configured webhooks by <c>NotificationService</c> — never contains secrets.
/// </summary>
public sealed class AlertRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("kind")] public string Kind { get; set; } = "health_check_failure"; // health_check_failure | incident_candidate | test
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "warning";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}
