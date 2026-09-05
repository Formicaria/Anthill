namespace Anthill.Modules.Micromound;

/// <summary>What a capability IS, for a human. One row of the presentation catalog.</summary>
/// <param name="Id">The canonical capability id. Never displayed as the primary label, never lost.</param>
/// <param name="Label">What an operator reads: "Temperature", "Position control".</param>
/// <param name="Kind">sensor | actuator | observation — what the operator DOES with it.</param>
/// <param name="Unit">The unit its readings or targets are in, or empty when it has none.</param>
/// <param name="Verifiable">
/// True when an action of this kind can be confirmed by a separate sensor. Drives whether the
/// authoring UI offers a "how do we confirm it moved?" question at all — asking it for a capability
/// nothing can witness produces a promise the evidence policy cannot keep.
/// </param>
public sealed record CapabilityPresentation(
    string Id, string Label, string Kind, string Unit = "", bool Verifiable = false)
{
    public static class Kinds
    {
        /// <summary>Reads a number about the world. Assigned to Scout.</summary>
        public const string Sensor = "sensor";
        /// <summary>Changes the world. Assigned to Forager, and the only kind that needs limits.</summary>
        public const string Actuator = "actuator";
        /// <summary>Captures something richer than a number — an image, a recording.</summary>
        public const string Observation = "observation";
        /// <summary>A named procedure the device runs. Delegation, not a primitive.</summary>
        public const string Routine = "routine";
    }
}

/// <summary>
/// THE ONE PLACE A CAPABILITY ID BECOMES A HUMAN SENTENCE. v0.3.8.123.
///
/// WHY THIS HAS TO EXIST AT ALL, AND WHY IT CANNOT BE FETCHED. The device's own capability registry
/// is rich — class, parameter ranges, units, availability — and it lives ONLY in the device
/// firmware. `docs/CAPABILITIES.md` in the micromound repository is explicit that it never crosses
/// the wire. What Anthill actually receives, in all three places it ever sees a capability, is a
/// bare id string: what the device reported at enrolment, what an operator wrote into a manifest,
/// and what a charter granted. So "use the existing catalog" was not available — there is no
/// catalog, on either side of the boundary, that Anthill can read.
///
/// That leaves two honest options and one dishonest one. The dishonest one is to invent rich
/// metadata and present it as the device's. The two honest ones are a table authored HERE, and
/// inference from the namespace convention the protocol does enforce (`sense.` / `act.` /
/// `routine.`). This is both, in that order: a known id gets a real label, and an unknown one gets
/// a derived one that says what it can prove and no more.
///
/// IT IS PRESENTATION AND NOTHING ELSE. Nothing here grants, restricts, validates or reaches a
/// device. Deleting this file would make the console unreadable and change no authority whatsoever.
/// That is the property that makes a hand-authored table acceptable where a second store of a
/// SECURITY fact would not be: being wrong here produces a bad label, never a bad grant.
///
/// ONE TABLE, NOT A DICTIONARY SCATTERED THROUGH THE UI. The brief asks for exactly this and the
/// reason is the console's own history — the role→sector map lived in the browser once, drifted
/// from the registry, and filed every new role under the Queen. A presentation map in one C# table,
/// served over one route, cannot drift from itself.
/// </summary>
public static class MicromoundCapabilityCatalog
{
    private static readonly IReadOnlyList<CapabilityPresentation> Known =
    [
        // ---- sensing: reads a number about the world ------------------------------------------
        new("sense.temperature",   "Temperature",        CapabilityPresentation.Kinds.Sensor, "°C"),
        new("sense.humidity",      "Humidity",           CapabilityPresentation.Kinds.Sensor, "%"),
        new("sense.pressure",      "Pressure",           CapabilityPresentation.Kinds.Sensor, "kPa"),
        new("sense.position",      "Position",           CapabilityPresentation.Kinds.Sensor, "°"),
        new("sense.distance",      "Distance",           CapabilityPresentation.Kinds.Sensor, "mm"),
        new("sense.motion",        "Motion",             CapabilityPresentation.Kinds.Sensor),
        new("sense.contact",       "Contact / switch",   CapabilityPresentation.Kinds.Sensor),
        new("sense.flow",          "Flow",               CapabilityPresentation.Kinds.Sensor, "L/min"),
        new("sense.level",         "Level",              CapabilityPresentation.Kinds.Sensor, "%"),
        new("sense.soil_moisture", "Soil moisture",      CapabilityPresentation.Kinds.Sensor, "%"),
        new("sense.voltage",       "Voltage",            CapabilityPresentation.Kinds.Sensor, "V"),
        new("sense.current",       "Current",            CapabilityPresentation.Kinds.Sensor, "A"),
        new("sense.power",         "Power",              CapabilityPresentation.Kinds.Sensor, "W"),

        // ---- observation: richer than a number -------------------------------------------------
        new("observe.image",       "Camera / images",    CapabilityPresentation.Kinds.Observation),
        new("observe.audio",       "Microphone / audio", CapabilityPresentation.Kinds.Observation),

        // ---- action: changes the world. Every one of these wants limits. -----------------------
        new("act.position",  "Position control",  CapabilityPresentation.Kinds.Actuator, "°",  Verifiable: true),
        new("act.velocity",  "Speed control",     CapabilityPresentation.Kinds.Actuator, "°/s", Verifiable: true),
        new("act.output",    "On / off control",  CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.light",     "Light",             CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.valve",     "Valve",             CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.pump",      "Pump",              CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.heater",    "Heater",            CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.fan",       "Fan",               CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
        new("act.lock",      "Lock",              CapabilityPresentation.Kinds.Actuator, "",   Verifiable: true),
    ];

    private static readonly Dictionary<string, CapabilityPresentation> ById =
        Known.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every row an operator could be shown. Served to the console as one payload.</summary>
    public static IReadOnlyList<CapabilityPresentation> All => Known;

    /// <summary>
    /// How to present one capability id — known or not.
    ///
    /// AN UNKNOWN ID IS NOT AN ERROR AND MUST NOT BE HIDDEN. A device is free to report capabilities
    /// this table has never heard of, and the operator still has to be able to assign and limit
    /// them. So an unknown id gets a label derived from the part of it the protocol guarantees: the
    /// namespace says what kind of thing it is, and the remainder, de-underscored, is the best name
    /// available. `act.water_valve` reads as "Water valve" and is correctly typed as an actuator,
    /// with no claim made about its unit — which is exactly what is known about it.
    /// </summary>
    public static CapabilityPresentation For(string? capabilityId)
    {
        var id = (capabilityId ?? "").Trim();
        if (id.Length == 0) return new CapabilityPresentation("", "(none)", CapabilityPresentation.Kinds.Sensor);
        if (ById.TryGetValue(id, out var known)) return known;

        var dot = id.IndexOf('.');
        var ns = dot > 0 ? id[..dot] : "";
        var rest = dot > 0 && dot + 1 < id.Length ? id[(dot + 1)..] : id;

        var kind = ns switch
        {
            "act" => CapabilityPresentation.Kinds.Actuator,
            "routine" => CapabilityPresentation.Kinds.Routine,
            "observe" => CapabilityPresentation.Kinds.Observation,
            _ => CapabilityPresentation.Kinds.Sensor,
        };

        var words = rest.Replace('_', ' ').Replace('-', ' ').Trim();
        var label = words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];

        // Verifiable is FALSE for an unknown action, deliberately. Offering to confirm an action
        // this catalog knows nothing about would invite an evidence requirement the operator cannot
        // reason about — and an unverifiable action presented as verified is the one outcome the
        // evidence policy exists to prevent.
        return new CapabilityPresentation(id, label, kind);
    }

    /// <summary>True when this id names something that changes the world, so it needs limits.</summary>
    public static bool IsAction(string? capabilityId) =>
        For(capabilityId).Kind == CapabilityPresentation.Kinds.Actuator;

    /// <summary>
    /// Which of the seven mound ants should hold this capability, by default.
    ///
    /// Sensing and observation go to the Scout, action to the Forager. That is ANTS.md's own
    /// division and not a choice made here; an operator may reassign, and the friendly model records
    /// the reassignment rather than this function being consulted again.
    /// </summary>
    public static string DefaultAnt(string? capabilityId) =>
        IsAction(capabilityId) ? MicromoundRoster.Forager : MicromoundRoster.Scout;
}
