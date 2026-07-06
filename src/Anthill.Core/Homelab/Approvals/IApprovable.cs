using System.Text.Json.Serialization;

namespace Anthill.Core.Homelab.Approvals;

/// <summary>
/// THE shared approval abstraction (v1.14.0, NORTH_STAR Phase 10 + §6 rule 1: "one approval
/// system — IApprovable, not separate queues for patches/actions/network changes").
///
/// Everything an operator can approve — today's patch proposals, V2.1's homelab action proposals,
/// V2.4's network-change previews — projects into this one shape, so there is exactly ONE pending
/// queue, ONE audit trail, and ONE dedupe path. What differs per kind is only the renderer
/// (<see cref="RendererHint"/>): a patch renders a diff, an action renders a blast-radius card, a
/// network change renders a rule preview.
///
/// Lifecycle (uniform for every kind):
///   pending → approved → executed
///   pending → rejected
///   pending → superseded   (a newer approvable with the same DedupeKey arrived)
/// Execution NEVER happens from state 'pending' — approval is a distinct human step, and executors
/// (patch applier today, ActionExecutor in V2.1) must re-check state at execution time.
/// Dedupe: two approvables with equal <see cref="DedupeKey"/> may not both be pending; the newer
/// one supersedes the older (same rule the patch approval dedupe uses today).
/// </summary>
public interface IApprovable
{
    string ApprovableId { get; }
    /// <summary>"patch" today; "homelab_action" (V2.1), "network_change" (V2.4) later.</summary>
    string Kind { get; }
    string Title { get; }
    string Summary { get; }
    /// <summary>low | medium | high | critical — feeds sorting and V2.1's blast-radius display.</summary>
    string RiskLevel { get; }
    /// <summary>pending | approved | rejected | superseded | executed.</summary>
    string State { get; }
    /// <summary>Equal keys may not both be pending — the single dedupe rule for every kind.</summary>
    string DedupeKey { get; }
    /// <summary>Which UI renderer draws the detail: "patch_diff", "action_proposal", "network_preview".</summary>
    string RendererHint { get; }
    string RequestedBy { get; }
    string CreatedAt { get; }
    /// <summary>Id of the underlying record (approval_requests.id, future action id, ...).</summary>
    string SourceId { get; }
}

/// <summary>Concrete serializable projection — what the unified pending-approvals API returns.</summary>
public sealed class ApprovableView : IApprovable
{
    [JsonPropertyName("approvable_id")] public string ApprovableId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("risk_level")] public string RiskLevel { get; set; } = "medium";
    [JsonPropertyName("state")] public string State { get; set; } = "pending";
    [JsonPropertyName("dedupe_key")] public string DedupeKey { get; set; } = "";
    [JsonPropertyName("renderer_hint")] public string RendererHint { get; set; } = "";
    [JsonPropertyName("requested_by")] public string RequestedBy { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
}

/// <summary>
/// V2.1 skeleton, designed now so the contract is reviewed before any executor exists: a proposed
/// homelab action (restart service, snapshot VM, run backup...). DELIBERATELY not executable in
/// v1.14 — there is no ActionExecutor, no endpoint creates one, and the blast-radius fields are
/// the rubric inputs NORTH_STAR Phase 12 requires before implementation.
/// </summary>
public sealed class ActionProposal : IApprovable
{
    [JsonPropertyName("approvable_id")] public string ApprovableId { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("kind")] public string Kind => "homelab_action";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("risk_level")] public string RiskLevel { get; set; } = "high"; // fail toward caution
    [JsonPropertyName("state")] public string State { get; set; } = "pending";
    [JsonPropertyName("dedupe_key")] public string DedupeKey { get; set; } = "";
    [JsonPropertyName("renderer_hint")] public string RendererHint => "action_proposal";
    [JsonPropertyName("requested_by")] public string RequestedBy { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";

    // Blast-radius rubric inputs (NORTH_STAR Phase 12) — populated by V2.1, reviewed now.
    [JsonPropertyName("action_type")] public string ActionType { get; set; } = "";   // restart_service | snapshot | run_backup | ...
    [JsonPropertyName("target_kind")] public string TargetKind { get; set; } = "";
    [JsonPropertyName("target_id")] public string TargetId { get; set; } = "";
    [JsonPropertyName("dependency_fanout")] public int DependencyFanout { get; set; }
    [JsonPropertyName("service_criticality")] public string ServiceCriticality { get; set; } = "";
    [JsonPropertyName("backup_covered")] public bool BackupCovered { get; set; }
    [JsonPropertyName("internet_exposed")] public bool InternetExposed { get; set; }
    [JsonPropertyName("rollback_note")] public string RollbackNote { get; set; } = "";
    [JsonPropertyName("dry_run_available")] public bool DryRunAvailable { get; set; }
}

/// <summary>
/// Projections of existing records into the unified queue. Today: the patch approval_requests
/// rows (SqliteMemory.ListApprovalRequests dictionaries). V2.1 adds action proposals; V2.4 adds
/// network changes — each is one more From* method, never one more queue.
/// </summary>
public static class ApprovableProjections
{
    /// <summary>Maps one approval_requests row (column-name keyed) into the unified view.</summary>
    public static ApprovableView FromPatchApproval(IReadOnlyDictionary<string, object?> row)
    {
        string S(string key) => row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        var status = S("status").ToLowerInvariant();
        return new ApprovableView
        {
            ApprovableId = "patch:" + S("id"),
            Kind = "patch",
            Title = S("title"),
            Summary = S("description"),
            RiskLevel = RiskFromMetadata(S("metadata_json")),
            State = status switch
            {
                "pending" => "pending", "approved" => "approved", "rejected" => "rejected",
                "superseded" => "superseded", "applied" or "executed" => "executed",
                _ => status.Length > 0 ? status : "pending",
            },
            // Same rule the patch dedupe uses today: one pending approval per action+target.
            DedupeKey = $"{S("action_type")}:{S("target_id")}".ToLowerInvariant(),
            RendererHint = "patch_diff",
            RequestedBy = S("requested_by"),
            CreatedAt = S("created_at"),
            SourceId = S("id"),
        };
    }

    private static string RiskFromMetadata(string metadataJson)
    {
        if (metadataJson.Length == 0) return "medium";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("risk_level", out var risk))
                return (risk.GetString() ?? "medium").ToLowerInvariant();
        }
        catch { /* malformed metadata never breaks the queue */ }
        return "medium";
    }

    /// <summary>The one dedupe rule, kind-agnostic: newer pending item supersedes an older equal key.</summary>
    public static IReadOnlyList<ApprovableView> DedupePending(IEnumerable<ApprovableView> items)
    {
        var result = new List<ApprovableView>();
        foreach (var group in items.GroupBy(i => $"{i.Kind}|{i.DedupeKey}", StringComparer.OrdinalIgnoreCase))
        {
            // Newest first; the newest still-pending item wins and EVERY older pending duplicate is
            // superseded. Track pending-kept rather than gating on index 0: if the newest item in the
            // group is already approved/rejected/executed, older pending duplicates must STILL collapse
            // to a single pending — otherwise "at most one pending per key" breaks and the unified queue
            // shows two live pending items for the same target. Non-pending items are left untouched.
            var ordered = group.OrderByDescending(i => i.CreatedAt, StringComparer.Ordinal).ToList();
            var keptPending = false;
            foreach (var item in ordered)
            {
                if (item.State == "pending")
                {
                    if (keptPending) item.State = "superseded";
                    else keptPending = true;
                }
                result.Add(item);
            }
        }
        return result.OrderByDescending(i => i.CreatedAt, StringComparer.Ordinal).ToList();
    }
}
