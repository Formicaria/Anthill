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
    IReadOnlyList<ColonyWorker> Workers,
    ColonyTrail? Trail = null);

/// <summary>
/// A WORKER, WITH BOTH OF ITS NAMES. v0.3.8.116.
///
/// <see cref="AntWorkerDefinition.WorkerId"/> is <c>{parent}.{id}</c> — "constraint.scope_guard" —
/// and it is the identity an event carries in <c>ant_name</c>, so it is the only thing a record can
/// be matched on. <see cref="AntWorkerDefinition.DisplayName"/> is "ScopeGuard", which is what the
/// 2D colony view has always shown an operator and what the roster editor writes.
///
/// Until now this projection carried the id ALONE, so the Live view labelled a worker
/// "constraint.scope_guard" while every other page in the console called the same ant "ScopeGuard".
/// Carrying one name and displaying it is how one ant ends up with two names in one product; a
/// parallel array of display names would be the same defect with an extra way to fall out of step.
/// Both names travel together, on the worker.
///
/// <paramref name="ParentRoleId"/> is carried for the same reason: the registry owns the fact that
/// scope_guard belongs to constraint, and a view that wants to draw that relationship should be
/// reading it rather than splitting the id on a dot and hoping.
/// </summary>
public sealed record ColonyWorker(
    string WorkerId,
    string DisplayName,
    string ParentRoleId,
    bool Enabled);

/// <summary>
/// A role's REPUTATION, as the pheromone layer actually recorded it. v0.3.8.116.
///
/// Summed over the role's workers, because that is where the layer writes: the key is
/// <c>worker:{id}</c>, exactly as <see cref="Anthill.Core.Pheromones.TrailGuidedSelection.TrailKeyFor"/>
/// forms it, so reader and writer cannot drift. `Strength` is the mean over workers that HAVE a
/// trail — a role whose workers have never run has no trail at all and this is null, which is a
/// different thing from a strength of zero and must render differently.
///
/// This is the one number in the view that says anything about how well an ant has done, and it is
/// real. There is no per-RECORD pheromone anywhere in Anthill; a design that shows one is showing
/// something this colony does not measure.
/// </summary>
public sealed record ColonyTrail(double Strength, int Successes, int Failures, int WorkersWithTrail);

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
/// So the mapping is `Colony` → sector, declared once, here. That is the rule the console cannot
/// express on its own: an extensible registry means the set of colonies is open, and a client-side
/// map of an open set is a map that is wrong as soon as somebody extends it.
///
/// v0.3.8.122 — anything unrecognised used to land in a visible `unassigned` chamber. It no longer
/// does, because the map is now TOTAL over the registry and a guard proves it against the live
/// roster: the visibility that chamber provided is provided earlier and louder, by a failing test
/// at the moment a colony is added rather than by an odd-looking sphere an operator has to notice.
/// See <see cref="ColonySectors.Fallback"/>.
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
    /// Where anything this presentation cannot place goes. v0.3.8.122.
    ///
    /// THIS USED TO BE A NINTH CHAMBER CALLED `unassigned`, and the argument for it was good: a
    /// neutral group is the honest answer to "we do not know where this belongs", and attributing
    /// it to an authority sector produces a picture that looks complete and is wrong.
    ///
    /// What changed is that the answer stopped being needed. <see cref="ByColony"/> now covers every
    /// one of the sixteen `Colony` values the registry declares, and a guard asserts that totality
    /// against the live registry rather than against this comment — so no ROLE and no WORKER can
    /// reach this fallback, and the chamber it existed to fill was permanently empty of residents.
    /// An empty chamber labelled UNASSIGNED does not report a gap; it just occupies a seat in the
    /// colony and invites the reader to wonder what is wrong.
    ///
    /// What CAN still reach it is an event whose `ant_name` resolves to nothing — a renamed worker,
    /// a system-authored row. Those belong with mission control, which is where mission-level events
    /// already live, and that is a placement rather than a shrug. **If a future role declares a new
    /// colony, the guard fails before this fallback is ever exercised**, which is the property that
    /// makes pointing it at a real sector safe: the fallback is a backstop, not a bucket.
    /// </summary>
    public const string Fallback = Queen;

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
        // v0.3.8.115.1 — THE TWO THAT FELL THROUGH. The registry declares seventeen distinct
        // `Colony` values and this map had fifteen, so `constraint` (Command / Safety) and
        // `scribe` (Communication / Docs) resolved to UNASSIGNED — the exact defect class this
        // release existed to remove, reintroduced one layer over. Placed where the console's
        // 2D chamber map has always placed them, which is the established answer.
        ["Command / Safety"] = Queen,
        ["Context"] = Intelligence,
        ["External Research"] = Intelligence,
        ["Code"] = Forge,
        ["Workspace"] = Forge,
        ["UI"] = Forge,
        ["Communication / Docs"] = Forge,
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
        // v0.3.8.122 — INFRASTRUCTURE, not HOMELAB. The sector id stays `homelab` because it is the
        // registry colony's name and an operator's saved layout is keyed on it; only the label an
        // operator reads changes. The roles in it are unchanged: this is the chamber's name, not a
        // re-placement of anything.
        [Homelab] = "INFRASTRUCTURE",
        [Micromound] = "MICROMOUND",
    };

    /// <summary>Presentation order. Micromound sits last: it is infrastructure beneath the colony.</summary>
    public static readonly IReadOnlyList<string> Order =
        [Queen, Intelligence, Forge, Validation, Memory, Output, Homelab, Micromound];

    public static string Label(string sectorId) => Labels.GetValueOrDefault(sectorId, sectorId);

    /// <summary>
    /// The sector a registry colony presents in.
    ///
    /// Never throws. The map is TOTAL over the registry and a guard proves it, so for every role
    /// the colony actually has this is a lookup and the fallback is dead code — which is the only
    /// condition under which a fallback may point at a real sector rather than a neutral one. A new
    /// colony value fails the guard; it does not quietly land somewhere plausible.
    /// </summary>
    public static string ForColony(string? colony) =>
        string.IsNullOrWhiteSpace(colony) ? Fallback : ByColony.GetValueOrDefault(colony, Fallback);

    /// <summary>Every colony this map places, for the guard that checks it against the registry.</summary>
    public static IReadOnlyCollection<string> MappedColonies => ByColony.Keys;
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
    /// <param name="trailFor">
    /// Optional lookup for a pheromone trail by key. Passed in rather than reached for, because this
    /// projection is over the REGISTRY and must stay callable — by tests and by the guard that reads
    /// the roster — without a database behind it. Absent lookup simply means no trails.
    /// </param>
    public static IReadOnlyList<ColonySector> Sectors(
        Func<string, Anthill.Core.Pheromones.TrailView?>? trailFor = null)
    {
        var residents = new Dictionary<string, List<ColonyResident>>(StringComparer.Ordinal);
        foreach (var sector in ColonySectors.Order) residents[sector] = [];

        var colonies = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var sector in ColonySectors.Order) colonies[sector] = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var role in AntRegistry.Roles)
        {
            var sector = ColonySectors.ForColony(role.Colony);

            ColonyTrail? trail = null;
            if (trailFor is not null && role.Workers.Count > 0)
            {
                double sum = 0; int ok = 0, bad = 0, seen = 0;
                foreach (var w in role.Workers)
                {
                    var view = trailFor(Anthill.Core.Pheromones.TrailGuidedSelection.TrailKeyFor(w));
                    if (view is null) continue;
                    sum += view.Strength; ok += view.SuccessCount; bad += view.FailureCount; seen++;
                }
                // Null, not zero, when nothing has run: "no reputation recorded" and "a reputation
                // of nothing" are different claims and only one of them is true here.
                if (seen > 0) trail = new ColonyTrail(sum / seen, ok, bad, seen);
            }

            residents[sector].Add(new ColonyResident(
                role.RoleId,
                role.DisplayName,
                role.Colony,
                role.Enabled,
                role.Executable,
                [.. role.Workers.Select(w => new ColonyWorker(w.WorkerId, w.DisplayName, w.ParentRoleId, w.Enabled))],
                trail));

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
