using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE FRIENDLY FORM AND THE CONTRACT ARE THE SAME MOUND. v0.3.8.123.
///
/// `MicromoundAuthoring` exists because the console asked an operator to type a charter: capability
/// ids, an action-class enum, a lease TTL in seconds, a `device_limits` map keyed by capability, an
/// evidence policy expressed as globs. It is a TRANSLATION and not a second set of rules, and that
/// claim is exactly what these tests are for — they check that plain answers land in the right
/// fields of the right document, and, just as importantly, that the friendly layer cannot say
/// anything the protocol would not have allowed.
///
/// Three properties get the most attention here, because they are the three ways a simplifying
/// layer usually goes wrong:
///
///   1. It quietly RELAXES something. The evidence policy can only ever gain patterns here, never
///      lose them, and a hazardous ceiling cannot be spelled at all.
///   2. It quietly DELETES something. A manifest and a charter are complete replacements, so a save
///      from the simple page writes the whole document — and everything the simple page cannot
///      author has to survive the round trip untouched.
///   3. It quietly ACCEPTS something the device will refuse. A form built from what the mound
///      reported cannot ask for what it does not have, and the compile says so in the operator's
///      words rather than after a round trip.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class AuthoringTests
{
    private static MoundRecord Mound(params string[] capabilities) => new()
    {
        MoundId = "mm-greenhouse",
        Name = "Greenhouse",
        PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
        Capabilities = [.. capabilities.Length > 0
            ? capabilities
            : new[] { "sense.soil_moisture", "sense.temperature", "act.valve", "act.pump" }],
    };

    private static FriendlyMoundConfiguration Form(
        IReadOnlyList<FriendlyDevice>? devices = null,
        string level = ActionLevels.Physical,
        string control = ControlModes.AskFirst,
        AdvancedCarry? advanced = null) =>
        new("mm-greenhouse",
            Purpose: "greenhouse bench 2",
            Devices: devices ?? [new FriendlyDevice("sense.soil_moisture", AssignedAnt: MicromoundRoster.Scout)],
            Routines: [],
            ControlMode: control,
            ActionLevel: level,
            CheckInMinutes: 20,
            AuthorityDays: 14,
            ProofIntervalSeconds: 90,
            Advanced: advanced);

    // ---- What an operator says becomes what the documents carry --------------------------------

    /// <summary>
    /// The three plain answers — how far it may go, who decides, how often it checks in — land in
    /// three different places, and only one of them is in either document. That spread is the whole
    /// reason this layer exists: an operator answering "on its own, within the limits below" is not
    /// thinking about `AutonomyPolicy`, and should not have to know it is a field on the record
    /// rather than on the charter.
    /// </summary>
    [Fact]
    public void PlainAnswers_LandInTheCharter_TheManifest_AndThePolicy()
    {
        var plan = MicromoundAuthoring.Compile(
            Form(level: ActionLevels.Physical, control: ControlModes.WithinLimits), Mound());

        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));
        Assert.Equal("controlled", plan.Charter!.ActionCeiling);
        Assert.Equal(AutonomyPolicy.WithinCharter, plan.Autonomy);
        Assert.Equal(TimeSpan.FromMinutes(20), plan.Charter.LeaseTtl);
        Assert.Equal(TimeSpan.FromDays(14), plan.Charter.Duration);
        Assert.Equal(90, plan.Charter.EvidenceMinIntervalSeconds);
    }

    /// <summary>
    /// "How far may this move?" is asked ONCE and written to the manifest's `device_limits`, never
    /// to the charter's `limits`. Both would have been legal and writing both would have been one
    /// fact in two stores — defect class 5b — with a projection that has two values to disagree
    /// about. The manifest wins because `device_limits` is the operator's own STANDING bound, the
    /// middle tier of SAFETY.md Layer 1's intersection; a bound that expires with the authority that
    /// mentioned it is a bound on one errand, not on the device.
    /// </summary>
    [Fact]
    public void ASafeRange_IsTheMoundsOwnStandingLimit_AndNotOneErrandsLimit()
    {
        var plan = MicromoundAuthoring.Compile(Form([
            new FriendlyDevice("act.valve", SafeMin: 0, SafeMax: 1, MaxRunSeconds: 30,
                MinRestSeconds: 600, MaxTimesPerHour: 4),
        ]), Mound());

        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));

        Assert.True(plan.Configuration!.DeviceLimits!.TryGetValue("act.valve", out var limits));
        Assert.Equal(0, limits!.Min);
        Assert.Equal(1, limits.Max);
        Assert.Equal(30, limits.MaxOnSeconds);
        Assert.Equal(600, limits.MinOffSeconds);
        Assert.Equal(4, limits.MaxRatePerHour);

        // And nowhere else. A charter carrying the same numbers would be the second store.
        Assert.Null(plan.Charter!.Limits);
    }

    /// <summary>
    /// Naming a witness adds a proof requirement; it can never take one away. `RequiredFor` starts
    /// at the protocol's own `act.*` / `routine.*` baseline and the friendly answers are a UNION on
    /// top of it, because a page that can quietly relax a proof requirement is a page that makes a
    /// device less safe — the one thing this layer must not be able to do.
    /// </summary>
    [Fact]
    public void AWitness_AddsAProofRequirement_AndTheBaselineIsNeverNarrowed()
    {
        var plan = MicromoundAuthoring.Compile(Form([
            new FriendlyDevice("sense.soil_moisture"),
            new FriendlyDevice("act.valve", VerifiedBy: "sense.soil_moisture"),
        ]), Mound());

        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));
        var required = plan.Charter!.EvidenceRequiredFor!;
        Assert.Contains("act.*", required);
        Assert.Contains("routine.*", required);
        Assert.Contains("act.valve", required);
    }

    /// <summary>
    /// A witness the mound was never given is a promise the evidence policy cannot keep, so it is a
    /// refusal rather than a silently dropped field — and the refusal names the capability in the
    /// words the operator was shown, not the id they never typed.
    /// </summary>
    [Fact]
    public void AWitnessTheMoundDoesNotHave_IsRefused_InTheOperatorsOwnWords()
    {
        var plan = MicromoundAuthoring.Compile(Form([
            new FriendlyDevice("act.valve", VerifiedBy: "sense.temperature"),
        ]), Mound());

        Assert.False(plan.Ok);
        Assert.Contains(plan.Refusals, r => r.Contains("Valve") && r.Contains("sense.temperature"));
    }

    /// <summary>
    /// `hazardous` is a real ActionClass and there is no friendly word for it, deliberately.
    /// `MicromoundCharters` refuses it as a standing ceiling; this is the earlier and quieter half
    /// of the same rule, and the better half, because an operator never meets an option they would
    /// then be told they cannot have. The check is on the VOCABULARY rather than on one mapping:
    /// a fourth level added tomorrow would fail here.
    /// </summary>
    [Fact]
    public void NoFriendlyAnswer_CanEverAskForAHazardousCeiling()
    {
        Assert.All(ActionLevels.All, level => Assert.NotEqual("hazardous", ActionLevels.ToCeiling(level)));
        Assert.Equal("observe", ActionLevels.ToCeiling("hazardous"));
        Assert.Equal("observe", ActionLevels.ToCeiling("something nobody has defined"));
        Assert.Equal(AutonomyPolicy.ManualOnly, ControlModes.ToPolicy("nonsense"));
    }

    /// <summary>
    /// Nothing may be granted that the device never reported. `MicromoundCharters` refuses this too
    /// and the mound refuses it a third time; duplicating it here is not defect class 5 for the
    /// reason the charter service already gives — it turns a round trip and an audited device-side
    /// refusal into an answer beside the field that caused it.
    /// </summary>
    [Fact]
    public void ACapabilityTheDeviceNeverReported_IsRefusedBeforeAnythingIsWritten()
    {
        var plan = MicromoundAuthoring.Compile(
            Form([new FriendlyDevice("act.laser")]), Mound("sense.temperature"));

        Assert.False(plan.Ok);
        Assert.Contains(plan.Refusals, r => r.Contains("act.laser"));
        Assert.Null(plan.Configuration);
        Assert.Null(plan.Charter);
    }

    /// <summary>
    /// A run time on a thermometer is not a narrower bound — it is a bound on something that never
    /// acts. `ManifestValidator` would accept it and the device would never consult it, which makes
    /// it a setting that reaches nobody: defect class 2, arrived at through a form field.
    /// </summary>
    [Fact]
    public void ALimitOnSomethingThatOnlyReads_IsRefusedRatherThanStored()
    {
        var plan = MicromoundAuthoring.Compile(
            Form([new FriendlyDevice("sense.temperature", MaxRunSeconds: 30)]), Mound());

        Assert.False(plan.Ok);
        Assert.Contains(plan.Refusals, r => r.Contains("Temperature"));
    }

    /// <summary>
    /// Two devices on one capability id would collide in `device_limits`, which is keyed by
    /// capability — the second set of bounds would silently replace the first. Saying so beats
    /// picking a winner.
    /// </summary>
    [Fact]
    public void TwoDevicesOnOneCapability_IsRefused_BecauseTheirLimitsWouldCollide()
    {
        var plan = MicromoundAuthoring.Compile(Form([
            new FriendlyDevice("act.valve", Device: "north", MaxRunSeconds: 10),
            new FriendlyDevice("act.valve", Device: "south", MaxRunSeconds: 90),
        ]), Mound());

        Assert.False(plan.Ok);
        Assert.Contains(plan.Refusals, r => r.Contains("two devices"));
    }

    /// <summary>
    /// Granting something the ceiling forbids is LEGAL and is almost certainly not what was meant,
    /// so it warns and does not block. A refusal here would be this layer overruling a staging
    /// decision an operator is entitled to make — configure the hardware now, raise the ceiling when
    /// the plumbing is finished — and the friendly layer does not get to have opinions the protocol
    /// does not have.
    /// </summary>
    [Fact]
    public void ActuatorsUnderAWatchOnlyCeiling_AreAWarningAndNotARefusal()
    {
        var plan = MicromoundAuthoring.Compile(
            Form([new FriendlyDevice("act.valve", MaxRunSeconds: 30)], level: ActionLevels.WatchOnly),
            Mound());

        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));
        Assert.Equal("observe", plan.Charter!.ActionCeiling);
        Assert.Contains(plan.Warnings, w => w.Contains("watch only"));
    }

    /// <summary>
    /// An action nothing witnesses is a warning too. The mound will report what it did and nothing
    /// will check it, which is a real state the protocol permits and an operator should meet with
    /// their eyes open rather than discover from an evidence feed that stays empty.
    /// </summary>
    [Fact]
    public void AnActionNothingConfirms_SaysSo_WithoutRefusingIt()
    {
        var plan = MicromoundAuthoring.Compile(Form([new FriendlyDevice("act.pump")]), Mound());

        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));
        Assert.Contains(plan.Warnings, w => w.Contains("confirms"));
    }

    // ---- The round trip, which is what makes a simple page over a rich document safe ------------

    /// <summary>
    /// Compile, issue, read back: the same answers. THIS IS THE TEST THE FEATURE STANDS ON. A simple
    /// page over a complete-replacement document either refuses to open what it cannot fully express
    /// or drops the rest on save; the second looks like it worked, which is why it is worse. The
    /// projection is what makes neither necessary, and a round trip that loses an answer is the bug
    /// that would produce exactly that outcome.
    /// </summary>
    [Fact]
    public void AFormThatWasCompiled_ReadsBackAsTheSameForm()
    {
        var mound = Mound();
        var original = Form([
            new FriendlyDevice("sense.soil_moisture"),
            new FriendlyDevice("act.valve", SafeMin: 0, SafeMax: 1, MaxRunSeconds: 30,
                MinRestSeconds: 600, MaxTimesPerHour: 4, VerifiedBy: "sense.soil_moisture"),
        ], level: ActionLevels.Physical, control: ControlModes.WithinLimits);

        var plan = MicromoundAuthoring.Compile(original, mound);
        Assert.True(plan.Ok, string.Join("; ", plan.Refusals));

        var manifest = ManifestFrom(plan.Configuration!);
        var charter = CharterFrom(plan.Charter!);
        mound.AutonomyPolicy = plan.Autonomy;

        var back = MicromoundAuthoring.Project(mound, manifest, charter).Configuration;

        Assert.Equal(original.ControlMode, back.ControlMode);
        Assert.Equal(original.ActionLevel, back.ActionLevel);
        Assert.Equal(original.CheckInMinutes, back.CheckInMinutes);
        Assert.Equal(original.AuthorityDays, back.AuthorityDays);
        Assert.Equal(original.ProofIntervalSeconds, back.ProofIntervalSeconds);
        Assert.Equal(original.SafeState, back.SafeState);

        var valve = Assert.Single(back.Devices!, d => d.Capability == "act.valve");
        Assert.Equal(0, valve.SafeMin);
        Assert.Equal(1, valve.SafeMax);
        Assert.Equal(30, valve.MaxRunSeconds);
        Assert.Equal(600, valve.MinRestSeconds);
        Assert.Equal(4, valve.MaxTimesPerHour);
        Assert.Equal("sense.soil_moisture", valve.VerifiedBy);

        // Nothing the friendly form authored is reported as beyond it — the whole point of the list
        // is that it names what the page did NOT write, and here the page wrote everything.
        Assert.Empty(MicromoundAuthoring.Project(mound, manifest, charter).Unrepresented);
    }

    /// <summary>
    /// A manifest-declared worker, a reasoning mode and a grant with no device row all survive a
    /// save from the simple page, AND they are all named on screen. Carrying something silently and
    /// losing it silently are one bug apart, so the projection does both — the operator is told the
    /// advanced page still holds something rather than finding out when it is gone.
    /// </summary>
    [Fact]
    public void WhatTheSimplePageCannotAuthor_SurvivesASaveFromIt_AndIsNamed()
    {
        var mound = Mound();
        var authored = new ConfigurationRequest(
            mound.MoundId,
            [new HardwareAssignment("soil_moisture", "ads1115", new Dictionary<string, string>())],
            ["sense.soil_moisture", "act.valve"],
            ["routine.water_cycle"],
            Workers: [new WorkerDefinition { Name = "Soil Ant", Purpose = "waters when dry" }],
            DeviceLimits: null,
            ReasoningMode: ReasoningModes.Remote);

        var projection = MicromoundAuthoring.Project(mound, ManifestFrom(authored), null);

        Assert.Contains(projection.Unrepresented, u => u.Contains("Soil Ant"));
        Assert.Contains(projection.Unrepresented, u => u.Contains(ReasoningModes.Remote));
        // `act.valve` has no hardware entry, so the simple page has no row to show it on.
        Assert.Contains(projection.Unrepresented, u => u.Contains("act.valve"));

        // And now the save. Everything named above has to come out the other side.
        var again = MicromoundAuthoring.Compile(projection.Configuration, mound);

        Assert.True(again.Ok, string.Join("; ", again.Refusals));
        Assert.Contains(again.Configuration!.Workers!, w => w.Name == "Soil Ant");
        Assert.Equal(ReasoningModes.Remote, again.Configuration.ReasoningMode);
        Assert.Contains("act.valve", again.Configuration.Capabilities);
    }

    /// <summary>
    /// A mound nobody has configured opens on a form that is safe rather than empty-and-permissive:
    /// manual only, watch only, and the protocol's own safe state. Absence resolves downward here
    /// for the same reason it does everywhere else in this module — a record written before a field
    /// existed reads as the most conservative state, not the most convenient one.
    /// </summary>
    [Fact]
    public void AMoundWithNoConfiguration_OpensOnTheConservativeAnswers()
    {
        var projection = MicromoundAuthoring.Project(Mound(), null, null);
        var form = projection.Configuration;

        Assert.Equal(ControlModes.ManualOnly, form.ControlMode);
        Assert.Equal(ActionLevels.WatchOnly, form.ActionLevel);
        Assert.Equal(MicromoundAuthoring.DefaultSafeState, form.SafeState);
        Assert.Empty(form.Devices!);
        Assert.Empty(projection.Unrepresented);
    }

    // ---- The catalog: presentation, and nothing else --------------------------------------------

    /// <summary>
    /// An unknown capability is not an error and must not be hidden — a device is free to report
    /// ids this table has never heard of, and the operator still has to be able to assign and limit
    /// them. So an unknown id gets a label derived from the namespace the protocol does enforce, is
    /// typed correctly, and claims NOTHING about a unit or about being verifiable, which is exactly
    /// what is known about it.
    /// </summary>
    [Fact]
    public void AnUnknownCapability_IsStillReadable_AndClaimsNothingItCannotProve()
    {
        var unknown = MicromoundCapabilityCatalog.For("act.water_valve");

        Assert.Equal("Water valve", unknown.Label);
        Assert.Equal(CapabilityPresentation.Kinds.Actuator, unknown.Kind);
        Assert.Equal("", unknown.Unit);
        Assert.False(unknown.Verifiable);
        Assert.Equal(MicromoundRoster.Forager, MicromoundCapabilityCatalog.DefaultAnt("act.water_valve"));
        Assert.Equal(MicromoundRoster.Scout, MicromoundCapabilityCatalog.DefaultAnt("sense.anything"));
    }

    /// <summary>
    /// The catalog is presentation and nothing else: deleting it would make the console unreadable
    /// and change no authority whatsoever. This pins the property that makes a hand-authored table
    /// acceptable where a second store of a SECURITY fact would not be — every id it names is one
    /// the protocol's own namespace rules already classify the same way.
    /// </summary>
    [Fact]
    public void EveryCatalogRow_AgreesWithTheNamespaceItWouldHaveBeenInferredFrom()
    {
        Assert.All(MicromoundCapabilityCatalog.All, row =>
        {
            var namespaceKind = MicromoundCapabilityCatalog.For(row.Id + "_unlisted_variant").Kind;
            Assert.Equal(namespaceKind, row.Kind);
        });
    }

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>
    /// The manifest a <see cref="ConfigurationRequest"/> becomes, built the way
    /// <see cref="MicromoundConfiguration"/> builds it. Constructed here rather than issued through
    /// the service so the round trip tests the TRANSLATION rather than the signing, the store and
    /// the event bus — those have their own tests, and a failure in one of them should not read as
    /// a failure of this.
    /// </summary>
    private static MoundManifest ManifestFrom(ConfigurationRequest request) => new()
    {
        ManifestId = "manifest-1",
        MoundId = request.MoundId,
        IssuedAt = "2026-09-05T12:00:00Z",
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

    private static Charter CharterFrom(CharterRequest request)
    {
        var issued = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        return new Charter
        {
            CharterId = "charter-1",
            MoundId = request.MoundId,
            IssuedAt = issued.ToWire(),
            ExpiresAt = issued.Add(request.Duration).ToWire(),
            LeaseTtlSeconds = (int)request.LeaseTtl.TotalSeconds,
            ActionCeiling = request.ActionCeiling,
            Capabilities = [.. request.Capabilities],
            Routines = [.. request.Routines],
            Limits = request.Limits?.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal) ?? [],
            Evidence = new EvidencePolicy
            {
                RequiredFor = [.. request.EvidenceRequiredFor ?? []],
                MinIntervalSeconds = request.EvidenceMinIntervalSeconds,
            },
            SafeState = request.SafeState,
        };
    }
}
