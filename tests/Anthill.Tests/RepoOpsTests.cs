using Anthill.Core.Projects;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.51 third round — git awareness. RepoOps is the one place Anthill asks a directory
/// what it is and the one place anything commits through, so its honesty is load-bearing:
/// a plain folder must be reported as a plain folder (not an error), a repo must report its
/// branch and dirty state truthfully, and Commit must stage only what it was told to.
///
/// Every test degrades to a no-op on a machine without git — git's absence is a fact RepoOps
/// is required to survive, so the tests must survive it too.
/// </summary>
public class RepoOpsTests : IDisposable
{
    private readonly string _root;

    public RepoOpsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anthill-repoops-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static bool GitAvailable => RepoOps.Git(Path.GetTempPath(), "--version").Ok;

    [Fact]
    public void PlainFolder_IsReportedAsPlainFolder_NotAsAnError()
    {
        var state = RepoOps.Describe(_root);
        Assert.False(state.IsRepo);
        Assert.Equal(0, state.DirtyCount);
        Assert.Null(state.Branch);
        // A folder simply not being a repo is NOT an error condition (git absence is noted, but
        // never invented for a folder that plainly exists).
        if (GitAvailable) Assert.Null(state.Error);
        Assert.Null(RepoOps.TopLevel(_root));
    }

    [Fact]
    public void MissingDirectory_DegradesToNotARepo()
    {
        var state = RepoOps.Describe(Path.Combine(_root, "does-not-exist"));
        Assert.False(state.IsRepo);
        Assert.NotNull(state.Error);
    }

    [Fact]
    public void InitDirtyCommitClean_TheFullCycle_IsReportedTruthfully()
    {
        if (!GitAvailable) return;
        Assert.True(RepoOps.Git(_root, "init").Ok);

        // A fresh repo with an untracked file is DIRTY, and says so.
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "the colony was here\n");
        var dirty = RepoOps.Describe(_root);
        Assert.True(dirty.IsRepo);
        Assert.False(string.IsNullOrWhiteSpace(dirty.Branch));
        Assert.Equal(1, dirty.DirtyCount);
        Assert.Contains(dirty.Dirty, d => d.Path.Contains("notes.txt"));

        // Commit stages the named path and lands with the deterministic anthill identity —
        // no reliance on whatever user.name this machine carries (possibly none).
        var (ok, message) = RepoOps.Commit(_root, new[] { "notes.txt" }, "first landing\n\nanthill test", "tester");
        Assert.True(ok, message);

        var clean = RepoOps.Describe(_root);
        Assert.True(clean.IsRepo);
        Assert.Equal(0, clean.DirtyCount);
        Assert.NotNull(clean.LastCommit);
        Assert.Contains("first landing", clean.LastCommit);

        // TopLevel resolves from a subdirectory to the repo root — the commit hook's question.
        var sub = Path.Combine(_root, "src");
        Directory.CreateDirectory(sub);
        Assert.Equal(Path.GetFullPath(_root), RepoOps.TopLevel(sub));
    }

    [Fact]
    public void Commit_WithNothingToCommit_SaysSoInsteadOfSucceeding()
    {
        if (!GitAvailable) return;
        Assert.True(RepoOps.Git(_root, "init").Ok);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        Assert.True(RepoOps.Commit(_root, new[] { "a.txt" }, "seed", "tester").Ok);

        var (ok, message) = RepoOps.Commit(_root, new[] { "a.txt" }, "empty follow-up", "tester");
        Assert.False(ok);
        Assert.Contains("Nothing to commit", message);
    }

    [Fact]
    public void Commit_RefusesAnEmptyMessage()
    {
        var (ok, message) = RepoOps.Commit(_root, Array.Empty<string>(), "  ", "tester");
        Assert.False(ok);
        Assert.Contains("message", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v0.3.8.52 — the commit train's data: per-file log with hash/author/time/subject, branch
    /// selectable without a checkout, one commit's diff on demand. And the guards that keep git
    /// arguments arguments: option-shaped refs are refused, non-hashes never reach `git show`.
    /// </summary>
    [Fact]
    public void CommitTrain_LogBranchesAndShow_ReadHistoryTruthfully()
    {
        if (!GitAvailable) return;
        Assert.True(RepoOps.Git(_root, "init").Ok);
        File.WriteAllText(Path.Combine(_root, "story.txt"), "first\n");
        Assert.True(RepoOps.Commit(_root, new[] { "story.txt" }, "chapter one", "tester").Ok);
        File.WriteAllText(Path.Combine(_root, "story.txt"), "first\nsecond\n");
        Assert.True(RepoOps.Commit(_root, new[] { "story.txt" }, "chapter two", "tester").Ok);
        File.WriteAllText(Path.Combine(_root, "unrelated.txt"), "noise");
        Assert.True(RepoOps.Commit(_root, new[] { "unrelated.txt" }, "noise lands", "tester").Ok);

        // The file's train carries ITS two stops, newest first — not the whole repo's three.
        var train = RepoOps.Log(_root, null, "story.txt");
        Assert.Equal(2, train.Count);
        Assert.Equal("chapter two", train[0].Subject);
        Assert.Equal("chapter one", train[1].Subject);
        Assert.All(train, c => Assert.False(string.IsNullOrWhiteSpace(c.Hash)));
        Assert.All(train, c => Assert.True(c.Time > 0));

        // No path = the repo's full train.
        Assert.Equal(3, RepoOps.Log(_root, null, null).Count);

        // Branch selection reads the OTHER branch's history without checking it out.
        var (current, branches) = RepoOps.Branches(_root);
        Assert.NotNull(current);
        Assert.Contains(current!, branches);
        Assert.True(RepoOps.Git(_root, "branch", "siding").Ok);
        File.WriteAllText(Path.Combine(_root, "story.txt"), "first\nsecond\nthird\n");
        Assert.True(RepoOps.Commit(_root, new[] { "story.txt" }, "chapter three", "tester").Ok);
        Assert.Equal(3, RepoOps.Log(_root, current, "story.txt").Count);       // current moved on
        Assert.Equal(2, RepoOps.Log(_root, "siding", "story.txt").Count);      // the siding did not
        Assert.Equal(current, RepoOps.Branches(_root).Current);                // and nothing was checked out

        // One stop's diff, on demand.
        var (ok, diff) = RepoOps.ShowCommit(_root, train[0].Hash, "story.txt");
        Assert.True(ok, diff);
        Assert.Contains("+second", diff);

        // The guards: option-shaped refs and non-hashes never reach git. An unsafe ref is
        // treated as ABSENT (HEAD history), never passed through as an argument.
        Assert.False(RepoOps.SafeRef("--exec=evil"));
        Assert.False(RepoOps.SafeRef("-D"));
        Assert.True(RepoOps.SafeRef("feat/v0.3.8.52"));
        Assert.Equal(3, RepoOps.Log(_root, "--all --not-a-ref", "story.txt").Count);
        Assert.False(RepoOps.ShowCommit(_root, "--patch", null).Ok);
    }

    [Fact]
    public void StagedScope_IsOnlyTheNamedPaths_NotTheWholeTree()
    {
        if (!GitAvailable) return;
        Assert.True(RepoOps.Git(_root, "init").Ok);
        File.WriteAllText(Path.Combine(_root, "wanted.txt"), "commit me");
        File.WriteAllText(Path.Combine(_root, "bystander.txt"), "leave me dirty");

        var (ok, message) = RepoOps.Commit(_root, new[] { "wanted.txt" }, "scoped commit", "tester");
        Assert.True(ok, message);

        // The bystander is still uncommitted — an applied patch commits ITS file, not the
        // operator's unrelated work-in-progress sitting in the same tree.
        var state = RepoOps.Describe(_root);
        Assert.Equal(1, state.DirtyCount);
        Assert.Contains(state.Dirty, d => d.Path.Contains("bystander.txt"));
    }
}
