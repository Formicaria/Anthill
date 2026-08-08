using Anthill.Core.Agents;

namespace Anthill.Core.Configuration;

/// <summary>
/// Which roles this colony runs, as ONE resolved answer. v3.8.26.
/// </summary>
public sealed record RosterActivation(
    bool SpecialistExecution,
    ActivationTier Tier,
    bool Tester,
    bool Soldier,
    bool Medic,
    bool Archivist,
    bool UiCartographer,
    bool Scribe,
    bool HandoffIngestion,
    bool AdaptiveMissionControl);

/// <summary>
/// One switch instead of nine. v3.8.26.
///
/// Turning the colony on required `specialist_ant_execution_enabled`, an activation tier and six
/// separate `*_ant_enabled` flags. Nine unrelated keys an operator had to know about and keep
/// consistent, where getting one wrong produces a role that is silently absent and nothing
/// correlates the nine into "is the roster on".
///
/// A PURE FUNCTION taking the already-resolved flags, and that signature is the point. The first
/// version of this lived inline in <c>ProjectConfig</c> and was placed thirty lines before
/// `ui_cartographer`, `scribe`, `handoff_ingestion` and `adaptive_mission_control` were read from
/// config — so it set them true and the config assignments immediately set them back to false. That
/// was the THIRD time in this release cycle a derived value was computed before its inputs arrived
/// (<c>RuntimeProfile</c> in v3.8.16, <c>CapabilityGrant</c> in v3.8.25, this).
///
/// Taking <paramref name="fromFlags"/> as an argument makes the mistake unrepresentable: there is
/// nothing to resolve until the flags exist, so it cannot be called too early.
/// </summary>
public static class RosterProfiles
{
    public const string Core = "core";
    public const string Full = "full";

    /// <summary>The six roles a profile can switch, named once.</summary>
    public static readonly IReadOnlySet<string> SwitchableRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" };

    /// <summary>
    /// Resolve the roster from a profile name, the explicit kill-switch list, and whatever the
    /// individual flags already said.
    ///
    /// A profile can only ever WIDEN <paramref name="fromFlags"/>, never narrow it: an operator who
    /// hand-enabled two roles and then adopts `full` keeps those two and gains the rest. Only
    /// <paramref name="disabledRoles"/> subtracts, and it is applied LAST and absolutely — a kill
    /// switch the profile could override would not be a kill switch.
    /// </summary>
    public static RosterActivation Resolve(string? profile, IEnumerable<string>? disabledRoles,
        RosterActivation fromFlags)
    {
        var name = (profile ?? Core).Trim().ToLowerInvariant();
        var full = name == Full;

        var resolved = full
            ? fromFlags with
            {
                SpecialistExecution = true,
                Tier = ActivationTier.Full,
                Tester = true,
                Soldier = true,
                Medic = true,
                Archivist = true,
                UiCartographer = true,
                Scribe = true,
                // The roster is not "on" in any useful sense without these two. A tester that cannot
                // hand off to a medic, in a mission that cannot grow the repair task the handoff
                // asks for, is six roles that run and never collaborate.
                HandoffIngestion = true,
                AdaptiveMissionControl = true,
            }
            : fromFlags;

        var off = new HashSet<string>(disabledRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (off.Count == 0) return resolved;

        return resolved with
        {
            Tester = resolved.Tester && !off.Contains("tester"),
            Soldier = resolved.Soldier && !off.Contains("soldier"),
            Medic = resolved.Medic && !off.Contains("medic"),
            Archivist = resolved.Archivist && !off.Contains("archivist"),
            UiCartographer = resolved.UiCartographer && !off.Contains("ui_cartographer"),
            Scribe = resolved.Scribe && !off.Contains("scribe"),
        };
    }

    /// <summary>Names that are not roles this can switch — reported rather than ignored, because a
    /// typo in a kill-switch list is a role an operator believes is off and which is running.</summary>
    public static IReadOnlyList<string> UnknownDisabledRoles(IEnumerable<string>? disabledRoles) =>
        (disabledRoles ?? Array.Empty<string>())
            .Where(r => !SwitchableRoles.Contains(r ?? ""))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
}
