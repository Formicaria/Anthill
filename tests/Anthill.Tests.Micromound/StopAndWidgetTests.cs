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
        var sync = new MicromoundSync(store, bus);
        var outcome = sync.AcceptUplink("mm-1", device.DrainUplink(), Now);

        // Stop halts action, not observation — the mound keeps sensing and syncing.
        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.True(outcome.StopInEffect);

        var downlink = sync.DownlinkKindsFor(outcome);
        Assert.Single(downlink);
        Assert.Equal(EnvelopeKinds.Stop, downlink[0]);
        Assert.True(bus.Saw(MicromoundEvents.StopInEffect));
    }

    [Fact]
    public void With_no_stop_in_effect_the_colony_sends_nothing_back()
    {
        using var workspace = new TempWorkspace();
        var sync = new MicromoundSync(new InMemoryMoundStore(), new RecordingEventBus());
        var outcome = new SyncOutcome(true, [], 3, "sha256:00", StopInEffect: false);

        Assert.Empty(sync.DownlinkKindsFor(outcome));
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
            MicromoundWidgets.BuildMissionStatus(mounds),
            MicromoundWidgets.BuildEvidenceFeed(store, mounds, 10)
        };

        foreach (var payload in payloads)
        {
            Assert.DoesNotContain("public_key", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("token", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 64), payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_mission_widget_says_plainly_that_there_is_no_command_path()
    {
        using var workspace = new TempWorkspace();

        using var doc = JsonDocument.Parse(MicromoundWidgets.BuildMissionStatus([]));

        Assert.Equal("M1", doc.RootElement.GetProperty("phase").GetString());
        Assert.False(doc.RootElement.GetProperty("command_path").GetBoolean());
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
