using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// RETIREMENT IS A STATE, NEVER A DELETE. P-3 — entitlement model §5.3 and §7.3, key management
/// §5.3, retention §4.4.
///
/// Before these fields existed the colony's only way to stop trusting a device deleted it and its
/// evidence, and a replaced device that kept beating kept being acknowledged and kept renewing its
/// own lease, because the sync path had no fact to gate on. These are the facts that had no test
/// because there was nothing to test: a retired mound is still heard and its evidence kept and
/// marked; it is granted nothing — no charter, mission, configuration, lease renewal or re-mint;
/// stop still reaches it; and the retirement survives the database.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class RetirementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");

    private sealed record Harness(Colony Colony, SimMound Device, MicromoundEnrollment Enrollment)
    {
        public InMemoryMoundStore Store => Colony.Store;
        public MoundRecord Mound => Store.GetMound(Device.MoundId)!;
        public static readonly string[] Capabilities = ["sense.soil_moisture", "act.water_valve"];
    }

    private static Harness Enrolled(string moundId = "mm-old", Colony? colony = null)
    {
        colony ??= Colony.Build();
        var enrollment = new MicromoundEnrollment(colony.Store, colony.Bus);
        var device = new SimMound(moundId);

        var minted = enrollment.MintToken(moundId, moundId, MoundTiers.EdgeQueen, "operator", Now);
        var result = enrollment.Enroll(new EnrollmentRequest(
            moundId, minted.Token, Convert.ToHexStringLower(device.PublicKey), MoundTiers.EdgeQueen,
            "raspberry-pi-5", Harness.Capabilities, ProtocolVersion.Current), Now);

        Assert.True(result.Accepted, result.Reason);
        return new Harness(colony, device, enrollment);
    }

    private static IReadOnlyList<Envelope> Beat(SimMound device, DateTimeOffset at, string state = "chartered")
    {
        device.EnqueueUplink(EnvelopeKinds.MoundSync, new { state }, at);
        return device.DrainUplink();
    }

    private static Charter Charter(Harness h, DateTimeOffset at)
    {
        var issue = h.Colony.Charters.Issue(new CharterRequest(
            h.Device.MoundId, ["sense.soil_moisture", "act.water_valve"], [], "benign",
            Duration: TimeSpan.FromHours(4), LeaseTtl: TimeSpan.FromMinutes(15)), "operator", at);
        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        return issue.Charter!;
    }

    private static RetireOutcome Retire(Harness h, string reason = MoundRetirement.Unlinked, string? replacedBy = null)
    {
        var outcome = MicromoundRetirement.Retire(h.Store, h.Device.MoundId, reason, replacedBy, Now);
        Assert.True(outcome.Retired, outcome.Refusal);
        return outcome;
    }

    private static AckBody AckOf(SyncOutcome outcome) =>
        JsonSerializer.Deserialize<AckBody>(
            outcome.Downlink.Single(e => e.Kind == EnvelopeKinds.Ack).Body.GetRawText(), ProtocolJson.Options)!;

    // ---- The state ----------------------------------------------------------------------------

    [Fact]
    public void Retiring_SetsTheState_AndKeepsTheRecordAndItsEvidence()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);

        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new EvidenceBundle
        {
            BundleId = "b-before",
            Items = [new EvidenceItem { EvidenceId = "ev-before", Type = "reading", CapturedAt = Now.AddSeconds(-30).ToWire() }],
        }, Now);
        Assert.True(h.Colony.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), Now).Accepted);

        var outcome = Retire(h);

        var mound = h.Mound;
        Assert.True(mound.IsRetired);
        Assert.Equal(Now.ToWire(), mound.RetiredAt);
        Assert.Equal(MoundRetirement.Unlinked, mound.RetirementReason);
        Assert.Equal("", mound.ReplacedBy);

        // The whole point: the row is there, and so is what it proved.
        Assert.NotNull(h.Store.GetMound(h.Device.MoundId));
        Assert.Contains(h.Store.EvidenceFor(h.Device.MoundId), e => e.EvidenceId == "ev-before");
        // Evidence from BEFORE the retirement is not marked: it came from a live identity.
        Assert.DoesNotContain("ev-before", h.Store.FromRetiredIdentity(h.Device.MoundId));
        // And the charter that was queued for collection did not survive.
        Assert.Equal(1, outcome.DiscardedDownlink);
        Assert.Equal(0, h.Store.PendingDownlinkCount(h.Device.MoundId));
    }

    [Fact]
    public void ASecondRetirement_ChangesNothing_AndSaysSo()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Retire(h);
        var first = h.Mound.RetiredAt;

        var again = MicromoundRetirement.Retire(h.Store, h.Device.MoundId, MoundRetirement.Revoked, null, Now.AddHours(1));

        Assert.False(again.Retired);
        Assert.Contains("already", again.Refusal, StringComparison.Ordinal);
        Assert.Equal(first, h.Mound.RetiredAt);
        Assert.Equal(MoundRetirement.Unlinked, h.Mound.RetirementReason);
    }

    [Fact]
    public void AReplacement_NamesALiveSuccessor_ThatIsNotItself()
    {
        using var workspace = new TempWorkspace();
        var old = Enrolled("mm-old");
        var successor = Enrolled("mm-new", old.Colony);

        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-old", MoundRetirement.Replaced, null, Now).Retired);
        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-old", MoundRetirement.Replaced, "mm-old", Now).Retired);
        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-old", MoundRetirement.Replaced, "mm-nobody", Now).Retired);
        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-old", MoundRetirement.Unlinked, "mm-new", Now).Retired,
            "replaced_by only accompanies a replacement");
        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-old", "evaporated", null, Now).Retired);

        var outcome = MicromoundRetirement.Retire(old.Store, "mm-old", MoundRetirement.Replaced, "mm-new", Now);
        Assert.True(outcome.Retired, outcome.Refusal);
        Assert.Equal("mm-new", old.Store.GetMound("mm-old")!.ReplacedBy);
        Assert.False(successor.Mound.IsRetired, "retiring the old device says nothing about the new one");

        // And a retired mound cannot be named as anybody's successor.
        var third = Enrolled("mm-third", old.Colony);
        Assert.False(MicromoundRetirement.Retire(old.Store, "mm-third", MoundRetirement.Replaced, "mm-old", Now).Retired);
        Assert.False(third.Mound.IsRetired);
    }

    // ---- Still heard, never renewed -----------------------------------------------------------

    /// <summary>
    /// A RETIRED MOUND'S BEAT IS VERIFIED, ITS EVIDENCE KEPT AND MARKED, AND ITS LEASE LEFT ALONE.
    /// Entitlement model §7.3: the overlap between a replaced device and its successor is bounded
    /// at one lease TTL after the last beat acknowledged as renewing — which is only a bound if
    /// this beat is not one of those.
    /// </summary>
    [Fact]
    public void ARetiredMoundsBeat_IsAccepted_ItsEvidenceMarked_AndItsLeaseNotRenewed()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);
        Assert.True(h.Colony.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now).Accepted);
        var leaseBefore = h.Mound.LeaseExpiresAt;
        Assert.NotEqual("", leaseBefore);

        Retire(h);

        var later = Now.AddMinutes(5);
        h.Device.EnqueueUplink(EnvelopeKinds.EvidenceBundle, new EvidenceBundle
        {
            BundleId = "b-after",
            Items = [new EvidenceItem { EvidenceId = "ev-after", Type = "reading", CapturedAt = later.AddSeconds(-5).ToWire() }],
        }, later);
        h.Device.EnqueueUplink(EnvelopeKinds.ActionRecord, new ActionRecord
        {
            ActionId = "act-after", Capability = "act.water_valve", CharterId = h.Mound.CharterId,
            Outcome = ActionOutcomes.Succeeded, StartedAt = later.AddSeconds(-4).ToWire(), EndedAt = later.AddSeconds(-3).ToWire(),
        }, later);

        var outcome = h.Colony.Sync.AcceptUplink(h.Device.MoundId, h.Device.DrainUplink(), later);

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.True(outcome.Retired);
        Assert.Equal("retired identity", AckOf(outcome).Detail);
        Assert.Equal(["ev-after"], AckOf(outcome).EvidenceIds);

        // Kept, and marked — evidence and action alike.
        Assert.Contains(h.Store.EvidenceFor(h.Device.MoundId), e => e.EvidenceId == "ev-after");
        var marked = h.Store.FromRetiredIdentity(h.Device.MoundId);
        Assert.Contains("ev-after", marked);
        Assert.Contains("act-after", marked);

        // Heard: the device is demonstrably there. Not renewed: the lease is meant to lapse.
        Assert.Equal(later.ToWire(), h.Mound.LastSeen);
        Assert.Equal(leaseBefore, h.Mound.LeaseExpiresAt);
        Assert.Contains(h.Colony.Bus.Events, e => e.EventType == MicromoundEvents.SyncAccepted
            && e.Metadata.TryGetValue("from_retired_identity", out var v) && v is true);
    }

    [Fact]
    public void AnEmptyBeatFromARetiredMound_MovesLastSeen_AndNothingElse()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);
        Assert.True(h.Colony.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now).Accepted);
        var leaseBefore = h.Mound.LeaseExpiresAt;
        Retire(h);

        var later = Now.AddMinutes(3);
        var outcome = h.Colony.Sync.AcceptUplink(h.Device.MoundId, [], later);

        Assert.True(outcome.Accepted);
        Assert.True(outcome.Retired);
        Assert.Equal("retired identity", AckOf(outcome).Detail);
        Assert.Equal(later.ToWire(), h.Mound.LastSeen);
        Assert.Equal(leaseBefore, h.Mound.LeaseExpiresAt);
    }

    /// <summary>
    /// STOP STILL REACHES A RETIRED MOUND — SAFETY.md gives every stop three routes and retirement
    /// closes none of them. A retired device may still be acting under the charter it holds until
    /// its lease lapses, which is exactly when an operator most needs the per-mound stop to work.
    /// </summary>
    [Fact]
    public void AStop_StillReachesARetiredMound()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);
        Retire(h);

        var mound = h.Mound;
        mound.Stopped = true;
        h.Store.UpsertMound(mound);

        var outcome = h.Colony.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now.AddMinutes(1)), Now.AddMinutes(1));

        Assert.True(outcome.Accepted, string.Join("; ", outcome.Refusals));
        Assert.True(outcome.StopInEffect);
        Assert.Contains(outcome.Downlink, e => e.Kind == EnvelopeKinds.Stop);
    }

    // ---- Granted nothing ----------------------------------------------------------------------

    [Fact]
    public void ARetiredMound_IsRefusedEveryNewGrant_ByName()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);
        Retire(h);

        var charter = h.Colony.Charters.Issue(new CharterRequest(
            h.Device.MoundId, ["sense.soil_moisture"], [], "benign",
            Duration: TimeSpan.FromHours(1), LeaseTtl: TimeSpan.FromMinutes(5)), "operator", Now.AddMinutes(1));
        Assert.False(charter.Issued);
        Assert.Contains(charter.Refusals, r => r.Contains("retired", StringComparison.Ordinal));

        var mission = h.Colony.Missions.Dispatch(new PhysicalMissionRequest(h.Device.MoundId,
            [new MissionStep { StepId = "s", Op = MissionStepOps.Sense, Capability = "sense.soil_moisture" }],
            PhysicalOrigin.User, "operator"), Now.AddMinutes(1));
        Assert.False(mission.Dispatched);
        Assert.Contains(mission.Refusals, r => r.Contains("retired", StringComparison.Ordinal));

        var configuration = h.Colony.Configuration.Issue(
            new ConfigurationRequest(h.Device.MoundId, [], ["sense.soil_moisture"], []), "operator", Now.AddMinutes(1));
        Assert.False(configuration.Issued);
        Assert.Contains(configuration.Refusals, r => r.Contains("retired", StringComparison.Ordinal));

        // Nothing was queued by any of the three.
        Assert.Equal(0, h.Store.PendingDownlinkCount(h.Device.MoundId));

        // And the lease is not renewed by anybody, whatever path asks.
        Assert.False(h.Colony.Charters.RenewLease(h.Mound, Now.AddMinutes(1)));
    }

    [Fact]
    public void ARetiredId_IsNeverReMinted_AndItsOldTokenNeverEnrols()
    {
        using var workspace = new TempWorkspace();
        var colony = Colony.Build();
        var enrollment = new MicromoundEnrollment(colony.Store, colony.Bus);

        // A mound with a token minted and never used, then retired before the device turned up.
        var minted = enrollment.MintToken("mm-boxed", "Boxed", MoundTiers.EdgeQueen, "operator", Now);
        Assert.True(MicromoundRetirement.Retire(colony.Store, "mm-boxed", MoundRetirement.Revoked, null, Now).Retired);

        var device = new SimMound("mm-boxed");
        var enrol = enrollment.Enroll(new EnrollmentRequest("mm-boxed", minted.Token,
            Convert.ToHexStringLower(device.PublicKey), MoundTiers.EdgeQueen, "esp32", [], ProtocolVersion.Current), Now.AddMinutes(1));
        Assert.False(enrol.Accepted);
        Assert.Contains("retired", enrol.Reason, StringComparison.Ordinal);
        Assert.Equal("", colony.Store.GetMound("mm-boxed")!.PublicKey);

        var ex = Assert.Throws<ArgumentException>(() =>
            enrollment.MintToken("mm-boxed", "Boxed again", MoundTiers.EdgeQueen, "operator", Now.AddMinutes(2)));
        Assert.Contains("retired", ex.Message, StringComparison.Ordinal);
        Assert.True(colony.Store.GetMound("mm-boxed")!.IsRetired, "a refused re-mint must not have touched the record");
    }

    // ---- What the fleet says ------------------------------------------------------------------

    [Fact]
    public void TheFleet_ReportsRetired_AboveEveryOtherStatus_AndTheResolverBlocksOnIt()
    {
        using var workspace = new TempWorkspace();
        var h = Enrolled();
        Charter(h, Now);
        Assert.True(h.Colony.Sync.AcceptUplink(h.Device.MoundId, Beat(h.Device, Now), Now).Accepted);
        Assert.Equal("online", MicromoundWidgets.StatusOf(h.Mound, workspace.Options, Now, globalStop: false));

        Retire(h);
        var mound = h.Mound;
        mound.Stopped = true;
        h.Store.UpsertMound(mound);

        Assert.Equal("retired", MicromoundWidgets.StatusOf(h.Mound, workspace.Options, Now, globalStop: true));

        var fleet = JsonSerializer.Deserialize<MicromoundWidgets.FleetPayload>(
            MicromoundWidgets.BuildFleet(h.Store.ListMounds(), workspace.Options, Now))!;
        Assert.Equal(1, fleet.Total);
        Assert.Equal(1, fleet.Retired);
        Assert.Equal(0, fleet.Online + fleet.Offline + fleet.Stopped + fleet.Unenrolled);

        var candidate = h.Colony.Resolver.Resolve("sense.soil_moisture", PhysicalOrigin.User, Now).Single();
        Assert.False(candidate.Eligible);
        Assert.Contains(candidate.Blockers, b => b.Contains("retired", StringComparison.Ordinal));
    }

    // ---- The database -------------------------------------------------------------------------

    /// <summary>
    /// The three retirement fields ride the reflection walk in StorePersistenceTests like every
    /// other MoundRecord field. The MARK does not — it lives beside the evidence, not on the record
    /// — so it gets its own restart.
    /// </summary>
    [Fact]
    public void TheMark_SurvivesTheDatabase_AndAnOlderDatabaseGainsTheColumns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-ret-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.db");
            using (var store = new SqliteMoundStore(path))
            {
                store.UpsertMound(new MoundRecord { MoundId = "mm-db", Name = "db", RetiredAt = Now.ToWire(), RetirementReason = MoundRetirement.Unlinked });
                store.PutEvidence("mm-db", new EvidenceItem { EvidenceId = "ev-live", Type = "reading", CapturedAt = Now.ToWire() });
                store.PutEvidence("mm-db", new EvidenceItem { EvidenceId = "ev-late", Type = "reading", CapturedAt = Now.ToWire() });
                store.PutAction("mm-db", new ActionRecord { ActionId = "act-late", Capability = "act.x", Outcome = ActionOutcomes.Succeeded }, ActionOutcomes.Unverified, "no evidence");
                store.MarkFromRetiredIdentity("mm-db", ["ev-late"], ["act-late"]);
            }

            using (var reopened = new SqliteMoundStore(path))
            {
                var marked = reopened.FromRetiredIdentity("mm-db");
                Assert.Equal(2, marked.Count);
                Assert.Contains("ev-late", marked);
                Assert.Contains("act-late", marked);
                Assert.DoesNotContain("ev-live", marked);
                Assert.True(reopened.GetMound("mm-db")!.IsRetired);
                // Marking is additive and idempotent, and a mark on nothing is not an error.
                reopened.MarkFromRetiredIdentity("mm-db", ["ev-late"], []);
                reopened.MarkFromRetiredIdentity("mm-db", [], []);
                Assert.Equal(2, reopened.FromRetiredIdentity("mm-db").Count);
                // A purge takes the marks with the rows.
                Assert.True(reopened.RemoveMound("mm-db"));
                Assert.Empty(reopened.FromRetiredIdentity("mm-db"));
            }

            // An OLDER database: the tables without any of P-3's columns. Opening it must add them,
            // and a row written before retirement existed must read as live.
            var old = Path.Combine(dir, "old.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={old}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE micromound_mounds (mound_id TEXT PRIMARY KEY, name TEXT NOT NULL, tier TEXT NOT NULL,
                        public_key TEXT NOT NULL DEFAULT '', hardware_profile TEXT NOT NULL DEFAULT '',
                        capabilities_json TEXT NOT NULL DEFAULT '[]', enrolled_at TEXT NOT NULL DEFAULT '',
                        last_seen TEXT NOT NULL DEFAULT '', last_seq INTEGER NOT NULL DEFAULT -1,
                        last_digest TEXT NOT NULL DEFAULT '', sync_interval_s INTEGER NOT NULL DEFAULT 15,
                        stopped INTEGER NOT NULL DEFAULT 0, protocol_version INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO micromound_mounds (mound_id, name, tier) VALUES ('mm-legacy', 'Legacy', 'edge_queen');
                    CREATE TABLE micromound_evidence (mound_id TEXT NOT NULL, evidence_id TEXT NOT NULL, item_json TEXT NOT NULL,
                        captured_at TEXT NOT NULL DEFAULT '', PRIMARY KEY (mound_id, evidence_id));
                    INSERT INTO micromound_evidence (mound_id, evidence_id, item_json) VALUES ('mm-legacy', 'ev-legacy', '{""evidence_id"":""ev-legacy""}');
                    CREATE TABLE micromound_actions (mound_id TEXT NOT NULL, action_id TEXT NOT NULL, mission_id TEXT NOT NULL DEFAULT '',
                        record_json TEXT NOT NULL, colony_outcome TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (mound_id, action_id));";
                cmd.ExecuteNonQuery();
            }

            using (var upgraded = new SqliteMoundStore(old))
            {
                var legacy = upgraded.GetMound("mm-legacy");
                Assert.NotNull(legacy);
                Assert.False(legacy.IsRetired);
                Assert.Empty(upgraded.FromRetiredIdentity("mm-legacy"));
                Assert.True(MicromoundRetirement.Retire(upgraded, "mm-legacy", MoundRetirement.Unlinked, null, Now).Retired);
                upgraded.MarkFromRetiredIdentity("mm-legacy", ["ev-legacy"], []);
                Assert.Contains("ev-legacy", upgraded.FromRetiredIdentity("mm-legacy"));
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
