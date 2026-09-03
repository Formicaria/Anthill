using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// A semantic assignment an operator made: this reported device serves this capability.
/// </summary>
/// <param name="Device">The logical device name the mound reported, e.g. "axis0".</param>
/// <param name="Driver">The driver it is bound to, as the mound reported it.</param>
/// <param name="Settings">Driver settings. Values are STRINGS — each driver parses its own.</param>
public sealed record HardwareAssignment(
    string Device,
    string Driver,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>What an operator authored for a mound, before it becomes a signed manifest.</summary>
public sealed record ConfigurationRequest(
    string MoundId,
    IReadOnlyList<HardwareAssignment> Hardware,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Routines,
    IReadOnlyList<WorkerDefinition>? Workers = null,
    IReadOnlyDictionary<string, CapabilityLimits>? DeviceLimits = null,
    string ReasoningMode = ReasoningModes.None,
    string SafeState = "all_actuators_off");

/// <summary>The outcome of authoring. A refusal names every reason, never just the first.</summary>
public sealed record ConfigurationIssue(
    bool Issued, IReadOnlyList<string> Refusals, MoundManifest? Manifest, Envelope? Envelope)
{
    public static ConfigurationIssue Refused(IReadOnlyList<string> reasons) => new(false, reasons, null, null);
}

/// <summary>
/// CONFIGURATION AUTHORING — CONFIGURATION.md, and PROTOCOL.md §10. v0.3.8.114.
///
/// `UPSTREAM.md` is unusually direct about where this belongs: "MicroMound is headless and ships no
/// UI. Everything an operator configures lives in the controller's interface." So the manifest is
/// authored HERE — hardware bindings, capability assignment, optional workers, device limits,
/// reasoning mode, safe state — signed, and delivered as a `config` envelope. The mound validates,
/// stores and executes it. It does not author it, and it has no settings page of its own.
///
/// THE MIDDLE LIMIT TIER LIVES IN THIS DOCUMENT, and that is the part worth understanding before
/// changing anything here. SAFETY.md Layer 1 intersects three tiers, innermost first:
///
///     hardware/firmware  ∩  device_limits  ∩  charter  =  effective
///
/// `device_limits` is the OPERATOR'S OWN bound, and it sits in the middle deliberately: a pump on a
/// smaller reservoir this season, a servo restricted after its mount changed. A charter cannot undo
/// it, and a later mission asking for more does not get more. That is why it belongs to the
/// manifest rather than to a charter — a bound that expires with the authority that mentioned it is
/// not a bound on the device, it is a bound on one errand.
///
/// FAILS CLOSED, ON BOTH SIDES. An invalid manifest leaves the previous one in force and the
/// refusal is reported — the mound's rule, and this colony's too: a configuration it will not sign
/// is one it does not queue, so the mound keeps running the last manifest it accepted rather than
/// briefly running none.
/// </summary>
public sealed class MicromoundConfiguration(IMoundStore store, MicromoundIdentity identity, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly MicromoundIdentity _identity = identity;
    private readonly IEventBus _events = events;

    /// <summary>
    /// Build, validate, sign and queue a manifest.
    ///
    /// The device-side validator is not available to us — it checks that every named driver exists
    /// in THIS BUILD of the firmware, which only the device can know — so `ManifestValidator` runs
    /// here without a driver set and the mound re-runs it with one. Everything else it checks is
    /// checkable from here, and is.
    /// </summary>
    public ConfigurationIssue Issue(ConfigurationRequest request, string issuedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mound = _store.GetMound(request.MoundId);
        if (mound is null) return Refuse(request.MoundId, ["no such mound"]);

        if (string.IsNullOrEmpty(mound.PublicKey))
            return Refuse(request.MoundId, ["mound is not enrolled, so nothing can be signed for it"]);

        // A stop halts mound-directed action, and delivering a new hardware map is directing it.
        // SAFETY.md gives stop precedence over "missions, configuration, routine work, autonomy,
        // backlog" — configuration is named in that list, second.
        if (MicromoundStop.AppliesTo(mound, MicromoundRuntime.Options))
            return Refuse(request.MoundId,
                ["a stop is in force; configuration is one of the things a stop takes precedence over"]);

        var manifest = new MoundManifest
        {
            ManifestId = Guid.NewGuid().ToString(),
            MoundId = request.MoundId,
            IssuedAt = now.ToWire(),
            Hardware = request.Hardware.ToDictionary(
                h => h.Device,
                h => new HardwareBinding
                {
                    Driver = h.Driver,
                    Settings = h.Settings.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal),
                },
                StringComparer.Ordinal),
            Capabilities = [.. request.Capabilities],
            Routines = [.. request.Routines],
            Workers = [.. request.Workers ?? []],
            DeviceLimits = request.DeviceLimits?.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal) ?? [],
            Reasoning = new ReasoningConfig { Mode = request.ReasoningMode },
            SafeState = request.SafeState,
        };

        // THE PROTOCOL'S OWN VALIDATOR. `knownDrivers` is deliberately omitted: which drivers exist
        // is a fact about the firmware build, and asserting it from here would be this colony
        // guessing at somebody else's binary. The mound checks it and refuses loudly if we are
        // wrong, which is the correct division — we are not the authority on its hardware.
        var validation = ManifestValidator.Validate(manifest, request.MoundId);
        if (!validation.IsValid) return Refuse(request.MoundId, validation.Errors);

        // AND A WORKER MAY NOT BE NAMED OVER ONE OF THE STANDARD SEVEN. The protocol validator
        // checks worker names are unique among themselves; it does not know the default roster,
        // because on the device those seven are the runtime rather than manifest entries. So a
        // manifest declaring its own "Witness Ant" passes validation there and would give a mound
        // two things called Witness — one that confirms outcomes and one an operator invented.
        // ANTS.md forbids changing a standard role, and this is where that becomes enforceable.
        var shadowed = manifest.Workers
            .Where(w => MicromoundRoster.Names.Contains(w.Name, StringComparer.Ordinal))
            .Select(w => w.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (shadowed.Count > 0)
            return Refuse(request.MoundId,
                [.. shadowed.Select(n =>
                    $"'{n}' is one of the standard seven workers and a manifest may not redefine it")]);

        var envelope = _identity.Sign(new Envelope
        {
            MoundId = request.MoundId,
            Kind = EnvelopeKinds.Config,
            SentAt = now.ToWire(),
            Body = System.Text.Json.JsonSerializer.SerializeToElement(manifest, ProtocolJson.Options),
        });

        _store.PutManifest(manifest);
        _store.QueueDownlink(request.MoundId, envelope);

        // The colony's record of what it AUTHORED, not of what the mound accepted. Those are
        // different facts and the distinction matters: the mound validates against its own drivers
        // and may still refuse, so this reads "sent" and the sync path is what turns it into
        // "in force". Recording acceptance here would be treating command-issued as effect.
        mound.ManifestId = manifest.ManifestId;
        mound.ConfigurationRevision = manifest.IssuedAt;
        _store.UpsertMound(mound);

        Publish(MicromoundEvents.ConfigurationIssued,
            $"Configuration issued to Micromound '{request.MoundId}'.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = request.MoundId,
                ["manifest_id"] = manifest.ManifestId,
                ["devices"] = manifest.Hardware.Count,
                ["capabilities"] = manifest.Capabilities.Count,
                ["routines"] = manifest.Routines.Count,
                ["workers"] = manifest.Workers.Count,
                ["reasoning_mode"] = manifest.Reasoning.Mode,
                ["issued_by"] = issuedBy,
            });

        return new ConfigurationIssue(true, [], manifest, envelope);
    }

    private ConfigurationIssue Refuse(string moundId, IReadOnlyList<string> reasons)
    {
        Publish(MicromoundEvents.ConfigurationRefused,
            $"Configuration refused for Micromound '{moundId}': {string.Join("; ", reasons)}",
            new Dictionary<string, object?> { ["mound_id"] = moundId, ["reasons"] = reasons });

        return ConfigurationIssue.Refused(reasons);
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }
}
