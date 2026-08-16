using Anthill.Api;
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
/// QUALIFICATION SCENARIO 7, composed: a soldier block stops a real lifecycle, and the thing it
/// stops is a WRITE. v0.3.8.71.
///
/// The ledger has held scenario 7 at PARTIAL since v0.3.8.57: `SoldierAntTests` and
/// `DeterministicBlockTests` prove the soldier reads the real patch set and that its block cannot be
/// argued away by model text, but "a composed mission where a soldier block stops a real lifecycle"
/// was open. Those are two different claims. The first is about the soldier; the second is about
/// everything downstream believing it.
///
/// WHAT THIS DRIVES, end to end: a Queen mission on the scripted provider proposes a documentation
/// patch whose content contains a credential. The soldier is POLICY-INSERTED on the patch set's
/// existence — no plan names it — reads the real proposals, and `PolicyScan`'s `secret_material`
/// rule fires as a BLOCKING finding. The mission then cannot reach a positive canonical evaluation,
/// and `AutoApplyRunner.Run` refuses to write.
///
/// THE WRITE GATES ARE DELIBERATELY ON. `autonomy_autoapply_enabled`, `patch_application_enabled`
/// and `file_writing_enabled` are all true for this run, which is the only configuration in which
/// the assertion means anything: with them off, nothing would be written no matter what the soldier
/// decided, and the test would pass while proving nothing about the block. The refusal must be
/// attributable to the missing verified evaluation, so the recorded reason is asserted too, not just
/// the absence of the file.
///
/// A note on the fixture's honesty: the tester also fails in this mission, environmentally — see
/// TheTesterHasNoSeam_ForAFixtureWorkspace below, which is this release's other finding. That does
/// NOT weaken the claim here, because the assertion is not "the mission failed"; it is that the
/// soldier recorded a deterministic block, that the block is present in the persisted record, and
/// that the write path refused and said why. A mission that failed for two reasons still refused
/// for both of them.
/// </summary>
[Collection("specialist-gates")]   // workspace root, route table and UseOllama are static
public class SoldierBlockLifecycleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _workspace;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _workspaceRootWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly bool _sandboxWas = AnthillRuntime.EnableSandboxExecution;
    private readonly bool _autoApplyWas = AnthillRuntime.AutonomyAutoApplyEnabled;
    private readonly bool _patchApplyWas = AnthillRuntime.EnablePatchApplication;
    private readonly bool _fileWriteWas = AnthillRuntime.EnableFileWriting;
    private readonly List<string> _autoApplyPathsWas = AnthillRuntime.AutonomyAutoApplyPaths;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public SoldierBlockLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-soldier-" + Guid.NewGuid().ToString("N")[..10]);
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
        AnthillRuntime.AutonomyAutoApplyEnabled = _autoApplyWas;
        AnthillRuntime.EnablePatchApplication = _patchApplyWas;
        AnthillRuntime.EnableFileWriting = _fileWriteWas;
        AnthillRuntime.AutonomyAutoApplyPaths = _autoApplyPathsWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Plan = """
        {
          "tasks": [
            { "title": "Frame the request", "description": "State what the runbook must document.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Propose the runbook patch", "description": "Propose the runbook as a structured patch, JSON only.",
              "assigned_ant": "coder", "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal", "depends_on": ["Frame the request"] },
            { "title": "Build the operator answer", "description": "Assemble the outcome for the operator.",
              "assigned_ant": "builder", "assigned_worker": "builder.response_builder",
              "task_type": "build_answer", "depends_on": ["Propose the runbook patch"] }
          ]
        }
        """;

    /// <summary>
    /// A runbook that pastes in a real-looking credential. This is the honest version of scenario 7:
    /// not an attack, but the ordinary way secrets reach a repository — someone documenting a
    /// procedure and copying the working example in with it. `PolicyScan.secret_material` is
    /// critical and blocking, and matches a quoted value after an `api_key`-shaped noun.
    /// </summary>
    private const string ProposalsWithASecret = """
        {
          "summary": "Add the deployment runbook.",
          "proposals": [
            {
              "file_path": "docs/RUNBOOK.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "# Deployment runbook\n\nExport the credential before running the deploy:\n\n    api_key = \"sk-live-9f3a2b7c4d1e\"\n",
              "reason": "The mission asks for a deployment runbook.",
              "risk": "low"
            }
          ]
        }
        """;

    [Fact]
    public void ASoldierBlock_StopsTheLifecycle_AndTheWriteRefusesForThatReason()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;

        // THE WRITE GATES ARE ON. Without this the refusal below would be unattributable.
        AnthillRuntime.AutonomyAutoApplyEnabled = true;
        AnthillRuntime.EnablePatchApplication = true;
        AnthillRuntime.EnableFileWriting = true;
        AnthillRuntime.AutonomyAutoApplyPaths = new List<string> { "docs/**" };

        var book = new ScriptBook()
            .Role("planner", Plan)
            .Role("researcher", "SCRIPTED: the runbook documents the deploy procedure.")
            .Role("coder", ProposalsWithASecret)
            .Role("builder", "SCRIPTED: the runbook was proposed and awaits review.")
            .Role("verifier", "SCRIPTED: the record addresses the request.")
            .Role("tester", "SCRIPTED: unused — the tester runs checks, not models.")
            .Role("soldier", "SCRIPTED: model prose the deterministic scan overrides.")
            .Role("medic", "SCRIPTED: the check failure is environmental to this tree.")
            .Role("archivist", "SCRIPTED: recorded.");

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "builder", "verifier",
            "tester", "soldier", "medic", "archivist", "fallback");

        using var memory = new SqliteMemory(Path.Combine(_dir, "soldier.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard(), new DocsScenarioToolGates()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        string? missionId = null;
        queen.RunMission("Document the deployment procedure in a runbook.",
            onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var tasks = queen.Memory.GetTasksForMission(missionId!);

        // 1. THE SOLDIER WAS INSERTED, not planned. The plan above names three roles and none is the
        //    soldier; it exists because a patch set does.
        Assert.DoesNotContain("soldier", Plan);
        var soldierTask = tasks.FirstOrDefault(t =>
            (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == "soldier");
        Assert.NotNull(soldierTask);

        // 2. THE BLOCK IS IN THE PERSISTED RECORD, as a typed marker rather than as prose. v3.8.22
        //    found this exact thing recorded only as a list of rule ids nothing downstream
        //    recognised, so the Summary's "not overridable" was a claim the code did not implement.
        var soldierResult = queen.Memory.LoadTaskResult(soldierTask!.GetValueOrDefault("id")?.ToString() ?? "");
        Assert.NotNull(soldierResult);
        Assert.Contains(Anthill.Core.Agents.SoldierAnt.SoldierBlockMarker, soldierResult!.Warnings);

        // 3. THE MISSION IS NOT CANONICALLY VERIFIED. This is the link the scenario is about: the
        //    soldier's finding has to reach the evaluator, or the block stops at the soldier.
        var evaluation = queen.Memory.LoadMissionEvaluation(missionId!);
        Assert.True(evaluation is null || !evaluation.IsPositive,
            "the mission reached a positive canonical evaluation with a deterministic security block "
          + $"recorded against its patch set (outcome: {evaluation?.OutcomeCode ?? "none"}). Every "
          + "downstream refusal — auto-apply, skill promotion, positive learning — reads that "
          + "evaluation, so a block that does not reach it stops nothing.");

        // 4. AND THE WRITE REFUSES, WITH THE RIGHT REASON. The gates are open; the only thing
        //    standing between this patch and the operator's tree is the evaluation.
        AutoApplyRunner.Run(queen, missionId!);

        Assert.False(File.Exists(Path.Combine(_workspace, "docs", "RUNBOOK.md")),
            "a patch carrying a credential was written to the workspace after a deterministic "
          + "security block. The write gates were deliberately ON for this run, so this is the "
          + "block failing to stop the write rather than a disabled feature standing in for it.");

        var skips = queen.Memory.GetRecentEvents(200, "autonomy_autoapply_skipped");
        Assert.Contains(skips, e =>
            (e.GetValueOrDefault("message")?.ToString() ?? "").Contains(missionId!, StringComparison.Ordinal)
            && (e.GetValueOrDefault("message")?.ToString() ?? "")
                .Contains("no canonical completed_verified evaluation", StringComparison.Ordinal));
    }

    // The defect this fixture uncovered — the soldier scanning a patch artifact's JSON
    // serialization instead of its values — is proved in SecretPatternEncodingTests, against the
    // real serializer rather than a transcription of its escaping. v0.3.8.72 moved it there and
    // deleted the copies that lived here: the earlier pair asserted the same property twice, and
    // one of them described the escaping as `\"`, which is not what JsonSerializer emits.

    /// <summary>
    /// THIS RELEASE'S OTHER FINDING, written as a test because a paragraph in a plan does not stay
    /// true. v0.3.8.71.
    ///
    /// Qualification scenarios 3 (an APPLIED docs patch) and 15's last edge (a check that fails
    /// BECAUSE of the proposal) both need one thing: a mission in a fixture workspace whose tester
    /// PASSES. There is currently no way to produce one, and the reason is structural rather than a
    /// missing script book — which is what the plan assumed, and why both have sat open.
    ///
    /// Three routes, all closed:
    ///   * REGISTER A CHECK. `CheckCatalog.Register` is documented as the "operator/test extension
    ///     point". `TesterAnt` selects from it by matching ids against its task's TITLE and
    ///     DESCRIPTION — and for a policy-inserted review those are fixed strings built by
    ///     `ExecutionService` ("Run checks on the proposed change" / "Run the workspace's declared
    ///     checks against patch set {id}"). A mission cannot mention a check id, so a registered
    ///     check can never be selected by the role that exists to run checks.
    ///   * LET IT FALL BACK. With no ids matched and no manifest, the tester runs
    ///     `{dotnet_version, dotnet_build}`. `dotnet build` in a directory with no project fails.
    ///   * GIVE THE FIXTURE A PROJECT. A `.csproj` makes the .NET adapter fire — and a detected
    ///     workspace runs EVERYTHING its adapters declare, deliberately ("a tester that picked a
    ///     subset would be choosing which failures the colony is allowed to notice"). That is
    ///     `dotnet_build`, `dotnet_test` AND `dotnet_format_check`. A minimal fixture project passes
    ///     the first and fails the other two, so adding a project makes it worse.
    ///
    /// None of that is a defect on its own — each piece is defending something real, and the
    /// adapter-detection direction is an explicit exit gate ("verification commands come from the
    /// manifest or operator configuration, never model invention"). The gap is that OPERATOR
    /// CONFIGURATION is named in that sentence and has no path to the tester: the catalog is
    /// reachable only through task text the operator does not write.
    ///
    /// This test pins the three facts so the next attempt starts from them instead of rediscovering
    /// them, and fails the moment any of them stops being true — at which point scenarios 3 and 15
    /// become writable and this test should be replaced by them.
    /// </summary>
    [Fact]
    public void TheTesterHasNoSeam_ForAFixtureWorkspace()
    {
        // 1. The inserted tester's task text is fixed and mentions no check id, so the catalog's
        //    documented extension point cannot be reached through it.
        var executionService = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));
        Assert.Contains("Run checks on the proposed change", executionService);
        Assert.Contains("Run the workspace's declared checks against patch set", executionService);

        // Demonstrated rather than argued: a check registered through the documented extension point
        // is in the catalog, and neither inserted-task string mentions it — nor could, since both
        // are built from the patch set id.
        const string probe = "fixture_probe";
        Anthill.Core.Tools.CheckCatalog.Register(new Anthill.Core.Tools.CheckDefinition(
            probe, "dotnet", "--version", 30, true, "test: reachable only if selectable"));
        try
        {
            Assert.NotNull(Anthill.Core.Tools.CheckCatalog.Get(probe));
            Assert.DoesNotContain(probe, "Run checks on the proposed change");
            Assert.DoesNotContain(probe, "Run the workspace's declared checks against patch set ");
        }
        finally { Anthill.Core.Tools.CheckCatalog.Unregister(probe); }

        // 2. The fallback for an undetected workspace is the .NET pair, and dotnet_build needs a
        //    project. Read from the ant so a change to the fallback fails here.
        var specialists = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));
        Assert.Contains("\"dotnet_version\", \"dotnet_build\"", specialists);

        // 3. A detected .NET workspace runs all three adapter checks, not a chosen subset.
        var adapter = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Workspaces", "WorkspaceAdapter.cs")));
        foreach (var check in new[] { "dotnet_build", "dotnet_test", "dotnet_format_check" })
            Assert.Contains(check, adapter);
        Assert.Contains("requested = manifest.IsEmpty", specialists);
    }

    /// <summary>Docs patches allowed; file tools on; web, shell and direct writes off. Auto-apply's
    /// own gates are set on the runtime by the test, which is where AutoApplyRunner reads them.</summary>
    private sealed class DocsScenarioToolGates : IToolRuntimeOptions
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
