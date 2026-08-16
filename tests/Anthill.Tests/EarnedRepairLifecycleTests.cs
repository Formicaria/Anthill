using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// QUALIFICATION SCENARIO 15's LAST EDGE: a check that fails BECAUSE of the proposal, and passes
/// because of the repair. v0.3.8.73.
///
/// WHAT WAS MISSING, and it is the only thing scenario 15 still lacked. v0.3.8.69 gave the mission a
/// goal that earned eleven roles honestly, and recorded the one that remained decorative: the
/// tester's failure was ENVIRONMENTAL. A materialized revision in a temp directory has no build, so
/// `dotnet_build` failed for a reason the patch had nothing to do with, and the medic then repaired
/// a failure the change did not cause. The medic's TRIGGER was real; the failure's relationship to
/// the change was not.
///
/// TWO RELEASES HAD TO LAND FIRST, and this test needs both, which is why it is the right consumer
/// for them:
///   * v0.3.8.70 — the check now runs inside the materialized revision. Before that it ran against
///     the original tree whenever the adapters detected no project type, so no patch could ever
///     change a check's outcome. The two runs below would have been identical.
///   * v0.3.8.73 — the operator can DECLARE a check. Before that an undetected workspace fell back
///     to `dotnet_build`, and the documented extension point was unreachable because `TesterAnt`
///     matches ids against task text that `ExecutionService` writes, not the operator.
///
/// THE SHAPE. The declared check passes only when `VERIFIED.md` exists in the tree it runs in. The
/// coder's first proposal does not create it, so the check fails against revision one — a real
/// failure, caused by what the patch did and did not contain. The medic hands back to the coder, the
/// second proposal adds the file, and the same check passes against revision two. Nothing about the
/// environment changed between them; only the patch did.
/// </summary>
[Collection("specialist-gates")]   // workspace root, route table, UseOllama and WorkspaceChecks are static
public class EarnedRepairLifecycleTests : IDisposable
{
    private const string Marker = "VERIFIED.md";
    private const string CheckId = "colony_marker_present";

    private readonly string _dir;
    private readonly string _workspace;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _workspaceRootWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly bool _sandboxWas = AnthillRuntime.EnableSandboxExecution;
    private readonly IReadOnlyList<CheckDefinition> _checksWere = AnthillRuntime.WorkspaceChecks;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public EarnedRepairLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-repair-" + Guid.NewGuid().ToString("N")[..10]);
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, "docs"));
        File.WriteAllText(Path.Combine(_workspace, "README.txt"), "seed workspace\n");
        AnthillRuntime.EnableSandboxExecution = false;
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceRootWas;
        AnthillRuntime.EnableSandboxExecution = _sandboxWas;
        AnthillRuntime.WorkspaceChecks = _checksWere;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The operator's declared check: succeeds iff the marker is readable from the working
    /// directory. Cross-platform because CI is Linux and releases are cut on Windows, and a check
    /// that only ran on one would leave this scenario proved on one.
    /// </summary>
    private static ConfiguredCheck MarkerCheck() => OperatingSystem.IsWindows()
        ? new ConfiguredCheck { Id = CheckId, Command = "cmd.exe", Arguments = $"/c type {Marker}",
                                TimeoutSeconds = 30, Description = "the change is documented" }
        : new ConfiguredCheck { Id = CheckId, Command = "cat", Arguments = Marker,
                                TimeoutSeconds = 30, Description = "the change is documented" };

    private const string Plan = """
        {
          "tasks": [
            { "title": "Frame the request", "description": "State what the note must say.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Propose the documentation patch", "description": "Propose the note as a structured patch, JSON only.",
              "assigned_ant": "coder", "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal", "depends_on": ["Frame the request"] }
          ]
        }
        """;

    /// <summary>First attempt: the note, and nothing that satisfies the declared check.</summary>
    private const string ProposalsWithoutTheMarker = """
        {
          "summary": "Add the colony note.",
          "proposals": [
            {
              "file_path": "docs/COLONY-NOTE.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "# Colony note\n\nThe colony wrote this.\n",
              "reason": "The mission asks for a documentation note.",
              "risk": "low"
            }
          ]
        }
        """;

    /// <summary>The repair: the same note, plus the file the check is about.</summary>
    private const string ProposalsWithTheMarker = """
        {
          "summary": "Add the colony note and record that it is documented.",
          "proposals": [
            {
              "file_path": "docs/COLONY-NOTE.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "# Colony note\n\nThe colony wrote this.\n",
              "reason": "The mission asks for a documentation note.",
              "risk": "low"
            },
            {
              "file_path": "VERIFIED.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "docs/COLONY-NOTE.md\n",
              "reason": "The declared check requires the change to be recorded here.",
              "risk": "low"
            }
          ]
        }
        """;

    [Fact]
    public void ACheckFailsBecauseOfTheProposal_AndPassesBecauseOfTheRepair()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;

        // THE SEAM. One declared check, from the operator's configuration — the workspace being
        // modified contributes nothing. With it set, the tester's default selection is this check
        // rather than `dotnet_build`, which is what makes the outcome a fact about the patch.
        var resolved = WorkspaceCheckConfig.Resolve(new[] { MarkerCheck() });
        Assert.Empty(resolved.Problems);
        AnthillRuntime.WorkspaceChecks = resolved.Checks;

        var book = new ScriptBook()
            .Role("planner", Plan)
            .Role("researcher", "SCRIPTED: the note should describe the colony.")
            // TWO answers, in order. The second is what the medic's handoff re-asks for.
            .Role("coder", ProposalsWithoutTheMarker, ProposalsWithTheMarker)
            .Role("builder", "SCRIPTED: the note was proposed and reviewed.")
            .Role("verifier", "SCRIPTED: the record addresses the request.")
            .Role("tester", "SCRIPTED: unused — the tester runs checks, not models.")
            .Role("soldier", "SCRIPTED: a documentation note introduces no security concern.")
            .Role("medic", "SCRIPTED: the declared check requires VERIFIED.md; the patch omits it.")
            .Role("archivist", "SCRIPTED: recorded.");

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "builder", "verifier",
            "tester", "soldier", "medic", "archivist", "fallback");

        using var memory = new SqliteMemory(Path.Combine(_dir, "repair.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard(), new DocsGates()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        string? missionId = null;
        queen.RunMission("Add a colony note to the documentation.", onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var tasks = queen.Memory.GetTasksForMission(missionId!);

        // 1. THE OPERATOR'S CHECK IS WHAT RAN — not dotnet_build. Read from the tester's own
        //    recorded evidence rather than inferred, because "the seam is wired" is exactly the kind
        //    of claim that passes while the fallback quietly runs instead.
        var testerTasks = tasks
            .Where(t => (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == "tester").ToList();
        Assert.True(testerTasks.Count >= 2,
            $"expected the tester to run twice (fail, then pass after repair); it ran {testerTasks.Count} time(s)");

        var evidence = testerTasks
            .Select(t => queen.Memory.LoadTaskResult(t.GetValueOrDefault("id")?.ToString() ?? ""))
            .Where(r => r is not null)
            .SelectMany(r => r!.Evidence)
            .ToList();
        Assert.Contains(evidence, e => e.Value == CheckId);
        Assert.DoesNotContain(evidence, e => e.Value == "dotnet_build");

        // 2. IT FAILED, THEN PASSED. Two outcomes from one unchanged check and one unchanged
        //    environment — the only difference between the runs is the patch.
        //
        //    Asserted on the CHECK'S OWN EVIDENCE as well as the task status, because the two say
        //    different things and only one of them is about the check. The persisted `status` column
        //    is the `TaskStatus` enum — `failed`, `complete` — while `failed_retryable` is the
        //    AntExecutionResult's StatusCode and lives in task_results. The first draft of this
        //    assertion mixed the two vocabularies and failed against ["failed", "complete"], which
        //    was the sequence it was looking for, spelled the other way.
        var statuses = testerTasks.Select(t => t.GetValueOrDefault("status")?.ToString() ?? "").ToList();
        Assert.Contains("failed", statuses);
        Assert.Contains("complete", statuses);

        var checkOutcomes = evidence.Where(e => e.Value == CheckId)
            .Select(e => e.Detail ?? "").ToList();
        Assert.Contains(checkOutcomes, d => d.Contains("success=False", StringComparison.Ordinal));
        Assert.Contains(checkOutcomes, d => d.Contains("success=True", StringComparison.Ordinal));

        // 3. THE MEDIC RAN BETWEEN THEM, on the failure — the trigger scenario 15 always had, now
        //    attached to a failure the change actually caused.
        Assert.Contains(tasks, t => (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == "medic");

        // 4. AND THE TWO RUNS JUDGED DIFFERENT REVISIONS. This is what v0.3.8.70 made true: before
        //    it, both runs executed in the original tree and no patch could change an outcome.
        var revisions = queen.Memory.GetRecentEvents(200, "task_ran_in_revision", missionId)
            .Select(e => e.GetValueOrDefault("message")?.ToString() ?? "")
            .ToList();
        Assert.True(revisions.Count >= 2,
            "fewer than two revision-scoped runs were recorded, so the repair was never judged "
          + "against its own materialized tree");

        // 5. THE SECOND PATCH SET IS A DIFFERENT SET, not a re-run of the first — the coder answered
        //    twice and the runtime treated the answers as distinct proposals.
        var patchSets = queen.Memory.GetRecentEvents(200, "patch_set_created", missionId);
        Assert.True(patchSets.Count >= 2,
            $"expected two patch sets (proposal, then repair); saw {patchSets.Count}");
    }

    /// <summary>Docs patches allowed; file tools on; web, shell and writes off. The declared check
    /// is the only thing that executes, and it comes from configuration.</summary>
    private sealed class DocsGates : IToolRuntimeOptions
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
