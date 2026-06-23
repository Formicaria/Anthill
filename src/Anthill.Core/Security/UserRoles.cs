namespace Anthill.Core.Security;

/// <summary>
/// Operator roles and the permissions each one grants. There are two roles:
///
/// - <b>admin</b> — full administrative control over the software: every API permission, plus
///   user management. The first account created on a fresh install is always an admin.
/// - <b>coordinator</b> — a "Mission Coordinator" / viewer. May only dispatch missions to the
///   Queen and watch the colony: send a mission, see live status, and read the event logs from
///   the Queen and ants. Everything else (settings, patches, approvals, autonomy control, user
///   management, pheromone pruning, ant configuration) is denied.
///
/// Role checks compose with the capability gates in <c>AnthillRuntime.ApiPermissions</c>: an
/// action is allowed only if the user's role permits it AND the capability is enabled at all.
/// </summary>
public static class UserRoles
{
    public const string Admin = "admin";
    public const string Coordinator = "coordinator";

    public static readonly string[] All = { Admin, Coordinator };

    public static bool IsValid(string? role) => role is not null && Array.Exists(All, r => r == role);

    public static string Normalize(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            Admin => Admin,
            Coordinator => Coordinator,
            "viewer" or "mission_coordinator" or "mission-coordinator" => Coordinator,
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

    /// <summary>True if the given role is permitted to use the named API permission.</summary>
    public static bool RoleAllows(string role, string permission) =>
        role == Admin || (role == Coordinator && CoordinatorPermissions.Contains(permission));

    /// <summary>True for permissions only an admin may use — used to label the UI.</summary>
    public static bool IsAdminOnly(string permission) => !CoordinatorPermissions.Contains(permission);
}
