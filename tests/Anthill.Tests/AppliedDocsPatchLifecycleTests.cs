using Anthill.Api;
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
/// QUALIFICATION SCENARIO 3 — a documentation patch, driven through the Queen, that ends up ON DISK.
/// v0.3.8.75, and it is the last of the twenty.
///
/// WHAT MADE IT LAST. Every lifecycle test in this suite runs with `patch_application_enabled`
/// false, so in the project's whole history no test had driven a change from a goal onto the
/// operator's tree and asserted the bytes were there. Scenario 4 stops at "materialized and
/// reviewed". The distance between those is the word APPLY.
///
/// FOUR DEFECTS STOOD BETWEEN THE TWO, and every one was found by trying to reach an outcome nothing
/// had needed before:
///   * v0.3.8.70 — the tester's check ran in the ORIGINAL tree whenever the adapters detected no
///     project type, so no patch could change a check's outcome.
///   * v0.3.8.73 — the tester had no operator seam, so a fixture workspace could not produce a
///     passing check at all (`CheckSource`, `workspace_checks`).
///   * v0.3.8.74 — a green mission was graded `escalated`, because one stop reason covered both
///     "the bound is spent" and "there was nothing left to add".
///   * v0.3.8.75 — a DOCUMENTATION patch was verified as a CODE patch and therefore compiled. The
///     `docs_patch` policy requiring no build had existed, unreachable, the whole time.
///
/// THE NINE GATES between a proposal and a byte, all live in this run: auto-apply enabled, a
/// persisted `completed_verified` evaluation, both write gates, a writable root, proposals still
/// `proposed`, `AutoApplyPolicy` eligibility, no `ROLLBACK_FAILED` marker, evidence stamped
/// `rev:{patchSetId}`, and the post-apply verify.
///
/// THE VERIFY IS A REAL COMMAND, not the break-glass. `keep_without_verify` would let the patch stay
/// unverified and the runner records that as a critical event declaring the installation
/// unqualifiable — a qualification scenario tripping it would assert the opposite of what the code
/// says about itself.
/// </summary>
[Collection("specialist-gates")]
public class AppliedDocsPatchLifecycleTests : IDisposable
{
    private const string Target = "docs/COLONY-NOTE.md";
    private const string CheckId = "colony_note_present";
    private const string Body = "# Colony note\n\nThe colony proposed, reviewed, verified and applied this.\n";

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

    public AppliedDocsPatchLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-apply-" + Guid.NewGuid().ToString("N")[..10]);
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, "docs"));
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
    /// The operator's declared check: the note is present in the tree being judged. It passes against
    /// the MATERIALIZED revision, which contains the proposal, and fails against the unpatched tree —
    /// so the tester's PASS is a fact about the change rather than about the environment.
    ///
    /// It reads the deliverable directly, in `docs/`. An earlier draft moved this to a root-level
    /// file on the theory that a subdirectory target was what broke it; that theory was never tested,
    /// because the failure it was invented to explain turned out to be v0.3.8.74's adaptive-stop
    /// defect. Guessing produced a workaround for a defect that did not exist, so the guess is undone
    /// and the straightforward thing is used.
    /// </summary>
    private static ConfiguredCheck NotePresent() => OperatingSystem.IsWindows()
        ? new ConfiguredCheck { Id = CheckId, Command = "cmd.exe", Arguments = @"/c type docs\COLONY-NOTE.md",
                                TimeoutSeconds = 30, Description = "the colony note exists" }
        : new ConfiguredCheck { Id = CheckId, Command = "cat", Arguments = Target,
                                TimeoutSeconds = 30, Description = "the colony note exists" };

    /// <summary>The post-apply verify: the same fact, against the LIVE tree this time.</summary>
    private static string VerifyCommand() => OperatingSystem.IsWindows()
        ? @"type docs\COLONY-NOTE.md" : $"test -f {Target}";

    private const string Plan = """
        {
          "tasks": [
            { "title": "Frame the note", "description": "State what the colony note must say.",
              "assigned_ant": "researcher", "assigned_worker": "researcher.mission_researcher",
              "task_type": "research", "depends_on": [] },
            { "title": "Propose the documentation patch", "description": "Propose the note as a structured patch, JSON only.",
              "assigned_ant": "coder", "assigned_worker": "coder.docs_coder",
              "task_type": "patch_proposal", "depends_on": ["Frame the note"] },
            { "title": "Verify the outcome", "description": "Check the proposal addresses the request.",
              "assigned_ant": "verifier", "assigned_worker": "verifier.result_verifier",
              "task_type": "verification", "depends_on": ["Propose the documentation patch"] }
          ]
        }
        """;

    /// <summary>A pure documentation patch — one proposal, a `docs/` path. That is what makes
    /// `VerificationPolicy` select `docs_patch` and skip the build (v0.3.8.75).</summary>
    private static readonly string Proposals = """
        {
          "summary": "Add the colony note.",
          "proposals": [
            {
              "file_path": "docs/COLONY-NOTE.md",
              "change_type": "add",
              "old_content": null,
              "new_content": "__BODY__",
              "reason": "The mission asks for a documentation note.",
              "risk": "low"
            }
          ]
        }
        """.Replace("__BODY__", Body.Replace("\n", "\\n"));

    [Fact]
    public void ADocumentationPatch_RunsFromGoalToAppliedBytes_AndTheEvidenceIsAboutThatRevision()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;

        var resolved = WorkspaceCheckConfig.Resolve(new[] { NotePresent() });
        Assert.Empty(resolved.Problems);
        AnthillRuntime.WorkspaceChecks = resolved.Checks;

        AnthillRuntime.AutonomyAutoApplyEnabled = true;
        AnthillRuntime.EnablePatchApplication = true;
        AnthillRuntime.EnableFileWriting = true;
        AnthillRuntime.AutonomyAutoApplyPaths = new List<string> { "docs/**" };
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = VerifyCommand();
        AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = false;

        var book = new ScriptBook()
            .Role("planner", Plan)
            .Role("researcher", "SCRIPTED: the note should record that the colony wrote it.")
            .Role("coder", Proposals)
            .Role("builder", "SCRIPTED: the note was proposed, reviewed and verified.")
            // The verifier answers in ITS OWN DECLARED FORMAT. `VerifierRules` asks for
            // "Verdict: Verification Passed / …" and MissionVerification reads that line off the
            // task result; prose saying the same thing parses to Unknown, which is not a pass.
            // Scripting the contract's shape is what a compliant model does.
            .Role("verifier", "Verdict: Verification Passed\n- Reasoning: the note was proposed, "
                            + "checked and reviewed.\n- Missing Steps: none\n- Risk Notes: none")
            .Role("tester", "SCRIPTED: unused — the tester runs checks, not models.")
            .Role("soldier", "SCRIPTED: a documentation note introduces no security concern.")
            .Role("medic", "SCRIPTED: unused — nothing failed.")
            .Role("archivist", "SCRIPTED: recorded.");

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "coder", "builder", "verifier",
            "tester", "soldier", "medic", "archivist", "fallback");

        using var memory = new SqliteMemory(Path.Combine(_dir, "apply.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        // The tool gate is ON — see DocsGates. The mission still cannot write: no mission ant may
        // dispatch a patch tool, which the roster contract pins. Production is the same shape.
        host.Load(new ToolsModule(new WorkspacePathGuard(), new DocsGates()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        string? missionId = null;
        queen.RunMission("Add a colony note to the documentation.", onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        var live = Path.Combine(_workspace, "docs", "COLONY-NOTE.md");

        // 1. NOTHING IS ON DISK YET — asserted before the apply, so the file's later existence
        //    cannot be an artefact of the fixture.
        Assert.False(File.Exists(live), "the mission wrote to the operator's tree by itself");

        // 2. THE MISSION IS CANONICALLY VERIFIED. The failure message names the LAYER, because
        //    completed_verified is a conjunction of four and the outcome code identifies none of
        //    them — two rounds of this test were spent inferring which one said no.
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
          + $"  evidence:     {string.Join(" | ", ((Anthill.SDK.Artifacts.IEvidenceStore)queen.Memory)
                .ForMission(missionId!).Select(e => $"{e.Kind}:{(e.Passed ? "pass" : "fail")}"
                    + $" det={e.Deterministic} rev={e.RevisionId ?? "(none)"}"))}\n"
          + "auto-apply consumes this evaluation, so nothing below can happen without it.");

        // 3. THE OPERATOR'S CHECK IS WHAT VERIFIED IT — not dotnet_build. "The seam is wired" is
        //    exactly the claim that passes while a fallback quietly runs instead.
        var checkEvidence = queen.Memory.GetTasksForMission(missionId!)
            .Select(t => queen.Memory.LoadTaskResult(t.GetValueOrDefault("id")?.ToString() ?? ""))
            .Where(r => r is not null)
            .SelectMany(r => r!.Evidence)
            .Where(e => e.Kind == "check").ToList();
        Assert.Contains(checkEvidence, e => e.Value == CheckId);
        Assert.DoesNotContain(checkEvidence, e => e.Value == "dotnet_build");

        // 4. AND NO BUILD RAN AT ALL, which is v0.3.8.75's narrowing seen from the mission: a
        //    documentation patch is verified as documentation.
        var evidence = ((Anthill.SDK.Artifacts.IEvidenceStore)queen.Memory).ForMission(missionId!);
        Assert.DoesNotContain(evidence, e => e.Kind == "build");
        Assert.Contains(evidence, e => e.Kind == "security_policy" && e.Passed);
        Assert.Contains(evidence, e => e.IdentifiesARevision);

        // ---- the apply ----
        AutoApplyRunner.Run(queen, missionId!);

        // 5. THE BYTES ARE THERE. The sentence scenario 3 exists for, and the first time the suite
        //    has been able to write it.
        // THE FAILURE MESSAGE NAMES THE GATE. AutoApplyRunner logs a reason for every refusal —
        // skipped, ineligible, stale_evidence, preflight_refused, halted — and a message that
        // omitted them would turn a decisive record into another round of inference, which is
        // exactly what assertion 2 above had to be rewritten to stop doing.
        var applyLog = queen.Memory.GetRecentEvents(300, null, missionId)
            .Concat(queen.Memory.GetRecentEvents(300, null, AnthillRuntime.SystemApiMissionId))
            .Where(e => (e.GetValueOrDefault("event_type")?.ToString() ?? "").StartsWith("autonomy_autoapply"))
            .Select(e => $"    {e.GetValueOrDefault("event_type")}: {e.GetValueOrDefault("message")}")
            .ToList();

        Assert.True(File.Exists(live),
            "the verified documentation patch was not applied to the workspace. The mission reached "
          + "completed_verified, so this is the apply path itself.\n"
          + "  auto-apply events:\n"
          + (applyLog.Count == 0 ? "    (none — the runner returned before logging anything)\n"
                                 : string.Join("\n", applyLog) + "\n")
          + $"  proposals: {string.Join(", ", queen.Memory.ListPatchProposalsForMission(missionId!)
                .Select(x => $"{x.GetValueOrDefault("file_path")}={x.GetValueOrDefault("status")}"))}\n"
          + $"  allowlist: {string.Join(", ", AnthillRuntime.AutonomyAutoApplyPaths)}\n"
          + $"  verify_cmd: {AnthillRuntime.AutonomyAutoApplyVerifyCmd}");
        Assert.Equal(Body, File.ReadAllText(live).Replace("\r\n", "\n"));

        // 6. A VERIFIED apply, not the break-glass.
        Assert.Empty(queen.Memory.GetRecentEvents(200, "autonomy_autoapply_break_glass", missionId));
        Assert.NotEmpty(queen.Memory.GetRecentEvents(200, "autonomy_autoapply_started", missionId));

        // 7. And the record moved with the bytes: a tree that changed while the ledger still says
        //    "awaiting approval" is what the approval pipeline exists to prevent.
        var proposals = queen.Memory.ListPatchProposalsForMission(missionId!);
        Assert.NotEmpty(proposals);
        Assert.DoesNotContain(proposals, p =>
            (p.GetValueOrDefault("status")?.ToString() ?? "") == "proposed");
    }

    /// <summary>
    /// The tool gates for this scenario. **Both write gates are TRUE**, which the first draft got
    /// wrong twice — once per flag, for the same plausible reason each time.
    ///
    /// That draft set them false so "the mission cannot write to the operator's tree", and the
    /// auto-apply events said what it actually did — first
    /// `Patch application is disabled by config`, then, after fixing only that one,
    /// `File writing is disabled by config` — each followed by a rollback of ZERO files.
    /// `AutoApplyRunner` writes THROUGH `ApplyPatchTool`, which checks both gates on consecutive
    /// lines, so the flags meant to stop the mission stopped the director. The fixture had disabled
    /// the very thing under test.
    ///
    /// They belong together and always have: §1b's containment configuration turns
    /// `patch_application_enabled` and `file_writing_enabled` off as a PAIR, because either one alone
    /// stops a write. Fixing one and re-running was the mistake — the second failure was the same
    /// finding arriving a second time.
    ///
    /// What actually prevents the mission from applying is neither flag: it is the roster contract,
    /// which forbids every mission ant from dispatching `apply_patch` (`RosterContractTests`). These
    /// are the operator's switch for whether the COLONY may write at all, and production has them on
    /// whenever auto-apply is on. Assertion 1 — the file is absent before `AutoApplyRunner.Run` — is
    /// what proves the mission did not write, and it now proves it against a configuration that
    /// would have permitted it to.
    /// </summary>
    private sealed class DocsGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => true;    // with PatchApplicationEnabled: the pair
        public bool ShellToolEnabled => false;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => true;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".md", ".txt" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }
}
