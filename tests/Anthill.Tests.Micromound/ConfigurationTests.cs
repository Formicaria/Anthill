using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// CONFIGURATION IS AUTHORED HERE — CONFIGURATION.md and PROTOCOL.md §10. v0.3.8.114.
///
/// `UPSTREAM.md`: "MicroMound is headless and ships no UI. Everything an operator configures lives
/// in the controller's interface." So the manifest is built, validated, signed and queued by the
/// colony; the mound validates, stores and executes it, and has no settings page of its own.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class ConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryMoundStore Store, MicromoundConfiguration Config, RecordingEventBus Events) Colony()
    {
        var store = new InMemoryMoundStore();
        var events = new RecordingEventBus();

        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-greenhouse",
            Name = "Greenhouse",
            PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
        });

        return (store, new MicromoundConfiguration(store, new MicromoundIdentity(store), events), events);
    }

    private static ConfigurationRequest Request(
        IReadOnlyList<WorkerDefinition>? workers = null,
        IReadOnlyDictionary<string, CapabilityLimits>? deviceLimits = null,
        IReadOnlyList<string>? capabilities = null) =>
        new("mm-greenhouse",
            Hardware:
            [
                new HardwareAssignment("soil", "ads1115",
                    new Dictionary<string, string> { ["channel"] = "0" }),
                new HardwareAssignment("irrigation", "gpio_relay",
                    new Dictionary<string, string> { ["pin"] = "17", ["normally_open"] = "false" }),
            ],
            Capabilities: capabilities ?? ["sense.soil_moisture", "act.water_valve"],
            Routines: ["routine.water_cycle"],
            Workers: workers,
            DeviceLimits: deviceLimits);

    /// <summary>
    /// A manifest is signed as the controller and carries the hardware map an operator authored.
    /// Driver settings stay STRINGS — the decoder is one fixed shape and each driver parses its own.
    /// </summary>
    [Fact]
    public void AnAuthoredManifest_IsSignedAndCarriesTheHardwareMap()
    {
        var (store, config, events) = Colony();

        var issue = config.Issue(Request(), "operator", Now);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        Assert.Equal(EnvelopeKinds.Config, issue.Envelope!.Kind);

        var manifest = issue.Manifest!;
        Assert.Equal("ads1115", manifest.Hardware["soil"].Driver);
        Assert.Equal("17", manifest.Hardware["irrigation"].Settings["pin"]);
        Assert.Equal(ReasoningModes.None, manifest.Reasoning.Mode);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller,
            Convert.FromHexString(new MicromoundIdentity(store).PublicKeyHex));

        var check = new Ed25519EnvelopeVerifier(directory)
            .Verify(KeyIds.Controller, issue.Envelope.CanonicalBytes(), issue.Envelope.Signature);

        Assert.True(check.IsValid, check.Describe());
        Assert.True(events.Saw(MicromoundEvents.ConfigurationIssued));
    }

    /// <summary>It waits for the mound to collect it, like every other downlink.</summary>
    [Fact]
    public void AnAuthoredManifest_WaitsInTheDownlinkQueue()
    {
        var (store, config, _) = Colony();

        config.Issue(Request(), "operator", Now);

        var drained = store.DrainDownlink("mm-greenhouse");
        Assert.Single(drained, e => e.Kind == EnvelopeKinds.Config);
    }

    /// <summary>
    /// THE COLONY RECORDS WHAT IT AUTHORED, NOT WHAT THE MOUND ACCEPTED. The mound validates
    /// against its own drivers and may still refuse, so this is "sent" — treating command-issued as
    /// effect is the one thing §33 names outright.
    /// </summary>
    [Fact]
    public void TheRecord_SaysAuthoredRatherThanInForce()
    {
        var (store, config, _) = Colony();

        var issue = config.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-greenhouse")!;
        Assert.Equal(issue.Manifest!.ManifestId, mound.ManifestId);
        Assert.Equal(issue.Manifest.IssuedAt, mound.ConfigurationRevision);
        Assert.NotNull(store.GetManifest(issue.Manifest.ManifestId));
    }

    /// <summary>
    /// `device_limits` IS THE MIDDLE TIER, and it lives in the manifest for a reason: a bound that
    /// expired with the authority that mentioned it would be a bound on one errand rather than on
    /// the device. A charter cannot undo it.
    /// </summary>
    [Fact]
    public void DeviceLimits_TravelWithTheManifestRatherThanAnyCharter()
    {
        var (_, config, _) = Colony();

        var issue = config.Issue(
            Request(deviceLimits: new Dictionary<string, CapabilityLimits>
            {
                ["act.water_valve"] = new() { MaxOnSeconds = 20 },
            }),
            "operator", Now);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        Assert.Equal(20, issue.Manifest!.DeviceLimits["act.water_valve"].MaxOnSeconds);
    }

    /// <summary>
    /// A LIMIT ON SOMETHING UNDECLARED IS AN ERROR, NOT A NO-OP. CONFIGURATION.md: "silently
    /// ignoring it is how an operator comes to believe a bound is in force when it is not." The
    /// protocol's own validator catches this, which proves the validator runs.
    /// </summary>
    [Fact]
    public void ADeviceLimitOnAnUndeclaredCapability_IsRefused()
    {
        var (_, config, events) = Colony();

        var issue = config.Issue(
            Request(deviceLimits: new Dictionary<string, CapabilityLimits>
            {
                ["act.nonexistent"] = new() { MaxOnSeconds = 5 },
            }),
            "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("act.nonexistent", StringComparison.Ordinal));
        Assert.True(events.Saw(MicromoundEvents.ConfigurationRefused));
    }

    /// <summary>
    /// A MANIFEST MAY NOT REDEFINE ONE OF THE STANDARD SEVEN — and this is OUR check, not the
    /// protocol's.
    ///
    /// `ManifestValidator` requires worker names to be unique among themselves; it has no idea what
    /// the default roster is, because on the device those seven are the runtime rather than manifest
    /// entries. So a manifest declaring its own "Witness Ant" with a convenient ceiling passes
    /// validation there and would give a mound two things called Witness — one that confirms
    /// outcomes and one an operator invented. ANTS.md forbids changing a standard role; this is
    /// where that becomes enforceable, because it is the only place that knows both facts.
    /// </summary>
    [Fact]
    public void AManifestNamingAStandardWorker_IsRefused()
    {
        var (_, config, _) = Colony();

        var issue = config.Issue(
            Request(workers:
            [
                new WorkerDefinition
                {
                    Name = MicromoundRoster.Witness,
                    Purpose = "definitely still a witness",
                    RuntimeType = RuntimeTypes.Actuator,
                    Consumes = ["act.water_valve"],
                    ActionCeiling = "controlled",
                },
            ]),
            "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals,
            r => r.Contains("Witness Ant", StringComparison.Ordinal)
              && r.Contains("standard", StringComparison.Ordinal));
    }

    /// <summary>An optional worker that does NOT collide is exactly what the extension point is for.</summary>
    [Fact]
    public void AnOptionalWorker_IsAccepted()
    {
        var (_, config, _) = Colony();

        var issue = config.Issue(
            Request(workers:
            [
                new WorkerDefinition
                {
                    Name = "Soil Ant",
                    Purpose = "soil moisture observation and trend",
                    RuntimeType = RuntimeTypes.Sensor,
                    Consumes = ["sense.soil_moisture"],
                    ActionCeiling = "observe",
                },
            ]),
            "operator", Now);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        Assert.Single(issue.Manifest!.Workers, w => w.Name == "Soil Ant");
    }

    /// <summary>
    /// A STOP TAKES PRECEDENCE OVER CONFIGURATION — SAFETY.md names it second in the list, after
    /// missions. Delivering a new hardware map is directing the mound.
    /// </summary>
    [Fact]
    public void AStoppedMound_IsNotReconfigured()
    {
        var (store, config, _) = Colony();

        var mound = store.GetMound("mm-greenhouse")!;
        mound.Stopped = true;
        store.UpsertMound(mound);

        var issue = config.Issue(Request(), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, store.PendingDownlinkCount("mm-greenhouse"));
    }

    /// <summary>
    /// A REFUSED CONFIGURATION QUEUES NOTHING, so the mound keeps running the last manifest it
    /// accepted rather than briefly running none. Fails closed on this side too.
    /// </summary>
    [Fact]
    public void ARefusedConfiguration_LeavesThePreviousOneUndisturbed()
    {
        var (store, config, _) = Colony();

        var first = config.Issue(Request(), "operator", Now);
        store.DrainDownlink("mm-greenhouse");

        var refused = config.Issue(
            Request(deviceLimits: new Dictionary<string, CapabilityLimits>
            {
                ["act.nonexistent"] = new(),
            }),
            "operator", Now.AddMinutes(1));

        Assert.False(refused.Issued);
        Assert.Equal(0, store.PendingDownlinkCount("mm-greenhouse"));
        Assert.Equal(first.Manifest!.ManifestId, store.GetMound("mm-greenhouse")!.ManifestId);
    }

    /// <summary>An unenrolled mound has nothing to sign for.</summary>
    [Fact]
    public void AnUnenrolledMound_IsNotConfigured()
    {
        var store = new InMemoryMoundStore();
        store.UpsertMound(new MoundRecord { MoundId = "mm-new", Name = "New Micromound" });

        var config = new MicromoundConfiguration(store, new MicromoundIdentity(store), new RecordingEventBus());

        var issue = config.Issue(
            new ConfigurationRequest("mm-new", [], [], []), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("not enrolled", StringComparison.Ordinal));
    }
}
