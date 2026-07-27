namespace Anthill.Core.Agents;

/// <summary>
/// v2.22.0 Phase D: how much of the colony the operator has switched on, as one deliberate dial.
///
/// Specialist activation was six independent booleans plus a master switch. Turning the colony up
/// meant knowing which six flags existed and setting them in the right combination; there was no
/// way to express "run the adaptive set" without enumerating it, and no single thing to read to
/// answer "what is switched on?". The ADR rejected one setting per knob for exactly this reason:
/// a small clamped set plus a profile.
///
/// The tier is a CEILING, not a switch. Per-role flags still apply on top — a role is executable
/// only when its tier admits it AND its own flag is set. Widening the tier can never turn a role
/// on by itself, which keeps every existing rollout gate exactly as binding as it was.
/// </summary>
public enum ActivationTier
{
    /// <summary>Only the six original ants. No specialist may execute, whatever its flag says.</summary>
    Core = 0,

    /// <summary>
    /// Core plus the specialists that support the adaptive loop: the tester and medic (detect and
    /// diagnose) and the ui_cartographer (read-only mapping). Deliberately excludes roles that
    /// PRODUCE operator-facing artifacts or security verdicts.
    /// </summary>
    Adaptive = 1,

    /// <summary>Every specialist the framework knows about, subject to its own flag.</summary>
    Full = 2,
}

/// <summary>Which roles each tier admits, and how a configured name resolves to one.</summary>
public static class ActivationTiers
{
    /// <summary>
    /// The adaptive set. Tester and medic are the detect/diagnose pair the repair loop needs;
    /// ui_cartographer is read-only and cannot propose changes. Soldier, scribe and archivist are
    /// deliberately absent: they issue security verdicts, write operator-facing documentation, and
    /// write durable memory respectively — none of which the adaptive loop requires, and each of
    /// which deserves a separate decision to switch on.
    /// </summary>
    private static readonly HashSet<string> AdaptiveRoles =
        new(StringComparer.OrdinalIgnoreCase) { "tester", "medic", "ui_cartographer" };

    /// <summary>
    /// Parse an operator-supplied tier name. Unrecognised input resolves to <see cref="ActivationTier.Core"/>
    /// — a typo in a config file must narrow what can run, never widen it.
    /// </summary>
    public static ActivationTier Parse(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "adaptive" => ActivationTier.Adaptive,
        "full" => ActivationTier.Full,
        _ => ActivationTier.Core,
    };

    public static string Name(ActivationTier tier) => tier switch
    {
        ActivationTier.Adaptive => "adaptive",
        ActivationTier.Full => "full",
        _ => "core",
    };

    /// <summary>
    /// Whether this tier admits a specialist role at all. This is the ceiling — the role's own
    /// rollout flag is still required, and is checked separately by
    /// <see cref="AntExecutorCatalog.SpecialistGateOpen"/>.
    /// </summary>
    public static bool Admits(ActivationTier tier, string roleId) => tier switch
    {
        ActivationTier.Full => true,
        ActivationTier.Adaptive => AdaptiveRoles.Contains(roleId ?? ""),
        _ => false,
    };

    /// <summary>Operator-facing description of what a tier turns on.</summary>
    public static string Explain(ActivationTier tier) => tier switch
    {
        ActivationTier.Full => "every specialist may run, subject to its own rollout flag",
        ActivationTier.Adaptive => $"the adaptive set ({string.Join(", ", AdaptiveRoles.OrderBy(r => r, StringComparer.Ordinal))}) "
                                   + "may run, subject to its own rollout flag",
        _ => "core ants only — no specialist may run, whatever its flag says",
    };
}
