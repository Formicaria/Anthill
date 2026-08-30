using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Contracts;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// CREATED ARTIFACTS, PROVEN TO EXIST. v0.3.8.100, PLAN.md §2b — documents, file transformation
/// and structured data artifacts.
///
/// THE FAILURE THIS CLASS HAS AND RESEARCH DOES NOT. A research answer can cite something never
/// retrieved; a creation answer can DESCRIBE a deliverable that was never made — "I have prepared
/// an onboarding guide covering setup and the twelve roles" is a complete, fluent, checkable-looking
/// sentence that checks out against nothing. The gate for this class is existence and provenance:
/// the created thing is a record (it EXISTS as bytes, not as a description), each stated
/// requirement is traceable INTO those bytes or marked unmet, and every input the creation claims
/// to rest on is something the mission actually holds. For a data analysis, the inputs and the
/// transformation ARE the deliverable's honesty: an analysis that does not say what it read and
/// what it did to it is a conclusion wearing an analysis's clothes.
///
/// AS IN `.99`, WHAT THIS GATE DOES NOT CLAIM: that the content is GOOD, or that a traced
/// requirement is truly SATISFIED by the section it points at. Those are semantic judgments; a
/// model asserting them is not evidence. What is checkable is that the content exists, that the
/// claimed trace resolves into it, that the claimed inputs resolve to held records — traceability,
/// not quality.
///
/// The scripted colony pins the mission path: real Queen, real planner validation, real evaluator,
/// deterministic scripts. The web search is shadowed through `AdoptModuleTools`, the same
/// registration path the composition root uses.
/// </summary>
[Collection("specialist-gates")]
public class CreatedArtifactMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _specialistWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly ActivationTier _tierWas = AnthillRuntime.ActivationTier;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _webSearchWas = AnthillRuntime.EnableWebSearch;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public CreatedArtifactMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-created-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSpecialistAntExecution = _specialistWas;
        AnthillRuntime.ActivationTier = _tierWas;
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.EnableWebSearch = _webSearchWas;
        AnthillRuntime.EnableObjectiveVerification = _objectiveWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The same ask, phrased three ways — including the "write" phrasing, deliberately: an operator
    /// who says "write me a document" means the document, and an implementation that only recognises
    /// the polite paraphrases has special-cased a fixture (the `.98` lesson, again).
    /// </summary>
    public static TheoryData<string> EquivalentDocumentRequests => new()
    {
        "Write a short onboarding document for new ANTHILL operators covering first-run setup and the twelve roles.",
        "Prepare an onboarding guide for someone new to operating ANTHILL: setup first, then the twelve roles.",
        "Put together a brief operator onboarding document — how to get running, and what the twelve roles are.",
    };

    /// <summary>Deterministic search stand-in, identical in role to `.99`'s: known sources in advance.</summary>
    private sealed class FakeWebSearchTool : ITool
    {
        public string Name => "web_search";
        public string Description => "deterministic search fixture";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, true, Anthill.SDK.Common.Json.Dumps(new
            {
                results = new[]
                {
                    new
                    {
                        title = "Ollama — run large language models locally",
                        url = "https://ollama.com/",
                        snippet = "Actively developed, with frequent releases.",
                    },
                    new
                    {
                        title = "llama.cpp — inference in plain C/C++",
                        url = "https://github.com/ggerganov/llama.cpp",
                        snippet = "A very active contributor base.",
                    },
                },
            }, indented: true));
    }

    // -------------------------------------------------------------------------------------------
    // The positive gates
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DOCUMENT GATE. The mission produces a `created_artifact` record whose content EXISTS,
    /// whose stated requirements each trace into that content or stand visibly unmet, and whose
    /// answer is the document itself — not a description of one.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalentDocumentRequests))]
    public void ADocumentRequest_ProducesARecordThatExists_WithRequirementsTracedOrMarked(string request)
    {
        var run = RunColony(request, DocumentScript());
        using var memory = run.Memory;

        // ---- 1. THE CREATED THING IS A RECORD, AND IT EXISTS ------------------------------------
        var record = CreatedRecord(memory, run.MissionId);
        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record!.Content),
            "the creation record exists but its content is empty — a deliverable that is a "
          + "description of itself.");
        Assert.Contains("First-run setup", record.Content, StringComparison.Ordinal);
        Assert.Equal(CreatedArtifact.KindDocument, record.Kind);

        // ---- 2. EVERY STATED REQUIREMENT IS TRACED OR MARKED ------------------------------------
        //
        // The scripted builder states three requirements and deliberately leaves one unmet. The
        // unmet one must SURVIVE into the record, marked: a deliverable that looks fully
        // requirement-complete because the unmet ones were deleted is worse than one that admits
        // the gap — the same rule `.99` pinned for unsourced claims.
        Assert.Equal(3, record.Requirements.Count);
        Assert.Equal(2, record.Requirements.Count(r => !r.Unmet));
        var unmet = Assert.Single(record.Requirements.Where(r => r.Unmet));
        Assert.Contains("troubleshooting", unmet.Text, StringComparison.OrdinalIgnoreCase);
        Assert.All(record.Requirements.Where(r => !r.Unmet), r =>
            Assert.Contains(r.Where!, record.Content, StringComparison.OrdinalIgnoreCase));

        // ---- 3. THE ANSWER THE OPERATOR READS IS THE DOCUMENT, GAP INCLUDED ---------------------
        var mission = memory.GetMission(run.MissionId);
        var answer = mission?.GetValueOrDefault("final_result")?.ToString() ?? "";
        Assert.Contains("First-run setup", answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(MarksUnmetRequirements(answer),
            $"the answer does not mark its unmet requirement — an operator cannot tell what the "
          + $"deliverable admits it lacks.\n\nAnswer:\n{answer}");

        // ---- 4. JUDGED AGAINST THE OBJECTIVE ----------------------------------------------------
        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"the document mission did not reach a positive canonical evaluation: "
          + $"{evaluation.OutcomeCode} — {evaluation.Explanation}");
    }

    /// <summary>
    /// THE DATA-ANALYSIS GATE, which is the exit line's second clause verbatim: "a data analysis
    /// records input identity and transformation". The record's inputs must resolve to artifacts
    /// this mission actually holds — id AND content hash, stamped by the deterministic layer, not
    /// asserted by the model — and the transformation account must be present.
    /// </summary>
    [Fact]
    public void ADataAnalysis_RecordsInputIdentity_AndItsTransformation()
    {
        var run = RunColony(
            "Analyse the retrieved information about local model runtimes and compare their activity.",
            AnalysisScript());
        using var memory = run.Memory;

        var record = CreatedRecord(memory, run.MissionId);
        Assert.NotNull(record);
        Assert.Equal(CreatedArtifact.KindDataAnalysis, record!.Kind);

        // INPUT IDENTITY: the builder referenced its input by schema ("what I read was the source
        // set"); the deterministic layer resolved that to concrete artifact ids and hashes. Each
        // must match a row the store actually holds — identity, not description.
        Assert.NotEmpty(record.Inputs);
        var held = ((IArtifactStore)memory).ForMission(run.MissionId)
            .ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
        Assert.All(record.Inputs, input =>
        {
            Assert.False(string.IsNullOrWhiteSpace(input.ArtifactId),
                $"input '{input.Reference}' was never resolved to a held artifact.");
            Assert.True(held.TryGetValue(input.ArtifactId!, out var row),
                $"input artifact '{input.ArtifactId}' is not held by this mission.");
            Assert.Equal(ArtifactSchemas.SourceSet, row!.Schema);
            Assert.Equal(row.ContentHash, input.ContentHash);
        });

        // THE TRANSFORMATION IS RECORDED — what was done to the inputs, step by step.
        Assert.True(record.Transformation.Count >= 2,
            "the analysis records no transformation account — a conclusion, not an analysis.");

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"the analysis mission did not reach a positive canonical evaluation: "
          + $"{evaluation.OutcomeCode} — {evaluation.Explanation}");
    }

    // -------------------------------------------------------------------------------------------
    // The negatives that give the positives their meaning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A FABRICATED INPUT FAILS THE MISSION. The builder claims its document rests on a
    /// `filesystem_snapshot` this mission never produced — the creation-class twin of `.99`'s
    /// invented citation, and equally invisible to an operator reading the answer: a provenance
    /// line naming a record looks exactly like a provenance line naming a real one.
    /// </summary>
    [Fact]
    public void AFabricatedInput_FailsTheMission_ByName()
    {
        var run = RunColony(
            "Prepare an onboarding guide for someone new to operating ANTHILL: setup first, then the twelve roles.",
            FabricatedInputScript());
        using var memory = run.Memory;

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            $"a creation claiming an input the mission never held reached a positive outcome: "
          + $"{evaluation.Explanation}");

        // FOR THE RIGHT REASON, named: the gate, and the input it refused.
        Assert.Contains("creation integrity", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filesystem_snapshot", evaluation.Explanation, StringComparison.Ordinal);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// A CREATION TASK THAT ANSWERS IN PROSE HAS CREATED NOTHING CHECKABLE. The plan typed the
    /// work as document creation; the builder returned a fluent description with no deliverable
    /// record. Without this negative, "the model wrote about a document" and "the mission produced
    /// a document" grade identically — which is the exact sentence this slice exists to end.
    /// </summary>
    [Fact]
    public void ACreationTaskAnsweredInProse_DoesNotGradeAsACreatedArtifact()
    {
        var run = RunColony(
            "Put together a brief operator onboarding document — how to get running, and what the twelve roles are.",
            ProseScript());
        using var memory = run.Memory;

        Assert.Null(CreatedRecord(memory, run.MissionId));

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            "a creation-typed mission with no creation record graded positive — a description of "
          + "a deliverable was accepted as the deliverable.");
        Assert.Contains("creation integrity", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    // -------------------------------------------------------------------------------------------
    // The gate's own edges, checked directly (no colony run needed)
    // -------------------------------------------------------------------------------------------

    private static Artifact Row(string id, string schema, string payload, string hash = "sha256:x") => new()
    {
        Id = id, MissionId = "m1", Schema = schema, ProducerRole = "builder",
        ContentHash = hash, Visibility = ArtifactVisibility.Colony, Payload = payload,
    };

    private static string RecordJson(string kind = CreatedArtifact.KindDocument,
        string content = "# Guide\n\n## Setup\nwords", CreatedInput[]? inputs = null,
        string[]? transformation = null, RequirementTrace[]? requirements = null) =>
        new CreatedArtifact(
            Kind: kind, Title: "t",
            Requirements: requirements ?? new[] { new RequirementTrace("explain setup", "## Setup", false) },
            Inputs: inputs ?? Array.Empty<CreatedInput>(),
            Transformation: transformation ?? Array.Empty<string>(),
            Content: content).ToJson();

    /// <summary>
    /// The store-unreadable asymmetry, same as `.99`'s and for the same reason: a null store means
    /// "cannot check", and a gate that converts "cannot check" into "guilty" would fail every
    /// mission a storage outage touches. The layers that catch contradiction, catch that.
    /// </summary>
    [Fact]
    public void AnUnreadableStore_MeansTheGateDoesNotApply() =>
        Assert.False(CreationIntegrity.Applies(new[] { "document_creation" }, artifacts: null));

    /// <summary>A READABLE store with no record, on a creation-typed mission, is the failure.</summary>
    [Fact]
    public void ACreationTask_WithNoRecord_Fails()
    {
        Assert.True(CreationIntegrity.Applies(new[] { "document_creation" }, Array.Empty<Artifact>()));
        var result = CreationIntegrity.Evaluate(new[] { "document_creation" }, Array.Empty<Artifact>());
        Assert.False(result.Satisfied);
        Assert.Contains("no created_artifact record", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARecordWithEmptyContent_DoesNotExist()
    {
        var rows = new[] { Row("c1", ArtifactSchemas.CreatedArtifact, RecordJson(content: "  ")) };
        var result = CreationIntegrity.Evaluate(new[] { "document_creation" }, rows);
        Assert.False(result.Satisfied);
        Assert.Contains("empty", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A trace pointing at a section the content does not contain is a fabricated trace.</summary>
    [Fact]
    public void ARequirementTracedToAMissingSection_Fails()
    {
        var rows = new[] { Row("c1", ArtifactSchemas.CreatedArtifact, RecordJson(
            requirements: new[] { new RequirementTrace("explain teardown", "## Teardown", false) })) };
        var result = CreationIntegrity.Evaluate(new[] { "document_creation" }, rows);
        Assert.False(result.Satisfied);
        Assert.Contains("## Teardown", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Unmet requirements are counted, not fatal: an admitted gap is the honest record.</summary>
    [Fact]
    public void AnUnmetRequirement_IsKept_NotFatal()
    {
        var rows = new[] { Row("c1", ArtifactSchemas.CreatedArtifact, RecordJson(
            requirements: new[]
            {
                new RequirementTrace("explain setup", "## Setup", false),
                new RequirementTrace("add an appendix", null, true),
            })) };
        var result = CreationIntegrity.Evaluate(new[] { "document_creation" }, rows);
        Assert.True(result.Satisfied, result.Explanation);
        Assert.Equal(1, result.Unmet);
    }

    /// <summary>The exit line's second clause, negatively: an analysis with no identified inputs
    /// or no transformation account fails — whichever half is missing.</summary>
    [Fact]
    public void ADataAnalysis_WithoutInputsOrTransformation_Fails()
    {
        var noInputs = CreationIntegrity.Evaluate(new[] { "data_analysis" },
            new[] { Row("c1", ArtifactSchemas.CreatedArtifact, RecordJson(
                kind: CreatedArtifact.KindDataAnalysis,
                transformation: new[] { "grouped rows" })) });
        Assert.False(noInputs.Satisfied);
        Assert.Contains("input", noInputs.Explanation, StringComparison.OrdinalIgnoreCase);

        var noTransform = CreationIntegrity.Evaluate(new[] { "data_analysis" },
            new[]
            {
                Row("s1", ArtifactSchemas.SourceSet, "{}", hash: "sha256:s1"),
                Row("c1", ArtifactSchemas.CreatedArtifact, RecordJson(
                    kind: CreatedArtifact.KindDataAnalysis,
                    inputs: new[] { new CreatedInput("schema:source_set", "s1", ArtifactSchemas.SourceSet, "sha256:s1") })),
            });
        Assert.False(noTransform.Satisfied);
        Assert.Contains("transformation", noTransform.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static ScriptBook DocumentScript() => Script(DocumentPlan, DocumentDeliverable);
    private static ScriptBook AnalysisScript() => Script(AnalysisPlan, AnalysisDeliverable);
    private static ScriptBook FabricatedInputScript() => Script(DocumentPlan, FabricatedInputDeliverable);
    private static ScriptBook ProseScript() => Script(DocumentPlan,
        "SCRIPTED: I have prepared a thorough onboarding document covering first-run setup and "
      + "the twelve roles. It walks a new operator through installation and then introduces each "
      + "specialist ant in turn.");

    private static ScriptBook Script(string plan, string builderAnswer) => new ScriptBook()
        .Role("planner", plan)
        .Role("researcher", "SCRIPTED: gathered the internal context the deliverable needs.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("builder", builderAnswer)
        .Role("verifier", "Verification Passed: the deliverable record is present and traced.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string DocumentPlan = """
        {
          "tasks": [
            {
              "title": "Gather context for the document",
              "description": "Collect what the onboarding document must cover.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Create the onboarding document",
              "description": "Write the document, tracing each stated requirement into it.",
              "assigned_ant": "builder",
              "task_type": "document_creation",
              "depends_on": []
            },
            {
              "title": "Verify the deliverable",
              "description": "Check the created record exists and its requirements are traced.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    private const string AnalysisPlan = """
        {
          "tasks": [
            {
              "title": "Frame the analysis",
              "description": "Identify what data the comparison needs.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Retrieve the data",
              "description": "Run read-only external research and save source records.",
              "assigned_ant": "web",
              "task_type": "external_research",
              "depends_on": []
            },
            {
              "title": "Analyse the retrieved data",
              "description": "Compare runtime activity, recording inputs and transformation.",
              "assigned_ant": "builder",
              "task_type": "data_analysis",
              "depends_on": []
            },
            {
              "title": "Verify the analysis",
              "description": "Check input identity and the transformation account.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    /// <summary>
    /// The builder's deliverable dialect. One requirement is deliberately [UNMET] — the gate must
    /// see it KEPT and MARKED. Inputs: none, honestly — this document rests on nothing retrieved.
    /// </summary>
    private const string DocumentDeliverable = """
        DELIVERABLE: Operator Onboarding Guide
        KIND: document
        REQUIREMENT: Explain first-run setup [WHERE: ## First-run setup]
        REQUIREMENT: Describe the twelve roles [WHERE: ## The twelve roles]
        REQUIREMENT: Include a troubleshooting appendix [UNMET]
        INPUT: none
        CONTENT:
        # Operator Onboarding Guide

        ## First-run setup
        Install .NET 9, clone the repository, start the API server, and open the console.

        ## The twelve roles
        Twelve specialist ants divide the colony's work, from the planner that shapes a mission
        to the archivist that consolidates what it learned.
        """;

    /// <summary>
    /// The analysis cites its input BY SCHEMA — the typed name of what it read — and the
    /// deterministic layer resolves that to concrete ids and hashes. A model naming a database id
    /// it was never shown would be the `.99` id-citation problem all over again.
    /// </summary>
    private const string AnalysisDeliverable = """
        DELIVERABLE: Local runtime activity analysis
        KIND: data_analysis
        REQUIREMENT: Compare activity across runtimes [WHERE: ## Activity comparison]
        INPUT: schema:source_set
        TRANSFORMATION: Extracted the release-activity statements from each retrieved source.
        TRANSFORMATION: Grouped the runtimes by stated activity and compared the groups.
        CONTENT:
        # Local runtime activity analysis

        ## Activity comparison
        Both retrieved runtimes describe active development; they differ in how the activity is
        described — release cadence for one, contributor base for the other.
        """;

    private const string FabricatedInputDeliverable = """
        DELIVERABLE: Operator Onboarding Guide
        KIND: document
        REQUIREMENT: Explain first-run setup [WHERE: ## First-run setup]
        INPUT: schema:filesystem_snapshot
        CONTENT:
        # Operator Onboarding Guide

        ## First-run setup
        Install .NET 9, clone the repository, start the API server, and open the console.
        """;

    private sealed record ColonyRun(SqliteMemory Memory, string MissionId);

    private ColonyRun RunColony(string request, ScriptBook book)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.EnableWebSearch = true;
        AnthillRuntime.EnableObjectiveVerification = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "web", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"created-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "created-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);
        // The fake search LAST, so it displaces the module's real one — same path as `.99`.
        queen.AdoptModuleTools(new ITool[] { new FakeWebSearchTool() });

        string? missionId = null;
        using var settled = new ManualResetEventSlim(false);
        var runner = new ConversationRunner(memory,
            (goal, _, onCreated, cancel) =>
            {
                try
                {
                    queen.RunMission(goal, onMissionCreated: id => { missionId = id; onCreated(id); }, cancel);
                    return missionId ?? "";
                }
                finally { settled.Set(); }
            });

        runner.Run(conversation, request, ConversationMode.Mission,
            answers: new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" });

        Assert.True(settled.Wait(TimeSpan.FromMinutes(2)),
            "the creation mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return new ColonyRun(memory, missionId!);
    }

    /// <summary>
    /// The mission's creation record, read from the typed artifact production is expected to write.
    /// RED UNTIL THE SLICE LANDS: nothing writes this schema today, so every positive gate above
    /// fails at its first assertion — which is the order the program requires.
    /// </summary>
    private static CreatedArtifact? CreatedRecord(SqliteMemory memory, string missionId) =>
        ((IArtifactStore)memory).ForMission(missionId)
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.CreatedArtifact, StringComparison.OrdinalIgnoreCase))
            .Select(a => CreatedArtifact.FromJson(a.Payload))
            .FirstOrDefault(r => r is not null);

    /// <summary>Substring families, not one spelling — the distinction must be visible, whatever
    /// the vocabulary (the `.98` coverage rule).</summary>
    private static bool MarksUnmetRequirements(string answer) =>
        new[] { "unmet", "not met", "not addressed", "unaddressed", "missing", "lacks" }
            .Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
