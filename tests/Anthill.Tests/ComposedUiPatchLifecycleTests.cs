using Anthill.Api;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.Modules.Tools;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Qualification scenario 5, composed: a UI change reaches applied bytes THROUGH a map the
/// cartographer actually drew. v0.3.8.78 (PLAN.md §2 R2).
///
/// WHAT THE LEDGER SAID, and why it was a problem. Scenario 5's entry cited `UiChangeGateTests` and
/// `UiCartographerAntTests` with the note "the gate and the producer are each proved; the composed
/// UI-patch lifecycle is not". That sentence is accurate and it was not labelled PARTIAL — so
/// `QualificationMatrixTests`, which has a guard for exactly this, could not see it. A scenario
/// admitting in prose that it is incomplete, in a ledger whose whole job is to say which scenarios
/// are incomplete, is the same defect class as a stale checkbox: the document knew, and nothing
/// mechanical did.
///
/// THE GAP THE TWO EXISTING SUITES LEAVE. `UiChangeGateTests` proves the gate REFUSES without a
/// conforming map, by handing it a map it constructed. `UiCartographerAntTests` proves the ant emits
/// one, in isolation. Neither proves the thing the scenario is about: that a cartographer running
/// inside a real mission produces a map that the gate then accepts for a coder in that same mission,
/// and that the resulting patch survives the tester, the soldier, verification and the apply. Each
/// half can be true while the join is broken — a map with the right shape and the wrong mission id,
/// a gate reading a store the cartographer never wrote to — and a join is not proved by proving its
/// ends.
///
/// WHAT MAKES THIS COMPOSED RATHER THAN SCRIPTED. `UiCartographerAnt` takes a `ToolRegistry` and no
/// model: it reads the workspace through `list_directory` and `read_text_file` and builds the map
/// from what it finds. So the map here is NOT scripted — the scripted colony has no say in it. The
/// fixture seeds real UI files, and if the ant reads nothing the gate refuses and this test fails,
/// which is precisely the coupling scenario 5 claims and could not previously demonstrate.
///
/// A PROPERTY OF THE ROLE THIS TEST HAD TO LEARN, recorded because the next fixture will hit it:
/// discovery does NOT recurse. The listing is top-level only, and everything below it is found by a
/// fixed list of thirteen conventional layout probes. A UI file in an unconventional subdirectory is
/// invisible to the cartographer — not an error, just absent from the map — so where a fixture puts
/// its files decides whether this scenario can run at all. See the constructor.
/// </summary>
[Collection("specialist-gates")]   // workspace root, route table and roster gates are process-wide
public class ComposedUiPatchLifecycleTests : IDisposable
{
    private const string Target = "static/app.js";
    private const string CheckId = "roster_has_accessible_name";
    private const string Label = "aria-label";

    private const string Before = """
        export function renderRoster(items) {
          const list = document.createElement('ul');
          return list;
        }
        """;

    private const string After = """
        export function renderRoster(items) {
          const list = document.createElement('ul');
          list.setAttribute('aria-label', 'Colony roster');
          return list;
        }
        """;

    private readonly string _dir;
    private readonly string _workspace;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _rootWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly bool _sandboxWas = AnthillRuntime.EnableSandboxExecution;
    private readonly IReadOnlyList<CheckDefinition> _checksWere = AnthillRuntime.WorkspaceChecks;
    private readonly bool _autoApplyWas = AnthillRuntime.AutonomyAutoApplyEnabled;
    private readonly bool _patchApplyWas = AnthillRuntime.EnablePatchApplication;
    private readonly bool _fileWriteWas = AnthillRuntime.EnableFileWriting;
    private readonly List<string> _pathsWere = AnthillRuntime.AutonomyAutoApplyPaths;
    private readonly string _verifyWas = AnthillRuntime.AutonomyAutoApplyVerifyCmd;
    private readonly bool _keepWas = AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public ComposedUiPatchLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-ui-" + Guid.NewGuid().ToString("N")[..10]);
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, "static"));

        // REAL UI files, at locations the cartographer can actually FIND — and the first draft of
        // this fixture put them in `ui/`, which it cannot.
        //
        // `UiCartographerAnt` discovers by two routes and neither one recurses. It calls
        // `list_directory` on `.`, and `DirectoryListTool` uses `GetFileSystemInfos()` — TOP LEVEL
        // ONLY, printing bare names — so a file one directory down never appears in the text its
        // extension regex reads. It then appends thirteen CONVENTIONAL layout probes
        // (`index.html`, `src/app.js`, `static/app.js`, `public/index.html`, …) and reads those
        // directly. `ui/app.js` is in neither set, so every read failed, `examined` came back empty,
        // and the ant returned "no UI files could be read from the workspace" — the gate then
        // refused the coder and the mission failed for a reason with nothing to do with the join
        // under test.
        //
        // So the seed uses both discovery paths deliberately: `index.html` at the root is found by
        // the LISTING (and is also probe one), and `static/app.js` is found by PROBE. If either
        // mechanism breaks, this test fails at assertion 2 with the cause named.
        File.WriteAllText(Path.Combine(_workspace, "index.html"),
            "<!doctype html>\n<div id=\"page-roster\"></div>\n"
          + "<script src=\"static/app.js\"></script>\n");
        File.WriteAllText(Path.Combine(_workspace, Target), Before.Replace("\r\n", "\n") + "\n");
        File.WriteAllText(Path.Combine(_workspace, "README.txt"), "seed workspace\n");

        AnthillRuntime.EnableSandboxExecution = false;
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _rootWas;
        AnthillRuntime.EnableSandboxExecution = _sandboxWas;
        AnthillRuntime.WorkspaceChecks = _checksWere;
        AnthillRuntime.AutonomyAutoApplyEnabled = _autoApplyWas;
        AnthillRuntime.EnablePatchApplication = _patchApplyWas;
        AnthillRuntime.EnableFileWriting = _fileWriteWas;
        AnthillRuntime.AutonomyAutoApplyPaths = _pathsWere;
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = _verifyWas;
        AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = _keepWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The operator's check, and it is about THE CHANGE rather than about the file.
    ///
    /// Scenario 3's check could be "the file exists", because its patch created one. This patch
    /// MODIFIES a file that is already there, so an existence check would pass identically against
    /// the unpatched tree — a check answering a question adjacent to the one asked, which is this
    /// repository's most frequent defect and would have made the tester's PASS meaningless here.
    /// Searching for the attribute fails before the patch and passes after it.
    /// </summary>
    private static ConfiguredCheck LabelPresent() => OperatingSystem.IsWindows()
        ? new ConfiguredCheck { Id = CheckId, Command = "cmd.exe",
                                Arguments = $@"/c findstr /C:""{Label}"" static\app.js",
                                TimeoutSeconds = 30, Description = "the roster list has an accessible name" }
        : new ConfiguredCheck { Id = CheckId, Command = "grep",
                                Arguments = $"-q {Label} {Target}",
                                TimeoutSeconds = 30, Description = "the roster list has an accessible name" };

    /// <summary>
    /// The post-apply verify, and it carries NO DOUBLE QUOTES — deliberately, against a defect in
    /// the runner rather than a preference.
    ///
    /// `AutoApplyRunner.RunShell` passes the whole command through
    /// `ProcessStartInfo.ArgumentList.Add(command)`. .NET escapes list arguments by C-RUNTIME rules,
    /// so an inner `"` is emitted as `\"` — and `cmd.exe` does not follow those rules. A verify
    /// command written as `findstr /C:"aria-label" file` therefore reaches findstr as
    /// `/C:\"aria-label\"`, matches nothing, exits 1, and the auto-applied patch is rolled back
    /// with "Verify FAILED" against a tree where the change is present and correct.
    ///
    /// Scenario 3's verify (`type docs\COLONY-NOTE.md`) has no quotes, which is why this went
    /// unseen. The same shape sits in the auto-commit command in that file
    /// (`git -c user.name="ANTHILL Auto-Apply" … -m "{msg}"`), which no test exercises. Recorded in
    /// PLAN.md as its own item: fixing the quoting is a change to every auto-apply verify already
    /// configured in the field, and it does not belong inside a release about qualification.
    ///
    /// `findstr` needs no `/C:` here because the search string contains no spaces.
    /// </summary>
    private static string VerifyCommand() => OperatingSystem.IsWindows()
        ? $@"findstr {Label} static\app.js" : $"grep -q {Label} {Target}";

    /// <summary>
    /// The plan schedules the cartographer and NOT the coder — and that is the scenario, not a
    /// shortcut.
    ///
    /// `UiCartographerAnt` emits a HANDOFF to `code_change` carrying its `ui_map`. So a coder task
    /// always arrives from the map, and the composed path scenario 5 describes is precisely that
    /// one: the map is what summons the coder, and `UiChangeGate` then admits the dispatch because
    /// the map exists.
    ///
    /// THE FIRST DRAFT SCHEDULED A CODER TASK TOO, and the mission produced TWO patch sets — the
    /// planned one and the handoff's — both proposing the identical modify from the identical base.
    /// The apply then did exactly what it should: the first write landed, the second was refused
    /// because `static/app.js` no longer hashed to the base it was built against, and the whole
    /// batch rolled back as a unit. Nothing was broken; the fixture had asked for the change twice
    /// and patch integrity declined to apply a stale one. Removing the planned task is the fix,
    /// because the handoff is not an extra route to the coder — it IS the route.
    /// </summary>
    private const string Plan = """
        {
          "tasks": [
            { "title": "Frame the accessibility change", "description": "State what the roster list needs.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Map the colony route", "description": "Map the UI surface the change touches.",
              "assigned_ant": "ui_cartographer", "assigned_worker": "ui_cartographer.route_mapper",
              "task_type": "ui_mapping", "depends_on": ["Frame the accessibility change"] },
            { "title": "Verify the outcome", "description": "Check the change does what was asked.",
              "assigned_ant": "verifier", "assigned_worker": "verifier.result_verifier",
              "task_type": "verification", "depends_on": ["Map the colony route"] }
          ]
        }
        """;

    private static readonly string Proposals = """
        {
          "summary": "Give the roster list an accessible name.",
          "proposals": [
            {
              "file_path": "static/app.js",
              "change_type": "modify",
              "old_content": "__OLD__",
              "new_content": "__NEW__",
              "reason": "The roster list needs an accessible name for screen readers.",
              "risk": "low - one attribute on one element"
            }
          ]
        }
        """
        .Replace("__OLD__", Before.Replace("\r\n", "\n").Replace("\n", "\\n"))
        .Replace("__NEW__", After.Replace("\r\n", "\n").Replace("\n", "\\n"));

    [Fact]
    public void AUiPatch_RunsFromGoalToAppliedBytes_ThroughAMapTheCartographerDrew()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.EnableUiCartographerAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;

        var resolved = WorkspaceCheckConfig.Resolve(new[] { LabelPresent() });
        Assert.Empty(resolved.Problems);
        AnthillRuntime.WorkspaceChecks = resolved.Checks;

        AnthillRuntime.AutonomyAutoApplyEnabled = true;
        AnthillRuntime.EnablePatchApplication = true;
        AnthillRuntime.EnableFileWriting = true;
        AnthillRuntime.AutonomyAutoApplyPaths = new List<string> { "static/**" };
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = VerifyCommand();
        AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = false;

        // The cartographer is NOT scripted — it has no router to script. Its entry here would be
        // ignored even if present, exactly as the tester's is.
        var book = new ScriptBook()
            .Role("planner", Plan)
            .Role("researcher", "SCRIPTED: the roster list is announced without a name.")
            .Role("coder", Proposals)
            .Role("builder", "SCRIPTED: the accessible name was added, reviewed and verified.")
            .Role("verifier", "Verdict: Verification Passed\n- Reasoning: the roster list now carries "
                            + "an accessible name.\n- Missing Steps: none\n- Risk Notes: none")
            .Role("tester", "SCRIPTED: unused — the tester runs checks, not models.")
            .Role("soldier", "SCRIPTED: an attribute on one element introduces no security concern.")
            .Role("medic", "SCRIPTED: unused — nothing failed.")
            .Role("archivist", "SCRIPTED: recorded.");

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "builder", "verifier",
            "tester", "soldier", "medic", "archivist", "ui_cartographer", "fallback");

        using var memory = new SqliteMemory(Path.Combine(_dir, "ui.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard(), new UiGates()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        string? missionId = null;
        queen.RunMission(
            "Give the colony route's roster list an accessible name in the UI.",
            onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var live = Path.Combine(_workspace, "static", "app.js");

        // 1. THE MISSION DID NOT WRITE. Asserted against gates that would have permitted it —
        //    see UiGates — so it is the roster contract being proved, not the configuration.
        Assert.DoesNotContain(Label, File.ReadAllText(live));

        // 2. A MAP EXISTS, IT CONFORMS, AND THIS MISSION PRODUCED IT.
        //    The join scenario 5 is about. A conforming map from some other mission would satisfy a
        //    shape check and prove nothing about this lifecycle, so the mission id is asserted too.
        var maps = ((IArtifactStore)queen.Memory).ForMission(missionId!)
            .Where(a => a.Schema == ArtifactSchemas.UiMap).ToList();

        Assert.True(maps.Count > 0,
            "no ui_map artifact was produced in this mission, so the coder's dispatch was either "
          + "refused or never gated. The cartographer builds the map by READING the workspace — if "
          + "it found no UI files, that is the cause. It does not recurse: the fixture seeds "
          + "index.html at the root (found by the listing) and static/app.js (found by the "
          + "conventional-layout probe) for exactly this reason.");
        Assert.All(maps, m => Assert.True(
            ArtifactSchemaCheck.Validate(m.Schema, m.Payload).Conforms,
            $"the cartographer emitted a ui_map that does not conform: {m.Payload}"));

        // 3. THE GATE ADMITTED THE CODER, and did not merely stay silent. A refusal is recorded, so
        //    its ABSENCE beside a produced patch set is what says the map was consulted and accepted.
        var refusals = queen.Memory.GetRecentEvents(300, null, missionId)
            .Where(e => (e.GetValueOrDefault("event_type")?.ToString() ?? "").Contains("ui_map"))
            .Select(e => $"    {e.GetValueOrDefault("event_type")}: {e.GetValueOrDefault("message")}")
            .ToList();
        var proposed = queen.Memory.ListPatchProposalsForMission(missionId!);
        Assert.True(proposed.Count > 0,
            "the coder produced no patch set. Either the cartographer's handoff never created the "
          + "coder task — it is the ONLY route to the coder in this plan — or the UI gate refused "
          + "the dispatch:\n"
          + (refusals.Count == 0 ? "    (no ui_map events recorded)" : string.Join("\n", refusals)));

        // EXACTLY ONE proposal for the target, and this pins a finding rather than a preference.
        // Scheduling a coder task alongside the cartographer's handoff produces two patch sets that
        // propose the same modify from the same base; the first applies, the second is refused as
        // stale, and the batch rolls back as a unit. That is patch integrity working — and it means
        // "the change was requested twice" is indistinguishable from "the apply is broken" unless
        // the count is asserted here, where the cause is still legible.
        Assert.True(proposed.Count(p => (p.GetValueOrDefault("file_path")?.ToString() ?? "") == Target) == 1,
            $"expected exactly one proposal for {Target}, found "
          + string.Join(", ", proposed.Select(p => $"{p.GetValueOrDefault("file_path")}"))
          + ". Two proposals of the same modify cannot both apply: the second no longer matches the "
          + "base hash it was built against, and the set applies as a unit or not at all.");

        // 4. CANONICALLY VERIFIED — the failure message names the layer, because completed_verified
        //    is a conjunction of four and the outcome code identifies none of them.
        var evaluation = queen.Memory.LoadMissionEvaluation(missionId!);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"the mission did not reach completed_verified.\n"
          + $"  outcome:      {evaluation.OutcomeCode}\n"
          + $"  structural:   {evaluation.StructuralStatus}\n"
          + $"  verification: {evaluation.VerificationStatus}\n"
          + $"  deliverable:  {evaluation.DeliverableStatus}\n"
          + $"  stop_reason:  {evaluation.StopReason ?? "(ran to its natural end)"}\n"
          + $"  explanation:  {evaluation.Explanation}\n"
          + $"  evidence:     {string.Join(" | ", ((IEvidenceStore)queen.Memory)
                .ForMission(missionId!).Select(e => $"{e.Kind}:{(e.Passed ? "pass" : "fail")}"
                    + $" det={e.Deterministic} rev={e.RevisionId ?? "(none)"}"))}");

        // 5. THE OPERATOR'S CHECK IS WHAT VERIFIED IT, and it is a check about the CHANGE: it
        //    searches for the attribute, so it fails against the tree as the fixture seeded it.
        var checkEvidence = queen.Memory.GetTasksForMission(missionId!)
            .Select(t => queen.Memory.LoadTaskResult(t.GetValueOrDefault("id")?.ToString() ?? ""))
            .Where(r => r is not null)
            .SelectMany(r => r!.Evidence)
            .Where(e => e.Kind == "check").ToList();
        Assert.Contains(checkEvidence, e => e.Value == CheckId);

        var evidence = ((IEvidenceStore)queen.Memory).ForMission(missionId!);
        Assert.Contains(evidence, e => e.Kind == "security_policy" && e.Passed);
        Assert.Contains(evidence, e => e.IdentifiesARevision);

        // ---- the apply ----
        AutoApplyRunner.Run(queen, missionId!);

        var applyLog = queen.Memory.GetRecentEvents(300, null, missionId)
            .Concat(queen.Memory.GetRecentEvents(300, null, AnthillRuntime.SystemApiMissionId))
            .Where(e => (e.GetValueOrDefault("event_type")?.ToString() ?? "").StartsWith("autonomy_autoapply"))
            .Select(e => $"    {e.GetValueOrDefault("event_type")}: {e.GetValueOrDefault("message")}")
            .ToList();

        // 6. THE BYTES ARE THERE — the sentence scenario 5 exists for.
        var applied = File.ReadAllText(live).Replace("\r\n", "\n");
        Assert.True(applied.Contains(Label),
            "the verified UI patch was not applied to the workspace. The mission reached "
          + "completed_verified, so this is the apply path itself.\n"
          + "  auto-apply events:\n"
          + (applyLog.Count == 0 ? "    (none — the runner returned before logging anything)\n"
                                 : string.Join("\n", applyLog) + "\n")
          + $"  proposals: {string.Join(", ", queen.Memory.ListPatchProposalsForMission(missionId!)
                .Select(x => $"{x.GetValueOrDefault("file_path")}={x.GetValueOrDefault("status")}"))}\n"
          + $"  allowlist: {string.Join(", ", AnthillRuntime.AutonomyAutoApplyPaths)}\n"
          + $"  verify_cmd: {AnthillRuntime.AutonomyAutoApplyVerifyCmd}");

        // …and it is the patch, not a truncation that happens to contain the attribute.
        Assert.Equal(After.Replace("\r\n", "\n"), applied.TrimEnd('\n'));

        // 7. A VERIFIED apply, not the break-glass.
        Assert.Empty(queen.Memory.GetRecentEvents(200, "autonomy_autoapply_break_glass", missionId));
        Assert.NotEmpty(queen.Memory.GetRecentEvents(200, "autonomy_autoapply_started", missionId));

        // 8. And the record moved with the bytes.
        var proposals = queen.Memory.ListPatchProposalsForMission(missionId!);
        Assert.NotEmpty(proposals);
        Assert.DoesNotContain(proposals, p =>
            (p.GetValueOrDefault("status")?.ToString() ?? "") == "proposed");
    }

    /// <summary>
    /// The tool gates for this scenario, and both write flags are TRUE for the reason
    /// `AppliedDocsPatchLifecycleTests.DocsGates` records at length: `AutoApplyRunner` writes THROUGH
    /// `ApplyPatchTool`, which checks `PatchApplicationEnabled` and `FileWritingEnabled` on
    /// consecutive lines, so turning either off stops the director rather than the mission.
    ///
    /// `PatchAllowedSuffixes` differs — this scenario patches `.js`, and a docs-only suffix list
    /// would refuse the write for a reason unrelated to the UI gate under test.
    /// </summary>
    private sealed class UiGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => true;    // with PatchApplicationEnabled: the pair
        public bool ShellToolEnabled => false;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => true;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } =
            new HashSet<string> { ".js", ".html", ".css", ".md" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }
}
