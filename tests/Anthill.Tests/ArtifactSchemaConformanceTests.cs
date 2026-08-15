using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A schema label is a promise, and both ends of it are now checked. v0.3.8.57.
///
/// The store has hashed payloads since v3.8.19, so it could prove a payload had not CHANGED and
/// nothing about whether it was ever right. `EvidenceKinds.SchemaValid` was declared in that same
/// release and produced by nothing — the check was intended and never built, and in the meantime any
/// string could be stored under any schema name and handed to a worker as that type.
///
/// THE DEFECT THIS FOUND. `ForAntKind` mapped the scribe's `docs_patch_set` onto `patch_set`. Their
/// payloads have nothing in common: a patch set is `{ patch_set_id, summary, proposals[] }` and
/// something materialises it; a docs proposal is `{ targets, source_mission, requires_approval }`
/// and the scribe holds no apply permission at all. `SoldierAnt.ReadPatchSetArtifacts` asks the
/// store for this mission's patch sets and reports how many it reviewed — so a docs proposal was
/// swept into a security review of a code change and counted in the total. Writing down the shapes
/// is what made that visible; it had been true since v3.8.20.
/// </summary>
public class ArtifactSchemaConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_schema_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    // -------------------------------------------------------------------------------------------
    // The ledger is complete
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every schema in the vocabulary has a shape decision. A schema added without one would
    /// validate as "unknown" forever — a silent hole in the exact check built to close silent holes.
    /// </summary>
    [Fact]
    public void EverySchemaInTheVocabulary_HasADeclaredShape()
    {
        var undeclared = ArtifactSchemas.All
            .Where(s => !ArtifactSchemaCheck.Shapes.ContainsKey(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            $"these schemas are in the vocabulary with no shape declared: {string.Join(", ", undeclared)}. "
          + "Read the producer and record what it writes — including Unfixed, if nothing produces one yet.");
    }

    /// <summary>
    /// And the shape table invents nothing. An entry for a schema the vocabulary does not contain
    /// would be a shape no artifact can ever have.
    /// </summary>
    [Fact]
    public void TheShapeTable_DeclaresNothingOutsideTheVocabulary()
    {
        var stray = ArtifactSchemaCheck.Shapes.Keys
            .Where(k => !ArtifactSchemas.All.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(stray.Count == 0, $"shapes declared for schemas not in the vocabulary: {string.Join(", ", stray)}");
    }

    // -------------------------------------------------------------------------------------------
    // The shapes match the real producers
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The payloads the colony actually writes conform. This is the test that keeps the shape table
    /// honest: a shape drawn from what would be nice rather than from the producer fails here, on
    /// every correct artifact in the store.
    /// </summary>
    [Theory]
    // ExecutionService.RecordPatchArtifact
    [InlineData(ArtifactSchemas.PatchSet, """{"patch_set_id":"ps-1","summary":"s","proposals":[]}""")]
    // ScribeAnt's docs proposal — its own schema since v0.3.8.57, and note it would FAIL as a patch_set.
    [InlineData(ArtifactSchemas.DocsPatchSet, """{"targets":["docs/X.md"],"source_mission":"m1","requires_approval":true}""")]
    [InlineData(ArtifactSchemas.VerificationBundle, """{"patch_set_id":"ps-1","proposals":[]}""")]
    [InlineData(ArtifactSchemas.WorkspaceSnapshot, """{"patch_set_id":"ps-1","applied_tree_hash":"sha256:aa"}""")]
    [InlineData(ArtifactSchemas.FileSet, """{"files":["a.cs"],"read_ok":1,"read_failed":0}""")]
    [InlineData(ArtifactSchemas.SourceSet, """{"query":"q","sources":[]}""")]
    [InlineData(ArtifactSchemas.UiMap, """{"routes":[],"api_calls":[]}""")]
    [InlineData(ArtifactSchemas.FailureContext, """{"failure_class":"Timeout"}""")]
    [InlineData(ArtifactSchemas.MemoryCandidate, """[{"memory_class":"procedural"}]""")]
    // TesterAnt writes KEY: VALUE lines. Declaring this JSON would fail every real test report.
    [InlineData(ArtifactSchemas.TestReport, "checks_run: 2\nchecks_passed: 2")]
    [InlineData(ArtifactSchemas.SecurityReview, "verdict: clean\npatch_artifacts_reviewed: 1")]
    [InlineData(ArtifactSchemas.FailureDiagnosis, "Build failed: missing reference.")]
    [InlineData(ArtifactSchemas.RepairRecommendation, "coder:code_change (single attempt, then fresh checks)")]
    [InlineData(ArtifactSchemas.ReleaseNotes, "Mission: do the thing\nCompleted stages: 3")]
    public void RealProducerPayloads_Conform(string schema, string payload) =>
        Assert.True(ArtifactSchemaCheck.Validate(schema, payload).Conforms,
            ArtifactSchemaCheck.Validate(schema, payload).Reason);

    /// <summary>
    /// The specific confusion, pinned. The scribe's docs payload is not a patch set and the check
    /// says so — this is the assertion that would have failed before the schema was split out, and
    /// the reason the split is not cosmetic.
    /// </summary>
    [Fact]
    public void ADocsProposal_DoesNotConformAsAPatchSet()
    {
        const string docs = """{"targets":["docs/X.md"],"source_mission":"m1","requires_approval":true}""";

        Assert.True(ArtifactSchemaCheck.Validate(ArtifactSchemas.DocsPatchSet, docs).Conforms);

        var asPatchSet = ArtifactSchemaCheck.Validate(ArtifactSchemas.PatchSet, docs);
        Assert.False(asPatchSet.Conforms);
        Assert.Equal(ArtifactSchemaCheck.Conformance.WrongShape, asPatchSet.Status);
        Assert.Contains("proposals", asPatchSet.Reason);
    }

    /// <summary>
    /// And the mapping no longer folds one onto the other, so a consumer asking the store for patch
    /// sets does not receive documentation proposals.
    /// </summary>
    [Fact]
    public void TheAntKindMapping_KeepsDocsProposalsOutOfPatchSets()
    {
        Assert.Equal(ArtifactSchemas.DocsPatchSet, ArtifactSchemas.ForAntKind("docs_patch_set"));
        Assert.NotEqual(ArtifactSchemas.PatchSet, ArtifactSchemas.ForAntKind("docs_patch_set"));
    }

    /// <summary>
    /// A consumer that queries by schema gets only that schema. The soldier is the live caller and
    /// it reports a COUNT of what it reviewed, so an extra row is not merely noise — it is a number
    /// in a security review that overstates what was examined.
    /// </summary>
    [Fact]
    public void QueryingForPatchSets_DoesNotReturnDocsProposals()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "schema separation" });

        store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"patch_set_id":"ps-1","summary":"s","proposals":[]}"""));
        store.Put(Artifact.Create(ArtifactSchemas.DocsPatchSet, "scribe", "m1",
            """{"targets":["docs/X.md"],"source_mission":"m1","requires_approval":true}"""));

        Assert.Single(store.ForMission("m1", ArtifactSchemas.PatchSet));
        Assert.Single(store.ForMission("m1", ArtifactSchemas.DocsPatchSet));
    }

    // -------------------------------------------------------------------------------------------
    // Refusals
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void MalformedJson_IsReportedAsMalformedNotAsWrongShape()
    {
        var result = ArtifactSchemaCheck.Validate(ArtifactSchemas.PatchSet, "{not json");
        Assert.Equal(ArtifactSchemaCheck.Conformance.Malformed, result.Status);
    }

    [Fact]
    public void AJsonArrayWhereAnObjectIsExpected_IsWrongShape() =>
        Assert.Equal(ArtifactSchemaCheck.Conformance.WrongShape,
            ArtifactSchemaCheck.Validate(ArtifactSchemas.PatchSet, "[]").Status);

    [Fact]
    public void AnEmptyPayload_NeverConforms()
    {
        // Including for narrative schemas: an artifact of nothing is not a record of anything.
        Assert.Equal(ArtifactSchemaCheck.Conformance.Empty,
            ArtifactSchemaCheck.Validate(ArtifactSchemas.TestReport, "   ").Status);
        Assert.Equal(ArtifactSchemaCheck.Conformance.Empty,
            ArtifactSchemaCheck.Validate(ArtifactSchemas.PatchSet, "").Status);
    }

    [Fact]
    public void AnUnknownSchema_IsReportedRatherThanAccepted() =>
        Assert.Equal(ArtifactSchemaCheck.Conformance.UnknownSchema,
            ArtifactSchemaCheck.Validate("not_a_real_schema", "anything").Status);

    /// <summary>
    /// A schema with no producer is UNDECIDED, not invalid. Reporting a violation for every read of
    /// something nobody writes would train a reader to ignore the report, which costs more than the
    /// check gains.
    /// </summary>
    [Fact]
    public void ASchemaWithNoProducer_IsUndecidedRatherThanViolated()
    {
        var result = ArtifactSchemaCheck.Validate(ArtifactSchemas.ChangePlan, "anything at all");
        Assert.Equal(ArtifactSchemaCheck.Conformance.ShapeUndecided, result.Status);
        Assert.True(result.Conforms);
    }

    // -------------------------------------------------------------------------------------------
    // Both boundaries
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// WRITE boundary: the store keeps the artifact and says what is wrong with it.
    ///
    /// Keeping it is the deliberate half. A producer with an off-shape payload has made a mistake
    /// worth surfacing loudly, but dropping the row trades a wrong artifact for a missing one, and
    /// the consumer of a missing artifact simply proceeds with less and never knows.
    /// </summary>
    [Fact]
    public void TheWriteBoundary_StoresTheArtifactAndReportsTheViolation()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "write boundary" });

        var id = store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1", "this is not a patch set"));

        Assert.NotNull(store.Get(id));

        Assert.NotEmpty(memory.GetRecentEvents(100, "artifact_schema_violation", "m1"));
    }

    /// <summary>
    /// And a conforming artifact is quiet. A check that fires on correct input is one every reader
    /// learns to skip.
    /// </summary>
    [Fact]
    public void TheWriteBoundary_SaysNothingAboutAConformingArtifact()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "quiet path" });

        store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"patch_set_id":"ps-1","summary":"s","proposals":[]}"""));

        Assert.Empty(memory.GetRecentEvents(100, "artifact_schema_violation", "m1"));
    }

    /// <summary>
    /// The report can never fail the write.
    ///
    /// Found by two existing tests on the first run of the write boundary: `events` carries a foreign
    /// key to missions(id) and `artifacts` does not, so an artifact stored against a mission with no
    /// row made the violation log throw and took the Put down with it. A diagnostic that breaks the
    /// operation it describes turns "this payload is the wrong shape" into "the artifact was never
    /// stored" — the harder failure, and the one nobody asked for.
    /// </summary>
    [Fact]
    public void AViolationReportThatCannotBeRecorded_StillDoesNotFailThePut()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;

        // Deliberately NO SaveMission: the mission row the event would reference does not exist.
        var id = store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "orphan-mission", "not a patch set"));

        Assert.NotNull(store.Get(id));
    }

    /// <summary>
    /// READ boundary: the worker is told what it is holding.
    ///
    /// This is the half that matters most. A malformed payload under a type label does not fail a
    /// worker — it gets consumed, and the worker produces confident output about a shape that was
    /// never there. The warning rides with the artifact, not in a summary at the top, because
    /// "something in this block is bad" is not actionable and "THIS one is bad" is.
    /// </summary>
    [Fact]
    public void TheReadBoundary_WarnsTheConsumerAboutANonConformingPayload()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "read boundary" });

        store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1", "PROSE-PRETENDING-TO-BE-A-PATCH-SET"));

        var block = ArtifactContext.Compile(store, "m1", 20_000);

        Assert.Contains("PROSE-PRETENDING-TO-BE-A-PATCH-SET", block);
        Assert.Contains("does not match its schema", block);
    }

    /// <summary>
    /// A conforming block carries no warning — otherwise the warning is noise and gets ignored,
    /// which is the same as not having one.
    /// </summary>
    [Fact]
    public void TheReadBoundary_IsSilentWhenEverythingConforms()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "clean block" });

        store.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"patch_set_id":"ps-1","summary":"s","proposals":[]}"""));

        Assert.DoesNotContain("does not match its schema", ArtifactContext.Compile(store, "m1", 20_000));
    }
}
