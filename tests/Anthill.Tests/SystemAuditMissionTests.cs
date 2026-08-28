using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — THE ACCEPTANCE GATE FOR THE FIRST UNIVERSAL VERTICAL SLICE, WRITTEN FIRST AND
/// EXPECTED TO FAIL UNTIL THE SLICE LANDS.
///
/// WHY THIS FILE EXISTS BEFORE THE FEATURE. Every prior attempt at this program built structures
/// and then looked for somewhere to call them; the result was components that compiled, tested
/// green in isolation, and changed nothing an operator could observe. So the order is inverted
/// here: the mission an operator actually asks for is asserted end to end, through the real
/// composition, from the first commit of the release. Each subsequent commit moves one assertion
/// from red to green, and the release is done when the file passes — not when the nouns exist.
///
/// WHAT IT DELIBERATELY DOES NOT DO. It asserts nothing about `MissionSpecification`, a
/// deliverable ledger, a capability resolver, or any other type this release intends to add. It
/// reads only what production already exposes: the tasks that ran, the workers that were
/// resolved, the artifacts and evidence recorded, the persisted evaluation, and the final answer
/// the operator reads. That restraint is the point. A test written against the design can be
/// satisfied by the design; a test written against OBSERVABLE BEHAVIOUR can only be satisfied by
/// behaviour, and cannot be quietly made to pass by scaffolding that never joins the path.
///
/// THE MISSION UNDER TEST is the recorded failure shape of `7afd85b2-e4a2-47ef-aa01-e5fa72ff00ca`:
/// two tasks completed, no checks, no evidence, and the requested assessment absent — a mission
/// that was structurally complete and objectively empty.
///
/// CLASSIFICATION IS BY MEANING, NOT BY STRING. Four semantically equivalent requests are driven
/// through the same path. They differ in wording — "colony", "repository", "workflow",
/// "orchestration" — and must resolve to the same mission class, the same capability floor and
/// the same worker set. An implementation that recognises the original sentence and not its
/// paraphrases has special-cased a fixture, which is the failure this file is designed to catch.
///
/// SCOPE BOUNDARY (v0.3.8.98 vs v0.3.8.101). This is ASSESSMENT of current state, read-only:
/// intent=assess, targets=repository+runtime, freshness=current, authority=observe. Diagnosing a
/// symptom, running invasive probes, or proposing a repair is troubleshooting and belongs to a
/// later release. A fault discovered here is reported as an evidenced finding, never silently
/// escalated into diagnosis or modification.
/// </summary>
[Collection("specialist-gates")]
public class SystemAuditMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    // The roster gates are process-global and this fixture forces them on. Captured and restored
    // so a leaked gate cannot decide a later test's result — the failure mode RosterGates exists for.
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public SystemAuditMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-audit-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The operator's own request, and three paraphrases that mean the same thing.
    ///
    /// The first is the recorded failing mission verbatim. The rest change the vocabulary a
    /// classifier would be tempted to key on: one says "colony", one says "repository", one says
    /// "workflow"; one asks about orchestration failing, one about tasks reaching workers, one
    /// about roles actually used. Their requested deliverables legitimately differ in wording.
    /// Their MISSION CLASS and capability floor must not.
    /// </summary>
    public static TheoryData<string> EquivalentAuditRequests => new()
    {
        "What is the Anthill colony capable of now? What is good and bad about its workflow? "
      + "Does it hit the proper ants it needs to?",

        "Audit this colony's current abilities and explain where its orchestration fails.",

        "Inspect the current repository and determine whether tasks reach the appropriate workers.",

        "Evaluate the implemented workflow, its strengths, its limitations, and the roles actually used.",
    };

    /// <summary>
    /// The planner's dialect, naming ROLES and leaving the worker to resolution.
    ///
    /// `assigned_worker` is omitted on purpose. Which worker serves an audit is the decision under
    /// test — naming `researcher.repo_researcher` here would assert that the fixture can spell it,
    /// which is precisely the "mocked plan contains the correct roles without proving that plan
    /// ran" anti-pattern. The runtime must choose it from what the mission needs.
    /// </summary>
    private const string AuditPlan = """
        {
          "tasks": [
            {
              "title": "Inspect the implementation",
              "description": "Read the repository to establish what is implemented and wired.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Compile the assessment",
              "description": "Assemble the capability assessment, workflow strengths and weaknesses, and whether the correct roles ran.",
              "assigned_ant": "builder",
              "task_type": "synthesis",
              "depends_on": []
            },
            {
              "title": "Verify the assessment",
              "description": "Check the assessment answers every question the operator asked and is supported by evidence.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    private static ScriptBook AuditScript() => new ScriptBook()
        .Role("planner", AuditPlan)
        .Role("researcher", "SCRIPTED: inspected the repository.")
        .Role("builder",
            "SCRIPTED: Capabilities: the colony plans, executes, verifies and promotes patches. "
          + "Strengths: deterministic gates. Weaknesses: worker selection is keyword-based. "
          + "Roles used: researcher, builder, verifier.")
        .Role("verifier", "SCRIPTED: the assessment addresses the request.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    /// <summary>
    /// THE GATE. One composed mission per phrasing, through ConversationRunner into the real
    /// Queen, asserting the whole spine.
    ///
    /// Entry is at the CONVERSATION, not at `RunMission`, because the composed goal is built
    /// there — and v0.3.8.96's hardest live defect was a gate that read the conversation
    /// transcript as though it were the operator's ask. A classifier that inherits that mistake
    /// must fail here rather than in a live run.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalentAuditRequests))]
    public void ASystemAuditRequest_ResolvesItsCapabilities_ProducesEvidence_AndAnswersEveryQuestion(string request)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(AuditScript(),
            "planner", "researcher", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        using var memory = new SqliteMemory(Path.Combine(_dir, $"audit-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "audit-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var queen = new Queen(memory);
        string? missionId = null;
        var runner = new ConversationRunner(memory,
            (goal, _, onCreated, cancel) =>
            {
                queen.RunMission(goal, onMissionCreated: id => { missionId = id; onCreated(id); }, cancel);
                return missionId ?? "";
            });

        // Mission mode explicitly: this is the operator asking for work, not chat.
        runner.Run(conversation, request, ConversationMode.Mission);
        Assert.NotNull(missionId);

        var tasks = memory.GetTasksForMission(missionId!);
        string Workers() => string.Join(", ", tasks
            .Select(t => t.GetValueOrDefault("assigned_worker")?.ToString())
            .Where(w => !string.IsNullOrWhiteSpace(w)));

        // ---- 1. CAPABILITY-CORRECT WORKERS RAN ---------------------------------------------------
        //
        // An audit inspects the REPOSITORY. `researcher.mission_researcher` reads mission history
        // and is the wrong specialization for this question — today's keyword resolver picks it
        // because the word "mission" appears in the task text, which is the defect this release
        // exists to remove.
        Assert.True(tasks.Any(t => t.GetValueOrDefault("assigned_worker")?.ToString() == "researcher.repo_researcher"),
            $"the audit did not resolve the repository researcher. Workers that ran: {Workers()}");
        Assert.DoesNotContain(tasks, t =>
            t.GetValueOrDefault("assigned_worker")?.ToString() == "researcher.mission_researcher");

        // Result completeness is the RESULT verifier's question. The safety verifier answers a
        // different one and must not stand in for it.
        Assert.True(tasks.Any(t => t.GetValueOrDefault("assigned_worker")?.ToString() == "verifier.result_verifier"),
            $"the audit did not resolve the result verifier. Workers that ran: {Workers()}");
        Assert.DoesNotContain(tasks, t =>
            t.GetValueOrDefault("assigned_worker")?.ToString() == "verifier.safety_verifier");

        // ---- 2. THE INSPECTION ACTUALLY HAPPENED -------------------------------------------------
        //
        // An assessment of "what is implemented" that read nothing is a model's opinion wearing a
        // mission's clothes. Something must have inspected the repository or the runtime records,
        // and it must have left a receipt.
        var evidence = ((Anthill.SDK.Artifacts.IEvidenceStore)memory).ForMission(missionId!);
        Assert.True(evidence.Count > 0,
            "the audit recorded no evidence at all — this is the 7afd85b2 shape: tasks completed, "
          + "nothing inspected, an assessment asserted rather than established.");

        // ---- 3. TYPED OUTPUT WAS PRODUCED AND CONSUMED -------------------------------------------
        //
        // The consumption ledger already exists. What has never been true is that a downstream
        // worker's claim to have used an upstream artifact is checkable. The verifier must have
        // consumed what it graded.
        var artifacts = ((Anthill.SDK.Artifacts.IArtifactStore)memory).ForMission(missionId!);
        Assert.True(artifacts.Count > 0, "the audit produced no typed artifacts.");
        var consumptions = ((Anthill.SDK.Artifacts.IArtifactStore)memory).ConsumptionsForMission(missionId!, 200);
        Assert.True(consumptions.Count > 0,
            "no artifact consumption was recorded — nothing proves the assessment was built from "
          + "the inspection, or that the verifier read what it graded.");

        // ---- 4. THE ANSWER COVERS EVERY REQUESTED SECTION ----------------------------------------
        //
        // Three questions were asked: what can it do, what is good and bad, did the right roles
        // run. A plausible paragraph that silently drops one is the defect; prose fluency is not
        // coverage. Asserted on the operator-facing answer, which is what the operator reads.
        var mission = memory.GetMission(missionId!);
        Assert.NotNull(mission);
        var answer = mission!.GetValueOrDefault("final_result")?.ToString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(answer), "the mission produced no final answer.");

        foreach (var (section, needles) in RequiredSections)
            Assert.True(needles.Any(n => answer.Contains(n, StringComparison.OrdinalIgnoreCase)),
                $"the final answer does not address '{section}'. The operator asked three questions "
              + $"and the answer must contain all three.\n\nAnswer:\n{answer}");

        // ---- 5. THE OUTCOME IS JUDGED AGAINST THE OBJECTIVE --------------------------------------
        //
        // The canonical evaluation must exist and must reflect the objective, not merely that the
        // scheduled tasks reached a terminal state.
        var evaluation = memory.LoadMissionEvaluation(missionId!);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"the audit did not reach a positive canonical evaluation: {evaluation.OutcomeCode}");
    }

    /// <summary>
    /// The three things the operator asked for, and words that would evidence each.
    ///
    /// Substring families rather than one exact phrase: the assembler is allowed to word the
    /// answer differently, and a coverage gate that demands one spelling would be graded on
    /// vocabulary rather than on content. The point is that the SUBJECT is present.
    /// </summary>
    private static readonly (string Section, string[] Needles)[] RequiredSections =
    {
        ("current capabilities", new[] { "capab", "can do", "able to" }),
        ("workflow strengths and weaknesses", new[] { "strength", "weak", "limitation", "good and bad" }),
        ("whether the correct roles ran", new[] { "role", "worker", "ant" }),
    };
}
