using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ADR-004's artifact and evidence stores. v3.8.19.
///
/// The store ships with no producer — ants still pass prose — so these tests are the only thing
/// exercising it. That makes them the specification rather than a safety net, and they are written
/// against ADR-004's five verification items rather than against the implementation.
/// </summary>
public class ArtifactStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anthill-artifacts-{Guid.NewGuid():N}.db");
    private readonly SqliteMemory _memory;
    private readonly IArtifactStore _artifacts;
    private readonly IEvidenceStore _evidence;

    public ArtifactStoreTests()
    {
        _memory = new SqliteMemory(_dbPath);
        _artifacts = _memory;
        _evidence = _memory;
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private Artifact Put(string schema, string payload, params string[] sources)
    {
        var artifact = Artifact.Create(schema, "coder", "m1", payload, taskId: "t1", sourceArtifactIds: sources);
        _artifacts.Put(artifact);
        return artifact;
    }

    // ---- "every task input and output is traceable through artifact ids" -------------------------

    [Fact]
    public void AnArtifactRoundTrips_WithItsProvenanceIntact()
    {
        var stored = Put(ArtifactSchemas.ChangePlan, """{"steps":["one"]}""");

        var read = _artifacts.Get(stored.Id);

        Assert.NotNull(read);
        Assert.Equal(ArtifactSchemas.ChangePlan, read!.Schema);
        Assert.Equal("coder", read.ProducerRole);
        Assert.Equal("m1", read.MissionId);
        Assert.Equal("t1", read.TaskId);
        Assert.Equal(stored.Payload, read.Payload);
    }

    /// <summary>The graph, both directions — ADR-004's "who produced it and what consumed it".</summary>
    [Fact]
    public void TheDependencyGraphIsTraversableBothWays()
    {
        var map = Put(ArtifactSchemas.RepositoryMap, "{}");
        var plan = Put(ArtifactSchemas.ChangePlan, """{"from":"map"}""", map.Id);

        var sources = _artifacts.SourcesOf(plan.Id);
        var consumers = _artifacts.ConsumersOf(map.Id);

        Assert.Single(sources, a => a.Id == map.Id);
        Assert.Single(consumers, a => a.Id == plan.Id);
    }

    /// <summary>An artifact citing nothing has no sources — and does not throw looking for them.</summary>
    [Fact]
    public void ARootArtifactHasNoSources() =>
        Assert.Empty(_artifacts.SourcesOf(Put(ArtifactSchemas.RepositoryMap, "{}").Id));

    // ---- "artifact hashes detect mutation or stale input" ---------------------------------------

    [Fact]
    public void TheHashIsComputedFromThePayload_AndDetectsMutation()
    {
        var original = Artifact.Create(ArtifactSchemas.TestReport, "verifier", "m1", "passed: 12");

        Assert.True(original.IsIntact());
        Assert.False((original with { Payload = "passed: 13" }).IsIntact());
    }

    /// <summary>
    /// Identical payloads hash identically — which is what makes "did my input change under me"
    /// answerable — while remaining separate artifacts. Two tasks independently reaching the same
    /// conclusion is a fact worth keeping, not a duplicate to collapse.
    /// </summary>
    [Fact]
    public void SamePayloadSameHash_ButStillTwoArtifacts()
    {
        var a = Put(ArtifactSchemas.FileSet, """["a.cs"]""");
        var b = Put(ArtifactSchemas.FileSet, """["a.cs"]""");

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.Id, b.Id);
    }

    // ---- filtering, which is the query a consumer actually makes ---------------------------------

    [Fact]
    public void ArtifactsFilterByMissionAndSchema()
    {
        Put(ArtifactSchemas.ChangePlan, "{}");
        Put(ArtifactSchemas.TestReport, "{}");

        Assert.Equal(2, _artifacts.ForMission("m1").Count);
        Assert.Single(_artifacts.ForMission("m1", ArtifactSchemas.TestReport));
        Assert.Empty(_artifacts.ForMission("no-such-mission"));
    }

    // ---- evidence, and the distinction the whole verification model rests on ---------------------

    [Fact]
    public void EvidenceAttachesToAnArtifactAndReadsBack()
    {
        var patch = Put(ArtifactSchemas.PatchSet, """{"files":1}""");
        _evidence.Put(Evidence.Create(
            EvidenceKinds.TestRun, deterministic: true, passed: true, "m1",
            artifactIds: new[] { patch.Id }, detail: "12 passed"));

        var found = _evidence.ForArtifact(patch.Id);

        Assert.Single(found);
        Assert.Equal(EvidenceKinds.TestRun, found[0].Kind);
        Assert.True(found[0].Deterministic);
    }

    /// <summary>
    /// The rule v2.26.0 established, now a property of the store: a model's opinion, however
    /// confident, cannot carry a mission to a verified outcome. Only reproducible evidence counts.
    /// </summary>
    [Fact]
    public void AModelReviewIsNotADeterministicPass()
    {
        _evidence.Put(Evidence.Create(
            EvidenceKinds.ModelReview, deterministic: false, passed: true, "m1", detail: "looks good to me"));

        Assert.False(_evidence.HasDeterministicPass("m1"));

        _evidence.Put(Evidence.Create(
            EvidenceKinds.Build, deterministic: true, passed: true, "m1"));

        Assert.True(_evidence.HasDeterministicPass("m1"));
    }

    [Fact]
    public void AFailingDeterministicCheckIsNotAPass()
    {
        _evidence.Put(Evidence.Create(
            EvidenceKinds.Build, deterministic: true, passed: false, "m1", detail: "CS1002"));

        Assert.False(_evidence.HasDeterministicPass("m1"));
    }

    /// <summary>
    /// The kind and the flag must agree. A "test_run" recorded as non-deterministic, or a
    /// "model_review" recorded as deterministic, is a caller mistake that would otherwise decide a
    /// promotion — so the vocabulary knows which of its own kinds are reproducible.
    /// </summary>
    [Theory]
    [InlineData(EvidenceKinds.Build, true, true)]
    [InlineData(EvidenceKinds.TestRun, true, true)]
    [InlineData(EvidenceKinds.ModelReview, false, true)]
    [InlineData(EvidenceKinds.ModelReview, true, false)]
    [InlineData(EvidenceKinds.Build, false, false)]
    public void EvidenceKindsKnowWhichAreReproducible(string kind, bool deterministic, bool agrees) =>
        Assert.Equal(agrees, EvidenceKinds.AgreesWithKind(kind, deterministic));

    /// <summary>
    /// Visibility that cannot be read fails CLOSED. A row whose audience is unparseable is not one to
    /// guess about — Secret is never rendered, so a corrupt value costs a hidden artifact rather than
    /// a leaked one.
    /// </summary>
    [Fact]
    public void UnreadableVisibilityFailsClosed()
    {
        var a = Artifact.Create(ArtifactSchemas.OperatorSummary, "scribe", "m1", "hello");
        _artifacts.Put(a);

        // Corrupted through a direct connection rather than a test-only method on SqliteMemory.
        // Production code should not grow a raw-SQL hatch so a test can reach a branch.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE artifacts SET visibility = 'nonsense' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", a.Id);
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(ArtifactVisibility.Secret, _artifacts.Get(a.Id)!.Visibility);
    }
}
