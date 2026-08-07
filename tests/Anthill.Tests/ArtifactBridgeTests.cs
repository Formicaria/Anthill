using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The store stops being empty. v3.8.20.
///
/// v3.8.19 shipped ADR-004's artifact and evidence stores with no producer. These are the two
/// producers, and both are bridges at an existing chokepoint rather than changes to any ant:
/// <c>SaveTaskResult</c> promotes an ant's declared artifacts into real rows, and
/// <c>ToolRegistry.RunTool</c> records a deterministic tool outcome as evidence.
/// </summary>
public class ArtifactBridgeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anthill-bridge-{Guid.NewGuid():N}.db");
    private readonly SqliteMemory _memory;

    public ArtifactBridgeTests()
    {
        _memory = new SqliteMemory(_dbPath);
        // task_results carries foreign keys to BOTH missions(id) and tasks(id), so both rows have to
        // exist before a result does.
        _memory.SaveMission(new Mission { Id = "m1", Goal = "bridge fixtures" });
        _memory.SaveTask("m1", new Anthill.Core.Domain.Task
        {
            Id = "t1", Title = "do the thing", Description = "d", AssignedAnt = "medic",
            TaskType = "work", Status = TaskStatus.Complete,
        });
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static AntExecutionResult ResultWith(params AntArtifact[] artifacts) =>
        new()
        {
            Success = true, StatusCode = "succeeded", Summary = "done",
            Artifacts = artifacts.ToList(),
        };

    // ---- the artifact bridge ----------------------------------------------

    /// <summary>
    /// A recognised ant kind becomes a first-class row with a hash and a producer — the thing the
    /// JSON blob on task_results could never be.
    /// </summary>
    [Fact]
    public void ARecognisedAntArtifact_BecomesAStoredArtifact()
    {
        _memory.SaveTaskResult("m1", "t1", "medic",
            ResultWith(new AntArtifact("failure_diagnosis", "Why it broke", "the parser threw")));

        var stored = ((IArtifactStore)_memory).ForMission("m1");

        var one = Assert.Single(stored);
        Assert.Equal(ArtifactSchemas.FailureDiagnosis, one.Schema);
        Assert.Equal("medic", one.ProducerRole);
        Assert.Equal("t1", one.TaskId);
        Assert.Equal("the parser threw", one.Payload);
        Assert.True(one.IsIntact());
    }

    /// <summary>
    /// Five of the seven kinds ants already emit map exactly onto schemas declared in v3.8.19,
    /// before this bridge existed. That is the evidence the vocabulary was drawn from the colony
    /// rather than from the ADR alone.
    /// </summary>
    [Theory]
    [InlineData("failure_diagnosis", ArtifactSchemas.FailureDiagnosis)]
    [InlineData("memory_candidate", ArtifactSchemas.MemoryCandidate)]
    [InlineData("security_review", ArtifactSchemas.SecurityReview)]
    [InlineData("test_report", ArtifactSchemas.TestReport)]
    [InlineData("ui_map", ArtifactSchemas.UiMap)]
    [InlineData("repair_recommendation", ArtifactSchemas.RepairRecommendation)]
    [InlineData("docs_patch_set", ArtifactSchemas.PatchSet)]
    public void EveryKindAntsEmit_MapsToASchema(string antKind, string expected) =>
        Assert.Equal(expected, ArtifactSchemas.ForAntKind(antKind));

    /// <summary>
    /// The six CORE ants emit <c>AntArtifact("text", ...)</c> — prose with a label on it. That is
    /// deliberately NOT mapped to a schema, and the reason is the whole point of ADR-004: naming
    /// prose <c>change_plan</c> would produce a row whose type is a claim nobody can rely on, and
    /// "two channels, one wins" is the failure mode the ADR explicitly rejects. Typing the core ants'
    /// output means giving it structure, not giving it a better label.
    /// </summary>
    [Fact]
    public void UntypedAntText_IsNotPromotedToASchema()
    {
        Assert.Null(ArtifactSchemas.ForAntKind("text"));

        _memory.SaveTaskResult("m1", "t1", "researcher",
            ResultWith(new AntArtifact("text", "researcher output", "I read some things")));

        Assert.Empty(((IArtifactStore)_memory).ForMission("m1"));
    }

    [Fact]
    public void AnUnknownKind_IsSkippedRatherThanGuessedAt()
    {
        _memory.SaveTaskResult("m1", "t1", "medic",
            ResultWith(new AntArtifact("something_new", "?", "content")));

        Assert.Empty(((IArtifactStore)_memory).ForMission("m1"));
    }

    /// <summary>
    /// The bridge is a PROJECTION. The blob on task_results stays the authority for the result, so
    /// a task with no artifacts still records perfectly well and writes nothing to the store.
    /// </summary>
    [Fact]
    public void AResultWithNoArtifacts_RecordsCleanlyAndStoresNothing()
    {
        _memory.SaveTaskResult("m1", "t1", "coder", ResultWith());

        Assert.Empty(((IArtifactStore)_memory).ForMission("m1"));
        Assert.NotEmpty(_memory.LoadTaskResult("t1")?.Summary ?? "");
    }

    // ---- the evidence bridge ----------------------------------------------

    private sealed class FakeTool : ITool
    {
        private readonly bool _ok;
        public FakeTool(string name, bool ok) { Name = name; _ok = ok; }
        public string Name { get; }
        public string Description => "fake";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            _ok ? new ToolResult(Name, true, "exit 0")
                : new ToolResult(Name, false, "", "exit 1", FailureClass.TargetRejection);
    }

    /// <summary>
    /// A declared, allowlisted check is the one tool whose outcome is reproducible, so it is the one
    /// tool that produces evidence — and that makes `HasDeterministicPass` true for the first time
    /// anywhere in production.
    /// </summary>
    [Fact]
    public void AnAllowlistedCheck_RecordsDeterministicEvidence()
    {
        var registry = new ToolRegistry(_memory);
        registry.Register(new FakeTool("run_allowlisted_check", ok: true));

        registry.RunTool("run_allowlisted_check", "m1", "t1");

        var evidence = ((IEvidenceStore)_memory).ForMission("m1");
        var one = Assert.Single(evidence);
        Assert.Equal(EvidenceKinds.CommandCheck, one.Kind);
        Assert.True(one.Deterministic);
        Assert.True(one.Passed);
        Assert.True(((IEvidenceStore)_memory).HasDeterministicPass("m1"));
    }

    /// <summary>A failing check is evidence too — evidence of failure, which is not a pass.</summary>
    [Fact]
    public void AFailingCheck_IsRecordedAndIsNotAPass()
    {
        var registry = new ToolRegistry(_memory);
        registry.Register(new FakeTool("run_allowlisted_check", ok: false));

        registry.RunTool("run_allowlisted_check", "m1", "t1");

        Assert.False(Assert.Single(((IEvidenceStore)_memory).ForMission("m1")).Passed);
        Assert.False(((IEvidenceStore)_memory).HasDeterministicPass("m1"));
    }

    /// <summary>
    /// Everything else produces NOTHING. A web search is not reproducible, a shell command runs
    /// whatever it was handed, and a file read reports state rather than testing a claim. Recording
    /// those as evidence would put "the ant looked at something" in the same table as "the suite
    /// passed", which is precisely what the deterministic flag exists to keep apart.
    /// </summary>
    [Theory]
    [InlineData("web_search")]
    [InlineData("shell_command")]
    [InlineData("read_text_file")]
    [InlineData("system_info")]
    public void NonReproducibleTools_ProduceNoEvidence(string toolName)
    {
        var registry = new ToolRegistry(_memory);
        registry.Register(new FakeTool(toolName, ok: true));

        registry.RunTool(toolName, "m1", "t1");

        Assert.Empty(((IEvidenceStore)_memory).ForMission("m1"));
    }

    /// <summary>A tool call outside a mission records nothing — there is nothing to attach it to.</summary>
    [Fact]
    public void ACheckOutsideAMission_RecordsNothing()
    {
        var registry = new ToolRegistry(_memory);
        registry.Register(new FakeTool("run_allowlisted_check", ok: true));

        registry.RunTool("run_allowlisted_check");

        Assert.Empty(((IEvidenceStore)_memory).ForMission("m1"));
    }
}
