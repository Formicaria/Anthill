using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// SAFETY.md: stop is reachable three ways and always wins. Two of the three live here — the
/// third is physical and is not ours to test.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class StopTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

    private static MoundRecord Enrolled(string moundId = "mm-1") => new()
    {
        MoundId = moundId,
        Name = moundId,
        Tier = MoundTiers.EdgeQueen,
        PublicKey = new string('a', 64),
        LastSeen = Now.ToWire(),
        SyncIntervalSeconds = 15
    };

    [Fact]
    public void The_stop_file_halts_every_mound_at_once()
    {
        using var workspace = new TempWorkspace();
        var mound = Enrolled();

        Assert.False(MicromoundStop.AppliesTo(mound, workspace.Options));

        workspace.EngageGlobalStop();

        Assert.True(MicromoundStop.IsEngaged(workspace.Options));
        Assert.True(MicromoundStop.AppliesTo(mound, workspace.Options));
    }

    [Fact]
    public void A_per_mound_stop_does_not_need_the_file()
    {
        using var workspace = new TempWorkspace();
        var mound = Enrolled();
        mound.Stopped = true;

        Assert.False(MicromoundStop.IsEngaged(workspace.Options));
        Assert.True(MicromoundStop.AppliesTo(mound, workspace.Options));
    }

    [Fact]
    public void A_stopped_mound_still_syncs_and_the_response_carries_the_stop()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();
        var enrollment = new MicromoundEnrollment(store, bus);
        var device = new SimMound("mm-1");

        var minted = enrollment.MintToken("mm-1", "mm-1", MoundTiers.EdgeQueen, "tyler", Now);
        enrollment.Enroll(new EnrollmentRequest("mm-1", minted.Token,
            Convert.ToHexStringLower(device.PublicKey), MoundTiers.EdgeQueen, "pi",
            ["sense.temp"], ProtocolVersion.Current), Now);

        workspace.EngageGlobalStop();

        device.EnqueueUplink(EnvelopeKinds.MoundSync, new { state = "chartered" }, Now);
        var sync = Colony.Build(store, bus).Sync;
        var outcome = sync.AcceptUplink("mm-1", device.DrainUplink(), Now);

        // Stop halts action, not observation — the mound keeps sensing and syncing.
        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.True(outcome.StopInEffect);

        // A STOP ORDER AND THE ACK, IN THAT ORDER — §7 puts the stop ahead of everything queued,
        // and the ack is still owed: the beat was accepted, and refusing to acknowledge it would
        // tell a stopped mound to hoard the very evidence the stop asked it to keep capturing.
        Assert.Equal([EnvelopeKinds.Stop, EnvelopeKinds.Ack], MicromoundSync.DownlinkKindsFor(outcome));
        Assert.True(bus.Saw(MicromoundEvents.StopInEffect));
    }

    /// <summary>
    /// The kinds come off the envelopes, so an outcome carrying none reports none — and, crucially,
    /// a stop flag with no signed stop envelope behind it does NOT report a stop. That is the
    /// disagreement the old implementation could produce: it answered from `StopInEffect` alone.
    /// </summary>
    [Fact]
    public void The_downlink_kinds_describe_the_envelopes_and_not_the_stop_flag()
    {
        using var workspace = new TempWorkspace();

        Assert.Empty(MicromoundSync.DownlinkKindsFor(
            new SyncOutcome(true, [], 3, "sha256:00", StopInEffect: false)));

        Assert.Empty(MicromoundSync.DownlinkKindsFor(
            new SyncOutcome(true, [], 3, "sha256:00", StopInEffect: true)));
    }
}

/// <summary>
/// Widget payloads are what the Integrations tab renders. They are also the thing most likely to
/// quietly leak a secret, so the shape is asserted rather than eyeballed.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class WidgetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

    private static MoundRecord Mound(string id, string lastSeen, bool enrolled = true, bool stopped = false) => new()
    {
        MoundId = id,
        Name = id,
        Tier = MoundTiers.EdgeQueen,
        PublicKey = enrolled ? new string('a', 64) : "",
        LastSeen = lastSeen,
        SyncIntervalSeconds = 15,
        Stopped = stopped
    };

    [Fact]
    public void The_fleet_widget_reports_each_mounds_real_state()
    {
        using var workspace = new TempWorkspace();

        var mounds = new List<MoundRecord>
        {
            Mound("mm-online", Now.AddSeconds(-10).ToWire()),
            Mound("mm-offline", Now.AddHours(-2).ToWire()),
            Mound("mm-stopped", Now.ToWire(), stopped: true),
            Mound("mm-new", "", enrolled: false)
        };

        using var doc = JsonDocument.Parse(MicromoundWidgets.BuildFleet(mounds, workspace.Options, Now));
        var root = doc.RootElement;

        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("online").GetInt32());
        Assert.Equal(1, root.GetProperty("offline").GetInt32());
        Assert.Equal(1, root.GetProperty("stopped").GetInt32());
        Assert.Equal(1, root.GetProperty("unenrolled").GetInt32());
        Assert.False(root.GetProperty("global_stop").GetBoolean());
    }

    [Fact]
    public void The_global_stop_shows_every_mound_as_stopped()
    {
        using var workspace = new TempWorkspace();
        workspace.EngageGlobalStop();

        var mounds = new List<MoundRecord> { Mound("mm-1", Now.ToWire()), Mound("mm-2", Now.ToWire()) };

        using var doc = JsonDocument.Parse(MicromoundWidgets.BuildFleet(mounds, workspace.Options, Now));

        Assert.True(doc.RootElement.GetProperty("global_stop").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("stopped").GetInt32());
    }

    [Fact]
    public void No_widget_payload_carries_a_key_or_a_token()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var mounds = new List<MoundRecord> { Mound("mm-1", Now.ToWire()) };
        store.UpsertMound(mounds[0]);
        store.RecordBeat(new MoundBeat
        {
            MoundId = "mm-1", ReceivedAt = Now.ToWire(), Seq = 4, State = "chartered",
            EnvelopeCount = 2, Accepted = true
        });

        var payloads = new[]
        {
            MicromoundWidgets.BuildFleet(mounds, workspace.Options, Now),
            MicromoundWidgets.BuildMissionStatus(store, mounds, new MicromoundEvidence(store, new RecordingEventBus()), Now),
            MicromoundWidgets.BuildEvidenceFeed(store, mounds, 10)
        };

        foreach (var payload in payloads)
        {
            Assert.DoesNotContain("public_key", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("token", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 64), payload, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE WIDGET SAYS THE COMMAND PATH EXISTS, BECAUSE IT DOES. v0.3.8.114.
    ///
    /// This test used to assert `phase: M1` and `command_path: false`, and it was right when it was
    /// written. Leaving it would have made the guard the thing keeping the lie in place: the field
    /// an operator reads to find out whether the colony can direct a mound, pinned to "no" by a
    /// test, three releases after the answer became yes.
    /// </summary>
    [Fact]
    public void The_mission_widget_reports_the_command_path_and_what_authority_is_out()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var evidence = new MicromoundEvidence(store, new RecordingEventBus());

        var chartered = Mound("mm-1", Now.ToWire());
        chartered.CharterId = "charter-1";
        chartered.LeaseExpiresAt = Now.AddMinutes(15).ToWire();
        store.UpsertMound(chartered);

        using var doc = JsonDocument.Parse(
            MicromoundWidgets.BuildMissionStatus(store, [chartered], evidence, Now));

        Assert.True(doc.RootElement.GetProperty("command_path").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("chartered").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("lease_held").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("awaiting_collection").GetInt32());
    }

    [Fact]
    public void The_evidence_feed_shows_refusals_not_only_what_was_believed()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var mounds = new List<MoundRecord> { Mound("mm-1", Now.ToWire()) };

        store.RecordBeat(new MoundBeat
        {
            MoundId = "mm-1", ReceivedAt = Now.ToWire(), Seq = -1, State = "refused",
            Accepted = false, Refusals = ["signature_refused: bad_signature"]
        });

        var payload = MicromoundWidgets.BuildEvidenceFeed(store, mounds, 10);

        Assert.Contains("signature_refused", payload, StringComparison.Ordinal);
        Assert.Contains("\"accepted\":false", payload, StringComparison.Ordinal);
    }
}
