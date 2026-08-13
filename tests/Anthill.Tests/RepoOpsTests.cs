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
