using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// The sync beat from the colony's side, driven by the real device simulator so the bytes,
/// signatures and hash chain all come from the implementation that will actually be on the wire.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class SyncTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

    private sealed record Harness(Colony Colony, SimMound Device)
    {
        public InMemoryMoundStore Store => Colony.Store;
        public RecordingEventBus Bus => Colony.Bus;
        public MicromoundSync Sync => Colony.Sync;
    }

    private static Harness Enrolled(string moundId = "mm-1", string tier = MoundTiers.EdgeQueen)
    {
        var colony = Colony.Build();
        var enrollment = new MicromoundEnrollment(colony.Store, colony.Bus);
        var device = new SimMound(moundId, tier);

        var minted = enrollment.MintToken(moundId, moundId, tier, "tyler", Now);
        var result = enrollment.Enroll(new EnrollmentRequest(
            moundId, minted.Token, Convert.ToHexStringLower(device.PublicKey), tier,
            "raspberry-pi-5", ["sense.temp", "act.relay_1"], ProtocolVersion.Current), Now);

        Assert.True(result.Accepted, result.Reason);
        return new Harness(colony, device);
    }

    private static IReadOnlyList<Envelope> Beats(SimMound device, int count, DateTimeOffset from)
    {
        for (var i = 0; i < count; i++)
            device.EnqueueUplink(EnvelopeKinds.MoundSync, new { state = "chartered", beat = i },
                from.AddSeconds(i));

        return device.DrainUplink();
    }

    [Fact]
    public void A_real_signed_backlog_from_the_simulator_is_accepted()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var outcome = h.Sync.AcceptUplink("mm-1", Beats(h.Device, 3, Now), Now);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.Equal(2, outcome.AcceptedThroughSeq);
        Assert.NotEqual("", outcome.AnchorDigest);
        Assert.True(h.Bus.Saw(MicromoundEvents.SyncAccepted));
    }

    [Fact]
    public void The_next_batch_must_continue_from_the_anchor_the_colony_acknowledged()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var first = h.Sync.AcceptUplink("mm-1", Beats(h.Device, 3, Now), Now);
        Assert.True(first.Accepted);

        // The device keeps its own chain running; the second batch links to the first.
        var second = h.Sync.AcceptUplink("mm-1", Beats(h.Device, 2, Now.AddMinutes(1)), Now.AddMinutes(1));

        Assert.True(second.Accepted, string.Join("; ", second.Refusals));
        Assert.Equal(4, second.AcceptedThroughSeq);
    }

    /// <summary>
    /// A REPLAYED BATCH IS ANSWERED WITH THE SAME ACK AND PROCESSED BY NOTHING. v0.3.8.114 —
    /// this test used to assert a refusal, and the refusal was a deadlock.
    ///
    /// The ack rides the sync RESPONSE. A response lost in transit means the device re-sends the
    /// identical batch, forever, against a colony refusing it forever — and the device's own loop
    /// does exactly that: `if (_queue.Depth >= depthBefore) break;` keeps the records queued and
    /// tries again next beat. The security property that actually matters is that nothing is
    /// believed twice, and that is what is asserted here.
    /// </summary>
    [Fact]
    public void A_replayed_batch_is_re_acknowledged_and_processed_once()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beats(h.Device, 3, Now);
        var first = h.Sync.AcceptUplink("mm-1", batch, Now);
        Assert.True(first.Accepted);

        var beatsAfterFirst = h.Store.RecentBeats("mm-1", 20).Count;

        var replay = h.Sync.AcceptUplink("mm-1", batch, Now.AddSeconds(30));

        Assert.True(replay.Accepted);
        Assert.True(replay.Duplicate);

        // Same ack, and the window did not move.
        Assert.Equal(first.AcceptedThroughSeq, replay.AcceptedThroughSeq);
        Assert.Equal(first.AnchorDigest, replay.AnchorDigest);
        Assert.Equal([EnvelopeKinds.Ack], MicromoundSync.DownlinkKindsFor(replay));

        // And nothing was recorded a second time.
        Assert.Equal(beatsAfterFirst, h.Store.RecentBeats("mm-1", 20).Count);
    }

    /// <summary>
    /// An envelope with a sequence the colony has NOT seen still has to chain from the anchor. A
    /// re-delivery is forgiven; a gap is not, and the two are told apart by sequence rather than by
    /// how the batch happened to be assembled.
    /// </summary>
    [Fact]
    public void A_batch_that_skips_a_sequence_is_still_refused()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        Assert.True(h.Sync.AcceptUplink("mm-1", Beats(h.Device, 2, Now), Now).Accepted);

        // Drop the envelope that would have continued the chain.
        var next = Beats(h.Device, 2, Now.AddMinutes(1)).Skip(1).ToList();

        var outcome = h.Sync.AcceptUplink("mm-1", next, Now.AddMinutes(1));

        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.Downlink);   // a refused batch is never acknowledged
    }

    [Fact]
    public void A_forged_signature_spoils_the_whole_batch_not_just_its_own_envelope()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beats(h.Device, 3, Now).ToList();
        batch[1].Signature = "ed25519:" + new string('0', 128);

        var outcome = h.Sync.AcceptUplink("mm-1", batch, Now);

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("signature_refused"));

        // Nothing advanced: a batch that cannot be believed teaches the colony nothing.
        Assert.Equal(-1, h.Store.GetMound("mm-1")!.LastSeq);
    }

    [Fact]
    public void A_mound_signing_with_a_key_the_colony_never_bound_is_refused()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        // Same mound_id, different device — a reflashed or impersonating board.
        var impostor = new SimMound("mm-1");
        var outcome = h.Sync.AcceptUplink("mm-1", Beats(impostor, 2, Now), Now);

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("signature_refused"));
    }

    [Fact]
    public void An_envelope_claiming_a_different_mound_is_refused()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beats(h.Device, 2, Now).ToList();
        batch[1].MoundId = "mm-somewhere-else";

        var outcome = h.Sync.AcceptUplink("mm-1", batch, Now);

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("mm-somewhere-else"));
    }

    [Fact]
    public void A_mound_that_never_finished_enrolling_is_refused()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();
        new MicromoundEnrollment(store, bus).MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        var outcome = Colony.Build(store, bus).Sync
            .AcceptUplink("mm-1", Beats(new SimMound("mm-1"), 2, Now), Now);

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("enrollment"));
    }

    [Fact]
    public void An_unknown_mound_is_refused()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();

        var outcome = Colony.Build(store, bus).Sync
            .AcceptUplink("mm-ghost", Beats(new SimMound("mm-ghost"), 1, Now), Now);

        Assert.False(outcome.Accepted);
        Assert.True(bus.Saw(MicromoundEvents.SyncRefused));
    }

    [Fact]
    public void Every_refusal_lands_in_the_evidence_trail()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beats(h.Device, 2, Now).ToList();
        batch[0].Signature = "";

        h.Sync.AcceptUplink("mm-1", batch, Now);

        var beats = h.Store.RecentBeats("mm-1", 10);
        Assert.Contains(beats, b => !b.Accepted && b.Refusals.Count > 0);
    }

    [Fact]
    public void A_deterministic_controller_is_held_to_the_reduced_profile()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled("mm-esp", MoundTiers.DeterministicController);

        // `evidence_bundle` is not in the reduced envelope set — PROTOCOL.md §8.
        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new { bundle_id = "b1" }, Now);
        var outcome = h.Sync.AcceptUplink("mm-esp", h.Device.DrainUplink(), Now);

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Refusals, r => r.Contains("refused_unknown_kind"));
    }

    [Fact]
    public void The_same_envelope_kind_is_fine_from_an_edge_queen()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new { bundle_id = "b1" }, Now);
        var outcome = h.Sync.AcceptUplink("mm-1", h.Device.DrainUplink(), Now);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
    }
}
