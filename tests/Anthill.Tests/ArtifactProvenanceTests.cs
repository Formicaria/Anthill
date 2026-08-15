using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// How an artifact came to exist, and an honest account of what still cannot be said. v0.3.8.57.
///
/// `Artifact` recorded the producer ROLE and nothing else about production, so "a coder wrote this
/// patch set" was answerable and "with which model" was not — a 7B and a 70B leave the same row
/// behind. The fix is not to add every field the brief listed. Half of them already exist under other
/// names, and inventing the rest would recreate `RequiredInputArtifactTypes`,
/// `EvidenceKinds.SchemaValid` and `Task.InputArtifactIds`: declared, never populated, indistinguishable
/// from working, and this release has now dug all three out.
///
/// So the deliverable is a LEDGER of the nine facets the brief named, each mapped to where it really
/// lives or recorded as a gap with the reason. A gap written down is a decision; a gap omitted is an
/// accident waiting to be mistaken for a feature.
/// </summary>
public class ArtifactProvenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_prov_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    // -------------------------------------------------------------------------------------------
    // The ledger
    // -------------------------------------------------------------------------------------------

    /// <param name="Property">
    /// The ArtifactProvenance property that carries this facet, or null when the facet is answered
    /// elsewhere in the model. Named EXPLICITLY rather than derived from Name: a first cut derived it
    /// by stripping underscores, which silently looked for "Runtime" and "Environment" while the real
    /// fields are RuntimeNode and EnvironmentFingerprint. A ledger whose lookup is approximate checks
    /// something adjacent to what it claims to check.
    /// </param>
    private sealed record Facet(string Name, bool Recorded, string? Property, string Where);

    /// <summary>
    /// The nine facets the provenance brief asked for. `Recorded: false` is not a to-do — it is a
    /// statement that nothing in the colony produces this, checked below against the type so it
    /// cannot quietly become true (or quietly stay false after someone thinks they fixed it).
    /// </summary>
    private static readonly Facet[] Ledger =
    {
        new("provider", true, nameof(ArtifactProvenance.Provider), "ArtifactProvenance.Provider — the provider that ACTUALLY served the "
          + "call, carried out of ModelResponse. Previously discarded by ToCallResult() one line "
          + "before it reached the ant."),

        new("model", true, nameof(ArtifactProvenance.Model), "ArtifactProvenance.Model, same path. AntMetrics counted ModelCalls and "
          + "never recorded which model made them."),

        new("tool", true, nameof(ArtifactProvenance.Tool), "ArtifactProvenance.Tool, read from the execution's own tool evidence — "
          + "the same read FailureContext uses, so the two agree on what 'the tool' means."),

        new("runtime", true, nameof(ArtifactProvenance.RuntimeNode), "ArtifactProvenance.RuntimeNode and ColonyVersion. Reproduction needs "
          + "to know what to run before it can know where."),

        new("environment", true, nameof(ArtifactProvenance.EnvironmentFingerprint), "ArtifactProvenance.EnvironmentFingerprint — OS family and runtime "
          + "major, the fingerprint FailureContext already carried for failures only."),

        new("limitations", true, nameof(ArtifactProvenance.Limitations), "ArtifactProvenance.Limitations, from AntExecutionResult.Warnings. "
          + "The one brief facet that turned out to have a producer already: the caveats existed and "
          + "died with the execution, so a degraded run's artifact looked like a clean run's."),

        new("sensitivity", true, null, "Artifact.Visibility, since v3.8.25 — Secret / Colony / Operator. "
          + "Adding a 'sensitivity' field would be a second answer to a question already answered, "
          + "and two fields that can disagree about who may read something is worse than one."),

        new("evidence_refs", true, null, "IEvidenceStore.ForArtifact plus Evidence.ArtifactIds, since "
          + "v3.8.19. The edge exists in the evidence table; duplicating it onto the artifact would "
          + "create a second copy that can fall out of step with the first."),

        // ---- genuine gaps ----

        new("assumptions", false, "Assumptions", "NOT PRODUCED. Nothing in the colony emits what a role assumed. "
          + "A field for it would be filled by whoever remembers, which is nobody, and would then "
          + "read as 'this artifact assumed nothing'. Needs a producer first — most plausibly the "
          + "structured core-ant output that is still outstanding."),

        new("retention", false, "Retention", "NOT PRODUCED. Maintenance prunes EVENTS by age; artifacts have no "
          + "retention class and nothing enforces one. A retention label no pruner reads is a "
          + "compliance claim the system does not keep, which is worse than an absent one."),
    };

    /// <summary>
    /// Every facet the ledger claims is recorded must exist on the type; every facet it calls a gap
    /// must NOT. Both directions, so the ledger cannot drift from the code in either.
    /// </summary>
    [Fact]
    public void TheLedgersClaims_MatchTheProvenanceType()
    {
        var properties = typeof(ArtifactProvenance).GetProperties()
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var facet in Ledger)
        {
            // A null Property means the facet is answered elsewhere in the model and deliberately
            // not duplicated here; the test below checks those citations are real.
            if (facet.Property is null) continue;

            var present = properties.Contains(facet.Property);

            Assert.True(present == facet.Recorded,
                facet.Recorded
                    ? $"the ledger says '{facet.Name}' is recorded ({facet.Where}) but ArtifactProvenance "
                      + "has no such field. Either add it back or move the facet to the gaps."
                    : $"ArtifactProvenance now has a '{facet.Name}' field, but the ledger records it as "
                      + $"a gap: {facet.Where}. If it has a real producer now, say so here — a field "
                      + "with no producer is the defect this ledger exists to prevent.");
        }
    }

    /// <summary>
    /// The two facets answered elsewhere really are answered elsewhere. Without this the entries
    /// above are an excuse rather than a citation.
    /// </summary>
    [Fact]
    public void TheFacetsDeclaredAsAnsweredElsewhere_AreActuallyAnsweredElsewhere()
    {
        Assert.Contains("Visibility", typeof(Artifact).GetProperties().Select(p => p.Name));
        Assert.NotNull(typeof(IEvidenceStore).GetMethod("ForArtifact"));

        // And provenance does NOT duplicate them — the reason they are cited rather than copied.
        var provenance = typeof(ArtifactProvenance).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Visibility", provenance);
        Assert.DoesNotContain("Sensitivity", provenance);
        Assert.DoesNotContain("Retention", provenance);
    }

    // -------------------------------------------------------------------------------------------
    // The record survives the round trip
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Provenance_SurvivesTheDatabase()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "provenance" });

        var id = store.Put(Artifact.Create(
            ArtifactSchemas.TestReport, "tester", "m1", "checks_run: 1",
            provenance: new ArtifactProvenance
            {
                ColonyVersion = "0.3.8.57",
                EnvironmentFingerprint = "linux-dotnet9",
                RuntimeNode = "tester",
                Provider = "ollama",
                Model = "qwen2.5-coder:7b",
                Tool = "dotnet_build",
                ModelCalls = 1,
                ToolCalls = 2,
                ModelInvolved = true,
                Limitations = new[] { "provider_failure[Timeout]" },
            }));

        var read = store.Get(id)!.Provenance;

        Assert.NotNull(read);
        Assert.Equal("0.3.8.57", read!.ColonyVersion);
        Assert.Equal("ollama", read.Provider);
        Assert.Equal("qwen2.5-coder:7b", read.Model);
        Assert.Equal("dotnet_build", read.Tool);
        Assert.Equal(2, read.ToolCalls);
        Assert.True(read.ModelInvolved);
        Assert.Equal(new[] { "provider_failure[Timeout]" }, read.Limitations);
    }

    /// <summary>
    /// Provenance is NOT part of the content hash. Two identical outputs produced on two machines
    /// must still hash the same, or the deduplication question the hash exists to answer stops
    /// working the moment origin is recorded.
    /// </summary>
    [Fact]
    public void Provenance_DoesNotChangeTheContentHash()
    {
        var bare = Artifact.Create(ArtifactSchemas.TestReport, "tester", "m1", "checks_run: 1");
        var provenanced = Artifact.Create(ArtifactSchemas.TestReport, "tester", "m1", "checks_run: 1",
            provenance: new ArtifactProvenance { ColonyVersion = "0.3.8.57", RuntimeNode = "elsewhere" });

        Assert.Equal(bare.ContentHash, provenanced.ContentHash);
    }

    /// <summary>
    /// An artifact written before this release reads back as provenance-ABSENT, not as an error and
    /// not as empty provenance. "Nobody recorded this" and "this was recorded as nothing" are
    /// different, and the migration must produce the first.
    /// </summary>
    [Fact]
    public void AnArtifactWithNoProvenance_ReadsBackAsNull()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "legacy" });

        var id = store.Put(Artifact.Create(ArtifactSchemas.TestReport, "tester", "m1", "checks_run: 1"));

        Assert.Null(store.Get(id)!.Provenance);
    }

    [Fact]
    public void UnreadableProvenance_ReadsAsAbsentRatherThanThrowing() =>
        Assert.Null(ArtifactProvenance.FromJson("{not json"));

    // -------------------------------------------------------------------------------------------
    // The producers
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The bridge is where most artifacts are born, and it now stamps origin on all of them.
    /// </summary>
    [Fact]
    public void TheAntBridge_StampsProvenanceOnWhatItStores()
    {
        using var memory = Memory();
        memory.SaveMission(new Mission { Id = "m1", Goal = "bridge provenance" });
        memory.SaveTask("m1", new Task
        {
            Id = "t1", Title = "test", Description = "d", AssignedAnt = "tester",
            TaskType = "test_execution", Status = TaskStatus.Complete,
        });

        memory.SaveTaskResult("m1", "t1", "tester", new AntExecutionResult
        {
            Success = true, StatusCode = "succeeded_with_warnings", Summary = "ran",
            Artifacts = { new AntArtifact("test_report", "Checks", "checks_run: 1\nchecks_passed: 1") },
            Evidence = { new AntEvidence("tool", "dotnet_build") },
            Warnings = { "one check was skipped" },
            Metrics = new AntMetrics
            {
                ModelCalls = 1, ToolCalls = 3,
                Provider = "ollama", Model = "qwen2.5-coder:7b",
                EnvironmentFingerprint = "linux-dotnet9",
            },
        });

        var stored = ((IArtifactStore)memory).ForMission("m1", ArtifactSchemas.TestReport);
        var provenance = Assert.Single(stored).Provenance;

        Assert.NotNull(provenance);
        Assert.Equal("ollama", provenance!.Provider);
        Assert.Equal("qwen2.5-coder:7b", provenance.Model);
        Assert.Equal("dotnet_build", provenance.Tool);
        Assert.Equal("linux-dotnet9", provenance.EnvironmentFingerprint);
        Assert.Equal(3, provenance.ToolCalls);
        Assert.True(provenance.ModelInvolved);
        Assert.Contains("one check was skipped", provenance.Limitations);
    }

    /// <summary>
    /// A deterministic ant records that NO model was involved — a positive fact about the work, not a
    /// hole in the record. This is the distinction `ModelInvolved` exists to keep: a provenance gap
    /// must never be readable as a determinism guarantee.
    /// </summary>
    [Fact]
    public void ADeterministicProducer_RecordsThatNoModelWasInvolved()
    {
        using var memory = Memory();
        memory.SaveMission(new Mission { Id = "m1", Goal = "deterministic" });
        memory.SaveTask("m1", new Task
        {
            Id = "t1", Title = "review", Description = "d", AssignedAnt = "soldier",
            TaskType = "security_review", Status = TaskStatus.Complete,
        });

        memory.SaveTaskResult("m1", "t1", "soldier", new AntExecutionResult
        {
            Success = true, StatusCode = "succeeded", Summary = "reviewed",
            Artifacts = { new AntArtifact("security_review", "Policy review", "verdict: clean") },
            Metrics = new AntMetrics { ModelCalls = 0, ToolCalls = 1 },
        });

        var provenance = Assert.Single(
            ((IArtifactStore)memory).ForMission("m1", ArtifactSchemas.SecurityReview)).Provenance;

        Assert.NotNull(provenance);
        Assert.False(provenance!.ModelInvolved);
        Assert.Null(provenance.Model);
    }

    /// <summary>
    /// The router's answer reaches the caller. `ToCallResult()` dropped provider and model, which is
    /// the whole reason no artifact could name its model — the information was resolved, recorded on
    /// the response, and thrown away one conversion before anything could use it.
    /// </summary>
    [Fact]
    public void TheModelThatServedACall_ReachesTheCaller()
    {
        var response = new Anthill.SDK.Reasoning.ModelResponse
        {
            Status = Anthill.SDK.Reasoning.ModelCallOutcome.Ok,
            Content = "done",
            Provider = "ollama",
            Model = "qwen2.5-coder:7b",
        };

        var call = response.ToCallResult();

        Assert.Equal("ollama", call.Provider);
        Assert.Equal("qwen2.5-coder:7b", call.Model);
    }
}
