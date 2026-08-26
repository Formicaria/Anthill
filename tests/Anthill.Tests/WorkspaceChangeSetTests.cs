using System.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.5.0 — a mission's workspace changes, turned into the change set the operator already reviews.
///
/// This closes the loop the phase opened. Isolating an agent in a worktree it cannot escape is safe
/// and, alone, useless: the work has to reach the operator, and the only sanctioned route into the
/// live checkout is the patch/approval pipeline that already exists. So the diff becomes ordinary
/// <see cref="PatchProposal"/>s — no second review path.
///
/// The property most worth testing is the least visible one: OldContent comes from the BASE
/// REVISION, not from the live checkout. apply_patch does an exact-match replacement, so old content
/// taken from a checkout that has moved on either fails to match (survivable) or matches the wrong
/// occurrence in a file someone else edited (not).
/// </summary>
public class WorkspaceChangeSetTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;
    private readonly string _worktree;

    public WorkspaceChangeSetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-cs-" + Guid.NewGuid().ToString("N")[..10]);
        _repo = Path.Combine(_dir, "repo");
        _worktree = Path.Combine(_dir, "worktree");
        Directory.CreateDirectory(_repo);

        Git(_repo, "init -b main");
        Git(_repo, "config user.email test@anthill.local");
        Git(_repo, "config user.name Test");
        File.WriteAllText(Path.Combine(_repo, "Existing.cs"), "original\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -m first");
        Git(_repo, $"worktree add --detach \"{_worktree}\" HEAD");
    }

    public void Dispose()
    {
        try { Git(_repo, $"worktree remove --force \"{_worktree}\""); } catch { }
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

    private MissionWorkspace Workspace() => new()
    {
        Id = "ws1", MissionId = "m1", Root = _worktree, SourceRoot = _repo,
        State = WorkspaceState.Active, Mode = "worktree",
        BaseRevision = Git(_repo, "rev-parse HEAD"),
    };

    private PatchSet Harvest() => WorkspaceChangeSet.Create(Workspace(), "m1", "t1", "summary");

    // ---- what a change set contains ---------------------------------------------------------

    [Fact]
    public void AModifiedFile_BecomesAModifyProposal()
    {
        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "changed\n");

        var proposal = Assert.Single(Harvest().Proposals);

        Assert.Equal("Existing.cs", proposal.FilePath);
        Assert.Equal(PatchChangeType.Modify, proposal.ChangeType);
        Assert.Equal("changed\n", proposal.NewContent);
    }

    /// <summary>
    /// The detail that matters. OldContent must be the text at the BASE REVISION — the only text
    /// this change was actually derived from, and the only anchor an exact-match replacement can
    /// safely use.
    /// </summary>
    [Fact]
    public void OldContent_ComesFromTheBaseRevision_NotTheLiveCheckout()
    {
        // The workspace is captured FIRST, pinning its base revision — as Prepare() does in
        // production. The first version of this test built the workspace lazily inside the harvest,
        // AFTER the commit below, so it pinned the new revision and read back "SOMEONE ELSE EDITED
        // THIS". It reproduced, in the test, the exact hazard the production code exists to prevent:
        // reading the revision too late. Worth leaving recorded here.
        var workspace = Workspace();

        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "changed\n");

        // meanwhile the operator edits and commits the same file in the live checkout
        File.WriteAllText(Path.Combine(_repo, "Existing.cs"), "SOMEONE ELSE EDITED THIS\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -m unrelated");

        var proposal = Assert.Single(
            WorkspaceChangeSet.Create(workspace, "m1", "t1", "summary").Proposals);

        Assert.Equal("original\n", proposal.OldContent);
        Assert.DoesNotContain("SOMEONE ELSE", proposal.OldContent);
    }

    /// <summary>
    /// A file the mission created is untracked, so it appears in NO diff against the base and would
    /// be silently dropped — the mission's most visible work, missing from its change set.
    /// </summary>
    [Fact]
    public void ANewUntrackedFile_IsProposedAsAnAdd()
    {
        File.WriteAllText(Path.Combine(_worktree, "Brand.cs"), "new file\n");

        var proposal = Assert.Single(Harvest().Proposals);

        Assert.Equal("Brand.cs", proposal.FilePath);
        Assert.Equal(PatchChangeType.Add, proposal.ChangeType);
        Assert.Null(proposal.OldContent);
    }

    /// <summary>
    /// Deletions are skipped. ApplyPatchTool supports add and modify only, so a delete proposal
    /// would produce a change set that cannot be applied — a review ending in a failure the reviewer
    /// could not have predicted.
    /// </summary>
    [Fact]
    public void ADeletion_IsNotProposed_BecauseItCouldNotBeApplied()
    {
        File.Delete(Path.Combine(_worktree, "Existing.cs"));

        Assert.Empty(Harvest().Proposals);
    }

    /// <summary>
    /// Every proposal requires approval. A workspace exists so an agent's work is reviewed before it
    /// reaches the live checkout; a change set that could apply itself makes the isolation pointless.
    /// </summary>
    [Fact]
    public void EveryProposal_RequiresApproval()
    {
        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "changed\n");
        File.WriteAllText(Path.Combine(_worktree, "Brand.cs"), "new\n");

        var set = Harvest();

        Assert.Equal(2, set.Proposals.Count);
        Assert.All(set.Proposals, p => Assert.True(p.RequiresApproval));
    }

    /// <summary>The proposal says which workspace and revision it came from — attribution, in the artifact.</summary>
    [Fact]
    public void AProposal_NamesItsWorkspaceAndBaseRevision()
    {
        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "changed\n");

        var proposal = Assert.Single(Harvest().Proposals);

        Assert.Contains("ws1", proposal.Reason);
        Assert.Contains(Git(_repo, "rev-parse HEAD")[..12], proposal.Reason);
    }

    /// <summary>
    /// A mission that changed nothing produces an EMPTY set, not an error. "It ran and changed
    /// nothing" is a real outcome, and collapsing it into a failure would make a legitimately no-op
    /// mission look broken.
    /// </summary>
    [Fact]
    public void AMissionThatChangedNothing_ProducesAnEmptySet_NotAFailure()
    {
        var set = Harvest();

        Assert.Empty(set.Proposals);
        Assert.Equal("m1", set.MissionId);
    }

    /// <summary>An unusable workspace yields nothing rather than diffing something else.</summary>
    [Fact]
    public void AnUnusableWorkspace_YieldsNothing()
    {
        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "changed\n");

        var set = WorkspaceChangeSet.Create(
            Workspace() with { State = WorkspaceState.Cleaned }, "m1", "t1", "s");

        Assert.Empty(set.Proposals);
    }

    /// <summary>
    /// v0.3.8.93, acceptance gate C — THE ORIGINAL IS BYTE-IDENTICAL UNTIL APPLY. An agent writing
    /// in its worktree — edits, new files, everything short of apply — changes NOTHING in the
    /// source checkout: same bytes in every tracked file, clean status, same HEAD. Harvesting the
    /// change set is a read and must not move anything either. The only sanctioned route into the
    /// source tree remains the patch/approval pipeline, and this is the test that says the
    /// isolation half of that sentence is literally true, not approximately.
    /// </summary>
    [Fact]
    public void TheSourceCheckout_IsByteIdentical_UntilApply()
    {
        var sourceFile = Path.Combine(_repo, "Existing.cs");
        var bytesBefore = File.ReadAllBytes(sourceFile);
        var headBefore = Git(_repo, "rev-parse HEAD");

        // The agent works: an edit and a new file, in the worktree only.
        File.WriteAllText(Path.Combine(_worktree, "Existing.cs"), "rewritten by the agent\n");
        File.WriteAllText(Path.Combine(_worktree, "Created.cs"), "new work\n");

        // Harvest is a read.
        var set = Harvest();
        Assert.Equal(2, set.Proposals.Count);

        Assert.Equal(bytesBefore, File.ReadAllBytes(sourceFile));
        Assert.False(File.Exists(Path.Combine(_repo, "Created.cs")));
        Assert.Equal(headBefore, Git(_repo, "rev-parse HEAD"));
        Assert.Equal("", Git(_repo, "status --porcelain"));
    }
}
