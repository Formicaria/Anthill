namespace Anthill.Core.Homelab.Actions;

/// <summary>
/// The V2.3.0 action catalog (NORTH_STAR Phase 12). This is an ALLOWLIST: any action_type not in
/// <see cref="Allowed"/> is refused — at proposal time AND again inside the executor, so a record
/// written around the API can still never run. The forbidden list exists on top of that for
/// explicit, named refusals of the NORTH_STAR "Forbidden in V2.1" set (delete/wipe/reset/etc.),
/// so an operator sees "structurally forbidden" rather than a generic "unknown action".
/// </summary>
public static class ActionCatalog
{
    /// <summary>The NORTH_STAR Phase 12 "Allowed initial actions" set.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "restart_service",
        "start_vm", "stop_vm", "restart_vm",
        "start_container", "stop_container", "restart_container",
        "create_snapshot",
        "run_backup",
        "resolve_incident",
        "update_inventory",
        "run_diagnostic",
    };

    /// <summary>
    /// The NORTH_STAR Phase 12 forbidden set — named so refusals are explicit. Being absent from
    /// <see cref="Allowed"/> already blocks these; this list only improves the error message and
    /// gives tests a stable contract that each stays forbidden.
    /// </summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "delete_vm", "delete_container", "delete_lxc",
        "delete_firewall_rule", "modify_firewall",
        "factory_reset", "wipe_disk",
        "modify_secret", "delete_credential",
        "disable_backup",
        "expose_service",
    };

    /// <summary>Action types that change infrastructure power state (weighted heavier in blast radius).</summary>
    public static readonly IReadOnlySet<string> PowerActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "restart_service", "start_vm", "stop_vm", "restart_vm",
        "start_container", "stop_container", "restart_container",
    };

    /// <summary>Purely local actions — they touch only ANTHILL's own database, never the network.</summary>
    public static readonly IReadOnlySet<string> LocalActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "resolve_incident", "update_inventory", "run_diagnostic",
    };

    /// <summary>Null when allowed; otherwise a human-readable refusal reason.</summary>
    public static string? Refusal(string actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType)) return "Action type is required.";
        if (Forbidden.Contains(actionType))
            return $"Action '{actionType}' is structurally forbidden in the V2.3 line (NORTH_STAR Phase 12) and cannot be proposed, approved, or executed.";
        if (!Allowed.Contains(actionType))
            return $"Unknown action '{actionType}' — only the allowlisted V2.3 action set can run: {string.Join(", ", Allowed.OrderBy(a => a))}.";
        return null;
    }
}
