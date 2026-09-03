using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE BEAT IS TWO-WAY — PROTOCOL.md §1, §2, §5, §6, §7. v0.3.8.114.
///
/// M1's beat verified an uplink and answered with nothing signed. These are the facts that had no
/// test because there was nothing to test: the ack that lets a device release its records, the lease
/// renewal that keeps a chartered mound out of `safe_state`, and the queue that only actually
/// delivers on an acknowledged beat.
///
/// Every one of them is asserted against the REAL device simulator, so the bytes, signatures and
/// chain come from the implementation that will be on the wire — and the downlink is verified with
/// the same verifier a mound uses, because an envelope this colony believes it signed and a mound
/// refuses is the failure mode that looks like a broken key rather than a broken controller.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class SyncBeatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

    private sealed record Harness(Colony Colony, SimMound Device)
    {
        public InMemoryMoundStore Store => Colony.Store;
        public RecordingEventBus Bus => Colony.Bus;
        public MicromoundSync Sync => Colony.Sync;

        /// <summary>Everything the device can physically do, as it reported at enrolment.</summary>
        public static readonly string[] Capabilities = ["sense.soil_moisture", "act.water_valve"];
    }

    private static Harness Enrolled(string moundId = "mm-greenhouse")
    {
        var colony = Colony.Build();
        var enrollment = new MicromoundEnrollment(colony.Store, colony.Bus);
        var device = new SimMound(moundId);

        var minted = enrollment.MintToken(moundId, "Greenhouse", MoundTiers.EdgeQueen, "tyler", Now);
        var result = enrollment.Enroll(new EnrollmentRequest(
            moundId, minted.Token, Convert.ToHexStringLower(device.PublicKey), MoundTiers.EdgeQueen,
            "raspberry-pi-5", Harness.Capabilities, ProtocolVersion.Current), Now);

        Assert.True(result.Accepted, result.Reason);
        return new Harness(colony, device);
    }

    private static IReadOnlyList<Envelope> Beat(SimMound device, DateTimeOffset at, string state = "chartered")
    {
        device.EnqueueUplink(EnvelopeKinds.MoundSync, new { state }, at);
        return device.DrainUplink();
    }

    /// <summary>The colony's own signed charter, so the mound has a lease to renew.</summary>
    private static Charter Charter(Harness h, DateTimeOffset at, TimeSpan? leaseTtl = null)
    {
        var issue = h.Colony.Charters.Issue(new CharterRequest(
            h.Device.MoundId, ["sense.soil_moisture", "act.water_valve"], [], "benign",
            Duration: TimeSpan.FromHours(4), LeaseTtl: leaseTtl ?? TimeSpan.FromMinutes(15)),
            "tyler", at);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        return issue.Charter!;
    }

    // ---- The ack ------------------------------------------------------------------------------

    /// <summary>
    /// AN ACCEPTED BEAT IS ACKNOWLEDGED, AND THE ACK IS THE POINT. PROTOCOL.md §6's retention rule
    /// is written in terms of exactly this message — "until an ack covers a sequence number, the
    /// uplink queue must retain the envelope and the evidence store must retain the proof." A
    /// controller that never sends one is a controller whose whole fleet fills its storage.
    /// </summary>
    [Fact]
    public void AnAcceptedBeat_IsAcknowledgedThroughItsSequence()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));

        var ack = Assert.Single(outcome.Downlink);
        Assert.Equal(EnvelopeKinds.Ack, ack.Kind);

        var body = JsonSerializer.Deserialize<AckBody>(
            ack.Body.GetRawText(), ProtocolJson.Options)!;

        Assert.Equal(AckStatuses.Ok, body.Status);
        Assert.Equal(outcome.AcceptedThroughSeq, body.ThroughSeq);
    }

    /// <summary>
    /// AND THE MOUND CAN VERIFY IT. The device holds `KeyIds.Controller` from enrolment and drops
    /// anything that does not check out against it, silently as far as the colony is concerned —
    /// so an ack we sign and it refuses is indistinguishable, from here, from an ack we never sent.
    /// </summary>
    [Fact]
    public void TheDownlink_VerifiesUnderTheControllerKeyTheDeviceHolds()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller, Convert.FromHexString(h.Colony.Identity.PublicKeyHex));
        var verifier = new Ed25519EnvelopeVerifier(directory);

        foreach (var envelope in outcome.Downlink)
            Assert.True(
                EnvelopeValidator.Validate(envelope, verifier, KeyIds.Controller).IsValid,
                $"a {envelope.Kind} envelope the colony signed does not verify as the mound would check it");
    }

    /// <summary>
    /// A REFUSED BEAT IS NOT ACKNOWLEDGED. An ack tells the device it may discard what it sent, and
    /// a batch the colony refused is a batch the colony does not hold — so acknowledging it would
    /// destroy the only remaining copy of records nobody accepted.
    /// </summary>
    [Fact]
    public void ARefusedBeat_CarriesNoAck()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beat(h.Device, Now).ToList();
        batch[0].Signature = "ed25519:" + new string('0', 128);

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, batch, Now);

        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.Downlink);
    }

    /// <summary>
    /// AN ACK NAMES ONLY EVIDENCE THE COLONY ACTUALLY STORED. Naming an id is telling the device it
    /// is safe to evict under pressure (§6), so an item refused on the way in must not appear —
    /// that would be the colony authorising the deletion of proof it does not have.
    /// </summary>
    [Fact]
    public void TheAck_NamesTheEvidenceItHolds_AndNotWhatItRefused()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new EvidenceBundle
        {
            BundleId = "bundle-1",
            Items =
            [
                new EvidenceItem { EvidenceId = "ev-1", Type = "reading", CapturedAt = Now.AddSeconds(-30).ToWire() },
                new EvidenceItem { EvidenceId = "", Type = "reading", CapturedAt = Now.AddSeconds(-30).ToWire() },
            ],
        }, Now);

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));

        var ack = JsonSerializer.Deserialize<AckBody>(
            outcome.Downlink.Single(e => e.Kind == EnvelopeKinds.Ack).Body.GetRawText(),
            ProtocolJson.Options)!;

        Assert.Equal(["ev-1"], ack.EvidenceIds);
    }

    // ---- The lease ----------------------------------------------------------------------------

    /// <summary>
    /// AN ACKNOWLEDGED BEAT RENEWS THE LEASE, AND IS THE ONLY THING THAT DOES (§5). Without this a
    /// perfectly healthy fleet quiesces on schedule while every console shows it beating.
    /// </summary>
    [Fact]
    public void AnAcknowledgedBeat_RenewsTheLease()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now, leaseTtl: TimeSpan.FromMinutes(15));

        var before = h.Store.GetMound(h.Device.MoundId)!.LeaseExpiresAt;

        var later = Now.AddMinutes(5);
        Assert.True(h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, later), later).Accepted);

        var after = h.Store.GetMound(h.Device.MoundId)!.LeaseExpiresAt;

        Assert.NotEqual(before, after);
        Assert.True(string.CompareOrdinal(after, before) > 0, "the lease moved backwards");
    }

    /// <summary>
    /// A LAPSED LEASE IS NOT RENEWED BY BEATING. §5: at expiry the mound enters `safe_state` and
    /// "fresh authority must be issued to resume — resumption is never implicit." A colony that
    /// renewed here would resume a quiesced mound by arithmetic, with nobody deciding to.
    /// </summary>
    [Fact]
    public void ALapsedLease_IsNotResumedByTheNextBeat()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now, leaseTtl: TimeSpan.FromMinutes(15));

        var lapsed = h.Store.GetMound(h.Device.MoundId)!.LeaseExpiresAt;

        var afterExpiry = Now.AddMinutes(30);
        Assert.True(h.Sync.AcceptUplink(
            h.Device.MoundId, Beat(h.Device, afterExpiry, "quiesced"), afterExpiry).Accepted);

        Assert.Equal(lapsed, h.Store.GetMound(h.Device.MoundId)!.LeaseExpiresAt);
    }

    /// <summary>
    /// AND THE COLONY SAYS SO IN THE VOCABULARY, once. Quiesced is not offline: the mound is there
    /// and holds no authority, so the remedy is a charter rather than a network cable.
    /// </summary>
    [Fact]
    public void AMoundReportingQuiesced_IsRecordedAsQuiescedAndAnnouncedOnce()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var at = Now.AddMinutes(1);
        h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, at, "quiesced"), at);

        Assert.True(h.Store.GetMound(h.Device.MoundId)!.Quiesced);
        Assert.True(h.Bus.Saw(MicromoundEvents.MoundQuiesced));

        var again = at.AddMinutes(1);
        var before = h.Bus.Events.Count(e => e.EventType == MicromoundEvents.MoundQuiesced);
        h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, again, "quiesced"), again);

        Assert.Equal(before, h.Bus.Events.Count(e => e.EventType == MicromoundEvents.MoundQuiesced));
    }

    // ---- The downlink queue -------------------------------------------------------------------

    /// <summary>
    /// A QUEUED CHARTER IS DELIVERED ON THE NEXT BEAT AND NOT BEFORE. The colony never dials a
    /// mound (§1), so "issued" and "delivered" are different facts — and issuing without this test
    /// would be signing envelopes into a queue nothing empties.
    /// </summary>
    [Fact]
    public void AQueuedCharter_TravelsOnTheNextBeat()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        var charter = Charter(h, Now);

        Assert.Equal(1, h.Store.PendingDownlinkCount(h.Device.MoundId));

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now);

        Assert.Equal(
            [EnvelopeKinds.Charter, EnvelopeKinds.Ack],
            MicromoundSync.DownlinkKindsFor(outcome));

        var delivered = JsonSerializer.Deserialize<Charter>(
            outcome.Downlink[0].Body.GetRawText(), ProtocolJson.Options)!;
        Assert.Equal(charter.CharterId, delivered.CharterId);

        // Drained on acknowledgement, and therefore gone: a second beat collects nothing.
        Assert.Equal(0, h.Store.PendingDownlinkCount(h.Device.MoundId));
    }

    /// <summary>
    /// A REFUSED BEAT DOES NOT DRAIN THE QUEUE. The queue empties on acknowledgement, and an
    /// envelope handed to a device whose batch was refused is an envelope nobody holds.
    /// </summary>
    [Fact]
    public void ARefusedBeat_LeavesTheQueueWhereItWas()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);

        var batch = Beat(h.Device, Now).ToList();
        batch[0].Signature = "";

        h.Sync.AcceptUplink(h.Device.MoundId, batch, Now);

        Assert.Equal(1, h.Store.PendingDownlinkCount(h.Device.MoundId));
    }

    /// <summary>
    /// A STOP PRECEDES THE QUEUE AND THE QUEUE DOES NOT SURVIVE IT — §7, both halves. "Clearing a
    /// stop restores nothing … the authority in force before the stop is not reinstated", and a
    /// charter queued before the stop and delivered after the resume reinstates exactly that.
    /// </summary>
    [Fact]
    public void AStop_ReplacesTheQueueRatherThanWaitingBehindIt()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);

        var mound = h.Store.GetMound(h.Device.MoundId)!;
        mound.Stopped = true;
        h.Store.UpsertMound(mound);

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now);

        Assert.True(outcome.StopInEffect);
        Assert.Equal([EnvelopeKinds.Stop, EnvelopeKinds.Ack], MicromoundSync.DownlinkKindsFor(outcome));

        // The charter is gone, not waiting. The way back is a charter somebody issues knowing the
        // stop happened.
        Assert.Equal(0, h.Store.PendingDownlinkCount(h.Device.MoundId));
    }

    // ---- What arrived -------------------------------------------------------------------------

    /// <summary>
    /// AN ACTION RECORD ARRIVING ON THE BEAT IS INGESTED AND GATED. `MicromoundEvidence` proved
    /// this logic in isolation; until the beat called it, nothing on the wire ever reached it.
    /// </summary>
    [Fact]
    public void ActionRecordsAndEvidence_ReachTheColonyThroughTheBeat()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        var charter = Charter(h, Now);

        var started = Now.AddSeconds(-60);

        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new EvidenceBundle
        {
            BundleId = "bundle-1",
            Items =
            [
                new EvidenceItem
                {
                    EvidenceId = "ev-1",
                    Type = "reading",
                    CapturedAt = started.AddSeconds(5).ToWire(),
                    Source = MicromoundRoster.Witness,
                    PayloadJson = """{"value":41.0,"unit":"percent"}""",
                },
            ],
        }, Now);

        h.Device.EnqueueUplink(EnvelopeKinds.ActionRecord, new ActionRecord
        {
            ActionId = "action-1",
            MissionId = "mission-1",
            CharterId = charter.CharterId,
            Capability = "act.water_valve",
            RequestedParameters = new Dictionary<string, double> { ["on_s"] = 30 },
            Parameters = new Dictionary<string, double> { ["on_s"] = 30 },
            StartedAt = started.ToWire(),
            EndedAt = started.AddSeconds(30).ToWire(),
            Outcome = ActionOutcomes.Succeeded,
            EvidenceRequired = true,
            EvidenceRefs = ["ev-1"],
        }, Now);

        var outcome = h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.True(h.Bus.Saw(MicromoundEvents.EvidenceIngested));

        var stored = Assert.Single(h.Store.ActionsForMission(h.Device.MoundId, "mission-1"));
        Assert.Equal(ActionOutcomes.Succeeded, stored.ColonyOutcome);
        Assert.True(stored.Verified);
    }

    /// <summary>
    /// A CLAIMED SUCCESS WHOSE PROOF IS STILL ON THE DEVICE ARRIVES AS `unverified`. The colony
    /// decides with the evidence that ARRIVED, and the mound's own verdict is kept beside it — this
    /// is the same rule `EvidenceIngestTests` proves, asserted here through the real wire.
    /// </summary>
    [Fact]
    public void AnUnprovenClaimArrivingOnTheBeat_IsDegradedAndBothVerdictsSurvive()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        var charter = Charter(h, Now);

        h.Device.EnqueueUplink(EnvelopeKinds.ActionRecord, new ActionRecord
        {
            ActionId = "action-2",
            MissionId = "mission-2",
            CharterId = charter.CharterId,
            Capability = "act.water_valve",
            StartedAt = Now.AddSeconds(-60).ToWire(),
            EndedAt = Now.AddSeconds(-30).ToWire(),
            Outcome = ActionOutcomes.Succeeded,
            EvidenceRequired = true,
            EvidenceRefs = ["ev-still-on-the-device"],
        }, Now);

        h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        var stored = Assert.Single(h.Store.ActionsForMission(h.Device.MoundId, "mission-2"));

        Assert.Equal(ActionOutcomes.Succeeded, stored.Record.Outcome);   // what the mound said
        Assert.Equal(ActionOutcomes.Unverified, stored.ColonyOutcome);   // what the colony can say
        Assert.True(h.Bus.Saw(MicromoundEvents.ActionDegraded));
    }

    /// <summary>
    /// A MISSION REPORT IS RECORDED AS A CLAIM, and announced beside what the colony can prove. The
    /// device says `completed`; with no action records behind it the colony says nothing is
    /// verified, and an operator gets to see both rather than whichever one the code preferred.
    /// </summary>
    [Fact]
    public void AMissionReport_IsStoredAndAnnouncedBesideTheColonysOwnVerdict()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        h.Device.EnqueueUplink(EnvelopeKinds.MissionReport, new MissionReport
        {
            MissionId = "mission-3",
            State = "completed",
            Detail = "watered",
        }, Now);

        h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        Assert.Equal("completed", h.Store.GetMissionReport(h.Device.MoundId, "mission-3")?.State);

        var announced = Assert.Single(h.Bus.Events,
            e => e.EventType == MicromoundEvents.MissionReported);

        Assert.Equal("completed", announced.Metadata["device_state"]);
        Assert.Equal(false, announced.Metadata["colony_verified"]);
    }

    /// <summary>
    /// A DEVICE REFUSING SOMETHING WE SENT IS NOT SILENT. It arrives as an uplink `ack` with
    /// `refused`, and without this the colony's record says "charter issued" while the mound is
    /// running under no authority at all.
    /// </summary>
    [Fact]
    public void ADeviceRefusingADownlinkEnvelope_IsAnnounced()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        h.Device.EnqueueUplink(EnvelopeKinds.Ack, new AckBody
        {
            Status = AckStatuses.Refused,
            RefersTo = "downlink-1",
            ThroughSeq = -1,
            Detail = "charter refused: grants a capability this build has no driver for",
        }, Now);

        h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        var announced = Assert.Single(h.Bus.Events,
            e => e.EventType == MicromoundEvents.DownlinkRefused);

        Assert.Equal(AckStatuses.Refused, announced.Metadata["status"]);
    }

    /// <summary>An ordinary `ok` ack from the device is not reported as a refusal.</summary>
    [Fact]
    public void AnOrdinaryDeviceAck_IsNotReportedAsARefusal()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        h.Device.EnqueueUplink(EnvelopeKinds.Ack, new AckBody
        {
            Status = AckStatuses.Ok, RefersTo = "downlink-1", ThroughSeq = 7,
        }, Now);

        h.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now);

        Assert.False(h.Bus.Saw(MicromoundEvents.DownlinkRefused));
    }
}
