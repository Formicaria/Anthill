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

    private sealed record Harness(
        InMemoryMoundStore Store,
        RecordingEventBus Bus,
        MicromoundSync Sync,
        SimMound Device);

    private static Harness Enrolled(string moundId = "mm-1", string tier = MoundTiers.EdgeQueen)
    {
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();
        var enrollment = new MicromoundEnrollment(store, bus);
        var device = new SimMound(moundId, tier);

        var minted = enrollment.MintToken(moundId, moundId, tier, "tyler", Now);
        var result = enrollment.Enroll(new EnrollmentRequest(
            moundId, minted.Token, Convert.ToHexStringLower(device.PublicKey), tier,
            "raspberry-pi-5", ["sense.temp", "act.relay_1"], ProtocolVersion.Current), Now);

        Assert.True(result.Accepted, result.Reason);
        return new Harness(store, bus, new MicromoundSync(store, bus), device);
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

    [Fact]
    public void A_replayed_batch_is_refused_because_the_sequence_does_not_resume()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();

        var batch = Beats(h.Device, 3, Now);
        Assert.True(h.Sync.AcceptUplink("mm-1", batch, Now).Accepted);

        var replay = h.Sync.AcceptUplink("mm-1", batch, Now.AddSeconds(30));

        Assert.False(replay.Accepted);
        Assert.Contains(replay.Refusals, r => r.Contains("seq does not resume"));
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

        var outcome = new MicromoundSync(store, bus)
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

        var outcome = new MicromoundSync(store, bus)
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
