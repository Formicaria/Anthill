using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Who read what, and which version of it. v0.3.8.57.
///
/// The store has recorded PRODUCTION in full since v3.8.19 — producer role, task, mission, content
/// hash. Consumption had no record at all. `IArtifactStore.ConsumersOf` reads like the other half and
/// is not: it walks `SourceArtifactIds` to answer "what artifacts were DERIVED from this one", which
/// is lineage between artifacts. A role that reads a patch set and writes prose creates no such edge,
/// so "did the verifier read the patch set, and which version" was unanswerable — and that is the
/// question a replay has to answer to reconstruct a decision.
///
/// RECORDED WHERE DELIVERY HAPPENS. `ArtifactContext.Compile` is the only place that knows what
/// actually reached a worker. A caller knows what it asked for; the budget decides what arrives, and
/// an artifact dropped for space was not consumed. Recording at the call site would produce a ledger
/// of intentions that reads exactly like a ledger of facts.
/// </summary>
public class ConsumptionLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_consume_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static string PutPatch(IArtifactStore store, string missionId, string payload = """{"proposals":[]}""") =>
        store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", missionId, payload));

    // -------------------------------------------------------------------------------------------
    // The ledger records deliveries
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void CompilingABlockForARole_RecordsWhatThatRoleReceived()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "consumption" });
        var id = PutPatch(store, "m1");

        ArtifactContext.Compile(store, "m1", 20_000, consumerRole: "verifier", consumerTaskId: "t1");

        var reads = store.ConsumptionsOf(id);
        var read = Assert.Single(reads);
        Assert.Equal("verifier", read.ConsumerRole);
        Assert.Equal("t1", read.ConsumerTaskId);
        Assert.Equal(ArtifactSchemas.PatchSet, read.Schema);
        Assert.Equal("m1", read.MissionId);
    }

    /// <summary>
    /// The VERSION, not just the artifact. Artifacts are immutable so the id would do today; the
    /// hash makes the row falsifiable, which is the property that matters — a consumption row whose
    /// hash no longer matches its artifact is the only signal that the append-only rule was broken.
    /// </summary>
    [Fact]
    public void TheLedger_RecordsTheHashThatWasActuallyRead()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "versions" });
        var id = PutPatch(store, "m1");

        ArtifactContext.Compile(store, "m1", 20_000, consumerRole: "verifier", consumerTaskId: "t1");

        var read = Assert.Single(store.ConsumptionsOf(id));
        var artifact = store.Get(id)!;

        Assert.Equal(artifact.ContentHash, read.ContentHash);
        Assert.True(read.StillMatches(artifact));
    }

    /// <summary>
    /// Two versions of the same schema are two different consumption records. This is the point of
    /// "role X consumed version Y" — a verifier that read the first patch set and a verifier that
    /// read the revised one made different decisions, and before this they were indistinguishable.
    /// </summary>
    [Fact]
    public void TwoVersions_AreTwoDistinguishableReads()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "two versions" });

        var first = PutPatch(store, "m1", """{"proposals":["a"]}""");
        ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { first },
            consumerRole: "verifier", consumerTaskId: "t1");

        var second = PutPatch(store, "m1", """{"proposals":["a","b"]}""");
        ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { second },
            consumerRole: "verifier", consumerTaskId: "t2");

        Assert.Single(store.ConsumptionsOf(first));
        Assert.Single(store.ConsumptionsOf(second));
        Assert.NotEqual(store.ConsumptionsOf(first)[0].ContentHash,
                        store.ConsumptionsOf(second)[0].ContentHash);
    }

    /// <summary>
    /// A retried task reading the same artifact is ONE relationship observed twice. Inserting a
    /// second row would turn a bounded ledger into a log, and "the verifier read this" would become
    /// a number that grows with retries rather than a fact.
    /// </summary>
    [Fact]
    public void ARepeatedRead_IsCountedRatherThanDuplicated()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "retries" });
        var id = PutPatch(store, "m1");

        for (var attempt = 0; attempt < 3; attempt++)
            ArtifactContext.Compile(store, "m1", 20_000, consumerRole: "verifier", consumerTaskId: "t1");

        var read = Assert.Single(store.ConsumptionsOf(id));
        Assert.Equal(3, read.ReadCount);
        Assert.True(read.LastReadAt >= read.FirstReadAt);
    }

    /// <summary>
    /// Different roles reading the same artifact are different rows — the ledger's whole subject.
    /// </summary>
    [Fact]
    public void DifferentRoles_AreDifferentRows()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "roles" });
        var id = PutPatch(store, "m1");

        ArtifactContext.Compile(store, "m1", 20_000, consumerRole: "verifier", consumerTaskId: "t1");
        ArtifactContext.Compile(store, "m1", 20_000, consumerRole: "soldier", consumerTaskId: "t2");

        Assert.Equal(new[] { "soldier", "verifier" },
            store.ConsumptionsOf(id).Select(c => c.ConsumerRole).OrderBy(r => r, StringComparer.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // The ledger records DELIVERIES, not intentions
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// An artifact the budget dropped was not consumed, and must not appear in the ledger. This is
    /// the assertion that makes the record worth trusting: a ledger that says a worker read
    /// something it never saw is worse than no ledger, because it will be believed.
    /// </summary>
    [Fact]
    public void AnArtifactOmittedForSpace_IsNotRecordedAsRead()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "budget" });

        // Sized so the FIRST fits and the SECOND does not. A budget that drops both would make this
        // pass without distinguishing "recorded only what was delivered" from "recorded nothing".
        var delivered = store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"proposals":[""" + new string('x', 300) + """]}"""));
        var dropped = store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"proposals":[""" + new string('y', 2_000) + """]}"""));

        var block = ArtifactContext.Compile(store, "m1", 1_000,
            declaredInputIds: new[] { delivered, dropped }, consumerRole: "verifier", consumerTaskId: "t1");

        Assert.Contains("omitted for space", block);

        var recorded = store.ConsumptionsForMission("m1").Select(c => c.ArtifactId).ToList();
        Assert.Contains(delivered, recorded);
        Assert.DoesNotContain(dropped, recorded);
    }

    /// <summary>
    /// A declared input that could not be found was not read either. It is REPORTED to the worker —
    /// that is a different fact from being consumed, and conflating them would make the ledger claim
    /// a read of something that does not exist.
    /// </summary>
    [Fact]
    public void AMissingDeclaredInput_IsNotRecordedAsRead()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "missing" });

        var block = ArtifactContext.Compile(store, "m1", 20_000,
            declaredInputIds: new[] { "art-missing" }, consumerRole: "verifier", consumerTaskId: "t1");

        Assert.Contains("NOT FOUND", block);
        Assert.Empty(store.ConsumptionsForMission("m1"));
    }

    /// <summary>
    /// No consumer role means no write. A read path that writes unconditionally would put the CLI,
    /// every test, and every diagnostic view into the ledger as if they were colony roles.
    /// </summary>
    [Fact]
    public void WithoutAConsumerRole_NothingIsRecorded()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "no role" });
        PutPatch(store, "m1");

        ArtifactContext.Compile(store, "m1", 20_000);

        Assert.Empty(store.ConsumptionsForMission("m1"));
    }

    // -------------------------------------------------------------------------------------------
    // The ledger cannot break the thing it describes
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A read for a mission with no row works, and is recorded.
    ///
    /// This is the whole reason `artifact_consumptions` carries NO foreign key to missions(id). The
    /// artifact write path in this same release did have that coupling by way of the event log, and
    /// it turned "this payload is the wrong shape" into "the artifact was never stored". A ledger
    /// that can refuse a row because a parent is absent will eventually refuse the row AND the
    /// operation, and the operation here is a worker receiving its context.
    ///
    /// Named for what it checks. The first draft of this test was called "AnUnrecordableRead..." and
    /// asserted only that the block came back — but the read is perfectly recordable, so the name
    /// described a scenario the test never created. That is the defect class this release keeps
    /// finding, in a test rather than in the code.
    /// </summary>
    [Fact]
    public void AReadForAMissionWithNoRow_SucceedsAndIsRecorded()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;

        var id = PutPatch(store, "orphan");

        var block = ArtifactContext.Compile(store, "orphan", 20_000, consumerRole: "verifier", consumerTaskId: "t1");

        Assert.Contains("TYPED ARTIFACTS", block);
        Assert.Single(store.ConsumptionsOf(id));
    }

    /// <summary>
    /// And when the ledger genuinely cannot be written, the worker still gets its context.
    ///
    /// The store is disposed underneath the compile, so RecordConsumption faults on a real closed
    /// connection rather than on a mock that agrees with my assumptions. What must survive is the
    /// return value: a failed ledger entry leaves a gap in the record, while a thrown one would
    /// leave a worker with nothing to work from.
    /// </summary>
    [Fact]
    public void WhenTheLedgerCannotBeWritten_TheContextIsStillReturned()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "broken ledger" });
        PutPatch(store, "m1");

        var artifacts = store.ForMission("m1");
        Assert.Single(artifacts);

        var block = ArtifactContext.Compile(new UnwritableStore(artifacts), "m1", 20_000,
            consumerRole: "verifier", consumerTaskId: "t1");

        Assert.Contains("TYPED ARTIFACTS", block);
    }

    /// <summary>
    /// A store that reads fine and refuses every write. Narrow on purpose: the only behaviour under
    /// test is that a throwing RecordConsumption does not reach the caller.
    /// </summary>
    private sealed class UnwritableStore(IReadOnlyList<Artifact> artifacts) : IArtifactStore
    {
        public string Put(Artifact artifact) => throw new InvalidOperationException("read-only");
        public Artifact? Get(string artifactId) => artifacts.FirstOrDefault(a => a.Id == artifactId);
        public IReadOnlyList<Artifact> ForMission(string missionId, int limit = 200) => artifacts;
        public IReadOnlyList<Artifact> ForMission(string missionId, string schema, int limit = 200) =>
            artifacts.Where(a => a.Schema == schema).ToList();
        public IReadOnlyList<Artifact> SourcesOf(string artifactId) => Array.Empty<Artifact>();
        public IReadOnlyList<Artifact> ConsumersOf(string artifactId) => Array.Empty<Artifact>();
        public void RecordConsumption(ArtifactConsumption consumption) =>
            throw new InvalidOperationException("the ledger is unavailable");
        public IReadOnlyList<ArtifactConsumption> ConsumptionsOf(string artifactId) =>
            Array.Empty<ArtifactConsumption>();
        public IReadOnlyList<ArtifactConsumption> ConsumptionsForMission(string missionId, int limit = 500) =>
            Array.Empty<ArtifactConsumption>();
    }

    /// <summary>
    /// The dispatch path wires the consumer identity through. `consumerRole` and `consumerTaskId` are
    /// both optional, so every ant compiles unchanged while recording nothing — the ledger would
    /// exist, be tested, and stay permanently empty in production.
    /// </summary>
    [Fact]
    public void EveryContextPacketCallSite_NamesTheTaskItIsReadingFor()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));

        var packets = System.Text.RegularExpressions.Regex.Matches(source, @"BuildContextPacketText\(").Count;
        var named = System.Text.RegularExpressions.Regex.Matches(source, @"consumerTaskId:\s*task\.Id").Count;

        Assert.True(packets > 0, "no context packet call sites found — this guard has stopped guarding anything");
        // The researcher calls ArtifactBlock directly rather than through a packet, and names both
        // its role and its task there; every packet site names its task.
        Assert.True(named >= packets,
            $"{packets} context packet call site(s) but only {named} name their task — an unnamed read "
          + "lands in the ledger as a role with no task, which cannot be tied back to a decision.");
    }
}
