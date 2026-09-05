using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>How much a mound may do on its own initiative, in an operator's words.</summary>
public static class ControlModes
{
    /// <summary>A person asks, every time.</summary>
    public const string ManualOnly = "manual_only";

    /// <summary>Anything may propose physical work; a person answers before it happens.</summary>
    public const string AskFirst = "ask_first";

    /// <summary>Anything may act within the limits already written — and not one step beyond them.</summary>
    public const string WithinLimits = "within_limits";

    public static readonly IReadOnlyList<string> All = [ManualOnly, AskFirst, WithinLimits];

    /// <summary>Unknown reads as the most conservative mode, never the most convenient one.</summary>
    public static AutonomyPolicy ToPolicy(string? mode) => mode switch
    {
        AskFirst => AutonomyPolicy.ApprovalRequired,
        WithinLimits => AutonomyPolicy.WithinCharter,
        _ => AutonomyPolicy.ManualOnly,
    };

    public static string FromPolicy(AutonomyPolicy policy) => policy switch
    {
        AutonomyPolicy.ApprovalRequired => AskFirst,
        AutonomyPolicy.WithinCharter => WithinLimits,
        _ => ManualOnly,
    };
}

/// <summary>
/// What kind of thing a mound is allowed to do at all, in an operator's words.
///
/// THREE LEVELS, AND THERE IS NO FOURTH. `hazardous` is never a legal standing ceiling — SAFETY.md
/// Layer 2 authorizes hazardous work per action, expiring on use — and the way that rule is kept
/// here is that the friendly vocabulary cannot spell it. <see cref="MicromoundCharters"/> refuses a
/// hazardous ceiling as well; this is the earlier, quieter half of the same rule, and it is the
/// better half because an operator never sees an option they would then be told they cannot have.
/// </summary>
public static class ActionLevels
{
    /// <summary>Read the world and report. Nothing moves.</summary>
    public const string WatchOnly = "watch_only";

    /// <summary>Small, self-reversing things: a light, a fan, a status LED.</summary>
    public const string Reversible = "reversible";

    /// <summary>Real physical action inside the limits written below it.</summary>
    public const string Physical = "physical";

    public static readonly IReadOnlyList<string> All = [WatchOnly, Reversible, Physical];

    public static string ToCeiling(string? level) => level switch
    {
        Reversible => "benign",
        Physical => "controlled",
        _ => "observe",
    };

    public static string FromCeiling(string? ceiling) => ceiling switch
    {
        "benign" => Reversible,
        "controlled" => Physical,
        // `hazardous` cannot arrive from anything this colony issued, and if one somehow did it
        // reads DOWN rather than inventing a fourth level to display it with.
        _ => WatchOnly,
    };
}

/// <summary>
/// One thing the mound is wired to, as an operator would describe it.
/// </summary>
/// <param name="Capability">The capability id this serves. The one field with no friendly form.</param>
/// <param name="Device">The logical device name on the mound, e.g. "axis0". Blank derives one.</param>
/// <param name="Driver">The driver the device is bound to. The mound validates it; we do not.</param>
/// <param name="Purpose">The operator's own note. Never reaches the device.</param>
/// <param name="AssignedAnt">Which of the mound's workers holds it. Never reaches the device.</param>
/// <param name="SafeMin">Lowest value it may be commanded to, in the capability's own unit.</param>
/// <param name="SafeMax">Highest value it may be commanded to.</param>
/// <param name="MaxRunSeconds">Longest single stretch it may stay on.</param>
/// <param name="MinRestSeconds">Shortest gap between two runs.</param>
/// <param name="MaxTimesPerHour">How many times an hour it may act.</param>
/// <param name="VerifiedBy">The capability that witnesses this one acting, or blank for none.</param>
/// <param name="Settings">Driver settings. Values are strings; each driver parses its own.</param>
public sealed record FriendlyDevice(
    string Capability,
    string Device = "",
    string Driver = "",
    string Purpose = "",
    string AssignedAnt = "",
    double? SafeMin = null,
    double? SafeMax = null,
    double? MaxRunSeconds = null,
    double? MinRestSeconds = null,
    double? MaxTimesPerHour = null,
    string VerifiedBy = "",
    IReadOnlyDictionary<string, string>? Settings = null)
{
    /// <summary>True when any of the five bounds was actually set.</summary>
    public bool HasLimits =>
        SafeMin is not null || SafeMax is not null || MaxRunSeconds is not null
        || MinRestSeconds is not null || MaxTimesPerHour is not null;

    /// <summary>The five bounds as the protocol carries them, or null when none were set.</summary>
    public CapabilityLimits? ToLimits() => HasLimits
        ? new CapabilityLimits
        {
            Min = SafeMin,
            Max = SafeMax,
            MaxOnSeconds = MaxRunSeconds,
            MinOffSeconds = MinRestSeconds,
            MaxRatePerHour = MaxTimesPerHour,
        }
        : null;
}

/// <summary>
/// The parts of a mound's configuration the friendly form cannot author but MUST NOT DELETE.
///
/// A manifest is a complete replacement and so is a charter. That means saving a friendly form
/// writes the whole document, and anything the form does not know about is gone. An operator who
/// opens the simple page to rename a pump and loses a manifest-declared worker somebody added
/// through the advanced page has been robbed by a convenience feature.
///
/// So the projection reads those parts out, the form carries them untouched, and the compile writes
/// them back exactly as they came. The operator is also TOLD they exist — see
/// <see cref="MoundProjection.Unrepresented"/> — because carrying something silently and losing it
/// silently are only one bug apart.
/// </summary>
public sealed record AdvancedCarry(
    IReadOnlyList<WorkerDefinition>? Workers = null,
    string ReasoningMode = ReasoningModes.None,
    IReadOnlyList<string>? ExtraCapabilities = null,
    IReadOnlyList<string>? ExtraRoutines = null,
    IReadOnlyList<string>? ExtraEvidencePatterns = null);

/// <summary>Everything an operator answers on the simple page, and nothing else.</summary>
public sealed record FriendlyMoundConfiguration(
    string MoundId,
    string Purpose = "",
    IReadOnlyList<FriendlyDevice>? Devices = null,
    IReadOnlyList<string>? Routines = null,
    string ControlMode = ControlModes.ManualOnly,
    string ActionLevel = ActionLevels.WatchOnly,
    int CheckInMinutes = 15,
    int AuthorityDays = 7,
    int ProofIntervalSeconds = 60,
    string SafeState = MicromoundAuthoring.DefaultSafeState,
    AdvancedCarry? Advanced = null);

/// <summary>
/// What the friendly form compiles to: the two documents, the policy, and every reason it would not.
/// </summary>
/// <param name="Ok">False when <paramref name="Refusals"/> is non-empty. Nothing is issued then.</param>
/// <param name="Refusals">Every reason, never just the first — the same contract the two services keep.</param>
/// <param name="Warnings">Legal, and probably not what the operator meant. Never blocking.</param>
/// <param name="Configuration">The manifest request, or null on a refusal.</param>
/// <param name="Charter">The charter request, or null on a refusal.</param>
/// <param name="Autonomy">Who may spend the authority. Belongs to the record, not to either document.</param>
public sealed record MoundPlan(
    bool Ok,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Warnings,
    ConfigurationRequest? Configuration,
    CharterRequest? Charter,
    AutonomyPolicy Autonomy)
{
    public static MoundPlan Refused(IReadOnlyList<string> reasons) =>
        new(false, reasons, [], null, null, AutonomyPolicy.ManualOnly);
}

/// <summary>The friendly form as read back off a mound, and what could not be shown on it.</summary>
public sealed record MoundProjection(
    FriendlyMoundConfiguration Configuration,
    IReadOnlyList<string> Unrepresented);

/// <summary>
/// THE AUTHORING LAYER — plain operator choices in, the existing micromound contracts out.
/// v0.3.8.123.
///
/// WHAT THIS IS FOR. The Micromound console asked an operator to type a charter and a manifest:
/// capability id strings, an action-class enum, a lease TTL in seconds, a `device_limits` map keyed
/// by capability, an evidence policy expressed as glob patterns. Every one of those is a real part
/// of the protocol and none of them is a question a person can answer. The operator's own summary
/// of the problem was the whole brief: "i just want it user friendly and easier to understand and
/// less of a json file communicated as settings."
///
/// THIS IS A TRANSLATION, NOT A SECOND SET OF RULES, and the distinction is the load-bearing one.
/// Nothing here validates authority, grants anything, or decides what a mound may do.
/// <see cref="MicromoundCharters"/> and <see cref="MicromoundConfiguration"/> are still the only
/// issuers, `CharterValidator` and `ManifestValidator` still run over the finished documents, and
/// the mound is still the authority that can refuse both. What this adds is a vocabulary an
/// operator can hold in their head, and a compile step that turns it into exactly the documents
/// those services already take. If this file and the protocol ever disagree, the protocol is right
/// and this is a bug — the same rule the read model lives under.
///
/// FOUR DECISIONS WORTH KNOWING BEFORE CHANGING ANYTHING HERE:
///
/// **1. Limits go in the MANIFEST, not the charter.** The friendly form asks "how far may this
/// move?" exactly once, and that single answer had two plausible homes: `device_limits` in the
/// manifest, or `limits` on the charter. Writing it to both would be one fact in two stores
/// (defect class 5b) and would leave the projection with two values to disagree about. It goes to
/// the manifest because that is what the middle tier of SAFETY.md Layer 1's intersection is FOR:
/// an operator's own standing bound on the hardware, independent of whatever any later mission
/// asks for. A bound that expires with the authority that mentioned it is not a bound on the
/// device, it is a bound on one errand — and "how far may this move" is plainly the former.
///
/// **2. The evidence policy can only ever get STRICTER here.** `RequiredFor` starts at the
/// protocol's own baseline — `act.*` and `routine.*`, everything that moves — and the friendly
/// "how do we confirm it moved?" answers add explicit entries on top. There is no friendly way to
/// REMOVE a pattern, and there must not be: a simplified page that can quietly relax a proof
/// requirement is a simplified page that makes a device less safe, which is the one thing this
/// layer must never be able to do.
///
/// **3. There is no "what should it do offline?" question, deliberately.** It is the obvious
/// question and the brief expected one. It is not asked because `offline_behaviour` is a field on a
/// WORKER, the friendly form authors no workers, and the seven standard ones are the device runtime
/// rather than manifest entries — so a mound-level answer would have had nowhere to go. Asking it
/// would have produced a control that reaches nobody, which is defect class 2, dressed up as
/// helpfulness. What an operator actually controls here is <see cref="FriendlyMoundConfiguration.CheckInMinutes"/>:
/// when the lease lapses the mound enters its safe state, so "how long may it work out of contact"
/// is the same question with a real mechanism behind it.
///
/// **4. Names and notes never leave the colony.** `Purpose` and `AssignedAnt` are the operator's
/// own words. They are recorded here, shown back to them, and are not written into either document,
/// because neither document has a field for them and inventing one would push presentation across a
/// signed boundary. This is the same rule the mound chamber's labels live under, stated in the one
/// place that could have broken it.
/// </summary>
public static class MicromoundAuthoring
{
    public const string DefaultSafeState = "all_actuators_off";

    /// <summary>The evidence patterns the protocol itself starts from. Never narrowed here.</summary>
    public static readonly IReadOnlyList<string> BaselineEvidence = ["act.*", "routine.*"];

    private const int MinCheckInMinutes = 1;
    private const int MaxCheckInMinutes = 24 * 60;
    private const int MinAuthorityDays = 1;
    private const int MaxAuthorityDays = 365;

    /// <summary>
    /// Turn what an operator answered into the two documents and the one policy flag.
    ///
    /// The mound record is required, not optional: three of the refusals below can only be made
    /// against what the DEVICE reported it has, and a compile that skipped them would hand the
    /// operator a form that saves cleanly and a charter the mound throws away.
    /// </summary>
    public static MoundPlan Compile(FriendlyMoundConfiguration form, MoundRecord mound)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(mound);

        var refusals = new List<string>();
        var warnings = new List<string>();
        var devices = form.Devices ?? [];

        // ---- What the device says it physically has. A fact, never a grant. --------------------
        var present = mound.Capabilities.ToHashSet(StringComparer.Ordinal);

        var seenDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCapability = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new List<(FriendlyDevice Row, string Device)>();

        foreach (var row in devices)
        {
            var capability = row.Capability.Trim();
            if (capability.Length == 0) { refusals.Add("one entry has no capability chosen"); continue; }

            var deviceName = row.Device.Trim();
            if (deviceName.Length == 0) deviceName = DeviceNameFor(capability);

            if (!seenDevice.Add(deviceName))
                refusals.Add($"two entries are both called '{deviceName}' — a mound's device names have to be distinct");

            // Two rows on one capability id would collide in `device_limits`, which is keyed by
            // capability: the second set of bounds would silently replace the first. Saying so is
            // better than picking a winner, and the remedy is a distinct capability id per device.
            if (!seenCapability.Add(capability))
                refusals.Add($"'{PresentationOf(capability).Label}' is assigned to two devices — give one of them its own capability");

            // A mound that has reported NOTHING is not refused everything, and that is the one place
            // this check is softer than `MicromoundCharters`'. An empty capability list means the
            // device has not enrolled or has not spoken yet — it does not mean the device has no
            // hardware — so refusing here would make it impossible to prepare a configuration before
            // the thing is plugged in, which is a normal way to work. The charter issuer still
            // refuses at the moment it matters, and the console offers nothing to assign anyway.
            if (!CapabilityId.IsRoutine(capability) && present.Count > 0 && !present.Contains(capability))
                refusals.Add($"this mound never reported '{capability}', so nothing can be granted for it");

            var kind = PresentationOf(capability).Kind;
            var isAction = kind == CapabilityPresentation.Kinds.Actuator;

            // A run length or a rate on a thermometer is not a narrower bound, it is a bound on
            // something that never acts — `ManifestValidator` would take it and the device would
            // never consult it, which is a setting that reaches nobody.
            if (!isAction && (row.MaxRunSeconds is not null || row.MinRestSeconds is not null || row.MaxTimesPerHour is not null))
                refusals.Add($"'{PresentationOf(capability).Label}' only reads — a run time or a rate limit has nothing to apply to");

            if (row.SafeMin is double lo && row.SafeMax is double hi && lo > hi)
                refusals.Add($"'{PresentationOf(capability).Label}': the lowest value is above the highest");

            if (isAction && !row.HasLimits)
                warnings.Add($"'{PresentationOf(capability).Label}' can act and has no limits set — it will be bounded only by the hardware");

            assigned.Add((row with { Capability = capability, Device = deviceName }, deviceName));
        }

        // ---- Witnesses. A capability named as proof has to be one the mound actually has. -------
        var granted = assigned.Select(a => a.Row.Capability).ToHashSet(StringComparer.Ordinal);
        var explicitEvidence = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (row, _) in assigned)
        {
            var witness = row.VerifiedBy.Trim();
            if (witness.Length == 0)
            {
                if (PresentationOf(row.Capability).Verifiable)
                    warnings.Add($"nothing confirms '{PresentationOf(row.Capability).Label}' actually acted — the mound will report what it did, and nothing will check it");
                continue;
            }

            if (!granted.Contains(witness))
            {
                refusals.Add($"'{PresentationOf(row.Capability).Label}' is confirmed by '{witness}', which this configuration does not give the mound");
                continue;
            }

            // The witness makes the claim explicit rather than leaving it to the `act.*` baseline.
            // It is redundant with the baseline by construction, and that is the point: an operator
            // who later narrows the baseline through the advanced page does not silently lose the
            // proof requirement they set here.
            explicitEvidence.Add(row.Capability);
        }

        var level = ActionLevels.All.Contains(form.ActionLevel) ? form.ActionLevel : ActionLevels.WatchOnly;
        var actionsAssigned = assigned.Any(a => PresentationOf(a.Row.Capability).Kind == CapabilityPresentation.Kinds.Actuator);

        // Legal, and almost certainly not the intent: granting something the ceiling forbids
        // produces a mound that holds a capability it will be refused every time it tries to use.
        // A refusal here would be this layer overruling a staging decision an operator is entitled
        // to make, so it is said out loud and allowed.
        if (actionsAssigned && level == ActionLevels.WatchOnly)
            warnings.Add("this mound is set to watch only, so the things you gave it to do will be refused until you raise it");

        if (refusals.Count > 0) return MoundPlan.Refused(refusals);

        var advanced = form.Advanced ?? new AdvancedCarry();
        var checkIn = Math.Clamp(form.CheckInMinutes, MinCheckInMinutes, MaxCheckInMinutes);
        var days = Math.Clamp(form.AuthorityDays, MinAuthorityDays, MaxAuthorityDays);
        var safeState = string.IsNullOrWhiteSpace(form.SafeState) ? DefaultSafeState : form.SafeState.Trim();

        // Capabilities the advanced page granted with no device row of their own ride along
        // untouched. They are listed to the operator as unrepresented; they are not deleted.
        var capabilities = new List<string>(granted);
        foreach (var extra in advanced.ExtraCapabilities ?? [])
            if (!string.IsNullOrWhiteSpace(extra) && !granted.Contains(extra)) capabilities.Add(extra);

        var routines = new List<string>();
        foreach (var r in form.Routines ?? []) if (!string.IsNullOrWhiteSpace(r) && !routines.Contains(r)) routines.Add(r);
        foreach (var r in advanced.ExtraRoutines ?? []) if (!string.IsNullOrWhiteSpace(r) && !routines.Contains(r)) routines.Add(r);

        var deviceLimits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal);
        foreach (var (row, _) in assigned)
            if (row.ToLimits() is { } limits) deviceLimits[row.Capability] = limits;

        var configuration = new ConfigurationRequest(
            mound.MoundId,
            [.. assigned.Select(a => new HardwareAssignment(
                a.Device,
                string.IsNullOrWhiteSpace(a.Row.Driver) ? DriverFor(a.Row.Capability) : a.Row.Driver.Trim(),
                a.Row.Settings ?? new Dictionary<string, string>(StringComparer.Ordinal)))],
            capabilities,
            routines,
            Workers: advanced.Workers,
            DeviceLimits: deviceLimits,
            ReasoningMode: ReasoningModes.All.Contains(advanced.ReasoningMode) ? advanced.ReasoningMode : ReasoningModes.None,
            SafeState: safeState);

        // Baseline first, then the witnesses, then anything the advanced page added. Union only —
        // see decision 2 in this type's summary.
        var evidence = new List<string>(BaselineEvidence);
        foreach (var pattern in explicitEvidence) if (!evidence.Contains(pattern, StringComparer.Ordinal)) evidence.Add(pattern);
        foreach (var pattern in advanced.ExtraEvidencePatterns ?? [])
            if (!string.IsNullOrWhiteSpace(pattern) && !evidence.Contains(pattern, StringComparer.Ordinal)) evidence.Add(pattern);

        var charter = new CharterRequest(
            mound.MoundId,
            capabilities,
            routines,
            ActionLevels.ToCeiling(level),
            Duration: TimeSpan.FromDays(days),
            LeaseTtl: TimeSpan.FromMinutes(checkIn),
            Limits: null,   // decision 1: the operator's bounds are the manifest's, not one errand's
            MissionRef: "",
            SafeState: safeState,
            EvidenceRequiredFor: evidence,
            EvidenceMinIntervalSeconds: Math.Clamp(form.ProofIntervalSeconds, 1, 24 * 60 * 60));

        return new MoundPlan(true, [], warnings, configuration, charter, ControlModes.ToPolicy(form.ControlMode));
    }

    /// <summary>
    /// Read a mound back out as the friendly form, and say what the form could not show.
    ///
    /// THE UNREPRESENTED LIST IS THE HONEST HALF OF THIS FEATURE. A simple page over a rich document
    /// either refuses to open anything it cannot fully express, or it opens everything and quietly
    /// drops the rest on save. Both are bad; the second is worse because it looks like it worked.
    /// This does neither: everything is carried through <see cref="AdvancedCarry"/> so a save is
    /// lossless, AND every carried thing is named so the operator knows the advanced page still has
    /// something in it. A round trip through <see cref="Compile"/> and back returns the same form.
    /// </summary>
    public static MoundProjection Project(MoundRecord mound, MoundManifest? manifest, Charter? charter)
    {
        ArgumentNullException.ThrowIfNull(mound);

        var unrepresented = new List<string>();
        var devices = new List<FriendlyDevice>();

        var limits = manifest?.DeviceLimits ?? new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal);
        var hardware = manifest?.Hardware ?? new Dictionary<string, HardwareBinding>(StringComparer.Ordinal);
        var manifestCapabilities = manifest?.Capabilities ?? [];

        // A device row exists for every capability the manifest declares that has a hardware entry
        // to sit on. The pairing is by DERIVED NAME, which is what `Compile` writes — a manifest
        // authored through the advanced page may name its devices anything, and those capabilities
        // are reported as unrepresented rather than guessed at.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in manifestCapabilities)
        {
            var name = DeviceNameFor(capability);
            if (!hardware.TryGetValue(name, out var binding)) continue;
            used.Add(name);

            var bound = limits.GetValueOrDefault(capability);
            devices.Add(new FriendlyDevice(
                Capability: capability,
                Device: name,
                Driver: binding.Driver,
                Purpose: "",
                AssignedAnt: DefaultAntFor(capability),
                SafeMin: bound?.Min,
                SafeMax: bound?.Max,
                MaxRunSeconds: bound?.MaxOnSeconds,
                MinRestSeconds: bound?.MinOffSeconds,
                MaxTimesPerHour: bound?.MaxRatePerHour,
                VerifiedBy: "",
                Settings: binding.Settings));
        }

        var extraCapabilities = manifestCapabilities.Where(c => !hardware.ContainsKey(DeviceNameFor(c))).ToList();
        if (extraCapabilities.Count > 0)
            unrepresented.Add($"{extraCapabilities.Count} capability grant(s) with no device of their own: {string.Join(", ", extraCapabilities)}");

        var strayDevices = hardware.Keys.Where(k => !used.Contains(k)).ToList();
        if (strayDevices.Count > 0)
            unrepresented.Add($"{strayDevices.Count} device binding(s) this page cannot pair with a capability: {string.Join(", ", strayDevices)}");

        var declaredWorkers = (manifest?.Workers ?? [])
            .Where(w => !MicromoundRoster.Names.Contains(w.Name, StringComparer.Ordinal))
            .ToList();
        if (declaredWorkers.Count > 0)
            unrepresented.Add($"{declaredWorkers.Count} manifest-declared worker(s): {string.Join(", ", declaredWorkers.Select(w => w.Name))}");

        var reasoning = manifest?.Reasoning.Mode ?? ReasoningModes.None;
        if (!string.Equals(reasoning, ReasoningModes.None, StringComparison.Ordinal))
            unrepresented.Add($"reasoning mode '{reasoning}'");

        var evidencePatterns = charter?.Evidence.RequiredFor ?? [.. BaselineEvidence];
        var extraEvidence = evidencePatterns
            .Where(p => !BaselineEvidence.Contains(p, StringComparer.Ordinal) && !manifestCapabilities.Contains(p, StringComparer.Ordinal))
            .ToList();
        if (extraEvidence.Count > 0)
            unrepresented.Add($"{extraEvidence.Count} evidence pattern(s) beyond the ordinary ones: {string.Join(", ", extraEvidence)}");

        if (charter is not null && charter.Limits.Count > 0)
            unrepresented.Add($"{charter.Limits.Count} charter-scoped limit(s) — this page writes the mound's own standing limits instead");

        // A capability a witness confirms is one the charter names explicitly on top of the
        // baseline. That is exactly what `Compile` writes, so reading it back restores the answer
        // an operator gave rather than losing it to the baseline it duplicates.
        var witnessed = evidencePatterns
            .Where(p => manifestCapabilities.Contains(p, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        if (witnessed.Count > 0)
        {
            var sensors = devices
                .Where(d => PresentationOf(d.Capability).Kind != CapabilityPresentation.Kinds.Actuator)
                .Select(d => d.Capability).ToList();
            for (var i = 0; i < devices.Count; i++)
                if (witnessed.Contains(devices[i].Capability) && sensors.Count > 0)
                    devices[i] = devices[i] with { VerifiedBy = WitnessFor(devices[i].Capability, sensors) };
        }

        var charterRoutines = charter?.Routines ?? [];
        var routines = (manifest?.Routines ?? []).ToList();
        foreach (var r in charterRoutines) if (!routines.Contains(r, StringComparer.Ordinal)) routines.Add(r);

        var form = new FriendlyMoundConfiguration(
            mound.MoundId,
            Purpose: "",
            Devices: devices,
            Routines: routines,
            ControlMode: ControlModes.FromPolicy(mound.AutonomyPolicy),
            ActionLevel: ActionLevels.FromCeiling(charter?.ActionCeiling),
            CheckInMinutes: charter is { LeaseTtlSeconds: > 0 } ? Math.Max(1, charter.LeaseTtlSeconds / 60) : 15,
            AuthorityDays: DaysBetween(charter),
            ProofIntervalSeconds: charter?.Evidence.MinIntervalSeconds ?? 60,
            SafeState: manifest?.SafeState ?? charter?.SafeState ?? DefaultSafeState,
            Advanced: new AdvancedCarry(
                Workers: declaredWorkers.Count > 0 ? declaredWorkers : null,
                ReasoningMode: reasoning,
                ExtraCapabilities: extraCapabilities.Count > 0 ? extraCapabilities : null,
                ExtraRoutines: null,
                ExtraEvidencePatterns: extraEvidence.Count > 0 ? extraEvidence : null));

        return new MoundProjection(form, unrepresented);
    }

    /// <summary>
    /// The logical device name a capability gets when the operator does not name one.
    ///
    /// Derived rather than random so the projection can find its way back: `sense.temperature`
    /// becomes `temperature`, and reading the manifest again pairs the binding with the capability
    /// that produced it. A manifest authored elsewhere may use any names it likes, and those rows
    /// are reported as unrepresented rather than mismatched.
    /// </summary>
    public static string DeviceNameFor(string? capabilityId)
    {
        var id = (capabilityId ?? "").Trim();
        var dot = id.IndexOf('.');
        return dot > 0 && dot + 1 < id.Length ? id[(dot + 1)..] : id;
    }

    /// <summary>Which of the seven holds this capability unless the operator says otherwise.</summary>
    public static string DefaultAntFor(string? capabilityId) => MicromoundCapabilityCatalog.DefaultAnt(capabilityId);

    private static CapabilityPresentation PresentationOf(string? capabilityId) =>
        MicromoundCapabilityCatalog.For(capabilityId);

    /// <summary>
    /// A placeholder driver name for a capability the operator has not bound by hand.
    ///
    /// WHICH DRIVERS EXIST IS A FACT ABOUT THE FIRMWARE BUILD and this colony is not the authority
    /// on somebody else's binary — `MicromoundConfiguration` says so and deliberately runs
    /// `ManifestValidator` without a driver set for the same reason. So this names the capability's
    /// own namespace back, which is honest about being a guess, and the mound refuses loudly if it
    /// is wrong. That refusal is the correct outcome: the operator picks the real driver on the
    /// advanced page, and the simple page never pretends to know one.
    /// </summary>
    private static string DriverFor(string? capabilityId) => DeviceNameFor(capabilityId);

    private static string WitnessFor(string capability, IReadOnlyList<string> sensors)
    {
        // The pairing a witness records is which SENSOR confirms an action, and the protocol has no
        // field for it — the evidence policy only says that the action needs proof. So the exact
        // sensor is not recoverable, and the projection names the closest one by namespace rather
        // than claiming a pairing the document never carried. An operator who cares re-picks it;
        // the requirement itself survives regardless, which is the part that matters.
        var tail = DeviceNameFor(capability);
        return sensors.FirstOrDefault(s => DeviceNameFor(s).Equals(tail, StringComparison.OrdinalIgnoreCase))
            ?? sensors[0];
    }

    private static int DaysBetween(Charter? charter)
    {
        if (charter is null) return 7;
        if (!ProtocolTime.TryParse(charter.IssuedAt, out var from)) return 7;
        if (!ProtocolTime.TryParse(charter.ExpiresAt, out var to)) return 7;
        var days = (int)Math.Round((to - from).TotalDays);
        return Math.Clamp(days, MinAuthorityDays, MaxAuthorityDays);
    }
}
