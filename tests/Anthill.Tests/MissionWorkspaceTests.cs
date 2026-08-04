using System.Diagnostics;
using Anthill.Core.Memory;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.5.0 — mission workspaces: created from a real git checkout, tracked through a lifecycle, and
/// recoverable after a restart.
///
/// These tests build an ACTUAL git repository in a temp directory and take real worktrees from it.
/// A faked git would test the manager's opinion of git rather than git, and every property under
/// test here — that a worktree does not move the source branch, that a base revision is the commit
/// that was actually HEAD, that removal prunes the worktree registration — is a property of git's
/// behaviour, not of the code that calls it.
///
/// The exit gates being proven, in the roadmap's words:
///   - a code mission cannot modify the active checkout through any agent path
///   - every change is attributable to one workspace and base revision
///   - workspace recovery after restart is tested
///   - cleanup cannot delete an operator-retained workspace
/// </summary>
public class MissionWorkspaceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;
    private readonly SqliteMemory _memory;
    private readonly MissionWorkspaceManager _manager;

    public MissionWorkspaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-ws-test-" + Guid.NewGuid().ToString("N")[..10]);
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);

        Git(_repo, "init -b main");
        Git(_repo, "config user.email test@anthill.local");
        Git(_repo, "config user.name Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "one\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -m first");

        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
        _manager = new MissionWorkspaceManager(_memory, _repo);
    }

    public void Dispose()
    {
        foreach (var w in _memory.LoadWorkspaces())
        {
            try { Git(_repo, $"worktree remove --force \"{w.Root}\""); } catch { }
        }
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
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

    /// <summary>
    /// Compare paths without arguing about separators.
    ///
    /// Windows CI found this: git reports worktree paths with FORWARD slashes even on Windows, while
    /// .NET builds them with backslashes, so a substring comparison of two references to the same
    /// directory failed. Production is unaffected — it hands paths TO git and never parses
    /// `worktree list` — so the defect is in the assertion, not the manager. Normalising both sides
    /// is the fix; asserting on a platform-specific spelling would just move the failure.
    /// </summary>
    private static string SameSlashes(string path) => path.Replace('\\', '/');

    // ---- attribution ------------------------------------------------------------------------

    /// <summary>
    /// The exit gate: every change is attributable to one workspace and one base revision. The
    /// revision recorded must be the commit that was ACTUALLY HEAD when the workspace was made —
    /// not one read back later, which would answer a different question after any commit.
    /// </summary>
    [Fact]
    public void APreparedWorkspace_RecordsTheCommitItWasBasedOn()
    {
        var head = Git(_repo, "rev-parse HEAD");

        var workspace = _manager.Prepare("mission-1");

        Assert.Equal(WorkspaceState.Ready, workspace.State);
        Assert.Equal(head, workspace.BaseRevision);
        Assert.Equal("worktree", workspace.Mode);
        Assert.True(Directory.Exists(workspace.Root));
    }

    /// <summary>
    /// The base revision is FIXED at creation. Committing in the source afterwards must not change
    /// what an existing workspace says it was based on — the entire value of the field is that it
    /// does not move.
    /// </summary>
    [Fact]
    public void TheBaseRevision_DoesNotMoveWhenTheSourceMovesOn()
    {
        var workspace = _manager.Prepare("mission-1");
        var recordedBase = workspace.BaseRevision;

        File.WriteAllText(Path.Combine(_repo, "second.txt"), "two\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -m second");

        var reloaded = _manager.Get(workspace.Id)!;
        Assert.Equal(recordedBase, reloaded.BaseRevision);
        Assert.NotEqual(Git(_repo, "rev-parse HEAD"), reloaded.BaseRevision);
    }

    /// <summary>
    /// The fingerprint identifies the REPOSITORY rather than the checkout, so two workspaces from
    /// the same history agree — chosen as the root commit because remotes get renamed, forks share
    /// URLs, and a path says only where a directory sits today.
    /// </summary>
    [Fact]
    public void TwoWorkspacesFromTheSameRepository_ShareAFingerprint()
    {
        var a = _manager.Prepare("mission-1");
        var b = _manager.Prepare("mission-2");

        Assert.NotEqual("", a.RepositoryFingerprint);
        Assert.Equal(a.RepositoryFingerprint, b.RepositoryFingerprint);
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Root, b.Root);
    }

    /// <summary>
    /// A source with no git history is REFUSED rather than copied. SandboxWorkspace copies because
    /// patch verification must see uncommitted state; a mission workspace must be attributable, and
    /// a copy of an unversioned directory has no revision to record. A workspace whose provenance
    /// is a fiction is worse than no workspace.
    /// </summary>
    [Fact]
    public void ASourceThatIsNotAGitCheckout_IsRejected_NotSilentlyCopied()
    {
        var plain = Path.Combine(_dir, "not-a-repo");
        Directory.CreateDirectory(plain);
        var manager = new MissionWorkspaceManager(_memory, plain);

        var workspace = manager.Prepare("mission-1");

        Assert.Equal(WorkspaceState.Rejected, workspace.State);
        Assert.Contains("base revision", workspace.Note ?? "");
    }

    // ---- the live checkout is not reachable ---------------------------------------------------

    /// <summary>
    /// The headline gate: a code mission cannot modify the active checkout. Two specific ways a
    /// worktree could violate that are checked — writing into the source tree, and moving the
    /// source's branch pointer (which is why the worktree is created DETACHED).
    /// </summary>
    [Fact]
    public void WorkInAWorkspace_DoesNotTouchTheSourceCheckout()
    {
        var branchBefore = Git(_repo, "rev-parse --abbrev-ref HEAD");
        var headBefore = Git(_repo, "rev-parse HEAD");

        var workspace = _manager.Prepare("mission-1");
        File.WriteAllText(Path.Combine(workspace.Root, "agent-wrote-this.txt"), "changed\n");

        Assert.False(File.Exists(Path.Combine(_repo, "agent-wrote-this.txt")));
        Assert.Equal(branchBefore, Git(_repo, "rev-parse --abbrev-ref HEAD"));
        Assert.Equal(headBefore, Git(_repo, "rev-parse HEAD"));
        Assert.Equal("", Git(_repo, "status --porcelain"));
    }

    /// <summary>And the change IS visible inside the workspace, attributed to it.</summary>
    [Fact]
    public void AChangeInsideTheWorkspace_IsVisibleAsThatWorkspacesChange()
    {
        var workspace = _manager.Prepare("mission-1");
        File.WriteAllText(Path.Combine(workspace.Root, "agent-wrote-this.txt"), "changed\n");

        Assert.Contains("agent-wrote-this.txt", _manager.ChangeSummary(workspace));
    }

    // ---- retention beats cleanup ---------------------------------------------------------------

    /// <summary>
    /// The gate that protects an operator mid-investigation. Retention is usually declared because
    /// something already went wrong, and a sweep that removes the evidence is the worst possible
    /// moment to be efficient.
    /// </summary>
    [Fact]
    public void CleanupCannotDelete_AnOperatorRetainedWorkspace()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Retain(workspace.Id, "zwright", "investigating a failed patch");

        // even asked directly, and even with a sweep afterwards
        Assert.Null(_manager.RequestCleanup(workspace.Id));
        Assert.Empty(_manager.Cleanup());

        Assert.True(Directory.Exists(workspace.Root));
        Assert.Equal(WorkspaceState.Retained, _manager.Get(workspace.Id)!.State);
    }

    /// <summary>
    /// Retention without a stated reason is refused. A "keep this" flag with no reason becomes
    /// permanent clutter nobody dares delete — the failure mode of every such flag.
    /// </summary>
    [Theory]
    [InlineData("", "a reason")]
    [InlineData("zwright", "")]
    public void RetentionRequiresBothWhoAndWhy(string who, string why)
    {
        var workspace = _manager.Prepare("mission-1");
        Assert.Null(_manager.Retain(workspace.Id, who, why));
        Assert.NotEqual(WorkspaceState.Retained, _manager.Get(workspace.Id)!.State);
    }

    /// <summary>Releasing is the one way out, and it is explicit rather than a side effect.</summary>
    [Fact]
    public void AReleasedWorkspace_BecomesReclaimable()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Retain(workspace.Id, "zwright", "looking at it");
        _manager.Release(workspace.Id);

        Assert.Equal(new[] { workspace.Id }, _manager.Cleanup());
        Assert.False(Directory.Exists(workspace.Root));
    }

    /// <summary>
    /// The row OUTLIVES the directory. "What was this change based on" is asked long after the
    /// files are gone, and a cleanup that erased the record would make the attribution this table
    /// exists for expire.
    /// </summary>
    [Fact]
    public void CleaningRemovesTheDirectory_ButKeepsTheAttribution()
    {
        var workspace = _manager.Prepare("mission-1");
        var baseRevision = workspace.BaseRevision;
        _manager.RequestCleanup(workspace.Id);
        _manager.Cleanup();

        var row = _manager.Get(workspace.Id)!;
        Assert.Equal(WorkspaceState.Cleaned, row.State);
        Assert.Equal(baseRevision, row.BaseRevision);
        Assert.False(Directory.Exists(workspace.Root));
    }

    /// <summary>Cleanup also prunes the worktree registration, so git does not accrue dead entries.</summary>
    [Fact]
    public void CleanupPrunesTheWorktreeRegistration()
    {
        var workspace = _manager.Prepare("mission-1");
        Assert.Contains(SameSlashes(workspace.Root), SameSlashes(Git(_repo, "worktree list")));

        _manager.RequestCleanup(workspace.Id);
        _manager.Cleanup();

        Assert.DoesNotContain(SameSlashes(workspace.Root), SameSlashes(Git(_repo, "worktree list")));
    }

    // ---- recovery after restart ------------------------------------------------------------------

    /// <summary>
    /// The restart gate, and the ugly case it exists for: a process that died mid-mission leaves a
    /// row claiming Active. The work survived, so it becomes Checkpointed — leaving it Active would
    /// claim an agent is running in it, and something would eventually wait for that agent.
    /// </summary>
    [Fact]
    public void AWorkspaceActiveAtRestart_IsCheckpointed_NotLeftClaimingAnAgentIsInIt()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Activate(workspace.Id);

        var notes = _manager.Recover();

        Assert.Equal(WorkspaceState.Checkpointed, _manager.Get(workspace.Id)!.State);
        Assert.Contains(notes, n => n.Contains(workspace.Id));
    }

    /// <summary>
    /// Recorded as live, directory gone. Distinguished from Cleaned on purpose: "we removed it" and
    /// "it vanished under us" call for different operator responses, and collapsing them hides the
    /// second entirely.
    /// </summary>
    [Fact]
    public void AWorkspaceWhoseDirectoryVanished_IsOrphaned_NotRecordedAsCleaned()
    {
        var workspace = _manager.Prepare("mission-1");
        Git(_repo, $"worktree remove --force \"{workspace.Root}\"");

        _manager.Recover();

        var row = _manager.Get(workspace.Id)!;
        Assert.Equal(WorkspaceState.Orphaned, row.State);
        Assert.NotEqual(WorkspaceState.Cleaned, row.State);
    }

    /// <summary>
    /// A workspace interrupted mid-preparation has no recorded base revision, so nothing made in it
    /// could ever be attributed. It is rejected and its directory removed rather than handed to an
    /// agent as if it were usable.
    /// </summary>
    [Fact]
    public void AnInterruptedPreparation_IsRejected_NotHandedToAnAgent()
    {
        var workspace = _manager.Prepare("mission-1");
        _memory.SaveWorkspace(workspace with { State = WorkspaceState.Preparing });

        _manager.Recover();

        var row = _manager.Get(workspace.Id)!;
        Assert.Equal(WorkspaceState.Rejected, row.State);
        Assert.False(row.Usable);
        Assert.False(Directory.Exists(workspace.Root));
    }

    /// <summary>Recovery must not disturb a retained workspace — that is the point of retaining it.</summary>
    [Fact]
    public void RecoveryLeavesARetainedWorkspaceAlone()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Retain(workspace.Id, "zwright", "keep for review");

        _manager.Recover();

        Assert.Equal(WorkspaceState.Retained, _manager.Get(workspace.Id)!.State);
        Assert.True(Directory.Exists(workspace.Root));
    }

    // ---- persistence -----------------------------------------------------------------------------

    /// <summary>
    /// The manifest survives the process. Without this the whole record is an in-memory object with
    /// extra steps, and neither exit gate above can be met.
    /// </summary>
    [Fact]
    public void TheManifestSurvivesAReopenedDatabase()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Activate(workspace.Id);

        using var reopened = new SqliteMemory(Path.Combine(_dir, "memory.db"));
        var row = reopened.LoadWorkspace(workspace.Id)!;

        Assert.Equal(WorkspaceState.Active, row.State);
        Assert.Equal(workspace.BaseRevision, row.BaseRevision);
        Assert.Equal(workspace.RepositoryFingerprint, row.RepositoryFingerprint);
        Assert.Equal(workspace.Root, row.Root);
    }

    /// <summary>
    /// State is stored by NAME. An enum's ordinal is an implementation detail that reorders the
    /// moment someone inserts a state in the middle, and a database of integers that silently mean
    /// something else cannot be recovered from.
    /// </summary>
    [Fact]
    public void StateIsStoredByName_NotByOrdinal()
    {
        var workspace = _manager.Prepare("mission-1");
        _manager.Retain(workspace.Id, "zwright", "why not");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dir, "memory.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM mission_workspaces WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", workspace.Id);

        Assert.Equal("Retained", cmd.ExecuteScalar()?.ToString());
    }

    /// <summary>
    /// An unreadable state reads as Orphaned, never as Ready. Fail closed: mislabelling a healthy
    /// workspace costs an operator note; the reverse dispatches an agent into a directory nothing
    /// can vouch for.
    /// </summary>
    [Fact]
    public void AnUnreadableState_FailsClosed()
    {
        var workspace = _manager.Prepare("mission-1");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dir, "memory.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mission_workspaces SET state='SomethingNobodyDefined' WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", workspace.Id);
        cmd.ExecuteNonQuery();

        var row = _manager.Get(workspace.Id)!;
        Assert.Equal(WorkspaceState.Orphaned, row.State);
        Assert.False(row.Usable);
    }

    // ---- which checkout workspaces are taken from -------------------------------------------------

    /// <summary>
    /// The wiring bug this closes, found in the browser against a live config rather than by a test.
    ///
    /// The manager was handed AllowedWorkspaceRoot — the agent file-tool SANDBOX, which is
    /// `.anthill/workspace` in a real deployment, not the repository a code mission modifies. Two
    /// different concepts that are both called "workspace". The sandbox is never a git checkout, so
    /// every Prepare would have been rejected and the feature would have silently done nothing but
    /// log workspace_unavailable — on a machine where it looked installed and configured.
    /// </summary>
    [Fact]
    public void ASandboxInsideARepository_ResolvesToThatRepository()
    {
        var sandbox = Path.Combine(_repo, ".anthill", "workspace");
        Directory.CreateDirectory(sandbox);

        var manager = new MissionWorkspaceManager(_memory, sandbox);

        Assert.Equal(Path.GetFullPath(_repo), manager.SourceRoot);

        // and it actually works from there — the point of the fix
        var workspace = manager.Prepare("mission-1");
        Assert.Equal(WorkspaceState.Ready, workspace.State);
        Assert.Equal(Git(_repo, "rev-parse HEAD"), workspace.BaseRevision);
    }

    /// <summary>
    /// With no enclosing checkout anywhere, the original path is kept so the rejection stays honest:
    /// "not a git checkout" is then true and says so, rather than silently pointing somewhere else.
    /// </summary>
    [Fact]
    public void WithNoEnclosingCheckout_ThePathIsKept_SoTheRejectionIsHonest()
    {
        var orphan = Path.Combine(Path.GetTempPath(), "anthill-no-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(orphan);
        try
        {
            var resolved = MissionWorkspaceManager.ResolveRepositoryRoot(orphan);
            Assert.Equal(Path.GetFullPath(orphan), resolved);
        }
        finally { try { Directory.Delete(orphan, true); } catch { } }
    }

    [Fact]
    public void WorkspacesAreListedPerMission()
    {
        _manager.Prepare("mission-1");
        _manager.Prepare("mission-1");
        _manager.Prepare("mission-2");

        Assert.Equal(2, _memory.LoadWorkspacesForMission("mission-1").Count);
        Assert.Single(_memory.LoadWorkspacesForMission("mission-2"));
    }
}
