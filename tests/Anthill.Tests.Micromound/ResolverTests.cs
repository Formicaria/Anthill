using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE PHYSICAL CAPABILITY RESOLVER — §18. v0.3.8.114.
///
/// It answers a question and issues nothing: no missions, no envelopes, no state change. That is
/// what makes it safe to expose before the autonomy it serves exists — the Queen can ask whether
/// physical work is possible today, and the answer costs nothing if it then does not ask.
///
/// The §18 worked example is the last fact in this class, asserted as the brief writes it.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class ResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryMoundStore Store() => new();

    /// <summary>An enrolled mound, beating, with a charter granting what it reported.</summary>
    private static void AddMound(InMemoryMoundStore store, string id, string name,
        IReadOnlyList<string> capabilities, AutonomyPolicy policy = AutonomyPolicy.WithinCharter,
        bool chartered = true, DateTimeOffset? lastSeen = null)
    {
        store.UpsertMound(new MoundRecord
        {
            MoundId = id,
            Name = name,
            PublicKey = Convert.ToHexStringLower(Ed25519KeyPair.Generate().PublicKey),
            Capabilities = [.. capabilities],
            SyncIntervalSeconds = 15,
            LastSeen = (lastSeen ?? Now).ToWire(),
            AutonomyPolicy = policy,
            CharterId = chartered ? id + "-charter" : "",
            CharterExpiresAt = chartered ? Now.AddHours(1).ToWire() : "",
            LeaseExpiresAt = chartered ? Now.AddMinutes(15).ToWire() : "",
        });

        if (!chartered) return;

        store.PutCharter(new Charter
        {
            CharterId = id + "-charter",
            MoundId = id,
            ActionCeiling = "benign",
            Capabilities = [.. capabilities],
            LeaseTtlSeconds = 900,
            ExpiresAt = Now.AddHours(1).ToWire(),
        });
    }

    /// <summary>A mound that has everything says so, with nothing blocking it.</summary>
    [Fact]
    public void AMoundThatCanDoTheWork_IsEligible()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature", "act.water_valve"]);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Eligible("sense.temperature", PhysicalOrigin.User, Now));

        Assert.Equal("mm-greenhouse", candidate.MoundId);
        Assert.Equal("online", candidate.Status);
        Assert.Empty(candidate.Blockers);
    }

    /// <summary>
    /// EVERY MOUND COMES BACK, NOT ONLY THE ELIGIBLE ONES. "No mound can do this" and "one mound
    /// could, but its lease lapsed" are different answers, and filtering collapses them into the
    /// same empty result.
    /// </summary>
    [Fact]
    public void TheResolver_ReportsWhyAMoundCannot()
    {
        var store = Store();
        AddMound(store, "mm-workshop", "Workshop", ["act.spindle"]);

        var all = new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now);

        var candidate = Assert.Single(all);
        Assert.False(candidate.Eligible);
        Assert.Contains(candidate.Blockers, b => b.Contains("sense.temperature", StringComparison.Ordinal));
    }

    /// <summary>The §18 shape: the one that has it wins, the one that does not is named and excluded.</summary>
    [Fact]
    public void TheResolver_PicksTheMoundThatHasTheCapability()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature", "sense.soil_moisture"]);
        AddMound(store, "mm-workshop", "Workshop", ["act.spindle"]);

        var eligible = new MicromoundResolver(store).Eligible("sense.temperature", PhysicalOrigin.User, Now);

        Assert.Equal(["mm-greenhouse"], eligible.Select(c => c.MoundId).ToList());
    }

    /// <summary>A stop blocks it, and says so as a stop rather than as a missing capability.</summary>
    [Fact]
    public void AStoppedMound_IsNotEligibleAndSaysWhy()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"]);

        var mound = store.GetMound("mm-greenhouse")!;
        mound.Stopped = true;
        store.UpsertMound(mound);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now));

        Assert.False(candidate.Eligible);
        Assert.Equal("stopped", candidate.Status);
        Assert.Contains(candidate.Blockers, b => b.Contains("stop", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AN OFFLINE MOUND IS NOT ELIGIBLE, AND OFFLINE IS NOT AN ERROR. PROTOCOL.md §1: offline is a
    /// normal state. The blocker names when it was last seen, because that is the fact an operator
    /// acts on.
    /// </summary>
    [Fact]
    public void AnOfflineMound_IsNotEligible()
    {
        var store = Store();
        AddMound(store, "mm-rover", "Rover", ["sense.temperature"], lastSeen: Now.AddHours(-2));

        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now));

        Assert.False(candidate.Eligible);
        Assert.Equal("offline", candidate.Status);
        Assert.Contains(candidate.Blockers, b => b.Contains("offline", StringComparison.Ordinal));
    }

    /// <summary>A mound with no charter may only observe, whatever it physically has.</summary>
    [Fact]
    public void AnUnchartedMound_IsNotEligible()
    {
        var store = Store();
        AddMound(store, "mm-new", "New Micromound", ["sense.temperature"], chartered: false);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now));

        Assert.False(candidate.Eligible);
        Assert.Contains(candidate.Blockers, b => b.Contains("holds no charter", StringComparison.Ordinal));
    }

    /// <summary>
    /// A LAPSED LEASE SAYS "NEEDS AUTHORITY", NOT "IS UNREACHABLE". Renewal is not resumption, and
    /// the two suggest opposite actions to whoever reads it.
    /// </summary>
    [Fact]
    public void AnExpiredLease_BlocksAndSaysItNeedsAuthorityRatherThanAConnection()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"]);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now.AddMinutes(20)));

        Assert.False(candidate.Eligible);
        Assert.Contains(candidate.Blockers, b => b.Contains("fresh authority", StringComparison.Ordinal));
    }

    /// <summary>Capability present on the device but not granted by the charter is still a refusal.</summary>
    [Fact]
    public void ACapabilityTheCharterDoesNotGrant_Blocks()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"]);

        // The device also has a valve, but the charter never mentioned it.
        var mound = store.GetMound("mm-greenhouse")!;
        mound.Capabilities = ["sense.temperature", "act.water_valve"];
        store.UpsertMound(mound);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("act.water_valve", PhysicalOrigin.User, Now));

        Assert.False(candidate.Eligible);
        Assert.Contains(candidate.Blockers, b => b.Contains("does not grant", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE ANSWER DEPENDS ON WHO IS ASKING, which is why origin is a parameter. Returning the
    /// operator's answer to the Queen would promise capacity the dispatcher then refuses, and a
    /// refusal moved later looks like a malfunction rather than a policy.
    /// </summary>
    [Fact]
    public void TheSameMound_ResolvesDifferentlyForTheQueenUnderManualOnly()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"],
            policy: AutonomyPolicy.ManualOnly);

        var resolver = new MicromoundResolver(store);

        Assert.Single(resolver.Eligible("sense.temperature", PhysicalOrigin.User, Now));
        Assert.Empty(resolver.Eligible("sense.temperature", PhysicalOrigin.Queen, Now));
    }

    /// <summary>
    /// AN OWED APPROVAL IS NOT A BLOCKER. The mound can do the work and the colony is willing to
    /// ask — it just asks a person first. Reporting it ineligible would hide every mound an
    /// operator could authorise in one click.
    /// </summary>
    [Fact]
    public void AMoundNeedingApproval_IsStillEligible()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"],
            policy: AutonomyPolicy.ApprovalRequired);

        var candidate = Assert.Single(
            new MicromoundResolver(store).Eligible("sense.temperature", PhysicalOrigin.Queen, Now));

        Assert.Empty(candidate.Blockers);
    }

    /// <summary>
    /// THE MANIFEST OUTRANKS THE DEVICE'S ENROLMENT REPORT once one has been authored — it is the
    /// colony's own view of what the hardware serves, and the charter is written against it.
    /// </summary>
    [Fact]
    public void OnceConfigured_TheManifestIsWhatCounts()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse", ["sense.temperature"]);

        store.PutManifest(new MoundManifest
        {
            ManifestId = "manifest-1",
            MoundId = "mm-greenhouse",
            Capabilities = ["sense.temperature", "sense.humidity"],
        });

        var mound = store.GetMound("mm-greenhouse")!;
        mound.ManifestId = "manifest-1";
        store.UpsertMound(mound);

        // Present per the manifest, but the charter still does not grant it — so the blocker is the
        // charter, which is the honest answer and a different fix.
        var candidate = Assert.Single(
            new MicromoundResolver(store).Resolve("sense.humidity", PhysicalOrigin.User, Now));

        Assert.False(candidate.Eligible);
        Assert.DoesNotContain(candidate.Blockers, b => b.Contains("does not have capability", StringComparison.Ordinal));
        Assert.Contains(candidate.Blockers, b => b.Contains("does not grant", StringComparison.Ordinal));
    }

    /// <summary>
    /// §18's WORKED EXAMPLE, as the brief writes it: `sense.temperature` wanted; the Greenhouse has
    /// it, is online, healthy and chartered; the Workshop does not; the answer is the Greenhouse.
    /// </summary>
    [Fact]
    public void TheBriefsWorkedExample()
    {
        var store = Store();
        AddMound(store, "mm-greenhouse", "Greenhouse Micromound", ["sense.temperature"]);
        AddMound(store, "mm-workshop", "Workshop Micromound", ["act.spindle"]);

        var resolved = new MicromoundResolver(store).Resolve("sense.temperature", PhysicalOrigin.User, Now);

        Assert.Equal(2, resolved.Count);

        // Eligible first, so the answer is the head of the list.
        Assert.Equal("mm-greenhouse", resolved[0].MoundId);
        Assert.True(resolved[0].Eligible);

        Assert.Equal("mm-workshop", resolved[1].MoundId);
        Assert.False(resolved[1].Eligible);
    }
}
