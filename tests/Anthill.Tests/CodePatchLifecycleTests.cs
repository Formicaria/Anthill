using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Artifacts;
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

    /// <summary>
    /// v0.3.8.87 — the roster, captured through `RosterGates` like the other five lifecycle
    /// fixtures, and captured as a FIELD so it runs before anything in the constructor body.
    ///
    /// `RosterGates.Capture` forces `AnthillRuntime.Initialize` first. Three tests in this file set
    /// roster flags and then built a Queen, whose constructor runs that one-shot bootstrap and
    /// reloads every flag from the operator's config before snapshotting role availability. Their
    /// settings survived only if an earlier test in the process had already triggered it — so
    /// `TheMemoryTrail`, which happened to run first, lost its scribe and its archivist while
    /// `AllTwelveRoles` twenty lines below kept both from identical code.
    /// </summary>
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

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
        RosterGates.Restore(_gatesWere);
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
        // v0.3.8.87 — the roster this test asserts on, stated. It asserts the review roles are
        // POLICY-INSERTED, and a role whose gate is shut is skipped with `policy_review_skipped`
        // rather than inserted — so without this the test was asking whether the operator's own
        // config.json happened to have the tester switched on. The fixture restores these.
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;

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

        // v0.3.8.87 — the three roles the repair loop is made of. The tester must run and FAIL for
        // the loop to exist at all, and the medic must be reachable for the diagnosis this asserts;
        // with their gates shut the mission proposes once, nothing reviews it, and the assertion
        // below reads as "the repair generation produced no patch set" when no repair was ever
        // triggered. The fixture restores these.
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;

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
            // v0.3.8.87 — the other three, and the tier above all of them. These blocks named the
            // three roles the test was written for and inherited the rest, so a twelve-role
            // assertion depended on the tester, soldier and medic being on in whatever ran before
            // it. The tier is a ceiling over every flag, and nothing here had ever pinned it.
            AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
            AnthillRuntime.EnableTesterAnt = true;
            AnthillRuntime.EnableSoldierAnt = true;
            AnthillRuntime.EnableMedicAnt = true;
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
            // v0.3.8.87 — the other three, and the tier above all of them. These blocks named the
            // three roles the test was written for and inherited the rest, so a twelve-role
            // assertion depended on the tester, soldier and medic being on in whatever ran before
            // it. The tier is a ceiling over every flag, and nothing here had ever pinned it.
            AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
            AnthillRuntime.EnableTesterAnt = true;
            AnthillRuntime.EnableSoldierAnt = true;
            AnthillRuntime.EnableMedicAnt = true;
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

    // ===============================================================================================
    // Qualification scenario 15 — the goal that EARNS the roles
    // ===============================================================================================

    /// <summary>
    /// A real frontend file, so the cartographer has a real surface to map. Every construct here is
    /// one the cartographer's extractor actually reads (SpecialistAnts.cs: <c>id="page-…"</c>,
    /// <c>function name(</c>, <c>api('/path')</c>, <c>&lt;style</c>) — the map it produces is derived
    /// from this text and would change if this text changed.
    /// </summary>
    private const string ConsolePage = """
        <!doctype html>
        <html>
        <head><style>body { font-family: sans-serif; }</style></head>
        <body>
          <div id="page-colony">
            <h1>Colony</h1>
            <ul id="roster"></ul>
          </div>
          <div id="page-missions"></div>
          <script>
            function renderColony(roster) { document.getElementById('roster').textContent = roster; }
            function loadColony() { return api('/api/colony/roster').then(renderColony); }
            function loadMissions() { return api('/api/missions'); }
          </script>
        </body>
        </html>
        """;

    /// <summary>
    /// The plan for the earned-roles mission. Compare it against <see cref="TwelveRolePlan"/>: the
    /// difference is not structural, it is that every task here has something to do.
    ///
    /// The cartographer maps a route the patch will CHANGE, so its output feeds the coder rather than
    /// reporting that a documentation note has no frontend. The web ant checks an external reference
    /// the goal names, so its search has a subject. Both are depended on by the coder, which is the
    /// executable form of "this role's output is needed" — remove either and the plan no longer makes
    /// sense, which is exactly what could not be said of the two decorative tasks.
    /// </summary>
    private const string EarnedTwelveRolePlan = """
        {
          "tasks": [
            { "title": "Frame the route change", "description": "State what the colony page must show and why.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Check the accessibility reference", "description": "Find the public guidance the goal cites for labelled list regions.",
              "assigned_ant": "web", "assigned_worker": "web.source_finder",
              "task_type": "external_research", "depends_on": [] },
            { "title": "Inspect the workspace", "description": "List the workspace files the change touches.",
              "assigned_ant": "file", "assigned_worker": "file.file_scout",
              "task_type": "file_inspection", "depends_on": [] },
            { "title": "Map the colony route", "description": "Map the console routes so the change lands on the right one.",
              "assigned_ant": "ui_cartographer", "assigned_worker": "ui_cartographer.route_mapper",
              "task_type": "ui_mapping", "depends_on": [] },
            { "title": "Propose the route and doc patch", "description": "Propose the labelled roster region and its documentation, JSON only.",
              "assigned_ant": "coder", "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal",
              "depends_on": ["Frame the route change", "Map the colony route", "Check the accessibility reference", "Inspect the workspace"] },
            { "title": "Build the operator answer", "description": "Assemble the outcome for the operator.",
              "assigned_ant": "builder", "assigned_worker": "builder.response_builder",
              "task_type": "build_answer",
              "depends_on": ["Propose the route and doc patch"] },
            { "title": "Draft the changelog line", "description": "Draft the changelog entry for the route change.",
              "assigned_ant": "scribe", "assigned_worker": "scribe.changelog_scribe",
              "task_type": "changelog_update",
              "depends_on": ["Propose the route and doc patch"] },
            { "title": "Verify the outcome", "description": "Check the change addresses the request.",
              "assigned_ant": "verifier", "assigned_worker": "verifier.result_verifier",
              "task_type": "verification",
              "depends_on": ["Build the operator answer"] }
          ]
        }
        """;

    /// <summary>
    /// The coder's proposals: a real edit to the mapped route, and the doc that describes it. Two
    /// files, because "changes a UI route AND updates the doc describing it" is the goal's shape and
    /// a one-file patch would let the doc task be decorative in the other direction.
    /// </summary>
    private const string EarnedProposals = """
        {
          "summary": "Label the colony roster region and document the route.",
          "proposals": [
            {
              "file_path": "index.html",
              "change_type": "modify",
              "old_content": "<ul id=\"roster\"></ul>",
              "new_content": "<ul id=\"roster\" aria-label=\"Colony roster\"></ul>",
              "reason": "The colony route's list region has no accessible name.",
              "risk": "low"
            },
            {
              "file_path": "docs/COLONY-ROUTE.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "# The colony route\n\n`page-colony` renders the roster from `/api/colony/roster`.\nThe list region is labelled so assistive technology can name it.\n",
              "reason": "The route's behaviour was undocumented.",
              "risk": "low"
            }
          ]
        }
        """;

    /// <summary>
    /// QUALIFICATION SCENARIO 15, second attempt — and the first one that is about the GOAL.
    /// v0.3.8.68.
    ///
    /// WHAT WAS WRONG WITH THE FIRST. <see cref="AllTwelveRoles_RunThroughTheirRealTriggers_InOneComposedScriptedMission"/>
    /// gets all twelve roles through their production triggers and proves the insertions are
    /// insertions — all of that stands and none of it is repeated here. What it fails is the
    /// scenario's other clause, "no role invoked to satisfy a count", and it fails it in the open:
    /// its goal is "Add a short colony note to the documentation", its plan contains a
    /// `ui_cartographer` task titled "Map the frontend surface", and the cartographer's own scripted
    /// answer is "no UI surface is touched by a documentation note." The web ant's is the same shape.
    /// Two roles planned so the count reaches twelve, each saying so when asked, and both ending in a
    /// failure or a block that the assertion "the role got a task row" counted as coverage.
    ///
    /// THE FIX IS THE GOAL, NOT A BIGGER PLAN. A mission that changes a UI route, updates the doc
    /// describing it and trips a check gives every role a reason that exists before the plan does:
    ///   * the cartographer maps a REAL route — the workspace has a console page with two `page-*`
    ///     regions, three functions and two API call sites, and the map it emits is extracted from
    ///     that file. It succeeds, and the coder depends on it.
    ///   * the web ant runs a REAL search — through <see cref="ScriptedWebSearchTool"/>, which fakes
    ///     the socket and nothing else: dedupe, SSRF refusal, domain scoring and source persistence
    ///     are the production code paths. It saves sources; the previous scenario blocked here.
    ///   * the coder patches BOTH the mapped route and its doc; tester and soldier are inserted on
    ///     the patch set's existence, as before; the medic arrives on the tester's failure; the
    ///     archivist after finalization.
    ///
    /// WHAT THIS STILL DOES NOT CLAIM, and the ledger says the same. The tester's failure is
    /// ENVIRONMENTAL — a materialized revision in a temp directory has no build — so the medic
    /// repairs a failure the patch did not cause. The medic's trigger is real; the failure's
    /// relationship to the change is not. Closing that needs an allowlisted check that fails BECAUSE
    /// of the proposal, which is a separate scenario and is recorded as such rather than implied by
    /// a green test here.
    /// </summary>
    [Fact]
    public void AGoalThatEarnsTheRoles_LeavesNoRoleAnsweringThatItHadNothingToDo()
    {
        var gatesWere = RosterGates.Capture();
        var webWas = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableUiCartographerAnt = true;
            AnthillRuntime.EnableScribeAnt = true;
            AnthillRuntime.EnableArchivistAnt = true;
            // v0.3.8.87 — the other three, and the tier above all of them. These blocks named the
            // three roles the test was written for and inherited the rest, so a twelve-role
            // assertion depended on the tester, soldier and medic being on in whatever ran before
            // it. The tier is a ceiling over every flag, and nothing here had ever pinned it.
            AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
            AnthillRuntime.EnableTesterAnt = true;
            AnthillRuntime.EnableSoldierAnt = true;
            AnthillRuntime.EnableMedicAnt = true;
            AnthillRuntime.UseOllama = true;
            AnthillRuntime.AllowedWorkspaceRoot = _workspace;
            // The ant's own gate, checked before it ever reaches a tool. The tool it then reaches is
            // the scripted one registered below; the module's real web_search stays gated OFF, so a
            // shadowing failure refuses rather than dials out.
            AnthillRuntime.EnableWebSearch = true;

            File.WriteAllText(Path.Combine(_workspace, "index.html"), ConsolePage);

            var book = new ScriptBook()
                .Role("planner", EarnedTwelveRolePlan)
                .Role("researcher", "SCRIPTED: the colony route's roster list needs an accessible name.")
                .Role("web", "SCRIPTED: the guidance supports naming list regions.")
                .Role("file", "SCRIPTED: the workspace holds index.html and README.txt.")
                // No script is needed for the cartographer, the tester or the scribe — all three are
                // deterministic ants that consume no model answer. One is provided anyway so that a
                // future change making any of them call a model fails on the mechanism rather than
                // on a missing script.
                .Role("ui_cartographer", "SCRIPTED: unused — the cartographer reads files, not models.")
                .Role("coder", EarnedProposals)
                .Role("builder", "SCRIPTED: the route was patched and documented; review pending.")
                .Role("scribe", "SCRIPTED: unused — the scribe is deterministic.")
                .Role("verifier", "SCRIPTED: the change addresses the request.")
                .Role("tester", "SCRIPTED: unused — the tester runs checks, not models.")
                .Role("soldier", "SCRIPTED: a static aria-label introduces no security concern.")
                .Role("medic", "SCRIPTED: the check failure is environmental to this tree.")
                .Role("archivist", "SCRIPTED: recorded.");

            using var scripted = ScriptedColony.Begin(book,
                "planner", "researcher", "web", "file", "ui_cartographer", "coder", "builder",
                "scribe", "verifier", "tester", "soldier", "medic", "archivist", "fallback");

            var search = new ScriptedWebSearchTool(
                ("Naming regions and lists", "https://www.w3.org/WAI/ARIA/apg/practices/names-and-descriptions/",
                 "Give every landmark and list region an accessible name."),
                ("Accessible name computation", "https://developer.mozilla.org/en-US/docs/Web/Accessibility/ARIA/ARIA_Techniques",
                 "How assistive technology derives a name for an element."));

            using var memory = new SqliteMemory(Path.Combine(_dir, "earned.db"));
            var host = new ModuleHost(memory, NullEventBus.Instance);
            host.Load(new ToolsModule(new WorkspacePathGuard(), new UiScenarioToolGates()));
            var queen = new Queen(memory);
            queen.AdoptModuleTools(host.ContributedTools);
            // Shadow the module's web_search AFTER adoption — Register is last-write-wins, and going
            // through AdoptModuleTools rather than Tools.Register keeps the profile and the
            // capability grant re-resolved with it, which is the whole reason that method exists.
            queen.AdoptModuleTools(new ITool[] { search });

            string? missionId = null;
            queen.RunMission(
                "Give the colony route's roster list an accessible name, following the public ARIA "
              + "guidance, and document the route.",
                onMissionCreated: id => missionId = id);
            Assert.NotNull(missionId);

            var tasks = queen.Memory.GetTasksForMission(missionId!);

            string Status(string ant) => tasks
                .FirstOrDefault(t => (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == ant)
                ?.GetValueOrDefault("status")?.ToString() ?? "<no task>";

            // 1. THE CARTOGRAPHER DID CARTOGRAPHY. Not "ran": succeeded, and its map is EXTRACTED
            //    from the console page above — the route id, the API call site. Change that file and
            //    this assertion changes with it, which is the difference between a map and a stub.
            Assert.Equal("complete", Status("ui_cartographer"));
            var maps = ((IArtifactStore)queen.Memory).ForMission(missionId!, ArtifactSchemas.UiMap).ToList();
            Assert.True(maps.Count > 0, "the cartographer produced no ui_map artifact");
            Assert.Contains("colony", maps[0].Payload);              // the route id, from the real file
            Assert.Contains("/api/colony/roster", maps[0].Payload);  // the call site the route makes

            //    AND THE MAP IS LOAD-BEARING, which is the strongest form of "not decorative"
            //    available here and was found by reading UiChangeGate rather than assumed. The coder
            //    patches index.html — a UI change — and the gate refuses a UI change unless the
            //    mission holds a ui_map that is both unmutated and schema-conformant. So the coder's
            //    completion below is only reachable THROUGH the cartographer's output. In the earlier
            //    mission the cartographer failed and nothing noticed, because nothing depended on it.
            Assert.True(maps[0].IsIntact() && ArtifactSchemaCheck.Validate(
                    maps[0].Schema, maps[0].Payload).Conforms,
                "the ui_map exists but would not satisfy UiChangeGate, so the coder's UI change "
              + "reached the patch path without a usable map");
            Assert.Equal("complete", Status("coder"));

            // 2. THE WEB ANT DID RESEARCH. It asked a query derived from the real goal, and the
            //    production source pipeline — SSRF refusal, dedupe, domain scoring, persistence —
            //    kept what came back. The previous scenario's web task ended `blocked`, its reason
            //    being the gate rather than anything about the mission.
            Assert.Equal("complete", Status("web"));
            Assert.True(search.Queries.Count > 0, "the web ant never reached the search tool");
            Assert.Contains("accessible name", search.Queries[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(queen.Memory.CountSourcesForMission(missionId!) > 0,
                "the search returned two results and no source record survived the pipeline");

            // 3. AND NOTHING ELSE REGRESSED: the eleven task-row roles are still all present, and
            //    tester and soldier are still INSERTIONS — no plan named them.
            var roles = tasks.Select(t => t.GetValueOrDefault("assigned_ant")?.ToString() ?? "").ToHashSet();
            foreach (var role in new[]
                     {
                         "researcher", "web", "file", "ui_cartographer", "coder", "builder",
                         "scribe", "verifier", "tester", "soldier", "medic",
                     })
                Assert.True(roles.Contains(role),
                    $"role '{role}' never received a task; roles that ran: {string.Join(",", roles)}");

            Assert.DoesNotContain("tester", EarnedTwelveRolePlan);
            Assert.DoesNotContain("soldier", EarnedTwelveRolePlan);

            Assert.DoesNotContain(tasks, t =>
            {
                var s = t.GetValueOrDefault("status")?.ToString() ?? "";
                return s is "running" or "pending" or "queued";
            });

            // 4. The twelfth role, post-finalization, as before.
            Assert.NotNull(queen.Memory.LoadMissionEvaluation(missionId!));
            Assert.True(queen.Memory.GetRecentEvents(100, "memory_candidate", missionId).Count > 0,
                "the archivist left no memory candidates after finalization");

            // 5. THE CLAUSE THIS SCENARIO EXISTS FOR, asserted rather than described.
            //
            //    "No role invoked to satisfy a count" needs an executable meaning, and the earlier
            //    mission supplied one by failing: its two decorative roles did not merely produce
            //    thin answers, they ended `blocked` (the web ant, on the search gate) and
            //    `failed_permanent` (the cartographer, "no UI files could be read"). A role given
            //    nothing to work on cannot finish, and the runtime says so in the status field.
            //
            //    So: no PLANNER-SELECTED role ends blocked or permanently failed. Scoped to the
            //    planned roles deliberately — the tester's failure here is real and expected (see
            //    the header), and folding an inserted role's honest failure into this clause would
            //    make the assertion answer a different question, which is the defect this
            //    repository keeps re-finding.
            var plannedRoles = Regex.Matches(EarnedTwelveRolePlan, @"""assigned_ant"":\s*""([a-z_]+)""")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var idle = tasks
                .Where(t => plannedRoles.Contains(t.GetValueOrDefault("assigned_ant")?.ToString() ?? ""))
                .Where(t => (t.GetValueOrDefault("status")?.ToString() ?? "")
                            is "blocked" or "failed_permanent")
                .Select(t => $"{t.GetValueOrDefault("assigned_ant")}={t.GetValueOrDefault("status")} "
                           + $"({t.GetValueOrDefault("blocked_reason") ?? t.GetValueOrDefault("failure_reason")})")
                .ToList();

            Assert.True(idle.Count == 0,
                "these planned roles could not do the work the plan gave them: "
              + string.Join("; ", idle)
              + ". A role that ends blocked or permanently failed for want of a subject is a role in "
              + "the plan to make the count, which is the clause qualification scenario 15 turns on. "
              + "Give the mission a goal that needs the role, or take the role out of the plan.");
        }
        finally
        {
            AnthillRuntime.EnableWebSearch = webWas;
            RosterGates.Restore(gatesWere);
        }
    }

    /// <summary>
    /// The earned-roles scenario's gates. Two deliberate differences from
    /// <see cref="ScenarioToolGates"/>:
    ///   * `.html` joins the patch suffixes, because the mission's whole point is that it changes a
    ///     UI route as well as a document;
    ///   * `WebSearchEnabled` stays FALSE even though this scenario's web ant runs. That is not a
    ///     contradiction — the ant's own gate is `AnthillRuntime.EnableWebSearch`, and the tool it
    ///     reaches is <see cref="ScriptedWebSearchTool"/>, registered over the module's. Leaving the
    ///     module's gate shut means the real tool underneath refuses if the shadowing ever stops
    ///     working, so this fixture cannot degrade into a unit test that makes network calls.
    /// </summary>
    private sealed class UiScenarioToolGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => false;
        public bool ShellToolEnabled => false;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => false;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } =
            new HashSet<string> { ".md", ".txt", ".html" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
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
