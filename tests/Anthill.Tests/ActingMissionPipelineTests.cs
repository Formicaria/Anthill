using System.Diagnostics;
using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Workspaces;
using Anthill.Modules.Reasoning;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.95 — the acting mission pipeline: per-project worktrees, the mission-worktree CLI
/// working directory, the acting coder judged by the tree, the bypass flag bounded out of
/// confined workspaces, and the capture that happens while the task graph is still open.
///
/// The rule these tests follow is the repository's own (CrossBoundaryAgreementTests): a value that
/// crosses a boundary is OBTAINED FROM ITS PRODUCER, never constructed to match. The base revision
/// a workspace records is compared against what git itself says; permission argv is built by the
/// real translator from a real access context; the one ordering property asserted at source level
/// follows FinalizationOrderTests' precedent, because the property IS the position of a call.
/// </summary>
public class ActingMissionPipelineTests : IDisposable
{
    private readonly string _dir;

    public ActingMissionPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-acting-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // -------------------------------------------------------------------------------------------
    // Per-project worktrees
    // -------------------------------------------------------------------------------------------

    private string NewGitRepo(string name, string fileName = "README.md")
    {
        var repo = Path.Combine(_dir, name);
        Directory.CreateDirectory(repo);
        Git(repo, "init -b main");
        Git(repo, "config user.email test@anthill.local");
        Git(repo, "config user.name Test");
        File.WriteAllText(Path.Combine(repo, fileName), $"{name}\n");
        Git(repo, "add -A");
        Git(repo, "commit -m first");
        return repo;
    }

    /// <summary>
    /// The override wins: a manager configured over repository A prepares a worktree of repository
    /// B when the mission's project points there. Base revision and source root are compared
    /// against what git says about B — the producer of both facts.
    /// </summary>
    [Fact]
    public void PrepareWithAProjectRoot_TakesTheWorktreeFromTheProjectsRepository()
    {
        var configured = NewGitRepo("configured-repo");
        var project = NewGitRepo("project-repo", "app.cs");

        using var memory = new SqliteMemory(Path.Combine(_dir, "per-project.db"));
        var manager = new MissionWorkspaceManager(memory, configured);

        var workspace = manager.Prepare("mission-b", project);
        try
        {
            Assert.True(workspace.Usable, workspace.Note ?? "not usable");
            Assert.Equal(SameSlashes(Path.GetFullPath(project)), SameSlashes(workspace.SourceRoot));
            Assert.Equal(Git(project, "rev-parse HEAD"), workspace.BaseRevision);
            Assert.NotEqual(Git(configured, "rev-parse HEAD"), workspace.BaseRevision);
            // The worktree really is B's tree: the file that only exists in B is present.
            Assert.True(File.Exists(Path.Combine(workspace.Root, "app.cs")),
                "the worktree does not contain the project repository's files");
        }
        finally
        {
            try { Git(project, $"worktree remove --force \"{workspace.Root}\""); } catch { }
        }
    }

    /// <summary>No override keeps the configured source — the pre-project behaviour, bit for bit.</summary>
    [Fact]
    public void PrepareWithoutAProjectRoot_KeepsTheConfiguredSource()
    {
        var configured = NewGitRepo("only-repo");
        using var memory = new SqliteMemory(Path.Combine(_dir, "no-override.db"));
        var manager = new MissionWorkspaceManager(memory, configured);

        var workspace = manager.Prepare("mission-a");
        try
        {
            Assert.True(workspace.Usable, workspace.Note ?? "not usable");
            Assert.Equal(SameSlashes(Path.GetFullPath(configured)), SameSlashes(workspace.SourceRoot));
            Assert.Equal(Git(configured, "rev-parse HEAD"), workspace.BaseRevision);
        }
        finally
        {
            try { Git(configured, $"worktree remove --force \"{workspace.Root}\""); } catch { }
        }
    }

    /// <summary>A project path that is not a git checkout is Rejected and NAMES the path — never a
    /// silent fall-through to the configured repository, which would put the mission's work in a
    /// tree the operator did not point it at.</summary>
    [Fact]
    public void AProjectRootThatIsNotACheckout_IsRejectedByName()
    {
        var configured = NewGitRepo("real-repo");
        var bare = Path.Combine(_dir, "not-a-repo");
        Directory.CreateDirectory(bare);

        using var memory = new SqliteMemory(Path.Combine(_dir, "rejected.db"));
        var manager = new MissionWorkspaceManager(memory, configured);

        var workspace = manager.Prepare("mission-c", bare);
        Assert.False(workspace.Usable);
        Assert.Equal(WorkspaceState.Rejected, workspace.State);
        Assert.Contains("not a git checkout", workspace.Note ?? "", StringComparison.Ordinal);
    }

    /// <summary>The mission carries its project, and the store keeps it.</summary>
    [Fact]
    public void AMissionsProject_SurvivesTheStoreAndTheDeepCopy()
    {
        using var memory = new SqliteMemory(Path.Combine(_dir, "project-id.db"));
        var mission = new Mission { Goal = "belongs to a project", ProjectId = "proj-42" };
        memory.SaveMission(mission);

        var row = memory.GetMission(mission.Id);
        Assert.NotNull(row);
        Assert.Equal("proj-42", row!.GetValueOrDefault("project_id")?.ToString());
        Assert.Equal("proj-42", mission.DeepCopy().ProjectId);
    }

    // -------------------------------------------------------------------------------------------
    // The CLI working directory is the mission worktree
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// EnterAgentAccess prefers the ambient mission worktree and withholds the live-tree grants
    /// with it. Source-position asserted, FinalizationOrderTests-style, because the defect this
    /// closes was one expression: `confinedWorkspace: true` declared beside a workingDirectory
    /// that was the live project path — the comment and the runtime disagreeing on the single
    /// property that decides where an acting agent's edits land.
    /// </summary>
    [Fact]
    public void TheAgentAccessScope_UsesTheMissionWorktree_WhenOneExists()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var method = source.IndexOf("private IDisposable EnterAgentAccess", StringComparison.Ordinal);
        Assert.True(method >= 0, "EnterAgentAccess is no longer recognisable");
        var body = source[method..source.IndexOf("AgentAccessScope.Enter", method, StringComparison.Ordinal)];

        Assert.Contains("MissionWorkspaceScope.CurrentRoot", body, StringComparison.Ordinal);
        Assert.Contains("grants = Array.Empty<string>()", body, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Bounded permissions: the bypass flag never crosses into a confined workspace
    // -------------------------------------------------------------------------------------------

    private static AgentCli Claude() =>
        AgentCliCatalog.ById("agent:claude-code") ?? throw new InvalidOperationException("claude-code left the catalogue");

    [Fact]
    public void BypassInsideAConfinedWorkspace_IsBoundedToEditsAndTools_NeverTheSkipFlag()
    {
        var access = new AgentAccessScope.Context(
            "bypass", Array.Empty<string>(), ConfinedWorkspace: true,
            WorkingDirectory: "/tmp/worktree", RoleMayWrite: true);

        var args = AgentCliCatalog.BuildAccessArgs(Claude(), access);

        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("acceptEdits", args);
    }

    /// <summary>The unconfined road still honours the operator's explicit Skip-all — the clamp is
    /// about confinement, not about deleting the policy the operator chose in words.</summary>
    [Fact]
    public void BypassOutsideConfinement_KeepsTheOperatorsExplicitChoice()
    {
        var access = new AgentAccessScope.Context(
            "bypass", Array.Empty<string>(), ConfinedWorkspace: false,
            WorkingDirectory: null, RoleMayWrite: true);

        var args = AgentCliCatalog.BuildAccessArgs(Claude(), access);

        Assert.Contains("--dangerously-skip-permissions", args);
    }

    /// <summary>And the role clamp still wins over everything: a read-only role under bypass gets
    /// no permission flags at all, confined or not.</summary>
    [Fact]
    public void AReadOnlyRoleUnderBypass_GetsNoPermissionFlags()
    {
        foreach (var confined in new[] { true, false })
        {
            var access = new AgentAccessScope.Context(
                "bypass", Array.Empty<string>(), ConfinedWorkspace: confined,
                WorkingDirectory: null, RoleMayWrite: false);

            var args = AgentCliCatalog.BuildAccessArgs(Claude(), access);

            Assert.DoesNotContain("--dangerously-skip-permissions", args);
            Assert.DoesNotContain("--permission-mode", args);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The acting coder is judged by the tree
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ChangesOnDisk_AreAnActingSuccess_AndTheNarrativeRidesAsArtifact()
    {
        var result = CoderAnt.ClassifyActingOutcome("edited two files", new[] { "src/A.cs", "src/B.cs" });

        Assert.True(result.Success);
        Assert.Contains("2 file(s)", result.Summary, StringComparison.Ordinal);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(CoderAnt.WorkspaceEditReportKind, artifact.Kind);
    }

    [Fact]
    public void ACleanTreeWithTheDeclaredMarker_IsALegitimateNoOp()
    {
        var result = CoderAnt.ClassifyActingOutcome(
            CoderAnt.NoChangesMarker + " — the guard already exists.", Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Contains("no changes were needed", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>A clean tree plus a story about edits is a FAILURE that says so. The narrative can
    /// never outrank the filesystem — that inversion is the reason acting mode classifies from
    /// disk in the first place.</summary>
    [Fact]
    public void ACleanTreeWithAWorkNarrative_Fails()
    {
        var result = CoderAnt.ClassifyActingOutcome("I refactored the parser and fixed the bug.",
            Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Contains(CoderAnt.NoChangesMarker, result.Failure?.Reason ?? result.Summary, StringComparison.Ordinal);
    }

    /// <summary>ChangedPaths reports what porcelain status reports — obtained from a real
    /// repository, because the property under test is git's, not the parser's.</summary>
    [Fact]
    public void ChangedPaths_SeesModificationsAndNewFiles()
    {
        var repo = NewGitRepo("changed-paths");
        Assert.Empty(WorkspaceChangeSet.ChangedPaths(repo));

        File.WriteAllText(Path.Combine(repo, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "born\n");

        var changed = WorkspaceChangeSet.ChangedPaths(repo);
        Assert.Contains("README.md", changed);
        Assert.Contains("new.txt", changed);
    }

    // -------------------------------------------------------------------------------------------
    // One workspace, one capture
    // -------------------------------------------------------------------------------------------

    /// <summary>The change-set producer stamps the workspace it diffed, and the store can answer
    /// for it — the idempotence key that stops finalization re-harvesting what the acting path
    /// already captured while the graph was open.</summary>
    [Fact]
    public void AWorkspaceDerivedPatchSet_CarriesItsWorkspaceId_AndTheStoreCanAnswerForIt()
    {
        var repo = NewGitRepo("stamped");
        File.WriteAllText(Path.Combine(repo, "born.txt"), "new\n");

        using var memory = new SqliteMemory(Path.Combine(_dir, "attribution.db"));
        var mission = new Mission { Goal = "attribute" };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Id = "task-1", Title = "coder task", Description = "acting",
            AssignedAnt = "coder", TaskType = "code_change",
        });
        memory.SaveMission(mission);

        var workspace = new MissionWorkspace
        {
            Id = "ws-stamp", MissionId = mission.Id, SourceRoot = repo, Root = repo,
            Mode = "worktree", BaseRevision = Git(repo, "rev-parse HEAD"),
            State = WorkspaceState.Active,
        };

        var set = WorkspaceChangeSet.Create(workspace, mission.Id, "task-1", "stamp test");
        Assert.Equal("ws-stamp", set.WorkspaceId);
        Assert.Contains(set.Proposals, p => p.FilePath == "born.txt");

        memory.SavePatchSet(set);
        Assert.True(memory.HasPatchSetForWorkspace(mission.Id, "ws-stamp"));
        Assert.False(memory.HasPatchSetForWorkspace(mission.Id, "ws-other"));
    }

    /// <summary>The finalization harvest checks before re-capturing — source-position asserted:
    /// the idempotence guard must sit before the change set is built.</summary>
    [Fact]
    public void TheFinalizationHarvest_SkipsAWorkspaceAlreadyCaptured()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        var harvest = source.IndexOf("private void HarvestWorkspaceChanges", StringComparison.Ordinal);
        Assert.True(harvest >= 0, "HarvestWorkspaceChanges is no longer recognisable");
        var body = source[harvest..];

        var guard = body.IndexOf("HasPatchSetForWorkspace", StringComparison.Ordinal);
        var create = body.IndexOf("WorkspaceChangeSet.Create", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the harvest no longer asks whether the workspace was already captured");
        Assert.True(create > guard,
            "the already-captured check must run before a second change set is built");
    }

    /// <summary>The acting capture feeds the ONE pipeline with a live scheduler — so reviewers
    /// are inserted and a revision is materialised, which the finalization harvest can never do.</summary>
    [Fact]
    public void TheActingCapture_FeedsTheOnePipeline_WhileTheGraphIsOpen()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var method = source.IndexOf("private void ProcessWorkspaceEdits", StringComparison.Ordinal);
        Assert.True(method >= 0, "ProcessWorkspaceEdits is no longer recognisable");
        var body = source[method..];
        var pipeline = body.IndexOf("ProcessPatchSet(mission, context, task, changes, scheduler)", StringComparison.Ordinal);
        Assert.True(pipeline >= 0 && pipeline < 4000,
            "the acting capture no longer routes through ProcessPatchSet with the live scheduler");

        // And the dispatch discriminator is the producer's own artifact kind, not re-derived config.
        Assert.Contains("CoderAnt.WorkspaceEditReportKind", source, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // The route-write permission exists
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// v0.3.8.95 — POST /routes/{role} requires "manage_models", and that key was never a member
    /// of ApiPermissions: ApiPermissionAllowed answers false for absent keys, so every route write
    /// 403'd for everyone, admin included. Found live, driving a real qualification run. The
    /// permission must exist and ship granted, like its read twin.
    /// </summary>
    [Fact]
    public void TheRouteWritePermission_ExistsAndShipsGranted()
    {
        Assert.True(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions.TryGetValue("manage_models", out var granted),
            "manage_models is not a key in ApiPermissions — POST /routes/{role} is un-grantable again");
        Assert.True(granted);
    }

    // -------------------------------------------------------------------------------------------
    // v0.3.8.96 — what the live qualification run found
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The UI gate matches "ui" as a WORD. As a substring it lived inside "build" — so the moment
    /// a conversation transcript said "Build final response" (every mission's plan does), every
    /// later mission in that conversation was refused for having no frontend map. Two live runs
    /// failed on it before the substring was suspected.
    /// </summary>
    [Theory]
    [InlineData("Create docs/NOTES.md. Transcript: [Colony] Build final response complete.", false)]
    [InlineData("follow the guide and keep it quite small for the builder", false)]
    [InlineData("fix the UI alignment on the settings screen", true)]
    [InlineData("polish the frontend spacing", true)]
    [InlineData("update the dashboard cards", true)]
    public void TheUiGoalSignal_MatchesTheWordUi_NeverTheLettersInsideAnotherWord(string goal, bool expected)
    {
        Assert.Equal(expected, UiChangeGate.LooksLikeUiWork(goal, ""));
    }

    /// <summary>
    /// THE GATE MUST NOT REFUSE ITSELF. Found live, the same day as the substring fix: once the
    /// gate refused a mission, its own refusal prose ("changes the ui … map the frontend") entered
    /// the conversation transcript, the transcript rides beneath every later composed goal under
    /// the "--- conversation context ---" delimiter, and every later mission in that conversation
    /// was refused — a self-sustaining refusal seeded by the gate quoting itself. The goal signal
    /// judges only the operator's ask, above the first section marker; colony narration below it
    /// is never evidence of operator intent. A markerless goal is judged whole, as ever.
    /// </summary>
    [Fact]
    public void TheGatesOwnRefusalInTheTranscript_CannotRefuseTheNextMission()
    {
        var poisoned =
            "Create a new file docs/ACTING-RUN-RECORD.md with one line. Do not modify any existing file.\n"
          + "\n--- conversation context (what the request above refers to) ---\n"
          + "Colony: \"Create structured patch proposal\" failed: this task changes the ui and the "
          + "mission has no ui_map. The cartographer must map the frontend before a change is proposed.";

        Assert.False(UiChangeGate.LooksLikeUiWork(poisoned, ""));

        // And a genuine UI ask ABOVE the marker still trips it — the reduction narrows the text,
        // never the rule.
        var genuine = "fix the UI alignment\n\n--- conversation context ---\nColony: earlier chatter";
        Assert.True(UiChangeGate.LooksLikeUiWork(genuine, ""));
    }

    /// <summary>
    /// The capture never proposes the colony's own scaffolding. ANTHILL materializes the agent's
    /// settings file into the worktree; the live run found that file riding in EVERY change set —
    /// proposed to the operator as mission work, tripping the soldier's script rule on the way.
    /// Producer-obtained: a real repository, the real settings path, a real diff.
    /// </summary>
    [Fact]
    public void TheCapture_NeverProposesTheMaterializedSettingsFile()
    {
        var repo = NewGitRepo("scaffolding");
        Directory.CreateDirectory(Path.Combine(repo, ".claude"));
        File.WriteAllText(Path.Combine(repo, ".claude", "settings.local.json"), "{\"_anthill\":\"m\"}\n");
        File.WriteAllText(Path.Combine(repo, "real-work.txt"), "the mission's actual output\n");

        var changed = WorkspaceChangeSet.ChangedPaths(repo);
        Assert.Contains("real-work.txt", changed);
        Assert.DoesNotContain(changed, p => p.Contains("settings.local.json", StringComparison.OrdinalIgnoreCase));

        var workspace = new MissionWorkspace
        {
            Id = "ws-scaf", MissionId = "m-scaf", SourceRoot = repo, Root = repo,
            Mode = "worktree", BaseRevision = Git(repo, "rev-parse HEAD"), State = WorkspaceState.Active,
        };
        var set = WorkspaceChangeSet.Create(workspace, "m-scaf", "t-scaf", "scaffolding test");
        Assert.Contains(set.Proposals, p => p.FilePath == "real-work.txt");
        Assert.DoesNotContain(set.Proposals, p => p.FilePath.Contains("settings.local.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>And a worktree whose ONLY change is the scaffolding classifies as an idle acting
    /// turn — the settings file must never make a no-op look like work.</summary>
    [Fact]
    public void ScaffoldingAlone_IsNotWork()
    {
        var repo = NewGitRepo("scaffolding-only");
        Directory.CreateDirectory(Path.Combine(repo, ".claude"));
        File.WriteAllText(Path.Combine(repo, ".claude", "settings.local.json"), "{\"_anthill\":\"m\"}\n");

        Assert.Empty(WorkspaceChangeSet.ChangedPaths(repo));
    }

    /// <summary>
    /// Core cannot reference the provider module, so the scaffolding path is duplicated — and this
    /// is the test the duplication comment promises: the two constants must be the same file.
    /// </summary>
    [Fact]
    public void TheScaffoldingPath_AgreesWithTheCatalogsSettingsPath()
    {
        var claude = AgentCliCatalog.ById("agent:claude-code");
        Assert.NotNull(claude);
        Assert.Equal(
            WorkspaceChangeSet.AgentSettingsRelativePath.Replace('\\', '/'),
            (claude!.LocalSettingsRelativePath ?? "").Replace('\\', '/'));
    }

    /// <summary>
    /// A route save persists BOTH halves. POST /routes/{role} used to mutate the live dictionary
    /// and then save the untouched Config object — every save wrote the stale routes, and a route
    /// lived exactly until the next restart. Found by restarting mid-qualification. Source-position
    /// asserted, FinalizationOrderTests-style, because the defect was two updates that each worked
    /// and never met.
    /// </summary>
    [Fact]
    public void ARouteSave_WritesTheLiveDictionaryAndThePersistedConfig_Together()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));
        var method = runtime.IndexOf("public static void SetModelRoute", StringComparison.Ordinal);
        Assert.True(method >= 0, "SetModelRoute is no longer recognisable in AnthillRuntime.cs");
        var body = runtime[method..(method + 900)];
        var live = body.IndexOf("ModelRouting[role]", StringComparison.Ordinal);
        var persisted = body.IndexOf("Config.ModelRoutes[role]", StringComparison.Ordinal);
        var save = body.IndexOf("SaveConfig()", StringComparison.Ordinal);
        Assert.True(live >= 0 && persisted >= 0, "SetModelRoute no longer writes both halves");
        Assert.True(save > live && save > persisted,
            "SaveConfig must run after BOTH the live dictionary and the persisted config are updated");

        var api = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Routes.cs")));
        Assert.Contains("AnthillRuntime.SetModelRoute(", api, StringComparison.Ordinal);
        Assert.DoesNotContain("AnthillRuntime.ModelRouting[role]", api, StringComparison.Ordinal);
    }

    /// <summary>
    /// The settings surface can write the acting gate, the startup warns about legacy config
    /// files, and the snapshot reports the checks that govern plus every declared check the
    /// resolver refused — three "present and unreachable" findings from one live run, pinned.
    /// </summary>
    [Fact]
    public void TheConfigSurface_CarriesTheQualificationRunsFindings()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        // acting_coder_enabled is an editable key…
        var editable = runtime.IndexOf("EditableConfigKeys", StringComparison.Ordinal);
        Assert.True(editable >= 0);
        Assert.Contains("\"acting_coder_enabled\"", runtime[editable..(editable + 2500)], StringComparison.Ordinal);

        // …the loader warns about the relic location…
        Assert.Contains("WarnAboutLegacyConfigs(path)", runtime, StringComparison.Ordinal);
        Assert.Contains("data/anthill.json", runtime, StringComparison.Ordinal);

        // …and the snapshot answers for the checks, including the refusals.
        var snapshot = Anthill.Core.Configuration.AnthillRuntime.SettingsSnapshot();
        Assert.True(snapshot.ContainsKey("workspace_checks_active"));
        Assert.True(snapshot.ContainsKey("workspace_check_problems"));
        Assert.True(snapshot.ContainsKey("acting_coder_enabled"));
    }

    private static string Git(string workdir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workdir, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return output.Trim();
    }

    private static string SameSlashes(string path) => path.Replace('\\', '/');
}
