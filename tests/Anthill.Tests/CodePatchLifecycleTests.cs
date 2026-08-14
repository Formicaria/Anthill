using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.54 — audit scenarios 3/4, composed: a PATCH mission driven end to end through
/// <see cref="Queen.RunMission(string)"/> with every answer scripted and every mechanism real.
/// The scripted DYNAMIC planner orders researcher → coder → verifier (speaking the planner's own
/// JSON dialect); the scripted coder proposes a structured patch (the coder's own proposals
/// dialect); production then does what production does: parses the PatchSet, persists it,
/// MATERIALIZES it into an isolated revision from <see cref="AnthillRuntime.AllowedWorkspaceRoot"/>,
/// registers the revision, and POLICY-INSERTS the review roles — none of which any script asked
/// for, which is the point.
///
/// What this proves that no test proved before: the coder→patch→materialize→review spine is
/// reachable through the Queen's public path with deterministic answers. What it deliberately
/// does NOT assert: the final verification verdict vocabulary for a docs-only patch — that
/// corner gets pinned from the record of a real run, not from an assumption (the v3.8.31 rule).
/// </summary>
[Collection("specialist-gates")]   // workspace root, route table and UseOllama are static
public class CodePatchLifecycleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _workspace;
    private readonly bool _useOllamaWas;
    private readonly string _workspaceRootWas;
    private readonly bool _sandboxWas;

    public CodePatchLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-lifecycle-" + Guid.NewGuid().ToString("N")[..10]);
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_workspace);
        // A seed file so the workspace is a real tree, not an empty directory.
        File.WriteAllText(Path.Combine(_workspace, "README.txt"), "seed workspace\n");

        _useOllamaWas = AnthillRuntime.UseOllama;
        _workspaceRootWas = AnthillRuntime.AllowedWorkspaceRoot;
        // The coder's in-sandbox iterate-and-build loop is a different scenario (and runs real
        // build commands); this one exercises the one-shot propose path deterministically.
        _sandboxWas = AnthillRuntime.EnableSandboxExecution;
        AnthillRuntime.EnableSandboxExecution = false;
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceRootWas;
        AnthillRuntime.EnableSandboxExecution = _sandboxWas;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The planner's OWN dialect — the shape Planner.ParsePlan documents in its prompt.</summary>
    private const string ScriptedPlan = """
        {
          "tasks": [
            {
              "title": "Understand the request",
              "description": "Frame what the documentation note must say.",
              "assigned_ant": "researcher",
              "assigned_worker": "researcher.mission_researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Propose the documentation patch",
              "description": "Propose adding the requested note as a structured patch, JSON only.",
              "assigned_ant": "coder",
              "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal",
              "depends_on": []
            },
            {
              "title": "Verify the outcome",
              "description": "Check the proposal addresses the request.",
              "assigned_ant": "verifier",
              "assigned_worker": "verifier.result_verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    /// <summary>The coder's OWN dialect — the proposals JSON ClassifyPatchJson and
    /// PatchProposalParser both read.</summary>
    private const string ScriptedProposals = """
        {
          "summary": "Add the requested colony note.",
          "proposals": [
            {
              "file_path": "docs/COLONY-NOTE.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "# Colony note\n\nThe scripted colony wrote this through the real lifecycle.\n",
              "reason": "The mission asks for a documentation note.",
              "risk": "low"
            }
          ]
        }
        """;

    [Fact]
    public void AScriptedPatchMission_ReachesTheCoder_MaterializesTheSet_AndInsertsTheReviewRoles()
    {
        var book = new ScriptBook()
            .Role("planner", ScriptedPlan)
            .Role("researcher", "SCRIPTED: the note should state that the scripted colony wrote it.")
            .Role("coder", ScriptedProposals)
            .Role("verifier", "SCRIPTED: the proposal addresses the request.")
            // Inserted roles may consult their models too; give every possibility an answer so
            // nothing fails for want of a script rather than on the mechanism under test.
            .Role("tester", "SCRIPTED: nothing further to test beyond the recorded checks.")
            .Role("soldier", "SCRIPTED: no security concern in a documentation note.")
            .Role("builder", "SCRIPTED: the note was proposed as a patch for review.")
            .Role("medic", "SCRIPTED: no diagnosis required.")
            .Role("scribe", "SCRIPTED: summary recorded.")
            .Role("archivist", "SCRIPTED: nothing to archive beyond the record.");

        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;
        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "verifier", "tester", "soldier",
            "builder", "medic", "scribe", "archivist", "fallback");

        var queen = new Queen(new SqliteMemory(Path.Combine(_dir, "lifecycle.db")));
        string? missionId = null;
        queen.RunMission("Add a short colony note to the documentation.",
            onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var tasks = queen.Memory.GetTasksForMission(missionId!);
        string Ants() => string.Join(",", tasks.Select(t => t.GetValueOrDefault("assigned_ant")?.ToString()));

        // The scripted plan drove the graph: the coder ran — and produced the scripted proposal.
        Assert.Contains(tasks, t => t.GetValueOrDefault("assigned_ant")?.ToString() == "coder");
        var coderResult = tasks.First(t => t.GetValueOrDefault("assigned_ant")?.ToString() == "coder")
            .GetValueOrDefault("result")?.ToString() ?? "";
        Assert.Contains("COLONY-NOTE.md", coderResult);

        // Production parsed and PERSISTED the patch set — the coder's JSON became a real record.
        var created = queen.Memory.GetRecentEvents(100, "patch_set_created", missionId);
        Assert.True(created.Count > 0, $"no patch_set_created event; ants ran: {Ants()}");

        // The set MATERIALIZED into an isolated revision (fail-closed otherwise, as its own event).
        Assert.Empty(queen.Memory.GetRecentEvents(100, "patch_set_materialization_failed", missionId));

        // The review roles were POLICY-INSERTED — present in the graph though no script and no
        // plan named them. This is audit scenario 8's composed half: the planner cannot omit
        // what the runtime inserts on its own evidence.
        Assert.Contains(tasks, t => t.GetValueOrDefault("assigned_ant")?.ToString() == "tester");

        // Propose-only: the operator's real tree is UNTOUCHED — the proposal exists as a record,
        // not as a write. (Ask policy: an approval card, never an application.)
        Assert.False(File.Exists(Path.Combine(_workspace, "docs", "COLONY-NOTE.md")),
            "the proposed file must not exist in the live workspace — proposals are not writes");
        Assert.Equal("seed workspace\n", File.ReadAllText(Path.Combine(_workspace, "README.txt")));

        // An approval request awaits the operator — the human gate the lifecycle routes through.
        Assert.True(queen.Memory.GetRecentEvents(100, "approval_request_created", missionId).Count > 0);

        // And the mission closed with a persisted canonical evaluation.
        Assert.NotNull(queen.Memory.LoadMissionEvaluation(missionId!));
    }

    /// <summary>
    /// Audit scenarios 5/6/bounded-repair, pinned from the OBSERVED record of the first composed
    /// run (the v3.8.31 rule: pin what production did, not what we assumed). What that run showed,
    /// unprompted by any assertion: the tester's check ran against the MATERIALIZED revision and
    /// legitimately failed (a temp tree builds nothing); tester→medic handed off on the typed
    /// retryable failure; medic→coder produced a GENERATION-1 patch set that was REmaterialized
    /// and got its own fresh tester review — generation 0's evidence never rode again; and the
    /// loop stopped at its bound with the adaptive stop naming it ("the bound is spent, not the
    /// problem"), leaving a terminal, UNVERIFIED outcome — which buys no completed_verified and
    /// therefore no positive reinforcement.
    /// </summary>
    [Fact]
    public void TheRepairLoop_MaterializesFreshEvidencePerGeneration_AndStopsAtItsBound()
    {
        var book = new ScriptBook()
            .Role("planner", ScriptedPlan)
            .Role("researcher", "SCRIPTED: frame the note.")
            .Role("coder", ScriptedProposals)          // repeats for the repair generation — deterministic
            .Role("verifier", "SCRIPTED: reviewed.")
            .Role("tester", "SCRIPTED: checks recorded.")
            .Role("soldier", "SCRIPTED: no concern.")
            .Role("builder", "SCRIPTED: proposed for review.")
            .Role("medic", "SCRIPTED: the check failure is environmental to this tree; re-propose.")
            .Role("scribe", "SCRIPTED: recorded.")
            .Role("archivist", "SCRIPTED: recorded.");

        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;
        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "verifier", "tester", "soldier",
            "builder", "medic", "scribe", "archivist", "fallback");

        var queen = new Queen(new SqliteMemory(Path.Combine(_dir, "repair.db")));
        string? missionId = null;
        queen.RunMission("Add a short colony note to the documentation.",
            onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var tasks = queen.Memory.GetTasksForMission(missionId!);

        // Two generations, each with ITS OWN patch set — the repair produced a new set, and the
        // policy inserted a FRESH tester for it. Generation 0's green (had there been any) could
        // not have ridden generation 1.
        Assert.True(queen.Memory.GetRecentEvents(200, "patch_set_created", missionId).Count >= 2,
            "the repair generation must create its own patch set");
        Assert.True(tasks.Count(t => t.GetValueOrDefault("assigned_ant")?.ToString() == "tester") >= 2,
            "each patch-set generation must receive its own tester review");

        // The medic ran — and only because a typed retryable failure summoned it.
        Assert.Contains(tasks, t => t.GetValueOrDefault("assigned_ant")?.ToString() == "medic");

        // The loop TERMINATED at its bound with one unambiguous outcome: terminal, adaptive-stop,
        // and NOT verified — an exhausted repair loop must never leave pending work or buy credit.
        var evaluation = queen.Memory.LoadMissionEvaluation(missionId!);
        Assert.NotNull(evaluation);
        Assert.Equal("adaptive_stop", evaluation!.StopReason);
        Assert.NotEqual(Anthill.Core.Outcomes.MissionOutcome.CompletedVerified, evaluation.OutcomeCode);

        // And no task was left running or pending behind the stop.
        Assert.DoesNotContain(tasks, t =>
        {
            var s = t.GetValueOrDefault("status")?.ToString() ?? "";
            return s is "running" or "pending" or "queued";
        });
    }
}
