using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// PHYSICAL MISSION DISPATCH — §15 and §16. v0.3.8.114.
///
/// The convergence fact at the bottom is the one the brief asks for by name, and the reason this
/// class exists in the shape it does: a Queen-originated request and a user-originated request that
/// are otherwise identical must reach the same execution path after policy evaluation. Everything
/// above it establishes the gates that path runs through.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class MissionDispatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed record Colony(
        InMemoryMoundStore Store, MicromoundMissions Missions, MicromoundCharters Charters,
        RecordingEventBus Events, MicromoundIdentity Identity);

    /// <summary>An enrolled, chartered mound at the given policy, with a live lease.</summary>
    private static Colony Chartered(AutonomyPolicy policy = AutonomyPolicy.ManualOnly,
        string ceiling = "benign")
    {
        var store = new InMemoryMoundStore();
        var events = new RecordingEventBus();
        var identity = new MicromoundIdentity(store);
        var charters = new MicromoundCharters(store, identity, events);

        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-workshop",
            Name = "Workshop",
            PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
            Capabilities = ["sense.temperature", "act.water_valve"],
            AutonomyPolicy = policy,
        });

        charters.Issue(new CharterRequest("mm-workshop",
            ["sense.temperature", "act.water_valve"], [], ceiling,
            Duration: TimeSpan.FromHours(1), LeaseTtl: TimeSpan.FromMinutes(15)),
            "operator", Now);

        store.DrainDownlink("mm-workshop");   // the charter itself is not what these tests measure

        return new Colony(store, new MicromoundMissions(store, identity, events), charters, events, identity);
    }

    /// <summary>SENSE → ACT → SENSE → VERIFY, the default workflow ANTS.md describes.</summary>
    private static IReadOnlyList<MissionStep> Steps() =>
    [
        new() { StepId = "before", Op = MissionStepOps.Sense, Capability = "sense.temperature",
                EvidenceTag = "before" },
        new() { StepId = "water", Op = MissionStepOps.Act, Capability = "act.water_valve",
                Parameters = new Dictionary<string, double> { ["on_s"] = 10 }, EvidenceTag = "watering" },
        new() { StepId = "after", Op = MissionStepOps.Sense, Capability = "sense.temperature",
                EvidenceTag = "after" },
        new() { StepId = "confirm", Op = MissionStepOps.Verify, Capability = "sense.temperature",
                Confirms = "water" },
    ];

    private static PhysicalMissionRequest Request(
        PhysicalOrigin origin = PhysicalOrigin.User, bool approved = false) =>
        new("mm-workshop", Steps(), origin, "operator", "a test of the watering routine",
            ApprovalGranted: approved);

    // -----------------------------------------------------------------------------------------
    // The happy path
    // -----------------------------------------------------------------------------------------

    /// <summary>A dispatched mission is signed, queued, and cites the charter it runs under.</summary>
    [Fact]
    public void ADispatchedMission_IsSignedAndCitesItsCharter()
    {
        var colony = Chartered();

        var dispatch = colony.Missions.Dispatch(Request(), Now);

        Assert.True(dispatch.Dispatched, string.Join("; ", dispatch.Refusals));
        Assert.Equal(EnvelopeKinds.Mission, dispatch.Envelope!.Kind);
        Assert.Equal(colony.Store.GetMound("mm-workshop")!.CharterId, dispatch.Mission!.CharterId);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller, Convert.FromHexString(colony.Identity.PublicKeyHex));

        Assert.True(new Ed25519EnvelopeVerifier(directory)
            .Verify(KeyIds.Controller, dispatch.Envelope.CanonicalBytes(), dispatch.Envelope.Signature)
            .IsValid);

        Assert.Equal(1, colony.Store.PendingDownlinkCount("mm-workshop"));
        Assert.True(colony.Events.Saw(MicromoundEvents.MissionDispatched));
    }

    /// <summary>
    /// THE MISSION'S SAFE STATE IS THE CHARTER'S, COPIED RATHER THAN ACCEPTED. §9 allows a mission
    /// only to restate it — "two documents disagreeing about where the hardware goes when the
    /// watchdog trips is a contradiction nobody can resolve at the moment it matters."
    /// </summary>
    [Fact]
    public void TheMissionSafeState_IsTheChartersOwn()
    {
        var colony = Chartered();

        var dispatch = colony.Missions.Dispatch(Request(), Now);

        var charter = colony.Store.GetCharter(dispatch.Mission!.CharterId)!;
        Assert.Equal(charter.SafeState, dispatch.Mission.SafeState);
    }

    /// <summary>Required capabilities and evidence tags are derived from the steps, not restated.</summary>
    [Fact]
    public void TheMissionDeclares_WhatItsStepsActuallyUse()
    {
        var colony = Chartered();

        var mission = colony.Missions.Dispatch(Request(), Now).Mission!;

        Assert.Equal(["sense.temperature", "act.water_valve"], mission.RequiredCapabilities);
        Assert.Equal(["before", "watering", "after"], mission.RequiredEvidence);
    }

    // -----------------------------------------------------------------------------------------
    // The gates
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A MISSION CARRIES NO AUTHORITY OF ITS OWN (§9). No charter is not a weaker mission — it is
    /// no mission, and the mound would refuse it whole.
    /// </summary>
    [Fact]
    public void AMoundWithNoCharter_GetsNoMission()
    {
        var store = new InMemoryMoundStore();
        var events = new RecordingEventBus();
        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-workshop",
            PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
        });

        var missions = new MicromoundMissions(store, new MicromoundIdentity(store), events);

        var dispatch = missions.Dispatch(Request(), Now);

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("no authority of its own", StringComparison.Ordinal));
        Assert.True(events.Saw(MicromoundEvents.MissionRefused));
    }

    /// <summary>
    /// AN EXPIRED LEASE MEANS THE MOUND HAS STOPPED LISTENING, CORRECTLY. It is in its safe state
    /// waiting for fresh authority — renewal is not resumption, so dispatching into it would be
    /// issuing work to something that has already quiesced.
    /// </summary>
    [Fact]
    public void AnExpiredLease_GetsNoMission()
    {
        var colony = Chartered();

        var dispatch = colony.Missions.Dispatch(Request(), Now.AddMinutes(20));

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("lease has expired", StringComparison.Ordinal));
    }

    /// <summary>A stop refuses the mission, and queues nothing.</summary>
    [Fact]
    public void AStoppedMound_GetsNoMission()
    {
        var colony = Chartered();

        var mound = colony.Store.GetMound("mm-workshop")!;
        mound.Stopped = true;
        colony.Store.UpsertMound(mound);

        var dispatch = colony.Missions.Dispatch(Request(), Now);

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, colony.Store.PendingDownlinkCount("mm-workshop"));
    }

    /// <summary>
    /// A STEP OUTSIDE THE CHARTER IS REFUSED BY THE PROTOCOL'S VALIDATOR — which proves it runs.
    /// Dispatch checks no capability itself; a second implementation of that rule would be defect
    /// class 5 in the layer where being wrong moves something physical.
    /// </summary>
    [Fact]
    public void AStepOutsideTheCharter_IsRefusedByTheProtocolValidator()
    {
        var colony = Chartered();

        var dispatch = colony.Missions.Dispatch(
            new PhysicalMissionRequest("mm-workshop",
                [new MissionStep { StepId = "s", Op = MissionStepOps.Act, Capability = "act.laser" }],
                PhysicalOrigin.User, "operator"),
            Now);

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("act.laser", StringComparison.Ordinal));
        Assert.Equal(0, colony.Store.PendingDownlinkCount("mm-workshop"));
    }

    // -----------------------------------------------------------------------------------------
    // Approval
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// AN APPROVAL-REQUIRED MISSION QUEUES NOTHING WHILE IT WAITS.
    ///
    /// The mission is built and validated so an operator can see exactly what they are approving,
    /// and then discarded rather than parked: a mission sitting in a downlink queue is authority
    /// nobody has granted yet, and the mound cannot tell the difference between one waiting for a
    /// human and one that was issued.
    /// </summary>
    [Fact]
    public void AMissionAwaitingApproval_IsNotQueued()
    {
        var colony = Chartered(AutonomyPolicy.ApprovalRequired);

        var dispatch = colony.Missions.Dispatch(Request(PhysicalOrigin.Queen), Now);

        Assert.False(dispatch.Dispatched);
        Assert.True(dispatch.ApprovalRequired);
        Assert.Empty(dispatch.Refusals);
        Assert.Equal(0, colony.Store.PendingDownlinkCount("mm-workshop"));
        Assert.True(colony.Events.Saw(MicromoundEvents.MissionApprovalRequired));
    }

    /// <summary>And once the answer comes back yes, the same request goes through.</summary>
    [Fact]
    public void AnApprovedMission_IsDispatched()
    {
        var colony = Chartered(AutonomyPolicy.ApprovalRequired);

        var dispatch = colony.Missions.Dispatch(Request(PhysicalOrigin.Queen, approved: true), Now);

        Assert.True(dispatch.Dispatched, string.Join("; ", dispatch.Refusals));
        Assert.Equal(1, colony.Store.PendingDownlinkCount("mm-workshop"));
    }

    /// <summary>
    /// AN APPROVAL DOES NOT BUY A REFUSAL. §19: "Approval means Anthill is willing to issue the
    /// request" — it is not a key past the charter, the lease, or a stop. The mound's kernel is the
    /// authority regardless, and the colony must not act as though a yes changed what was granted.
    /// </summary>
    [Fact]
    public void AnApproval_DoesNotOverrideAStop()
    {
        var colony = Chartered(AutonomyPolicy.ApprovalRequired);

        var mound = colony.Store.GetMound("mm-workshop")!;
        mound.Stopped = true;
        colony.Store.UpsertMound(mound);

        var dispatch = colony.Missions.Dispatch(Request(PhysicalOrigin.Queen, approved: true), Now);

        Assert.False(dispatch.Dispatched);
        Assert.False(dispatch.ApprovalRequired);
        Assert.Contains(dispatch.Refusals, r => r.Contains("stop", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Under manual-only, the Queen is refused outright rather than asked to wait.</summary>
    [Fact]
    public void UnderManualOnly_TheQueenIsRefusedRatherThanQueuedForApproval()
    {
        var colony = Chartered(AutonomyPolicy.ManualOnly);

        var dispatch = colony.Missions.Dispatch(Request(PhysicalOrigin.Queen), Now);

        Assert.False(dispatch.Dispatched);
        Assert.False(dispatch.ApprovalRequired);
        Assert.Contains(dispatch.Refusals, r => r.Contains("manual-only", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // Provenance and convergence
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY OUTCOME RECORDS THE SAME PROVENANCE — §16. A refusal that recorded less than a dispatch
    /// would make "why did nothing happen" the harder question, and it is the one asked more often.
    /// </summary>
    [Fact]
    public void EveryOutcome_RecordsWhoAskedAndWhy()
    {
        var colony = Chartered(AutonomyPolicy.ManualOnly);

        colony.Missions.Dispatch(Request(PhysicalOrigin.User), Now);                  // dispatched
        colony.Missions.Dispatch(Request(PhysicalOrigin.Automation), Now);            // refused

        foreach (var name in new[] { MicromoundEvents.MissionDispatched, MicromoundEvents.MissionRefused })
        {
            var recorded = colony.Events.Events.Single(e => e.EventType == name);

            foreach (var key in new[] { "mound_id", "origin", "requested_by", "reason", "policy_reason" })
                Assert.True(recorded.Metadata.ContainsKey(key), $"{name} recorded no '{key}'");
        }
    }

    /// <summary>
    /// THE CONVERGENCE FACT THE BRIEF ASKS FOR BY NAME.
    ///
    /// "A Queen-originated request and user-originated request that are otherwise identical must
    /// converge on the same controller execution path after policy evaluation. There must not be a
    /// ManualMicromoundController and an AutonomousMicromoundController."
    ///
    /// Asserted on the SIGNED BODY rather than on the code path, because that is the observable
    /// consequence: if two paths existed they would eventually produce different missions, and the
    /// difference would reach a device. Mission id and expiry are excluded — a fresh id per mission
    /// is correct, not divergence.
    /// </summary>
    [Fact]
    public void AQueenMissionAndAUserMission_ProduceTheSameSignedWork()
    {
        var colony = Chartered(AutonomyPolicy.WithinCharter);

        var user = colony.Missions.Dispatch(Request(PhysicalOrigin.User), Now);
        var queen = colony.Missions.Dispatch(Request(PhysicalOrigin.Queen), Now);

        Assert.True(user.Dispatched, string.Join("; ", user.Refusals));
        Assert.True(queen.Dispatched, string.Join("; ", queen.Refusals));

        static string Comparable(Mission mission)
        {
            var copy = JsonSerializer.Deserialize<Mission>(
                JsonSerializer.Serialize(mission, ProtocolJson.Options), ProtocolJson.Options)!;
            copy.MissionId = "";
            return JsonSerializer.Serialize(copy, ProtocolJson.Options);
        }

        Assert.Equal(Comparable(user.Mission!), Comparable(queen.Mission!));

        // And both are real downlink, in the same queue, indistinguishable to the mound.
        Assert.Equal(2, colony.Store.PendingDownlinkCount("mm-workshop"));
        Assert.All(colony.Store.DrainDownlink("mm-workshop"),
            e => Assert.Equal(EnvelopeKinds.Mission, e.Kind));
    }
}
