using Anthill.Core.Agents;
using Anthill.Core.Configuration;

// NOT `Anthill.Core.Colony`. These records carry a `Colony` property, and a namespace whose last
// segment matches a member name is the shadowing trap v0.3.8.114 paid for twice in the Micromound
// module — inside it, a qualified `Colony.X` resolves to the namespace rather than the type, and
// the error names neither.
namespace Anthill.Core.ColonyLive;

/// <summary>
/// One presentation sector of the Colony Live view, and the roles that actually live in it.
/// </summary>
/// <param name="SectorId">Stable id. Layout, overrides and selection key on this, never on the label.</param>
/// <param name="Label">The default operator-facing name. An operator may override it; the id does not move.</param>
/// <param name="Colonies">The registry `Colony` values this sector presents. Empty for `unassigned`.</param>
/// <param name="Residents">Every role the registry places here. May be empty — an empty sector is a fact.</param>
public sealed record ColonySector(
    string SectorId,
    string Label,
    IReadOnlyList<string> Colonies,
    IReadOnlyList<ColonyResident> Residents);

/// <summary>
/// One ant the operator can see standing in a chamber.
/// </summary>
/// <param name="RoleId">The registry role id. The only identity the renderer may key on.</param>
/// <param name="DisplayName">What the registry calls it.</param>
/// <param name="Colony">The registry's own grouping, carried through so the console can explain placement.</param>
/// <param name="Enabled">Whether the roster profile and activation tier leave it switched on.</param>
/// <param name="Executable">
/// Whether it can actually run work. An enabled role that is not executable is a real and common
/// state — the Queen herself is one — and showing it as a working ant would be a lie the operator
/// cannot check.
/// </param>
/// <param name="Workers">Named workers under this role, when it has them.</param>
public sealed record ColonyResident(
    string RoleId,
    string DisplayName,
    string Colony,
    bool Enabled,
    bool Executable,
    IReadOnlyList<string> Workers);

/// <summary>
/// THE SECTOR MAP, SERVER-SIDE, AND WHY IT MOVED HERE. v0.3.8.115.
///
/// `colony-topology.js` carried a hand-written `SECTOR_OF` object mapping ~22 role ids to six
/// sectors. That is defect class 5b — two stores of one fact — with the second store in a language
/// that cannot see the first: `AntRoleDefinition` has carried a `Colony` field since the registry
/// existed, and the browser copy was maintained by hand against it.
///
/// It failed in the way an unsynchronised copy always fails. A role the map did not name resolved
/// to `null`, and the records path then read `sectorOfAnt(ant) || 'queen'` — so every role added
/// after the map was last edited, and every role a plugin contributes, was silently attributed to
/// the QUEEN. Not dropped, where somebody might notice: filed under the colony's highest authority.
///
/// So the mapping is `Colony` → sector, declared once, here, and anything unrecognised lands in
/// <see cref="Unassigned"/> where it is VISIBLE. That is the rule the console cannot express on its
/// own: an extensible registry means the set of colonies is open, and a client-side map of an open
/// set is a map that is wrong as soon as somebody extends it.
/// </summary>
public static class ColonySectors
{
    public const string Queen = "queen";
    public const string Intelligence = "intel";
    public const string Forge = "forge";
    public const string Validation = "valid";
    public const string Memory = "memory";
    public const string Output = "output";
    public const string Homelab = "homelab";
    public const string Micromound = "mound";

    /// <summary>
    /// Where a role goes when the colony it declares is not one this presentation knows.
    ///
    /// NOT Queen, and not a guess from the role's name. A neutral group is the honest answer to "we
    /// do not know where this belongs", and it is the one that gets noticed and fixed; attributing
    /// it to an authority sector produces a picture that looks complete and is wrong.
    /// </summary>
    public const string Unassigned = "unassigned";

    /// <summary>
    /// Registry `Colony` → sector. The ONE place this is decided.
    ///
    /// `Homelab` gets its own sector rather than being folded into Forge or dropped into
    /// `unassigned`: it is eight real roles and a named colony in the registry, and both
    /// alternatives would state something false — that homelab work happens in the code sector, or
    /// that the colony does not know where it happens. It is deliberately NOT Micromound: Micromound
    /// is physical devices reporting over the wire, and the homelab roles do not execute there
    /// unless and until the backend says so.
    /// </summary>
    private static readonly Dictionary<string, string> ByColony = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = Queen,
        ["Command"] = Queen,
        ["Context"] = Intelligence,
        ["External Research"] = Intelligence,
        ["Code"] = Forge,
        ["Workspace"] = Forge,
        ["UI"] = Forge,
        ["Resources"] = Forge,
        ["Verification"] = Validation,
        ["Testing"] = Validation,
        ["Security"] = Validation,
        ["Repair"] = Validation,
        ["Memory"] = Memory,
        ["Output"] = Output,
        ["Homelab"] = Homelab,
    };

    /// <summary>Default operator-facing labels. An operator override replaces the label, never the id.</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [Queen] = "QUEEN'S CORE",
        [Intelligence] = "INTELLIGENCE",
        [Forge] = "FORGE",
        [Validation] = "VALIDATION",
        [Memory] = "MEMORY",
        [Output] = "OUTPUT",
        [Homelab] = "HOMELAB",
        [Micromound] = "MICROMOUND",
        [Unassigned] = "UNASSIGNED",
    };

    /// <summary>Presentation order. Micromound sits last: it is infrastructure beneath the colony.</summary>
    public static readonly IReadOnlyList<string> Order =
        [Queen, Intelligence, Forge, Validation, Memory, Output, Homelab, Unassigned, Micromound];

    public static string Label(string sectorId) => Labels.GetValueOrDefault(sectorId, sectorId);

    /// <summary>
    /// The sector a registry colony presents in, or <see cref="Unassigned"/>.
    ///
    /// Never throws and never guesses. An unknown colony — which is exactly what a contributed role
    /// declaring its own produces — is a neutral placement, not an error and not an assumption.
    /// </summary>
    public static string ForColony(string? colony) =>
        string.IsNullOrWhiteSpace(colony) ? Unassigned : ByColony.GetValueOrDefault(colony, Unassigned);
}

/// <summary>
/// THE COLONY LIVE READ MODEL — the one projection both the 3D renderer and the classic fallback
/// consume. v0.3.8.115.
///
/// It answers "what is true", never "how should this look". Nothing here decides a colour, a
/// position or an animation, and nothing here invents a fact: every field traces to the registry,
/// the runtime's own activation state, or a persisted row.
///
/// It lives in `Anthill.Core` rather than at the API edge so it can be tested without booting a
/// host — the projection is the part with rules in it, and rules that need a web server to check
/// are rules that get checked less often.
/// </summary>
public static class ColonyLiveProjection
{
    /// <summary>
    /// Every sector, with the roles the REGISTRY places in it.
    ///
    /// Sectors with no residents are returned rather than filtered out. A colony where nothing is
    /// enabled should look empty, and a view that silently drops empty chambers cannot show the
    /// difference between "this sector has no roles" and "this sector was never built".
    /// </summary>
    public static IReadOnlyList<ColonySector> Sectors()
    {
        var residents = new Dictionary<string, List<ColonyResident>>(StringComparer.Ordinal);
        foreach (var sector in ColonySectors.Order) residents[sector] = [];

        var colonies = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var sector in ColonySectors.Order) colonies[sector] = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var role in AntRegistry.Roles)
        {
            var sector = ColonySectors.ForColony(role.Colony);

            residents[sector].Add(new ColonyResident(
                role.RoleId,
                role.DisplayName,
                role.Colony,
                role.Enabled,
                role.Executable,
                [.. role.Workers.Select(w => w.WorkerId)]));

            if (!string.IsNullOrWhiteSpace(role.Colony)) colonies[sector].Add(role.Colony);
        }

        return
        [
            .. ColonySectors.Order.Select(id => new ColonySector(
                id,
                ColonySectors.Label(id),
                [.. colonies[id]],
                [.. residents[id].OrderBy(r => r.RoleId, StringComparer.Ordinal)]))
        ];
    }

    /// <summary>
    /// Does this event type mean the colony WROTE something durable?
    ///
    /// The distinction the console could not previously make. `colony-topology.js` declared a
    /// `RECORD_EVENTS` regex with exactly this intent and then never called it — `project()` turned
    /// every one of the last 120 events into a "record" with `verif: 'recorded'`, so a task starting
    /// and a memory being written grew the same chamber by the same amount. Declared, and reaching
    /// nobody: defect class 2, inside a file whose header promises "everything in the scene traces
    /// to a backend fact".
    ///
    /// The list is deliberately conservative. An event this does not name is still shown in the
    /// event feed; it simply does not become a durable particle, because a particle claims the
    /// colony stored something and most events are the colony SAYING something.
    /// </summary>
    public static bool CreatesDurableRecord(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType)) return false;

        return eventType.EndsWith("_recorded", StringComparison.Ordinal)
            || eventType.EndsWith("_stored", StringComparison.Ordinal)
            || eventType.EndsWith("_written", StringComparison.Ordinal)
            || eventType is "memory_candidate"
                        or "pheromone_scored"
                        or "verification_bound_to_evidence"
                        or "mission_evaluated"
                        or "mission_outcome";
    }

    /// <summary>
    /// The runtime facts the view needs to explain what it is showing, and to stop claiming
    /// capability the colony does not have.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Runtime() => new Dictionary<string, object?>
    {
        ["version"] = AnthillRuntime.Version,
        ["roster_profile"] = AnthillRuntime.RosterProfile,
        ["activation_tier"] = ActivationTiers.Name(AnthillRuntime.ActivationTier),
        ["specialist_execution_enabled"] = AnthillRuntime.EnableSpecialistAntExecution,
    };
}
