using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sim;
using Micromound.Sync;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE END-TO-END SIMULATED PEER — §30. v0.3.8.114.
///
/// WHY THIS EXISTS, in one sentence: every other test in this project drives ANTHILL's controller
/// with envelopes a harness built, and the defects that mattered most in this release were ones
/// where ANTHILL and the DEVICE disagreed about what a message is.
///
/// M1's `/v0/enroll` demanded a `mound_id` the device does not send and read its key from a field
/// the device does not write; `/v0/sync` expected a wrapper object where the device POSTs one raw
/// envelope, and answered an object where the device parses an array. The colony's beat sent no
/// `ack`, so every mound's uplink queue would have grown forever and every chartered mound would
/// have quiesced on schedule while looking perfectly healthy. Not one of those was catchable by a
/// test whose other end was also ours.
///
/// So here the other end is the REAL DEVICE — `SimMound`, composed through `MoundComposition`, the
/// same runtime the shipped host builds. It drains its own backlog, verifies our signatures against
/// the controller key it was enrolled with, decides for itself whether our ack renewed its lease,
/// executes missions we send, and refuses what it should refuse. Nothing here reaches into it: the
/// assertions read `device.State`, `device.LeaseAlive(...)` and the records it chose to send.
///
/// THE TRANSPORT ROUND-TRIPS THROUGH JSON, deliberately (see <see cref="ColonyTransport"/>). An
/// in-process call that passed object references would prove the logic and skip the encoding, and
/// the encoding is exactly where this release found its defects.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class SimulatedPeerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

    private const string MoundId = "mm-greenhouse";
    private const string Sense = "sense.temp";
    private const string Act = "act.relay_1";

    /// <summary>
    /// The colony's clock, shared with the transport.
    ///
    /// A captured local would freeze at the moment the peer was built, and every lease assertion
    /// below would then be testing arithmetic against a colony that believed no time had passed —
    /// which is the shape of a test that proves nothing while looking thorough.
    /// </summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; }
    }

    /// <summary>
    /// The wire, standing in for HTTP and shaped exactly like it.
    ///
    /// `HttpSyncTransport` serializes ONE envelope as the whole request body with
    /// <see cref="ProtocolJson.Options"/> and parses the whole response body as
    /// <c>List&lt;Envelope&gt;</c>. This does the same, both ways, so a field that does not survive
    /// serialization fails here rather than in a shed.
    ///
    /// <see cref="Online"/> makes offline a first-class state, because it is one: PROTOCOL.md §1
    /// says so, and a failed exchange must leave the device's backlog intact.
    /// </summary>
    private sealed class ColonyTransport(MicromoundSync sync, Clock clock) : ISyncTransport
    {
        public bool Online { get; set; } = true;

        public int Exchanges { get; private set; }

        /// <summary>Every outcome the colony produced, for asserting on refusals the device swallows.</summary>
        public List<SyncOutcome> Outcomes { get; } = [];

        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            downlink = [];
            detail = "";

            if (!Online)
            {
                detail = "offline";
                return false;
            }

            Exchanges++;

            var request = Roundtrip<Envelope>(uplink)!;
            var outcome = sync.AcceptUplink(request.MoundId, [request], clock.Now);
            Outcomes.Add(outcome);

            downlink = Roundtrip<List<Envelope>>(outcome.Downlink)!;
            detail = $"{downlink.Count} downlink envelope(s)";
            return true;
        }

        private static T? Roundtrip<T>(object value) =>
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProtocolJson.Options),
                ProtocolJson.Options);
    }

    private sealed record Peer(
        Colony Colony, SimMound Device, ColonyTransport Transport, MicromoundEnrollment Enrollment,
        Clock Clock)
    {
        public InMemoryMoundStore Store => Colony.Store;
        public RecordingEventBus Bus => Colony.Bus;

        public MoundRecord Mound => Store.GetMound(MoundId)!;

        /// <summary>
        /// One beat at a given moment, with BOTH clocks moved together — the device's and the
        /// colony's. Returns whether the exchange reached the colony at all, which is a different
        /// question from whether the colony believed it.
        /// </summary>
        public bool Beat(DateTimeOffset at)
        {
            Clock.Now = at;
            return Device.Sync(at).Delivered;
        }
    }

    /// <summary>
    /// A colony and a device that have completed enrolment and can talk.
    ///
    /// THE KEY EXCHANGE IS THE FIDDLY PART AND IT IS HONEST. `SimMound` learns its controller's
    /// public key only through `ConnectTo(SimController)`, and `MicromoundIdentity` will not hand
    /// its private key to anybody — deliberately; SAFETY.md prohibits reading one back, and there
    /// is no accessor to add. So the seed goes the other way: the sim controller's keypair is
    /// planted in the mound store BEFORE anything signs, ANTHILL adopts it as its own identity, and
    /// the device is then pointed at ANTHILL. The device is verifying a real signature made by the
    /// key it was told to trust; only the route by which it learned that key is a shortcut.
    ///
    /// ENROLMENT ITSELF IS NOT SHORTCUT. It goes through <see cref="MicromoundEnrollment"/> with a
    /// minted token and NO `mound_id` — the shape the real device client sends, which is the shape
    /// M1 could not accept.
    /// </summary>
    private static Peer Connected(DateTimeOffset now, SimMound? device = null)
    {
        var colony = Colony.Build();

        var sim = new SimController();
        colony.Store.PutControllerSeed(sim.Keys.Seed);

        device ??= new SimMound(MoundId);
        device.ConnectTo(sim);

        var clock = new Clock { Now = now };
        var transport = new ColonyTransport(colony.Sync, clock);
        device.UseTransport(transport);

        var enrollment = new MicromoundEnrollment(colony.Store, colony.Bus);
        var minted = enrollment.MintToken(MoundId, "Greenhouse", MoundTiers.EdgeQueen, "tyler", now);

        var result = enrollment.Enroll(new EnrollmentRequest(
            MoundId: "", minted.Token, Convert.ToHexStringLower(device.PublicKey),
            MoundTiers.EdgeQueen, "raspberry-pi-5", [Sense, Act], ProtocolVersion.Current), now);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(MoundId, result.Mound!.MoundId);

        return new Peer(colony, device, transport, enrollment, clock);
    }

    private static Charter Charter(Peer p, DateTimeOffset at,
        TimeSpan? leaseTtl = null, TimeSpan? duration = null, string ceiling = "benign")
    {
        var issue = p.Colony.Charters.Issue(new CharterRequest(
            MoundId, [Sense, Act], [], ceiling,
            Duration: duration ?? TimeSpan.FromHours(4),
            LeaseTtl: leaseTtl ?? TimeSpan.FromMinutes(15)),
            "tyler", at);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        return issue.Charter!;
    }

    private static List<MissionStep> WaterAndCheck() =>
    [
        new() { StepId = "s1", Op = MissionStepOps.Sense, Capability = Sense, EvidenceTag = "before" },
        new()
        {
            StepId = "s2", Op = MissionStepOps.Act, Capability = Act,
            Parameters = new Dictionary<string, double> { ["on_s"] = 10 },
        },
        new()
        {
            StepId = "s3", Op = MissionStepOps.Verify, Capability = Sense,
            Confirms = "s2", EvidenceTag = "after",
        },
    ];

    // ---- The arc ------------------------------------------------------------------------------

    /// <summary>
    /// THE WHOLE LOOP, END TO END: enrol, beat, charter, configure, dispatch, execute, report.
    ///
    /// Each step is asserted from the DEVICE's side, because the colony believing it issued a
    /// charter is not the fact in question — the fact is whether the mound accepted it. Every one of
    /// those beliefs was true in M1 and every one of them was wrong.
    /// </summary>
    [Fact]
    public void ADeviceIsEnrolled_Chartered_AndCarriesOutWorkTheColonySent()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        // 1. A first beat, with no authority anywhere. The device says what it is.
        Assert.True(p.Beat(now));
        Assert.Equal(MoundStates.ObserveOnly, p.Device.State);
        Assert.True(p.Mound.LastSeq >= 0, "the colony did not accept the device's first beat");

        // 2. The colony grants authority. It is queued, not sent — nothing dials a mound.
        var charter = Charter(p, now);
        Assert.Equal(1, p.Store.PendingDownlinkCount(MoundId));
        Assert.Equal(MoundStates.ObserveOnly, p.Device.State);

        // 3. …and the next beat collects it. THE DEVICE decides this, by verifying our signature
        //    against the controller key it holds and validating the charter against its own drivers.
        now = now.AddSeconds(15);
        Assert.True(p.Beat(now));

        Assert.Equal(MoundStates.Chartered, p.Device.State);
        Assert.True(p.Device.LeaseAlive(now), "the device did not accept the lease our ack renewed");
        Assert.Equal(0, p.Store.PendingDownlinkCount(MoundId));

        // 4. Physical work, through the ordinary dispatcher, at an operator's request.
        var dispatch = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.User, "tyler", "the greenhouse is dry"), now);

        Assert.True(dispatch.Dispatched, string.Join("; ", dispatch.Refusals));
        Assert.Equal(charter.CharterId, dispatch.Mission!.CharterId);

        // 5. The device collects it, RUNS it, and queues its own report and records.
        now = now.AddSeconds(15);
        Assert.True(p.Beat(now));

        // 6. Which reach the colony on the beat after — a backlog drains oldest-first, and the
        //    records a mission produced are not in the same beat that carried the mission.
        now = now.AddSeconds(15);
        p.Beat(now);
        now = now.AddSeconds(15);
        p.Beat(now);

        var missionId = dispatch.Mission.MissionId;

        var report = p.Store.GetMissionReport(MoundId, missionId);
        Assert.NotNull(report);

        var actions = p.Store.ActionsForMission(MoundId, missionId);
        Assert.NotEmpty(actions);

        // THE COLONY'S VERDICT, NOT THE DEVICE'S CLAIM. A real sensor produced real evidence, so
        // these agree — which is the point of asserting it rather than assuming it.
        var summary = p.Colony.Evidence.SummarizeMission(MoundId, missionId);
        Assert.True(summary.AllVerified, summary.Detail);
        Assert.True(p.Bus.Saw(MicromoundEvents.MissionReported));
    }

    // ---- The lease ----------------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT THAT WOULD HAVE TAKEN THE WHOLE FLEET DOWN, asserted from the device's side.
    ///
    /// PROTOCOL.md §5: an acknowledged `mound_sync` renews the lease, and nothing on-device can
    /// extend it. The device renews when it sees an `ack` covering its beat's sequence number. M1
    /// sent no ack, so a mound with a fifteen-minute lease would have entered `safe_state` fifteen
    /// minutes after being chartered — beating perfectly, reported online by the fleet widget, and
    /// silently refusing every actuation from then on.
    ///
    /// So this beats for an hour past a fifteen-minute lease and asserts the mound is still
    /// chartered. It is the acks doing that, and nothing else could.
    /// </summary>
    [Fact]
    public void TheLeaseSurvivesFarPastItsTtl_BecauseEveryBeatIsAcknowledged()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now, leaseTtl: TimeSpan.FromMinutes(15), duration: TimeSpan.FromHours(4));

        now = now.AddSeconds(15);
        p.Beat(now);
        Assert.Equal(MoundStates.Chartered, p.Device.State);

        // An hour of ordinary beats, four times the lease.
        for (var i = 0; i < 60; i++)
        {
            now = now.AddMinutes(1);
            Assert.True(p.Beat(now));
        }

        Assert.True(p.Device.LeaseAlive(now),
            "the lease lapsed while the device was beating and being acknowledged — §5's only "
          + "renewal path is not working");
        Assert.Equal(MoundStates.Chartered, p.Device.State);
    }

    /// <summary>
    /// AND IT DOES LAPSE WHEN THE BEATS STOP — the positive control, without which the test above
    /// would pass on a device that simply never quiesces.
    /// </summary>
    [Fact]
    public void TheLeaseLapsesWhenTheDeviceCannotReachTheColony()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now, leaseTtl: TimeSpan.FromMinutes(15));

        now = now.AddSeconds(15);
        p.Beat(now);
        Assert.True(p.Device.LeaseAlive(now));

        // The link drops. Offline is normal — the device keeps sensing and keeps its backlog.
        p.Transport.Online = false;

        now = now.AddMinutes(30);
        Assert.False(p.Beat(now));

        Assert.False(p.Device.LeaseAlive(now));
    }

    /// <summary>
    /// A QUIESCED MOUND IS NOT RESUMED BY RECONNECTING. §5: "fresh authority must be issued to
    /// resume — resumption is never implicit, and renewal is not resumption." The colony must not
    /// renew a lease that has already lapsed, and this asserts it through the device: the link comes
    /// back, beats are acknowledged again, and the mound stays quiesced until somebody decides
    /// otherwise.
    /// </summary>
    [Fact]
    public void ReconnectingAfterALapse_DoesNotResumeTheMound()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now, leaseTtl: TimeSpan.FromMinutes(15), duration: TimeSpan.FromHours(4));

        now = now.AddSeconds(15);
        p.Beat(now);

        p.Transport.Online = false;
        now = now.AddMinutes(45);
        p.Beat(now);

        // QUIESCING IS ITS OWN ACT, driven by the device's clock tick — not something an expired
        // timestamp does by itself. `KernelAuthority.QuiesceIfExpired` is what sets the flag, and
        // the real host calls it from its loop; a test that skipped it would be describing a device
        // that does not exist, and asserting a property nothing enforces.
        Assert.True(p.Device.QuiesceIfExpired(now), "the device did not quiesce when its lease ran out");
        Assert.Equal(MoundStates.Quiesced, p.Device.State);

        // Back online, acknowledged, and STILL not resumed — because the DEVICE refuses to renew
        // while quiesced ("a fresh charter is the only way out of quiesce"), not because the colony
        // withholds anything. It cannot withhold the ack: that is what lets the mound release its
        // records, and a controller that stopped acking to keep a mound down would be filling its
        // storage to enforce a state the device already enforces.
        p.Transport.Online = true;
        for (var i = 0; i < 3; i++)
        {
            now = now.AddSeconds(15);
            Assert.True(p.Beat(now));
        }

        Assert.False(p.Device.LeaseAlive(now));
        Assert.Equal(MoundStates.Quiesced, p.Device.State);

        // AND THE COLONY AGREES, by the same rule rather than by a second one of its own. The mound
        // reported `quiesced` on those beats, the colony recorded it, and `RenewLease` reads that
        // flag — not the arithmetic. A timestamp rule here would hold a mound as expired that the
        // device was perfectly willing to work.
        Assert.True(p.Mound.Quiesced);
        Assert.False(p.Colony.Charters.RenewLease(p.Mound, now));
    }

    /// <summary>
    /// AND A LATE BEAT FROM A MOUND THAT HAS NOT QUIESCED DOES RENEW — the other half, and the half
    /// that pins where the rule lives.
    ///
    /// The device renews on any acknowledged beat unless it is stopped or quiesced; the lease
    /// TIMESTAMP having passed is not by itself a refusal. The colony's first version of this rule
    /// keyed on the timestamp and was therefore stricter than the authority — it would have held a
    /// mound as lease-expired, refusing to dispatch, while the device sat willing to work. Two
    /// implementations of one rule, disagreeing in the direction that looks like a malfunction.
    /// </summary>
    [Fact]
    public void ALateBeatFromAMoundThatHasNotQuiesced_StillRenews()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now, leaseTtl: TimeSpan.FromMinutes(15), duration: TimeSpan.FromHours(4));

        now = now.AddSeconds(15);
        p.Beat(now);

        // Past the lease, and the device has NOT been told to quiesce.
        now = now.AddMinutes(20);
        Assert.True(p.Beat(now));

        Assert.False(p.Mound.Quiesced);
        Assert.True(p.Device.LeaseAlive(now), "the device refused to renew on an acknowledged beat");
        Assert.False(MicromoundCharters.LeaseExpired(p.Mound, now),
            "the colony holds this mound as lease-expired while the device is working");
    }

    // ---- Offline is a normal state -------------------------------------------------------------

    /// <summary>
    /// A BACKLOG SURVIVES THE OUTAGE AND DRAINS ON RECONNECT — PROTOCOL.md §1. What makes this a
    /// real test rather than a device-side one is the other half: the colony must ACCEPT a chain
    /// that resumes after a gap in wall-clock time but not in sequence, and must acknowledge it, or
    /// the device never lets go.
    /// </summary>
    [Fact]
    public void ABacklogAccumulatedOffline_DrainsAndIsAcknowledged()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now);
        now = now.AddSeconds(15);
        p.Beat(now);

        p.Transport.Online = false;

        for (var i = 0; i < 5; i++)
        {
            now = now.AddSeconds(15);
            p.Beat(now);
        }

        var beforeReconnect = p.Mound.LastSeq;

        p.Transport.Online = true;
        now = now.AddSeconds(15);
        Assert.True(p.Beat(now));

        Assert.True(p.Mound.LastSeq > beforeReconnect,
            "the colony did not accept the backlog the device had been holding");

        // A second beat with nothing new is answered the same way rather than refused: the ack
        // rides the response, so re-delivery is the ordinary case and not an attack.
        now = now.AddSeconds(15);
        Assert.True(p.Beat(now));
        Assert.DoesNotContain(p.Transport.Outcomes, o => !o.Accepted);
    }

    // ---- Stop --------------------------------------------------------------------------------

    /// <summary>
    /// A STOP CROSSES THE WIRE AND THE DEVICE OBEYS IT, and what was queued does not survive.
    /// §7: a stop precedes all queued downlink, and clearing it reinstates nothing.
    /// </summary>
    [Fact]
    public void AnOperatorStop_ReachesTheDeviceAndTakesTheQueueWithIt()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now);
        now = now.AddSeconds(15);
        p.Beat(now);
        Assert.Equal(MoundStates.Chartered, p.Device.State);

        // A second charter is queued, and then a stop is engaged before the device collects it.
        Charter(p, now);
        Assert.Equal(1, p.Store.PendingDownlinkCount(MoundId));

        var mound = p.Mound;
        mound.Stopped = true;
        p.Store.UpsertMound(mound);

        now = now.AddSeconds(15);
        p.Beat(now);

        Assert.Equal(MoundStates.Stopped, p.Device.State);
        Assert.Equal(0, p.Store.PendingDownlinkCount(MoundId));
    }

    // ---- The security matrix — §27 -------------------------------------------------------------

    /// <summary>
    /// A DIFFERENT DEVICE CLAIMING AN ENROLLED IDENTITY IS REFUSED. A reflashed board, a stolen
    /// mound id, an impostor on the same network: all of them sign with a key this colony never
    /// bound, and PROTOCOL.md §2 makes a key the verifier does not hold a refusal rather than a
    /// prompt to learn one.
    /// </summary>
    [Fact]
    public void AnImpostorSigningAsAnEnrolledMound_IsRefusedAndNotAcknowledged()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        p.Beat(now);

        // Same id, different hardware. It even holds the real controller key, which buys it nothing.
        var impostor = new SimMound(MoundId);
        var sim = new SimController();
        impostor.ConnectTo(sim);
        var impostorTransport = new ColonyTransport(p.Colony.Sync, p.Clock);
        impostor.UseTransport(impostorTransport);

        now = now.AddSeconds(15);
        impostor.Sync(now);

        var outcome = Assert.Single(impostorTransport.Outcomes);
        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("signature_refused", StringComparison.Ordinal));

        // NOT ACKNOWLEDGED. An ack would tell whoever sent it that the colony holds those records.
        Assert.Empty(outcome.Downlink);
        Assert.True(p.Bus.Saw(MicromoundEvents.SyncRefused));
    }

    /// <summary>
    /// A TAMPERED ENVELOPE SPOILS THE BATCH IT IS IN, and the colony's anchor does not move. A
    /// chain that has been altered says nothing trustworthy about the envelopes before the break
    /// either, so there is no good prefix to keep.
    /// </summary>
    [Fact]
    public void AnEnvelopeAlteredInFlight_IsRefusedAndAdvancesNothing()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        p.Beat(now);
        var anchor = p.Mound.LastDigest;
        var seq = p.Mound.LastSeq;

        // A man in the middle: the device's own runtime is untouched, only the wire is.
        var tamperer = new TamperingTransport(p.Colony.Sync, p.Clock);
        p.Device.UseTransport(tamperer);

        now = now.AddSeconds(15);
        p.Beat(now);

        var outcome = Assert.Single(tamperer.Outcomes);
        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.Downlink);

        Assert.Equal(anchor, p.Mound.LastDigest);
        Assert.Equal(seq, p.Mound.LastSeq);
    }

    /// <summary>
    /// A DOWNLINK ENVELOPE THE COLONY DID NOT SIGN IS DROPPED BY THE DEVICE. This is the other
    /// direction, and it is why the enrolment response must carry `controller_public_key`: a mound
    /// that never learned the key would drop everything, including our real charters.
    /// </summary>
    [Fact]
    public void ACharterSignedByAnybodyElse_IsIgnoredByTheDevice()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now);

        // Re-sign every downlink envelope with a key nobody enrolled.
        var forger = new ForgingTransport(p.Colony.Sync, p.Clock);
        p.Device.UseTransport(forger);

        now = now.AddSeconds(15);
        p.Beat(now);

        // The colony believes it issued a charter; the device is correctly still observe-only.
        Assert.Equal(MoundStates.ObserveOnly, p.Device.State);
        Assert.False(p.Device.LeaseAlive(now));
    }

    /// <summary>
    /// A CHARTER CANNOT GRANT WHAT THE DEVICE NEVER REPORTED. The colony refuses before the wire,
    /// in the operator's words — and the mound would refuse it too, which is why refusing here is a
    /// courtesy rather than a second implementation of the rule.
    /// </summary>
    [Fact]
    public void AGrantBeyondTheReportedHardware_IsRefusedBeforeItIsSigned()
    {
        using var workspace = new TempWorkspace();
        var p = Connected(Start);

        var issue = p.Colony.Charters.Issue(new CharterRequest(
            MoundId, [Sense, "act.hydraulic_ram"], [], "benign",
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)), "tyler", Start);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("act.hydraulic_ram", StringComparison.Ordinal));
        Assert.Equal(0, p.Store.PendingDownlinkCount(MoundId));
    }

    /// <summary>
    /// A MISSION INTO A STOPPED MOUND IS REFUSED, and nothing is queued for the device to find
    /// later. Stop precedes autonomy — SAFETY.md names it in the list a stop takes precedence over.
    /// </summary>
    [Fact]
    public void AMissionToAStoppedMound_IsRefusedAndQueuesNothing()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now);
        now = now.AddSeconds(15);
        p.Beat(now);

        var mound = p.Mound;
        mound.Stopped = true;
        p.Store.UpsertMound(mound);

        var queued = p.Store.PendingDownlinkCount(MoundId);

        var dispatch = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.User, "tyler"), now);

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(queued, p.Store.PendingDownlinkCount(MoundId));
    }

    /// <summary>
    /// AN EXPIRED LEASE REFUSES THE MISSION RATHER THAN ISSUING IT INTO A MOUND THAT HAS STOPPED
    /// LISTENING. The device is in its safe state waiting for fresh authority; a mission delivered
    /// there would be work nobody could carry out and a colony that believed it had.
    /// </summary>
    [Fact]
    public void AMissionAfterTheLeaseLapsed_IsRefusedAsNeedingAuthority()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now, leaseTtl: TimeSpan.FromMinutes(15), duration: TimeSpan.FromHours(4));
        now = now.AddSeconds(15);
        p.Beat(now);

        p.Transport.Online = false;
        now = now.AddMinutes(45);
        p.Beat(now);

        var dispatch = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.User, "tyler"), now);

        Assert.False(dispatch.Dispatched);
        Assert.Contains(dispatch.Refusals, r => r.Contains("fresh authority", StringComparison.Ordinal));
    }

    /// <summary>
    /// AN AUTONOMOUS REQUEST TO A MANUAL-ONLY MOUND IS REFUSED, AND THE SAME REQUEST FROM A PERSON
    /// IS NOT. Both go through the one dispatcher and differ only in the `Origin` field — §15's
    /// property, asserted as a pair because either half alone proves nothing about the other.
    /// </summary>
    [Fact]
    public void OriginIsTheOnlyDifference_BetweenAnOperatorAndTheQueen()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now);

        Charter(p, now);
        now = now.AddSeconds(15);
        p.Beat(now);

        var queen = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.Queen, "queen"), now);

        Assert.False(queen.Dispatched);
        Assert.False(queen.ApprovalRequired);   // manual-only refuses outright rather than asking

        var operatorRequest = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.User, "tyler"), now);

        Assert.True(operatorRequest.Dispatched, string.Join("; ", operatorRequest.Refusals));
    }

    /// <summary>
    /// DEAD INSTRUMENTATION MEANS `unverified`, NOT SUCCESS — end to end, from a device whose
    /// sensors stopped producing evidence to the colony's own re-run of the gate. "Commands are not
    /// evidence": the relay fired, the mound cannot prove anything happened, and the colony records
    /// what it can prove rather than what it was told.
    /// </summary>
    [Fact]
    public void WhenTheSensorsGoDark_TheColonyRecordsUnverifiedRatherThanSuccess()
    {
        using var workspace = new TempWorkspace();
        var now = Start;
        var p = Connected(now, new SimMound(MoundId) { SensorHealthy = false });

        Charter(p, now);
        now = now.AddSeconds(15);
        p.Beat(now);

        var dispatch = p.Colony.Missions.Dispatch(new PhysicalMissionRequest(
            MoundId, WaterAndCheck(), PhysicalOrigin.User, "tyler"), now);
        Assert.True(dispatch.Dispatched, string.Join("; ", dispatch.Refusals));

        for (var i = 0; i < 3; i++)
        {
            now = now.AddSeconds(15);
            p.Beat(now);
        }

        var summary = p.Colony.Evidence.SummarizeMission(MoundId, dispatch.Mission!.MissionId);

        // VACUITY FLOOR. `SummarizeMission` answers "not verified" for a mission nothing has
        // reported on at all, so without this the test would pass just as happily on a colony that
        // never received the records — which is the shape of a check that answers a different
        // question than the one in its name.
        Assert.NotEmpty(p.Store.ActionsForMission(MoundId, dispatch.Mission.MissionId));

        Assert.False(summary.AllVerified,
            "a mission whose actuation nothing observed was recorded as proven");
    }

    // ---- Wires that misbehave ------------------------------------------------------------------

    /// <summary>Flips a byte in the signature after the device signed and before the colony reads.</summary>
    private sealed class TamperingTransport(MicromoundSync sync, Clock clock) : ISyncTransport
    {
        public List<SyncOutcome> Outcomes { get; } = [];

        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            var altered = JsonSerializer.Deserialize<Envelope>(
                JsonSerializer.Serialize(uplink, ProtocolJson.Options), ProtocolJson.Options)!;

            altered.Signature = "ed25519:" + new string('0', 128);

            var outcome = sync.AcceptUplink(altered.MoundId, [altered], clock.Now);
            Outcomes.Add(outcome);

            downlink = outcome.Downlink;
            detail = "tampered";
            return true;
        }
    }

    /// <summary>Replaces every downlink signature with one from a key nobody enrolled.</summary>
    private sealed class ForgingTransport(MicromoundSync sync, Clock clock) : ISyncTransport
    {
        private readonly Ed25519EnvelopeSigner _forger =
            new(KeyIds.Controller, Ed25519KeyPair.Generate());

        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            var outcome = sync.AcceptUplink(uplink.MoundId, [uplink], clock.Now);

            downlink = [.. outcome.Downlink.Select(e =>
                EnvelopeSigning.Sign(new Envelope
                {
                    Id = e.Id, MoundId = e.MoundId, Seq = e.Seq, SentAt = e.SentAt,
                    Kind = e.Kind, Body = e.Body, PrevDigest = e.PrevDigest,
                }, _forger))];

            detail = "forged";
            return true;
        }
    }
}
