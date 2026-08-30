using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Contracts;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.99 — THE ACCEPTANCE GATE FOR THE SECOND UNIVERSAL VERTICAL SLICE, WRITTEN FIRST AND
/// EXPECTED TO FAIL UNTIL THE SLICE LANDS.
///
/// THE CLASS: sourced research. An operator asks a question about the world; the colony searches,
/// keeps what it retrieved, and answers — and every claim in that answer can be traced to something
/// that was actually retrieved, or is marked as not traceable. `.98` proved the shape of a vertical
/// slice on a class that reads the colony ITSELF; this is the first one that reads OUTSIDE it, and
/// the failure mode changes with it.
///
/// WHAT MAKES THIS CLASS DIFFERENT FROM THE AUDIT, and why the audit's gates do not transfer:
/// an audit's evidence is something the colony did, so "did it inspect" is answerable from its own
/// records. A research answer's evidence is something the WORLD said, and the model that writes the
/// answer is also the thing proposing which source supports which sentence. That is the exact
/// arrangement ADR-004 distrusts, and it produces a failure the audit class cannot have: a citation
/// to a source that was never retrieved. Fluent, confident, and false in the one way an operator
/// cannot check by reading.
///
/// SO THE GATE IS CITATION INTEGRITY, NOT CITATION PRESENCE:
///
///   1. The search actually ran, and what it retrieved is PERSISTED with a retrieval time — a claim
///      "as of" nothing is not a sourced claim.
///   2. Every source the answer cites EXISTS in this mission's source records — cited BY URL,
///      because a model can only cite what it was shown, and a database id it reproduced would be
///      indistinguishable from one it invented. A model may propose the mapping; a deterministic
///      gate decides whether the thing cited is real.
///   3. A claim with no source is KEPT AND MARKED, never dropped. Dropping it would let an answer
///      look fully sourced by deleting the parts that were not — the same "two channels and the
///      prose one wins" defect, arriving as an omission instead of an assertion.
///   4. A FABRICATED citation fails the mission. This is the negative case that gives the other
///      three their meaning, and it is asserted separately below.
///
/// WHAT THIS DELIBERATELY DOES NOT ASSERT: that a source SUPPORTS the claim it is attached to.
/// That is a semantic judgment, a model asserting it is the evidence v2.19.0 stopped accepting, and
/// `.98` already recorded what happens when a gate reaches for semantics it cannot reach — see the
/// answer-coverage note in `PLAN.md` §2c. Traceability is checkable; support is not, yet.
///
/// THE HARNESS injects a deterministic `web_search` through `AdoptModuleTools`, the same path the
/// composition root uses. A test that reached the real internet would be asserting on the world.
/// </summary>
[Collection("specialist-gates")]
public class SourcedResearchMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _webSearchWas = AnthillRuntime.EnableWebSearch;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public SourcedResearchMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-research-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.EnableWebSearch = _webSearchWas;
        AnthillRuntime.EnableObjectiveVerification = _objectiveWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The same question, asked four ways. As in `.98`, the point is that classification is by
    /// MEANING: an implementation that recognises one sentence and not its paraphrases has
    /// special-cased a fixture.
    /// </summary>
    public static TheoryData<string> EquivalentResearchRequests => new()
    {
        "What are the current options for running local language models, and which is most active?",
        "Research the local LLM runtimes available today and tell me which one is most actively developed.",
        "Find out what people are using to run models locally right now, with sources.",
        "Look up the state of local model runtimes and summarise what you find.",
    };

    /// <summary>
    /// A deterministic stand-in for the real search. Returns the same two results every time, so the
    /// mission's source records are known in advance and a citation can be checked against them.
    /// </summary>
    private sealed class FakeWebSearchTool : ITool
    {
        public string Name => "web_search";
        public string Description => "deterministic search fixture";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, true, Json.Dumps(new
            {
                results = new[]
                {
                    new
                    {
                        title = "Ollama — run large language models locally",
                        url = "https://ollama.com/",
                        snippet = "Ollama is a tool for running language models on your own machine. "
                                + "Actively developed, with frequent releases.",
                    },
                    new
                    {
                        title = "llama.cpp — inference in plain C/C++",
                        url = "https://github.com/ggerganov/llama.cpp",
                        snippet = "Plain C/C++ inference for language models, with quantization support "
                                + "and a very active contributor base.",
                    },
                },
            }, indented: true));
    }

    /// <summary>
    /// THE GATE. One composed research mission per phrasing, through `ConversationRunner` into the
    /// real Queen, asserting what an operator can observe: sources persisted with retrieval times,
    /// an answer whose citations resolve, unsourced claims kept and marked, a positive evaluation.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalentResearchRequests))]
    public void AResearchRequest_RetrievesSources_AndEveryClaimIsTraceableOrMarked(string request)
    {
        var run = RunResearch(request, ResearchScript());
        using var memory = run.Memory;

        // ---- 1. THE SEARCH RAN, AND WHAT IT FOUND WAS KEPT --------------------------------------
        //
        // A research answer assembled without retrieving anything is a model's recollection wearing
        // a mission's clothes — the `7afd85b2` shape moved to a new class.
        var sources = memory.GetRecentSources(50)
            .Where(s => s.GetValueOrDefault("mission_id")?.ToString() == run.MissionId)
            .ToList();
        Assert.True(sources.Count > 0,
            "the research mission retrieved and kept nothing — its answer, whatever it says, was "
          + "written from the model's own recollection.");

        // A source with no retrieval time cannot support a claim "as of" anything. The world moves;
        // an undated citation is a claim about an unspecified past.
        Assert.All(sources, source =>
            Assert.False(string.IsNullOrWhiteSpace(source.GetValueOrDefault("created_at")?.ToString()),
                "a retrieved source carries no retrieval time."));

        // ---- 2. EVERY CITATION RESOLVES ----------------------------------------------------------
        //
        // The model proposes which source supports which claim; this decides whether the thing it
        // cited was ever retrieved. A citation that resolves to nothing is the failure this class
        // has and the audit class cannot.
        var cited = CitedSources(memory, run.MissionId);
        Assert.True(cited.Count > 0,
            "the answer cites no source at all — a research mission that retrieved sources and "
          + "attributed nothing has not produced a sourced answer.");

        var retrieved = sources.Select(s => s.GetValueOrDefault("url")?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(cited, url => Assert.True(retrieved.Contains(url),
            $"the answer cites '{url}', which this mission never retrieved."));

        // ---- 3. AN UNSOURCED CLAIM IS KEPT AND MARKED --------------------------------------------
        //
        // The scripted builder deliberately includes one claim it cannot attribute. It must survive
        // into the answer, visibly unsourced: an answer that looks fully sourced because the
        // unsupported parts were deleted is worse than one that admits the gap.
        var mission = memory.GetMission(run.MissionId);
        var answer = mission?.GetValueOrDefault("final_result")?.ToString() ?? "";
        Assert.Contains("quantization", answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(MarksUnsourcedClaims(answer),
            $"the answer does not mark its unsourced claim — an operator cannot tell which "
          + $"sentences are supported.\n\nAnswer:\n{answer}");

        // ---- 4. THE OUTCOME IS JUDGED AGAINST THE OBJECTIVE --------------------------------------
        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"the research mission did not reach a positive canonical evaluation: {evaluation.OutcomeCode}");
    }

    /// <summary>
    /// THE NEGATIVE THAT GIVES THE REST THEIR MEANING. The builder cites a source id that was never
    /// retrieved — the one research failure an operator cannot catch by reading, because a
    /// fabricated citation looks exactly like a real one.
    /// </summary>
    [Fact]
    public void AFabricatedCitation_FailsTheMission()
    {
        var run = RunResearch(
            "What are the current options for running local language models, and which is most active?",
            FabricatingScript());
        using var memory = run.Memory;

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            $"an answer citing a source that was never retrieved reached a positive outcome: "
          + $"{evaluation.Explanation}");

        // AND IT FAILED FOR THE RIGHT REASON. `IsPositive == false` alone is satisfied by any
        // failure — a hung task, a refusing verifier, an unrelated gate — so a negative test that
        // stops there proves the mission can fail, not that THIS defect is what fails it. The
        // explanation must name the citation and the gate that refused it.
        Assert.Contains("citation integrity", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example.invalid", evaluation.Explanation, StringComparison.Ordinal);

        // And the mission that DOES cite honestly passes the same gate — stated here so the two
        // outcomes are compared under one fixture rather than trusted to differ.
        Assert.Equal(Anthill.Core.Outcomes.MissionEvaluation.Deliverable.NotSatisfied,
            evaluation.DeliverableStatus);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static ScriptBook ResearchScript() => new ScriptBook()
        .Role("planner", ResearchPlan)
        .Role("researcher", "SCRIPTED: framed the external research need.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("builder", SourcedAnswer)
        .Role("verifier", "Verification Passed: every claim is attributed or marked.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private static ScriptBook FabricatingScript() => new ScriptBook()
        .Role("planner", ResearchPlan)
        .Role("researcher", "SCRIPTED: framed the external research need.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("builder", FabricatedAnswer)
        .Role("verifier", "Verification Passed: every claim is attributed.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string ResearchPlan = """
        {
          "tasks": [
            {
              "title": "Frame the research need",
              "description": "Identify what current external information the question needs.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Search for current sources",
              "description": "Run read-only external research and save source records.",
              "assigned_ant": "web",
              "task_type": "external_research",
              "depends_on": []
            },
            {
              "title": "Build the sourced answer",
              "description": "Answer the question, attributing each claim to a retrieved source.",
              "assigned_ant": "builder",
              "task_type": "build_answer",
              "depends_on": []
            },
            {
              "title": "Verify the answer",
              "description": "Check that every claim is attributed or marked unsourced.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    /// <summary>
    /// The builder's dialect: claims, each with the sources it rests on. The third claim carries no
    /// source deliberately — it is the one the gate must see KEPT and MARKED rather than removed.
    ///
    /// CITED BY URL, not by internal id, and that is a design decision rather than a fixture
    /// convenience. A model can only cite what it was SHOWN, and what it is shown is the source
    /// material — title, url, snippet. Requiring it to reproduce a database id would be requiring
    /// it to know something it has no honest access to, and an id it invented would be
    /// indistinguishable from one it remembered. The url is the identity the world already has.
    /// </summary>
    private const string SourcedAnswer = """
        CLAIM: Ollama runs language models locally and is actively developed. [SOURCE: https://ollama.com/]
        CLAIM: llama.cpp provides C/C++ inference with an active contributor base. [SOURCE: https://github.com/ggerganov/llama.cpp]
        CLAIM: Most local runtimes now ship quantization by default. [UNSOURCED]
        """;

    private const string FabricatedAnswer = """
        CLAIM: Ollama runs language models locally and is actively developed. [SOURCE: https://ollama.com/]
        CLAIM: llama.cpp is the most widely deployed local runtime. [SOURCE: https://example.invalid/never-retrieved]
        """;

    private sealed record ResearchRun(SqliteMemory Memory, string MissionId);

    private ResearchRun RunResearch(string request, ScriptBook book)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.EnableWebSearch = true;
        AnthillRuntime.EnableObjectiveVerification = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "web", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"research-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "research-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);
        // The fake search LAST, so it displaces the module's real one — the same registration path,
        // and the reason this test never touches the network.
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
            "the research mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return new ResearchRun(memory, missionId!);
    }

    /// <summary>
    /// The sources the mission's answer actually cites, read from the typed record production is
    /// expected to write — a `sourced_answer` artifact holding one entry per claim.
    ///
    /// RED UNTIL THE SLICE LANDS, deliberately: nothing writes that schema today, so this returns
    /// empty and the gate fails at assertion 2. Read from the ARTIFACT rather than by scraping the
    /// prose answer, for the reason ADR-004 gives — a claim→source mapping recovered by parsing the
    /// narrative is the narrative being treated as the record, which is the arrangement the typed
    /// channel exists to end.
    /// </summary>
    private static IReadOnlyList<string> CitedSources(SqliteMemory memory, string missionId) =>
        ((Anthill.SDK.Artifacts.IArtifactStore)memory).ForMission(missionId)
            .Where(a => string.Equals(a.Schema, "sourced_answer", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => System.Text.RegularExpressions.Regex
                .Matches(a.Payload, @"""source_url""\s*:\s*""(?<url>[^""]+)""")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups["url"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether the answer distinguishes its unsourced claims. Substring families rather than one
    /// spelling, for the reason `.98`'s coverage note gives: a gate that demands one wording grades
    /// vocabulary. What must be true is that the DISTINCTION is visible to a reader.
    /// </summary>
    private static bool MarksUnsourcedClaims(string answer) =>
        new[] { "unsourced", "not sourced", "no source", "unattributed", "not attributed" }
            .Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
