namespace Anthill.Core.Security;

/// <summary>
/// Operator roles and the permissions each one grants. There are three roles:
///
/// - <b>admin</b> — full administrative control over the software: every API permission, plus
///   user management. The first account created on a fresh install is always an admin.
/// - <b>coordinator</b> — a "Mission Coordinator" / viewer. May only dispatch missions to the
///   Queen and watch the colony: send a mission, see live status, and read the event logs from
///   the Queen and ants. Everything else (settings, patches, approvals, autonomy control, user
///   management, pheromone pruning, ant configuration) is denied.
/// - <b>homelab_operator</b> — the NORTH_STAR D3 homelab tier (v1.9.0): may view everything the
///   coordinator can, read the homelab (inventory, health, events, allowlist, secret-free
///   credential statuses), and approve homelab action proposals (shipped v2.3.0). May NOT manage
///   integrations/credentials, execute actions, touch providers, settings, users, or the shell.
///
/// Role checks compose with the capability gates in <c>AnthillRuntime.ApiPermissions</c>: an
/// action is allowed only if the user's role permits it AND the capability is enabled at all
/// (the execute/approve homelab gates still ship disabled — fail closed — even though v2.3.0
/// implemented the action pipeline behind them).
/// </summary>
public static class UserRoles
{
    public const string Admin = "admin";
    public const string Coordinator = "coordinator";
    public const string HomelabOperator = "homelab_operator";

    public static readonly string[] All = { Admin, Coordinator, HomelabOperator };

    public static bool IsValid(string? role) => role is not null && Array.Exists(All, r => r == role);

    public static string Normalize(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            Admin => Admin,
            Coordinator => Coordinator,
            HomelabOperator => HomelabOperator,
            "viewer" or "mission_coordinator" or "mission-coordinator" => Coordinator,
            "homelab-operator" or "homelab" => HomelabOperator,
            _ => "",
        };

    // What a Mission Coordinator is allowed to do — nothing more. Admins bypass this set entirely.
    private static readonly HashSet<string> CoordinatorPermissions = new()
    {
        "run_mission",     // send missions to the Queen
        "read_status",     // see live colony status + their own jobs/results
        "read_events",     // view the event logs from the Queen and ants
        "read_ui_state",   // load the saved colony layout so the map renders
    };

    // Homelab Operator (NORTH_STAR D3): view + approve, never manage or execute.
    // Includes the coordinator view set so the console works, plus homelab read/approve.
    private static readonly HashSet<string> HomelabOperatorPermissions = new()
    {
        "run_mission", "read_status", "read_events", "read_ui_state",
        "read_homelab",            // inventory, health, events, allowlist, credential statuses
        "approve_homelab_actions", // approve v2.3.0 action proposals (capability gate still ships off)
        // Deliberately absent: manage_homelab_integrations (credentials/allowlist writes),
        // execute_homelab_actions, and every provider/settings/user/shell permission.
    };

    /// <summary>True if the given role is permitted to use the named API permission.</summary>
    public static bool RoleAllows(string role, string permission) =>
        role == Admin
        || (role == Coordinator && CoordinatorPermissions.Contains(permission))
        || (role == HomelabOperator && HomelabOperatorPermissions.Contains(permission));

    /// <summary>True for permissions only an admin may use — used to label the UI.</summary>
    public static bool IsAdminOnly(string permission) =>
        !CoordinatorPermissions.Contains(permission) && !HomelabOperatorPermissions.Contains(permission);
}
