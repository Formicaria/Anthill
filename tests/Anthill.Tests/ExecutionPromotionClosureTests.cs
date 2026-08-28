using System.Diagnostics;
using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Verification;
using Anthill.Core.Workspaces;
using Anthill.Modules.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.97 — EXECUTION/PROMOTION CLOSURE, the two-repository acceptance. Repository A stands in
/// for the colony's configured live root; repository B is the operator's selected project. The
/// properties under test are the release's seven gates: the selected project identity survives the
/// whole transaction (patch set → workspace → target root → preflight → apply); a multi-file set
/// applies completely or not at all on EVERY path including the operator's Apply; deletion and
/// rename are captured faithfully and applied correctly; A is byte-for-byte untouched throughout;
/// an unresolvable target fails closed; and a writable agent CLI with no worktree never starts.
///
/// CrossBoundaryAgreementTests' rule throughout: values that cross a boundary are OBTAINED FROM
/// THEIR PRODUCER — the change set from a real worktree diff, the target from the real resolver,
/// the applies through the real ApplyPatchTool — never constructed to match.
/// </summary>
public class ExecutionPromotionClosureTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _patchApplyWas = AnthillRuntime.EnablePatchApplication;
    private readonly bool _fileWriteWas = AnthillRuntime.EnableFileWriting;
    private readonly string _rootWas = AnthillRuntime.AllowedWorkspaceRoot;

    public ExecutionPromotionClosureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-epc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnablePatchApplication = _patchApplyWas;
        AnthillRuntime.EnableFileWriting = _fileWriteWas;
        AnthillRuntime.AllowedWorkspaceRoot = _rootWas;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private string NewGitRepo(string name, params (string Path, string Content)[] files)
    {
        var repo = Path.Combine(_dir, name);
        Directory.CreateDirectory(repo);
        Git(repo, "init -b main");
        Git(repo, "config user.email test@anthill.local");
        Git(repo, "config user.name Test");
        foreach (var (path, content) in files.Length > 0 ? files : new[] { ("README.md", $"{name}\n") })
            File.WriteAllText(Path.Combine(repo, path), content);
        Git(repo, "add -A");
        Git(repo, "commit -m first");
        return repo;
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

    /// <summary>Every file in a tree (excluding .git and the apply journal's own scaffolding), as
    /// one deterministic "path=sha" listing — the byte-for-byte comparison the acceptance names,
    /// in a shape whose equality failure prints WHAT differed.</summary>
    private static string TreeBytes(string root) =>
        string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !rel.StartsWith(".git", StringComparison.Ordinal)
                       && !rel.Contains(".anthill", StringComparison.OrdinalIgnoreCase))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .Select(rel => $"{rel}={ApplyTransaction.HashFile(Path.Combine(root, rel))}"));

    private sealed class Gates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => true;
        public bool ShellToolEnabled => false;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => true;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } =
            new HashSet<string> { ".md", ".txt", ".cs" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory { get; init; } = ".";
        public string BackupDirectory => "backups";
    }

    /// <summary>The REAL applier, per proposal: the same ApplyPatchTool production runs, rooted at
    /// the target through its guard. The delegate shape is Queen.ApplyPatchForAutomation's.</summary>
    private Func<string, Anthill.Core.Orchestration.Queen.AutoApplyOutcome> ApplyVia(
        string targetRoot, IReadOnlyList<(string PatchId, PatchProposal Proposal)> set)
    {
        var tool = new ApplyPatchTool(
            new Anthill.Core.Security.WorkspacePathGuard(targetRoot),
            new Gates { ScriptDirectory = _dir });

        return patchId =>
        {
            var proposal = set.First(x => x.PatchId == patchId).Proposal;
            var result = tool.Run(new Dictionary<string, object?>
            {
                ["patch"] = new Dictionary<string, object?>
                {
                    ["change_type"] = proposal.ChangeType.Value(),
                    ["file_path"] = proposal.FilePath,
                    ["old_content"] = proposal.OldContent,
                    ["new_content"] = proposal.NewContent,
                    ["base_hash"] = proposal.BaseHash,
                    ["destination_path"] = proposal.DestinationPath,
                },
            });

            string? backup = null, resolved = null, destination = null, appliedHash = null;
            try
            {
                var root = JsonDocument.Parse(string.IsNullOrEmpty(result.Output) ? "{}" : result.Output).RootElement;
                backup = root.TryGetProperty("backup_path", out var b) ? b.GetString() : null;
                resolved = root.TryGetProperty("file_path", out var f) ? f.GetString() : null;
                destination = root.TryGetProperty("destination_path", out var d) ? d.GetString() : null;
                appliedHash = root.TryGetProperty("applied_hash", out var h) ? h.GetString() : null;
            }
            catch { /* failure outcomes carry no JSON */ }

            return new Anthill.Core.Orchestration.Queen.AutoApplyOutcome(
                result.Success, patchId, result.Success ? null : result.Error,
                resolved, backup, proposal.ChangeType.Value(), proposal.FilePath,
                destination, appliedHash);
        };
    }

    // ---- the target resolver: project identity, fail-closed ----------------------------------

    [Fact]
    public void ASetWithNoRecordedWorkspace_ResolvesToTheLiveRoot()
    {
        var live = NewGitRepo("live-null");
        AnthillRuntime.AllowedWorkspaceRoot = live;
        using var memory = new SqliteMemory(Path.Combine(_dir, "resolver-null.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;   // SqliteMemory re-initializes config statics

        var target = PatchTargetResolver.Resolve(memory, null);
        Assert.True(target.Ok);
        Assert.True(target.IsLiveTree);
        Assert.Equal(Path.GetFullPath(live), target.Root);
    }

    [Fact]
    public void ASetWhoseWorkspaceNamesAnotherRepository_ResolvesToThatRepository()
    {
        var live = NewGitRepo("live-a1");
        var project = NewGitRepo("project-b1");
        using var memory = new SqliteMemory(Path.Combine(_dir, "resolver-b.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;

        var (mission, task) = SeedMissionAndTask(memory, "resolve to B");
        memory.SaveWorkspace(new MissionWorkspace
        {
            Id = "ws-b1", MissionId = mission.Id, Root = project, SourceRoot = project,
            Mode = "worktree", State = WorkspaceState.Active,
        });
        var set = SeedPatchSet(memory, mission, task, "ws-b1",
            NewProposal("README.md", "project-b1\n", "changed\n"));

        var target = PatchTargetResolver.Resolve(memory, set.Id);

        Assert.True(target.Ok, target.Problem);
        Assert.False(target.IsLiveTree);
        Assert.Equal(Path.GetFullPath(project), target.Root);
    }

    /// <summary>The set states where it belongs and that place cannot be established — refused,
    /// never redirected to the live root. Item 1's fail-closed half, at the resolver and at the
    /// gate, whose refusal enum carries it as TargetUnresolvable.</summary>
    [Fact]
    public void AWorkspaceTheStoreCannotProduce_FailsClosed_AtResolverAndGate()
    {
        var live = NewGitRepo("live-a2");
        using var memory = new SqliteMemory(Path.Combine(_dir, "resolver-ghost.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;
        AnthillRuntime.EnablePatchApplication = true;
        AnthillRuntime.EnableFileWriting = true;

        var (mission, task) = SeedMissionAndTask(memory, "ghost workspace");
        var set = SeedPatchSet(memory, mission, task, "ws-ghost",
            NewProposal("README.md", "live-a2\n", "changed\n"));

        var target = PatchTargetResolver.Resolve(memory, set.Id);
        Assert.False(target.Ok);
        Assert.Contains("ws-ghost", target.Problem ?? "");

        var verdict = PatchPromotionGate.Evaluate(
            memory, (Anthill.SDK.Artifacts.IEvidenceStore)memory,
            set.Proposals[0].Id, PromotionActor.Human);

        Assert.False(verdict.Promotable);
        Assert.Equal(PromotionRefusal.TargetUnresolvable, verdict.Refusal);
        Assert.Equal("target-resolver", verdict.Layer);
    }

    // ---- the two-repository apply: B changes wholly, A not at all -----------------------------

    /// <summary>
    /// THE ACCEPTANCE SCENARIO, END TO END: an agent edits B's worktree (modify, add, delete,
    /// rename), the capture represents all four faithfully, the whole verified set applies INTO B
    /// as one unit, and A — the configured live root — is byte-for-byte unchanged from before the
    /// mission to after promotion.
    /// </summary>
    [Fact]
    public void TheWholeSet_AppliesIntoB_AndANeverChangesByteForByte()
    {
        var live = NewGitRepo("live-a3");
        var project = NewGitRepo("project-b3",
            ("README.md", "project-b3\n"), ("gone.txt", "delete me\n"), ("old.cs", "class Old {}\n"));
        var liveBefore = TreeBytes(live);

        using var memory = new SqliteMemory(Path.Combine(_dir, "apply-b.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;

        // The agent's worktree of B, edited the way an acting coder edits.
        var worktree = Path.Combine(_dir, "wt-b3");
        Git(project, $"worktree add --detach \"{worktree}\" HEAD");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "changed by mission\n");
        File.WriteAllText(Path.Combine(worktree, "born.txt"), "new work\n");
        File.Delete(Path.Combine(worktree, "gone.txt"));
        Git(worktree, "mv old.cs renamed.cs");

        var workspace = new MissionWorkspace
        {
            Id = "ws-b3", MissionId = "m-b3", Root = worktree, SourceRoot = project,
            Mode = "worktree", BaseRevision = Git(project, "rev-parse HEAD"),
            State = WorkspaceState.Active,
        };
        memory.SaveWorkspace(workspace);

        var capture = WorkspaceChangeSet.Create(workspace, "m-b3", "t-b3", "acceptance");
        Assert.True(capture.Faithful, string.Join(" | ", capture.Problems));
        Assert.Equal(4, capture.Set.Proposals.Count);
        Assert.Contains(capture.Set.Proposals, p => p.ChangeType == PatchChangeType.Delete);
        Assert.Contains(capture.Set.Proposals, p => p.ChangeType == PatchChangeType.Rename);

        // B's ORIGINAL CHECKOUT is untouched by everything up to promotion.
        Assert.Equal("delete me\n", File.ReadAllText(Path.Combine(project, "gone.txt")));
        Assert.Equal("project-b3\n", File.ReadAllText(Path.Combine(project, "README.md")));

        var set = capture.Set.Proposals
            .Select(p => (PatchId: p.Id, Proposal: p))
            .OrderBy(x => x.PatchId, StringComparer.Ordinal).ToList();
        var outcome = PatchSetApply.ApplySet(memory, capture.Set.Id, set,
            ApplyVia(Path.GetFullPath(project), set),
            rollBack: (_, tx, _) => tx.Rollback(),
            targetRoot: Path.GetFullPath(project));

        Assert.True(outcome.Applied, outcome.Message + " | " + string.Join(" | ", outcome.Refusals));
        Assert.Equal(4, outcome.Count);

        // B carries the entire change: modify, add, delete, rename.
        Assert.Equal("changed by mission\n", File.ReadAllText(Path.Combine(project, "README.md")));
        Assert.Equal("new work\n", File.ReadAllText(Path.Combine(project, "born.txt")));
        Assert.False(File.Exists(Path.Combine(project, "gone.txt")), "the deletion was not applied");
        Assert.False(File.Exists(Path.Combine(project, "old.cs")), "the rename left its source behind");
        Assert.Equal("class Old {}\n", File.ReadAllText(Path.Combine(project, "renamed.cs")));

        // And A — the configured live root — is byte-for-byte what it was.
        Assert.Equal(liveBefore, TreeBytes(live));

        Git(project, $"worktree remove --force \"{worktree}\"");
    }

    /// <summary>A failing set applies NOTHING: one stale member refuses the whole set at preflight,
    /// and B keeps every byte it had — including the file the passing members would have changed.</summary>
    [Fact]
    public void AFailingSet_AppliesNothingToB()
    {
        var live = NewGitRepo("live-a4");
        var project = NewGitRepo("project-b4",
            ("README.md", "project-b4\n"), ("keep.txt", "keep\n"));

        using var memory = new SqliteMemory(Path.Combine(_dir, "apply-fail.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;

        var worktree = Path.Combine(_dir, "wt-b4");
        Git(project, $"worktree add --detach \"{worktree}\" HEAD");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "wanted change\n");
        File.WriteAllText(Path.Combine(worktree, "keep.txt"), "also changed\n");

        var workspace = new MissionWorkspace
        {
            Id = "ws-b4", MissionId = "m-b4", Root = worktree, SourceRoot = project,
            Mode = "worktree", BaseRevision = Git(project, "rev-parse HEAD"),
            State = WorkspaceState.Active,
        };
        var capture = WorkspaceChangeSet.Create(workspace, "m-b4", "t-b4", "stale");
        Assert.True(capture.Faithful, string.Join(" | ", capture.Problems));

        // B moves on underneath ONE member — its base is now stale.
        File.WriteAllText(Path.Combine(project, "README.md"), "the operator edited this\n");

        var treeBefore = TreeBytes(project);
        var set = capture.Set.Proposals.Select(p => (PatchId: p.Id, Proposal: p)).ToList();
        var outcome = PatchSetApply.ApplySet(memory, capture.Set.Id, set,
            ApplyVia(Path.GetFullPath(project), set),
            rollBack: (_, tx, _) => tx.Rollback(),
            targetRoot: Path.GetFullPath(project));

        Assert.False(outcome.Applied);
        Assert.Equal(0, outcome.Count);
        Assert.Equal(treeBefore, TreeBytes(project));

        Git(project, $"worktree remove --force \"{worktree}\"");
    }

    // ---- the writable CLI without a worktree never starts -------------------------------------

    /// <summary>
    /// Item 4's acceptance: a rejected/missing mission worktree PREVENTS the writable agent CLI
    /// launching — refused by the worktree gate before any process starts, working directory or
    /// not. The refusal is typed ConfigError and names the gate.
    /// </summary>
    [Fact]
    public void AWritableAgentCli_WithTheWorktreeMissingFlag_NeverStarts()
    {
        var claude = AgentCliCatalog.ById("agent:claude-code")!;
        var provider = new AgentCliProvider(claude, TimeSpan.FromSeconds(5),
            workingDirectory: _dir);   // a perfectly valid directory — the flag must still refuse

        using (AgentAccessScope.Enter("bypass", Array.Empty<string>(), confinedWorkspace: true,
                   workingDirectory: _dir, roleMayWrite: true, missionWorktreeMissing: true))
        {
            var response = provider.Send(new ModelRequest
            {
                Messages = new[] { new ModelMessage(ModelMessage.User, "edit something") },
            });

            Assert.Equal(ModelCallOutcome.ConfigError, response.Status);
            Assert.Contains("worktree gate", response.Content, StringComparison.Ordinal);
            Assert.Contains("never run in the live project", response.Content, StringComparison.Ordinal);
        }
    }

    /// <summary>And the mission lane SETS that flag: EnterAgentAccess computes it from the role's
    /// write capability and the absent worktree. Source-position asserted, because the property is
    /// the wiring between two files.</summary>
    [Fact]
    public void TheMissionLane_SetsTheWorktreeMissingFlag_ForWritableRolesWithoutAWorktree()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));
        var method = source.IndexOf("private IDisposable EnterAgentAccess", StringComparison.Ordinal);
        Assert.True(method >= 0, "EnterAgentAccess is no longer recognisable");
        var body = source[method..source.IndexOf("return Anthill.SDK.Reasoning.AgentAccessScope.Enter", method, StringComparison.Ordinal)];

        Assert.Contains("roleMayWrite && missionWorktree is null", body, StringComparison.Ordinal);

        // And the acting coder fails closed by name rather than falling through to propose-only.
        var acting = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));
        Assert.Contains("refused by the worktree gate", acting, StringComparison.OrdinalIgnoreCase);
    }

    // ---- iteration commands: repository-declared, tester stays authoritative ------------------

    [Fact]
    public void RepositoryDeclaredCheckStems_ReachTheAskCell_AsBoundedBashGrants()
    {
        var claude = AgentCliCatalog.ById("agent:claude-code")!;
        var access = new AgentAccessScope.Context("ask", Array.Empty<string>(),
            ConfinedWorkspace: true, WorkingDirectory: "/tmp/worktree", RoleMayWrite: true,
            CheckCommandStems: new[] { "dotnet" });

        // ToList() because BuildAccessArgs returns IReadOnlyList, which has no IndexOf — the
        // flag's VALUE is the assertion, and reading it positionally is how this test proves the
        // stem reached the flag rather than merely appearing somewhere in the vector.
        var args = AgentCliCatalog.BuildAccessArgs(claude, access).ToList();
        Assert.Contains("--allowedTools", args);
        Assert.Contains("Bash(dotnet:*)", args[args.IndexOf("--allowedTools") + 1]);

        // The settings channel mirrors the argv channel — one policy, two transports.
        var json = AgentCliCatalog.BuildLocalSettingsJson(access);
        Assert.NotNull(json);
        Assert.Contains("Bash(dotnet:*)", json!, StringComparison.Ordinal);

        // No stems, no grants: the pre-.97 ask cell is unchanged.
        var without = AgentCliCatalog.BuildAccessArgs(claude, access with { CheckCommandStems = null });
        Assert.DoesNotContain("--allowedTools", without);
    }

    /// <summary>
    /// The rule text and the mechanism state the same boundary: iteration allowed, evidence not
    /// delegated — ANTHILL's tester re-runs the checks and remains authoritative.
    ///
    /// Asserted on the ASSEMBLED constant, not on Ants.cs's text. The first cut read the source and
    /// failed while the rule was correct: "…tester re-runs the / declared checks independently…"
    /// spans two C# literals, so the sentence the model actually receives appears nowhere in the
    /// file. A source search is the wrong subject twice over — it fails on a re-wrap that changes
    /// nothing, and it would pass on a deleted rule that happened to stay on one line. The value
    /// the model is handed is what this guard is about, so the value is what it reads.
    /// </summary>
    [Fact]
    public void TheActingRules_AllowIteration_AndKeepTheTesterAuthoritative()
    {
        var rules = Anthill.Core.Agents.CoderAnt.ActingCoderRules;

        Assert.Contains("MAY run the repository's own declared build/test commands", rules, StringComparison.Ordinal);
        Assert.Contains("re-runs the declared checks independently", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not run builds, tests, or git", rules, StringComparison.Ordinal);

        // And the worktree framing the iteration grant sits inside is intact: the agent is told the
        // tree is disposable and that promotion is ANTHILL's, which is what makes a build command
        // inside it safe to grant at all.
        Assert.Contains("ISOLATED git worktree", rules, StringComparison.Ordinal);
    }

    // ---- promotion sequencing and per-path atomicity, pinned at source ------------------------

    /// <summary>Item 2: the Apply button routes a multi-file set through the one transaction.</summary>
    [Fact]
    public void TheApplyButton_RoutesMultiFileSets_ThroughTheTransactionalLane()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.Views.cs")));

        var typed = source.IndexOf("public PatchApplyResult ApplyApprovedPatchTyped", StringComparison.Ordinal);
        Assert.True(typed >= 0, "ApplyApprovedPatchTyped is no longer recognisable");
        var body = SourceText.MemberBody(source, typed);
        Assert.Contains("PatchSetApply.LoadSet", body, StringComparison.Ordinal);
        Assert.Contains("ApplyApprovedSetAsAUnit", body, StringComparison.Ordinal);

        var setLane = source.IndexOf("private PatchApplyResult ApplyApprovedSetAsAUnit", StringComparison.Ordinal);
        var laneBody = SourceText.MemberBody(source, setLane);
        // Every member faces the gate as Human BEFORE the transaction runs.
        var gate = laneBody.IndexOf("PatchPromotionGate.Evaluate", StringComparison.Ordinal);
        var apply = laneBody.IndexOf("ApplyPatchSetTransactionally", StringComparison.Ordinal);
        Assert.True(gate >= 0 && apply > gate,
            "the set lane must gate every member as Human before the transactional apply");
    }

    /// <summary>Item 3: bypass application moved AFTER review completion — ProcessPatchSet defers
    /// when reviews were inserted, and the completion hook re-attempts only when every review for
    /// the set is complete.</summary>
    [Fact]
    public void BypassApplication_WaitsForTheReviews_ItUsedToRaceAheadOf()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var process = source.IndexOf("internal void ProcessPatchSet", StringComparison.Ordinal);
        Assert.True(process >= 0, "ProcessPatchSet is no longer recognisable");
        var body = SourceText.MemberBody(source, process);

        // The immediate call is CONDITIONAL on no reviews having been inserted.
        Assert.Contains("reviewsPending", body, StringComparison.Ordinal);
        Assert.Contains("patch_bypass_deferred", body, StringComparison.Ordinal);

        // The completion hook exists, is wired to tester/soldier completion, and checks that ALL
        // reviews are complete before the attempt.
        Assert.Contains("MaybeApplyBypassAfterReviews(mission, task)", source, StringComparison.Ordinal);
        var hook = source.IndexOf("private void MaybeApplyBypassAfterReviews", StringComparison.Ordinal);
        Assert.True(hook >= 0, "MaybeApplyBypassAfterReviews is missing");
        var hookBody = SourceText.MemberBody(source, hook);
        var allComplete = hookBody.IndexOf("reviews.Any(t => t.Status != TaskStatus.Complete)", StringComparison.Ordinal);
        var attempt = hookBody.IndexOf("ApplyUnderBypass(mission, producer, patchSet)", StringComparison.Ordinal);
        Assert.True(allComplete >= 0 && attempt > allComplete,
            "the deferred attempt must verify every review completed before applying");
    }

    /// <summary>Item 1's verification leg: the freshness fingerprint is captured from the
    /// mission's own source tree and compared against the resolved target — never the configured
    /// live root on either side.</summary>
    [Fact]
    public void TheFreshnessFingerprint_IsCapturedAndCompared_AgainstTheTargetTree()
    {
        var execution = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));
        Assert.Contains("WorkspaceFingerprint.Capture(materializationSource)", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceFingerprint.Capture(AnthillRuntime.AllowedWorkspaceRoot)", execution, StringComparison.Ordinal);

        var gate = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Verification", "PatchPromotionGate.cs")));
        Assert.Contains("WorkspaceFingerprint.Compare(recorded, target.Root)", gate, StringComparison.Ordinal);
        Assert.Contains("HasRollbackFailure(target.Root)", gate, StringComparison.Ordinal);
        Assert.Contains("PatchTargetResolver.Resolve", gate, StringComparison.Ordinal);
    }

    // ---- the mission report carries the execution outcome (item 7) ----------------------------

    [Fact]
    public void TheMissionReport_NamesFilesApprovalApplicationAndTarget()
    {
        var live = NewGitRepo("live-a5");
        var project = NewGitRepo("project-b5");
        using var memory = new SqliteMemory(Path.Combine(_dir, "report.db"));
        AnthillRuntime.AllowedWorkspaceRoot = live;

        var (mission, task) = SeedMissionAndTask(memory, "report the outcome");
        memory.SaveWorkspace(new MissionWorkspace
        {
            Id = "ws-b5", MissionId = mission.Id, Root = project, SourceRoot = project,
            Mode = "worktree", State = WorkspaceState.Active,
        });

        var applied = NewProposal("README.md", "project-b5\n", "changed\n");
        applied.Status = PatchStatus.Applied;
        var pending = NewProposal("docs/new.md", null, "born\n");
        var set = SeedPatchSet(memory, mission, task, "ws-b5", applied, pending);
        memory.SaveApprovalRequest(new ApprovalRequest
        {
            MissionId = mission.Id, TaskId = task.Id,
            ActionType = ApprovalActionType.PatchProposal, TargetId = pending.Id,
            Title = "approve", Description = "approve the pending file",
        });

        var report = Anthill.Core.Outcomes.MissionReport.Compile(memory, mission.Id);

        var line = Assert.Single(report.PatchSets);
        Assert.Equal(set.Id, line.Id);
        Assert.Equal("ws-b5", line.WorkspaceId);
        Assert.Equal(Path.GetFullPath(project), line.TargetRoot);
        Assert.Equal(2, line.Files.Count);
        Assert.Contains(line.Files, f => f.Path == "README.md" && f.Status == "applied");
        Assert.Contains(line.Files, f => f.Path == "docs/new.md" && f.ApprovalState == "pending");
        Assert.Contains("1 of 2", line.ApplicationState);

        var rendered = Anthill.Core.Outcomes.MissionReport.Render(report);
        Assert.Contains($"patch set {set.Id}:", rendered, StringComparison.Ordinal);
        Assert.Contains("target:", rendered, StringComparison.Ordinal);
        Assert.Contains("approval:", rendered, StringComparison.Ordinal);
    }

    // ---- seeding helpers ----------------------------------------------------------------------

    private static (Mission Mission, Task Task) SeedMissionAndTask(SqliteMemory memory, string goal)
    {
        var mission = new Mission { Goal = goal };
        var task = new Task
        {
            Title = "coder task", Description = "produce the change",
            AssignedAnt = "coder", TaskType = "code_change",
        };
        mission.Tasks.Add(task);
        memory.SaveMission(mission);
        memory.SaveTask(mission.Id, task);
        return (mission, task);
    }

    private static PatchProposal NewProposal(string path, string? oldContent, string newContent) => new()
    {
        FilePath = path,
        ChangeType = oldContent is null ? PatchChangeType.Add : PatchChangeType.Modify,
        OldContent = oldContent,
        NewContent = newContent,
        BaseHash = PatchApply.HashOf(oldContent),
        Reason = "test", Risk = "low",
    };

    private static PatchSet SeedPatchSet(SqliteMemory memory, Mission mission, Task task,
        string? workspaceId, params PatchProposal[] proposals)
    {
        var set = new PatchSet
        {
            MissionId = mission.Id, TaskId = task.Id, WorkspaceId = workspaceId,
            Summary = "seeded",
        };
        set.Proposals.AddRange(proposals);
        memory.SavePatchSet(set);
        return set;
    }
}
