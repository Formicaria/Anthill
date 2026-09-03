using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// CHARTERS — PROTOCOL.md §4, and the first authority this colony has ever been able to grant.
/// v0.3.8.114.
///
/// The refusals matter more than the happy path here, and there are more of them for that reason.
/// A charter is the document that lets a mound move something physical; every test below that ends
/// in a refusal is a way the colony declines to sign one, and each maps to a rule the mound would
/// enforce anyway. That duplication is deliberate — the mound is the authority and this is a
/// controller not asking for what it knows is wrong — so these tests are about the colony being a
/// good citizen, never about it being the safety boundary.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class CharterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryMoundStore Store, MicromoundCharters Charters, RecordingEventBus Events)
        Colony(params string[] capabilities)
    {
        var store = new InMemoryMoundStore();
        var events = new RecordingEventBus();

        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-workshop",
            Name = "Workshop",
            PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
            Capabilities = [.. capabilities],
            SyncIntervalSeconds = 15,
        });

        return (store, new MicromoundCharters(store, new MicromoundIdentity(store), events), events);
    }

    private static CharterRequest Request(
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? routines = null,
        string ceiling = "benign",
        IReadOnlyDictionary<string, CapabilityLimits>? limits = null) =>
        new("mm-workshop",
            capabilities ?? ["sense.temperature"],
            routines ?? [],
            ceiling,
            Duration: TimeSpan.FromHours(1),
            LeaseTtl: TimeSpan.FromMinutes(15),
            Limits: limits);

    // -----------------------------------------------------------------------------------------
    // Issuing
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A charter is signed by the colony and verifies under the key id a mound resolves it by. The
    /// document a mound receives has to be one it can actually check.
    /// </summary>
    [Fact]
    public void AnIssuedCharter_IsSignedAsTheController()
    {
        var (store, charters, events) = Colony("sense.temperature", "act.water_valve");

        var issue = charters.Issue(Request(["sense.temperature", "act.water_valve"]), "operator", Now);

        Assert.True(issue.Issued, string.Join("; ", issue.Refusals));
        Assert.NotNull(issue.Charter);
        Assert.NotNull(issue.Envelope);
        Assert.Equal(EnvelopeKinds.Charter, issue.Envelope!.Kind);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller,
            Convert.FromHexString(new MicromoundIdentity(store).PublicKeyHex));

        var check = new Ed25519EnvelopeVerifier(directory)
            .Verify(KeyIds.Controller, issue.Envelope.CanonicalBytes(), issue.Envelope.Signature);

        Assert.True(check.IsValid, check.Describe());
        Assert.True(events.Saw(MicromoundEvents.CharterIssued));
    }

    /// <summary>
    /// IT WAITS IN THE DOWNLINK QUEUE, because the colony never dials a mound. This is the whole
    /// reason a device behind NAT can still be governed — and the reason issuing is not sending.
    /// </summary>
    [Fact]
    public void AnIssuedCharter_WaitsForTheMoundToCollectIt()
    {
        var (store, charters, _) = Colony("sense.temperature");

        Assert.Equal(0, store.PendingDownlinkCount("mm-workshop"));

        charters.Issue(Request(), "operator", Now);

        Assert.Equal(1, store.PendingDownlinkCount("mm-workshop"));

        var drained = store.DrainDownlink("mm-workshop");

        Assert.Single(drained);
        Assert.Equal(EnvelopeKinds.Charter, drained[0].Kind);

        // Drained once. Handing the same authority out twice is how a mound ends up acting on a
        // charter the colony believes it has already superseded.
        Assert.Empty(store.DrainDownlink("mm-workshop"));
    }

    /// <summary>The colony records what it granted, so the console can answer without the device.</summary>
    [Fact]
    public void TheColony_RecordsTheAuthorityItGranted()
    {
        var (store, charters, _) = Colony("sense.temperature");

        var issue = charters.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-workshop")!;
        Assert.Equal(issue.Charter!.CharterId, mound.CharterId);
        Assert.Equal(issue.Charter.ExpiresAt, mound.CharterExpiresAt);
        Assert.False(mound.Quiesced);
        Assert.NotNull(store.GetCharter(issue.Charter.CharterId));
    }

    // -----------------------------------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// `hazardous` is never a legal charter ceiling. Hazardous work is authorized per action and
    /// expires on use; a standing grant of it is the one thing SAFETY.md Layer 2 will not have.
    /// </summary>
    [Fact]
    public void AHazardousCeiling_IsRefused()
    {
        var (_, charters, events) = Colony("act.water_valve");

        var issue = charters.Issue(Request(["act.water_valve"], ceiling: "hazardous"), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("hazardous", StringComparison.Ordinal));
        Assert.True(events.Saw(MicromoundEvents.CharterRefused));
    }

    /// <summary>
    /// A CHARTER IS NOT ISSUED INTO A STOP. §4 says a mound does not accept one while a stop is in
    /// force, "and paperwork must not be able to substitute for" clearing it — issuing into a
    /// stopped mound is that same attempt made from the controller's end.
    /// </summary>
    [Fact]
    public void AStoppedMound_IsNotChartered()
    {
        var (store, charters, _) = Colony("sense.temperature");

        var mound = store.GetMound("mm-workshop")!;
        mound.Stopped = true;
        store.UpsertMound(mound);

        var issue = charters.Issue(Request(), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, store.PendingDownlinkCount("mm-workshop"));
    }

    /// <summary>
    /// NOTHING IS GRANTED THAT THE DEVICE DID NOT REPORT. `Capabilities` on the record is what the
    /// mound said it physically has — a fact, never a grant — and a charter beyond it is one the
    /// mound refuses whole.
    /// </summary>
    [Fact]
    public void ACapabilityTheMoundNeverReported_IsRefused()
    {
        var (_, charters, _) = Colony("sense.temperature");

        var issue = charters.Issue(Request(["sense.temperature", "act.water_valve"]), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("act.water_valve", StringComparison.Ordinal));
    }

    /// <summary>An unenrolled mound has no identity to bind authority to.</summary>
    [Fact]
    public void AnUnenrolledMound_IsNotChartered()
    {
        var store = new InMemoryMoundStore();
        store.UpsertMound(new MoundRecord { MoundId = "mm-new", Name = "New Micromound" });

        var charters = new MicromoundCharters(store, new MicromoundIdentity(store), new RecordingEventBus());

        var issue = charters.Issue(
            new CharterRequest("mm-new", [], [], "observe", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
            "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("not enrolled", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE PROTOCOL'S OWN VALIDATOR IS THE LAST GATE, and this proves it runs rather than being
    /// declared. A routine id among `capabilities` is a drafting error the protocol names
    /// specifically — none of the colony's four pre-checks look for it, so a refusal here can only
    /// have come from `CharterValidator`.
    /// </summary>
    [Fact]
    public void ARoutineIdAmongCapabilities_IsRefusedByTheProtocolValidator()
    {
        var (_, charters, _) = Colony("sense.temperature");

        var issue = charters.Issue(Request(["routine.water_cycle"]), "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals,
            r => r.Contains("routine", StringComparison.Ordinal)
              && r.Contains("capabilities", StringComparison.Ordinal));
    }

    /// <summary>
    /// A limit keyed to something the charter never granted is an error rather than a no-op —
    /// "silently ignoring them is how an operator comes to believe a bound is in force when it is
    /// not." Also the protocol validator's rule, not ours.
    /// </summary>
    [Fact]
    public void ALimitOnSomethingUngranted_IsRefused()
    {
        var (_, charters, _) = Colony("sense.temperature");

        var issue = charters.Issue(
            Request(limits: new Dictionary<string, CapabilityLimits>
            {
                ["act.water_valve"] = new() { MaxOnSeconds = 30 },
            }),
            "operator", Now);

        Assert.False(issue.Issued);
        Assert.Contains(issue.Refusals, r => r.Contains("limits key", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // Leases
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// ACKNOWLEDGING A BEAT IS THE ONLY RENEWAL PATH — PROTOCOL.md §5, and nothing on the device
    /// can extend a lease. So the colony's record is what it granted, not what the mound believes.
    /// </summary>
    [Fact]
    public void RenewingALease_ExtendsItByTheCharterTtl()
    {
        var (store, charters, _) = Colony("sense.temperature");
        charters.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-workshop")!;
        var later = Now.AddMinutes(10);

        Assert.True(charters.RenewLease(mound, later));

        Assert.True(ProtocolTime.TryParse(store.GetMound("mm-workshop")!.LeaseExpiresAt, out var expiry));
        Assert.Equal(later.AddMinutes(15), expiry);
    }

    /// <summary>
    /// AND IT CANNOT OUTLIVE THE DOCUMENT THAT GRANTED IT. A lease renewed past its charter is
    /// authority the charter never gave, arrived at by arithmetic rather than by decision.
    /// </summary>
    [Fact]
    public void ALease_IsClampedToTheChartersOwnExpiry()
    {
        var (store, charters, _) = Colony("sense.temperature");
        var issue = charters.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-workshop")!;

        // Fifty-five minutes in: the 15-minute TTL would reach past the charter's one hour.
        Assert.True(charters.RenewLease(mound, Now.AddMinutes(55)));

        Assert.True(ProtocolTime.TryParse(store.GetMound("mm-workshop")!.LeaseExpiresAt, out var expiry));
        Assert.True(ProtocolTime.TryParse(issue.Charter!.ExpiresAt, out var charterExpiry));
        Assert.Equal(charterExpiry, expiry);
    }

    /// <summary>
    /// PAST THE CHARTER, THERE IS NOTHING TO RENEW. Reconnection resumes nothing: a mound whose
    /// charter has expired needs fresh authority, and renewal is not resumption.
    /// </summary>
    [Fact]
    public void APastCharter_CannotBeRenewedIntoLife()
    {
        var (store, charters, _) = Colony("sense.temperature");
        charters.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-workshop")!;

        Assert.False(charters.RenewLease(mound, Now.AddHours(2)));
    }

    /// <summary>A mound with no charter at all has no lease either — and that is not an expiry.</summary>
    [Fact]
    public void AMoundWithNoCharter_HasNoLeaseToRenew()
    {
        var (store, charters, _) = Colony("sense.temperature");

        var mound = store.GetMound("mm-workshop")!;

        Assert.False(charters.RenewLease(mound, Now));
        Assert.True(MicromoundCharters.LeaseExpired(mound, Now));
    }

    /// <summary>The lease reads expired once its moment passes, and not before.</summary>
    [Fact]
    public void LeaseExpiry_IsReadFromWhatTheColonyGranted()
    {
        var (store, charters, _) = Colony("sense.temperature");
        charters.Issue(Request(), "operator", Now);

        var mound = store.GetMound("mm-workshop")!;

        Assert.False(MicromoundCharters.LeaseExpired(mound, Now.AddMinutes(14)));
        Assert.True(MicromoundCharters.LeaseExpired(mound, Now.AddMinutes(16)));
    }
}
