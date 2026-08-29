using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — THE AUDIT GATE, PUSHED IN THE DIRECTIONS IT MUST REFUSE.
///
/// WHY THIS FILE EXISTS BESIDE THE PASSING ONE. `SystemAuditMissionTests` proves the audit path
/// reaches a positive grade. That is worth exactly as much as the grade's ability to be negative:
/// a gate nothing can fail is a formality, and this repository has shipped several — a filter that
/// could not match, a switch nothing read, a capability branch that never ran. So each test here
/// breaks one thing the composed audit depends on and asserts the runtime NOTICES.
///
/// Three failures, chosen because each targets a different layer:
///
///   * the PLAN names workers whose contracts cannot serve the mission — the resolution layer;
///   * the PLAN omits the steps the class requires — the coverage layer;
///   * the VERIFIER returns a refusal — the grading layer, which must then not be positive.
///
/// The harness deliberately mirrors the acceptance test's rather than sharing it. Extracting a
/// common runner would let a change made for one file quietly alter what the other proves, and
/// these two files are checking opposite claims about the same path.
/// </summary>
[Collection("specialist-gates")]
public class SystemAuditNegativeTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly bool _fileToolsWas = AnthillRuntime.EnableFileTools;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public SystemAuditNegativeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-audit-neg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceWas;
        AnthillRuntime.EnableFileTools = _fileToolsWas;
        AnthillRuntime.EnableObjectiveVerification = _objectiveWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Request =
        "Assess what this colony can do today and whether its missions reach the right workers.";

    /// <summary>The plan a keyword-era planner would write: both workers named, both wrong for an audit.</summary>
    private const string IncompatiblePlan = """
        {
          "tasks": [
            {
              "title": "Research the mission",
              "description": "Establish what is implemented and wired.",
              "assigned_ant": "researcher",
              "assigned_worker": "researcher.mission_researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Compile the assessment",
              "description": "Assemble the capability assessment, strengths and weaknesses, and the roles used.",
              "assigned_ant": "builder",
              "assigned_worker": "builder.result_compiler",
              "task_type": "synthesis",
              "depends_on": []
            },
            {
              "title": "Verify the assessment",
              "description": "Check the assessment against the request.",
              "assigned_ant": "verifier",
              "assigned_worker": "verifier.safety_verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    /// <summary>A plan with nothing but research: no compiler, no verifier, no inspection.</summary>
    private const string ResearchOnlyPlan = """
        {
          "tasks": [
            {
              "title": "Research the implementation",
              "description": "Read the repository to establish what is implemented.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            }
          ]
        }
        """;

    private static ScriptBook Script(string plan, string verifierVerdict) => new ScriptBook()
        .Role("planner", plan)
        .Role("researcher", "SCRIPTED: inspected the repository.")
        .Role("builder",
            "SCRIPTED: Capabilities: the colony plans, executes, verifies and promotes patches. "
          + "Strengths: deterministic gates. Weaknesses: worker selection is keyword-based. "
          + "Roles used: researcher, builder, verifier.")
        .Role("verifier", verifierVerdict)
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string Passes = "Verification Passed: the assessment addresses the request.";
    private const string Refuses = "Verification Failed: the assessment cites nothing that was inspected.";

    /// <summary>Run one composed audit and hand back the settled mission's memory and id.</summary>
    private (SqliteMemory Memory, string MissionId) RunAudit(ScriptBook book)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();
        AnthillRuntime.EnableFileTools = true;
        AnthillRuntime.EnableObjectiveVerification = true;

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"neg-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "audit-negative", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

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

        runner.Run(conversation, Request, ConversationMode.Mission,
            answers: new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" });

        Assert.True(settled.Wait(TimeSpan.FromMinutes(2)), "the mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return (memory, missionId!);
    }

    /// <summary>
    /// THE PLAN THAT RAN IS THE PLAN THE FIXTURE WROTE.
    ///
    /// Required by `ScriptedPlanConformanceTests` for any fixture whose plan is built rather than
    /// declared as a readable constant — and it caught a real hole here, not a formality. If the
    /// Planner discarded these plans (a bad role, a parse failure) the mission would run
    /// `FallbackTasks` instead, and `APlanMissingTheStepsTheClassRequires_HasThemSupplied` would
    /// pass on a fallback plan that already contains a builder and a verifier: a green test proving
    /// nothing about the coverage layer it exists to check.
    ///
    /// A SUBSET, not an equality. `EnsureClassCoverage` deliberately ADDS the steps an audit
    /// requires, so demanding an exact match would assert that the feature under test did not run.
    /// </summary>
    private static void AssertTheMissionRanTheScriptedPlan(
        SqliteMemory memory, string missionId, params string[] scriptedTitles)
    {
        var planned = memory.GetTasksForMission(missionId)
            .Select(t => t.GetValueOrDefault("title")?.ToString() ?? "")
            .ToList();

        foreach (var title in scriptedTitles)
            Assert.True(planned.Contains(title, StringComparer.Ordinal),
                $"the mission did not run the scripted plan — '{title}' is absent, so the Planner "
              + $"discarded the fixture's plan and ran its own.\n\nTasks that ran: {string.Join(", ", planned)}");
    }

    private static IReadOnlyList<string> WorkersOf(SqliteMemory memory, string missionId) =>
        memory.GetTasksForMission(missionId)
            .Select(t => t.GetValueOrDefault("assigned_worker")?.ToString() ?? "")
            .Where(w => w.Length > 0)
            .ToList();

    /// <summary>
    /// THE PLAN'S NAMED WORKER IS A PROPOSAL. A planner that writes `mission_researcher` for a
    /// repository audit and `safety_verifier` for a completeness check has named two workers whose
    /// own contracts serve neither capability the mission declared. Until v0.3.8.98 that was final
    /// — an explicit assignment skipped resolution entirely — so the capability system could be
    /// bypassed by the planner simply being specific.
    /// </summary>
    [Fact]
    public void APlanNamingIncompatibleWorkers_IsRepaired_AndTheRepairIsAnnounced()
    {
        var (memory, missionId) = RunAudit(Script(IncompatiblePlan, Passes));
        using var owned = memory;

        AssertTheMissionRanTheScriptedPlan(memory, missionId,
            "Research the mission", "Compile the assessment", "Verify the assessment");

        var workers = WorkersOf(memory, missionId);
        Assert.Contains("researcher.repo_researcher", workers);
        Assert.Contains("verifier.result_verifier", workers);
        Assert.DoesNotContain("researcher.mission_researcher", workers);
        Assert.DoesNotContain("verifier.safety_verifier", workers);

        // ANNOUNCED. A dispatch that silently differs from the plan an operator previewed is a
        // divergence nobody can reconcile afterwards.
        var events = memory.GetRecentEvents(500, missionId: missionId)
            .Select(e => e.GetValueOrDefault("event_type")?.ToString() ?? "")
            .ToList();
        Assert.Contains("worker_repaired_by_capability", events);
    }

    /// <summary>
    /// THE CLASS'S REQUIRED STEPS ARE NOT THE PLANNER'S OPTION. A plan of pure research produces an
    /// assessment nobody compiled and nobody checked — and the class requires both, so the runtime
    /// supplies what is missing rather than grading a mission that could not have succeeded.
    /// </summary>
    [Fact]
    public void APlanMissingTheStepsTheClassRequires_HasThemSupplied()
    {
        var (memory, missionId) = RunAudit(Script(ResearchOnlyPlan, Passes));
        using var owned = memory;

        AssertTheMissionRanTheScriptedPlan(memory, missionId, "Research the implementation");

        var roles = memory.GetTasksForMission(missionId)
            .Select(t => t.GetValueOrDefault("assigned_ant")?.ToString() ?? "")
            .ToList();

        Assert.Contains("file", roles);        // the inspection the assessment must rest on
        Assert.Contains("builder", roles);     // the answer the operator asked for
        Assert.Contains("verifier", roles);    // the check that it was produced
    }

    /// <summary>
    /// THE GRADE CAN STILL BE NEGATIVE. Everything else about this run is identical to the passing
    /// acceptance case — same request, same inspection, same artifacts and consumption — and the
    /// verifier refuses. If this reached `completed_verified` anyway, the acceptance test's final
    /// assertion would be proving nothing at all.
    /// </summary>
    [Fact]
    public void AVerifierThatRefuses_KeepsTheMissionOutOfAVerifiedOutcome()
    {
        var (memory, missionId) = RunAudit(Script(IncompatiblePlan, Refuses));
        using var owned = memory;

        AssertTheMissionRanTheScriptedPlan(memory, missionId, "Verify the assessment");

        var evaluation = memory.LoadMissionEvaluation(missionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            $"a refused verification still reached a positive outcome: {evaluation.Explanation}");
        Assert.Equal(Anthill.Core.Outcomes.MissionEvaluation.Verification.Failed, evaluation.VerificationStatus);
    }
}
