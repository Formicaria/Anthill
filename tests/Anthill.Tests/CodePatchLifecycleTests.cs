using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
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

    // ---- audit scenario 20: all twelve, through their real triggers -----------------------------

    /// <summary>
    /// The planner-selectable eight, each on its real worker and its CONTRACT'S OWN task type
    /// (the first run's record taught both corrections: 'research' normalized to 'general', which
    /// ui_cartographer and scribe rightly refused — their contracts speak ui_mapping and
    /// changelog_update). Dependencies are EXPLICIT along the patch spine (auto-wiring chains the
    /// coder behind EVERY source task, so the honestly-blocked web ant took the coder — and with
    /// it the whole inserted-review chain — down with it; explicit deps are respected verbatim,
    /// and depends_on resolves task titles, per the parser).
    /// </summary>
    private const string TwelveRolePlan = """
        {
          "tasks": [
            { "title": "Frame the request", "description": "Understand what the note must say.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Check public context", "description": "Note any public context worth citing.",
              "assigned_ant": "web", "assigned_worker": "web.source_finder",
              "task_type": "external_research", "depends_on": [] },
            { "title": "Inspect the workspace", "description": "List the workspace files relevant to the note.",
              "assigned_ant": "file", "assigned_worker": "file.file_scout",
              "task_type": "file_inspection", "depends_on": [] },
            { "title": "Map the frontend surface", "description": "Note any UI surface the change could touch.",
              "assigned_ant": "ui_cartographer", "assigned_worker": "ui_cartographer.route_mapper",
              "task_type": "ui_mapping", "depends_on": [] },
            { "title": "Propose the documentation patch", "description": "Propose the note as a structured patch, JSON only.",
              "assigned_ant": "coder", "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal",
              "depends_on": ["Frame the request", "Inspect the workspace"] },
            { "title": "Build the operator answer", "description": "Assemble the outcome for the operator.",
              "assigned_ant": "builder", "assigned_worker": "builder.response_builder",
              "task_type": "build_answer",
              "depends_on": ["Propose the documentation patch"] },
            { "title": "Draft the changelog line", "description": "Draft a one-line changelog entry for the note.",
              "assigned_ant": "scribe", "assigned_worker": "scribe.changelog_scribe",
              "task_type": "changelog_update", "depends_on": [] },
            { "title": "Verify the outcome", "description": "Check the record addresses the request.",
              "assigned_ant": "verifier", "assigned_worker": "verifier.result_verifier",
              "task_type": "verification",
              "depends_on": ["Build the operator answer"] }
          ]
        }
        """;

    /// <summary>
    /// AUDIT SCENARIO 20 — docs/PLAN.md §6's named gap, closed the only honest way: every one of
    /// the twelve contracted roles reached through ITS OWN production trigger in one composed
    /// mission. Eight are planner-selected because the scripted plan names them (the planner's
    /// real dialect, real workers, validated by the real registry). Tester and soldier appear
    /// though NO plan named them — policy-inserted on the patch set's existence. The medic
    /// appears only because the tester's check legitimately failed against the materialized
    /// revision (failure-triggered). The archivist runs only after the canonical evaluation
    /// persisted (post-finalization), and its evidence is memory candidates, not a task row.
    /// Nothing here is ceremonial: remove the patch and tester/soldier/medic vanish; remove the
    /// evaluation and the archivist does.
    /// </summary>
    [Fact]
    public void AllTwelveRoles_RunThroughTheirRealTriggers_InOneComposedScriptedMission()
    {
        var specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
        var cartographerWas = AnthillRuntime.EnableUiCartographerAnt;
        var scribeWas = AnthillRuntime.EnableScribeAnt;
        var archivistWas = AnthillRuntime.EnableArchivistAnt;
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableUiCartographerAnt = true;
            AnthillRuntime.EnableScribeAnt = true;
            AnthillRuntime.EnableArchivistAnt = true;
            AnthillRuntime.UseOllama = true;
            AnthillRuntime.AllowedWorkspaceRoot = _workspace;

            var book = new ScriptBook()
                .Role("planner", TwelveRolePlan)
                .Role("researcher", "SCRIPTED: the note should describe the colony.")
                .Role("web", "SCRIPTED: no external sources are needed for an internal note.")
                .Role("file", "SCRIPTED: the workspace holds README.txt.")
                .Role("ui_cartographer", "SCRIPTED: no UI surface is touched by a documentation note.")
                .Role("coder", ScriptedProposals)
                .Role("builder", "SCRIPTED: the note was proposed as a patch and awaits review.")
                .Role("scribe", "SCRIPTED: docs: add the colony note.")
                .Role("verifier", "SCRIPTED: the record addresses the request.")
                .Role("tester", "SCRIPTED: checks recorded.")
                .Role("soldier", "SCRIPTED: no security concern in a documentation note.")
                .Role("medic", "SCRIPTED: the check failure is environmental to this tree.")
                .Role("archivist", "SCRIPTED: recorded.");

            using var scripted = ScriptedColony.Begin(book,
                "planner", "researcher", "web", "file", "ui_cartographer", "coder", "builder",
                "scribe", "verifier", "tester", "soldier", "medic", "archivist", "fallback");

            using var memory = new SqliteMemory(Path.Combine(_dir, "twelve.db"));
            // The production tool drain, exactly as both composition roots do it: the Tools
            // module's contributions adopted into the Queen's registry. File tools ON (the file
            // ant lists the real temp workspace — local and deterministic); web/shell/writes OFF.
            var host = new ModuleHost(memory, NullEventBus.Instance);
            host.Load(new ToolsModule(new WorkspacePathGuard(), new ScenarioToolGates()));
            var queen = new Queen(memory);
            queen.AdoptModuleTools(host.ContributedTools);

            string? missionId = null;
            queen.RunMission("Add a short colony note to the documentation.",
                onMissionCreated: id => missionId = id);
            Assert.NotNull(missionId);

            var tasks = queen.Memory.GetTasksForMission(missionId!);
            var roles = tasks.Select(t => t.GetValueOrDefault("assigned_ant")?.ToString() ?? "").ToHashSet();

            // Eleven roles as task rows, each on its own trigger…
            foreach (var role in new[]
                     {
                         "researcher", "web", "file", "ui_cartographer", "coder", "builder",
                         "scribe", "verifier", "tester", "soldier", "medic",
                     })
                Assert.True(roles.Contains(role),
                    $"role '{role}' never received a task; roles that ran: {string.Join(",", roles)}");

            // …and every one of their tasks reached a TERMINAL state: ran and answered, or
            // refused with a typed outcome. Nothing hung, nothing was silently skipped as
            // pending — the trigger fired and the runtime answered for it.
            Assert.DoesNotContain(tasks, t =>
            {
                var s = t.GetValueOrDefault("status")?.ToString() ?? "";
                return s is "running" or "pending" or "queued";
            });

            // The inserted pair really were INSERTED: the scripted plan named neither.
            Assert.DoesNotContain("tester", TwelveRolePlan);
            Assert.DoesNotContain("soldier", TwelveRolePlan);

            // The twelfth role: the archivist runs post-finalization, and its evidence is the
            // memory candidates it recorded after the persisted evaluation — not a task row.
            Assert.NotNull(queen.Memory.LoadMissionEvaluation(missionId!));
            Assert.True(queen.Memory.GetRecentEvents(100, "memory_candidate", missionId).Count > 0,
                "the archivist left no memory candidates after finalization");
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = specialistsWas;
            AnthillRuntime.EnableUiCartographerAnt = cartographerWas;
            AnthillRuntime.EnableScribeAnt = scribeWas;
            AnthillRuntime.EnableArchivistAnt = archivistWas;
        }
    }

    /// <summary>
    /// v0.3.8.55 (operator's E2E ask) — THE MEMORY TRAIL, end to end, deterministic: the same
    /// composed twelve-role mission, asserted this time on what the colony REMEMBERS rather than
    /// on who ran. Four claims, each against a persisted record:
    ///   1. the scribe's record survives — its deterministic release notes, assembled from the
    ///      mission's own results (never from a model answer), naming the mission they document;
    ///   2. the archivist's finalization claim is USED UP — both ledger claims (learning,
    ///      archivist) refuse a second caller, so one mission can never buy double memory;
    ///   3. the memory candidates the archivist recorded are real events with content;
    ///   4. the mission left pheromone trails in the persisted store.
    /// The scenario's outcome is an honest adaptive_stop (the tester legitimately fails against
    /// the materialized tree), which is exactly the point: even a mission that did NOT verify
    /// leaves an auditable memory trail — recorded, never strengthened into false reputation.
    /// </summary>
    [Fact]
    public void TheMemoryTrail_ScribeWritesArchivistClaims_AndTheLedgerRefusesSeconds()
    {
        var specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
        var cartographerWas = AnthillRuntime.EnableUiCartographerAnt;
        var scribeWas = AnthillRuntime.EnableScribeAnt;
        var archivistWas = AnthillRuntime.EnableArchivistAnt;
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableUiCartographerAnt = true;
            AnthillRuntime.EnableScribeAnt = true;
            AnthillRuntime.EnableArchivistAnt = true;
            AnthillRuntime.UseOllama = true;
            AnthillRuntime.AllowedWorkspaceRoot = _workspace;

            var book = new ScriptBook()
                .Role("planner", TwelveRolePlan)
                .Role("researcher", "SCRIPTED: the note should describe the colony.")
                .Role("web", "SCRIPTED: no external sources are needed.")
                .Role("file", "SCRIPTED: the workspace holds README.txt.")
                .Role("ui_cartographer", "SCRIPTED: no UI surface is touched.")
                .Role("coder", ScriptedProposals)
                .Role("builder", "SCRIPTED: the note was proposed and awaits review.")
                .Role("scribe", "SCRIPTED: unused — the scribe is deterministic (see claim 1).")
                .Role("verifier", "SCRIPTED: the record addresses the request.")
                .Role("tester", "SCRIPTED: checks recorded.")
                .Role("soldier", "SCRIPTED: no security concern.")
                .Role("medic", "SCRIPTED: environmental to this tree.")
                .Role("archivist", "SCRIPTED: recorded.");

            using var scripted = ScriptedColony.Begin(book,
                "planner", "researcher", "web", "file", "ui_cartographer", "coder", "builder",
                "scribe", "verifier", "tester", "soldier", "medic", "archivist", "fallback");

            using var memory = new SqliteMemory(Path.Combine(_dir, "memory-trail.db"));
            var host = new ModuleHost(memory, NullEventBus.Instance);
            host.Load(new ToolsModule(new WorkspacePathGuard(), new ScenarioToolGates()));
            var queen = new Queen(memory);
            queen.AdoptModuleTools(host.ContributedTools);

            string? missionId = null;
            queen.RunMission("Add a short colony note to the documentation.",
                onMissionCreated: id => missionId = id);
            Assert.NotNull(missionId);

            // 1. The scribe's record survives — and this test's first run taught what it IS.
            //    The scribe is a DETERMINISTIC ant: it never consumes a model answer (the scripted
            //    line above is deliberately unused), it assembles release notes from the mission's
            //    own prior results — evidence over prose, in one role. Its persisted narrative
            //    therefore opens with the mission it documents, and that is the memory asserted.
            var tasks = queen.Memory.GetTasksForMission(missionId!);
            var scribeTask = tasks.FirstOrDefault(t =>
                (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == "scribe");
            Assert.NotNull(scribeTask);
            Assert.Equal("complete", scribeTask!.GetValueOrDefault("status")?.ToString());
            Assert.Contains("Mission: Add a short colony note to the documentation.",
                scribeTask.GetValueOrDefault("result")?.ToString() ?? "");

            // 2. Finalization happened EXACTLY once: both ledger claims are spent. A second
            //    caller — a crash-retry, a duplicated finalizer — is refused by the store itself.
            var evaluation = queen.Memory.LoadMissionEvaluation(missionId!);
            Assert.NotNull(evaluation);
            Assert.False(MissionFinalizationLedger.TryClaimLearning(queen.Memory, missionId!, evaluation!),
                "the learning claim was still open after finalization — double-learning is possible");
            Assert.False(MissionFinalizationLedger.TryClaimArchivist(queen.Memory, missionId!, evaluation!),
                "the archivist claim was still open after finalization — double-archiving is possible");

            // 3. The archivist's memory candidates are real recorded events with content.
            var candidates = queen.Memory.GetRecentEvents(100, "memory_candidate", missionId);
            Assert.True(candidates.Count > 0, "no memory candidates were recorded");
            Assert.All(candidates, c =>
                Assert.False(string.IsNullOrWhiteSpace(c.GetValueOrDefault("message")?.ToString()),
                    "a memory candidate with no content is not a memory"));

            // 4. The mission left pheromone trails in the persisted store.
            Assert.True(queen.Memory.ListPheromoneTrails(300).Count > 0,
                "the mission left no pheromone trails");
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = specialistsWas;
            AnthillRuntime.EnableUiCartographerAnt = cartographerWas;
            AnthillRuntime.EnableScribeAnt = scribeWas;
            AnthillRuntime.EnableArchivistAnt = archivistWas;
        }
    }

    /// <summary>File tools on (local, deterministic); web, shell, writes and auto-apply off.</summary>
    private sealed class ScenarioToolGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => false;
        public bool ShellToolEnabled => false;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => false;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".md", ".txt" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }
}
