using Anthill.Core.Agents;

namespace Anthill.Core.Planning;

/// <summary>
/// v2.21.0 Phase B3: the roster the planner is actually allowed to plan with, computed at plan
/// time from the live registry rather than hardcoded in the prompt.
///
/// The prompt used to list six roles as a literal string. That was wrong in both directions:
///
///  - A role the operator had disabled was still offered, so the planner would emit tasks for it
///    that `AntRegistry.ValidateTask` then rejected — the plan silently losing steps it believed
///    it had, with the only trace a line on stderr.
///  - A specialist whose rollout gates were OPEN could never be planned with, because the prompt
///    did not mention it. Enabling the tester bought nothing unless some other code path inserted
///    a tester task by hand.
///
/// Both failures share a shape with the recurring defect in this codebase: a declaration and the
/// thing it describes drifting apart with nothing checking. Deriving the list removes the drift.
///
/// Deterministic: ordered by role id, no model call, no I/O. The same gate configuration always
/// produces the same roster text, which is what lets routing decisions be reproducible.
/// </summary>
public static class RuntimeRoster
{
    /// <summary>
    /// Roles the planner may assign work to right now: enabled, executable (or a specialist whose
    /// rollout gates are open), and never a control-plane role.
    ///
    /// Control-plane roles (queen, director, planner) are excluded structurally rather than by
    /// convention — a planner that could plan a task for the planner is a loop generator, and
    /// NORTH_STAR's doctrine keeps control-plane roles non-executable regardless.
    /// </summary>
    public static IReadOnlyList<AntRoleDefinition> PlannableRoles() =>
        AntRegistry.Roles
            .Where(r => r.Enabled)
            .Where(r => AntRegistry.ExecutableRoleIds.Contains(r.RoleId))
            .Where(r => !ControlPlane.Contains(r.RoleId))
            .OrderBy(r => r.RoleId, StringComparer.Ordinal)
            .ToList();

    private static readonly HashSet<string> ControlPlane =
        new(StringComparer.OrdinalIgnoreCase) { "queen", "director", "planner", "constraint" };

    /// <summary>
    /// The "Available ants" block for the planner prompt, built from the live roster.
    /// <paramref name="emphasis"/> supplies the hand-written guidance that a bare registry purpose
    /// cannot express (for example that the coder is the ONLY ant that changes files), keyed by
    /// role id; roles without an entry fall back to their registry purpose.
    /// </summary>
    public static string PromptBlock(IReadOnlyDictionary<string, string>? emphasis = null)
    {
        var roles = PlannableRoles();
        if (roles.Count == 0) return "- (no ants are currently executable — the mission cannot be planned)";

        return string.Join("\n", roles.Select(r =>
            $"- {r.RoleId}: {(emphasis is not null && emphasis.TryGetValue(r.RoleId, out var note) ? note : r.Purpose)}"));
    }

    /// <summary>True when the planner may assign work to this role right now.</summary>
    public static bool CanPlanFor(string? roleId) =>
        !string.IsNullOrWhiteSpace(roleId) && PlannableRoles().Any(r =>
            string.Equals(r.RoleId, roleId, StringComparison.OrdinalIgnoreCase));
}
