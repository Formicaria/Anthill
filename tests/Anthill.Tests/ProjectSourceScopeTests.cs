using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A PROJECT MISSION READS ITS OWN PROJECT, AND ONLY READS IT. v0.3.8.132.
///
/// WHAT THIS IS ABOUT. An operator asked the colony to review a repository. It reviewed a different
/// tree, proposed patches against paths inside that tree, and reported the result as findings about
/// the repository. Nothing errored. `Project.Path` was loaded and used for exactly one purpose — as
/// the source of a git WORKTREE — and the worktree is prepared only when a write gate is on. All
/// three default off, so the shipped proposal-only configuration entered NO workspace scope, and
/// every file and check tool fell back to `agent_workspace_dir`.
///
/// The fix gives such a mission a scope over the project's own source, materialising nothing. That
/// creates a second, sharper hazard, which is what most of this file is about: five consumers read
/// the ambient scope as licence to WRITE. Handed the operator's live checkout they would run an
/// agent CLI in it, diff the operator's uncommitted work and file it as a patch set the mission
/// produced, or summarise it as "what this mission changed". Each of those is a confident, wrong
/// answer rather than an error, which is the worst shape a defect can take here.
///
/// So the record carries `Writable`, and "what tree am I looking at" and "what tree may I change"
/// stop being the same question.
/// </summary>
public class ProjectSourceScopeTests
{
    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SourceText.RepoRoot(), "src" }.Concat(parts).ToArray()));

    private static MissionWorkspace ReadOnlySource(string root) => new()
    {
        Id = "project-source-m1", MissionId = "m1", Root = root, SourceRoot = root,
        Mode = "project-source", State = WorkspaceState.Active, Writable = false,
    };

    private static MissionWorkspace Worktree(string root) => new()
    {
        Id = "w1", MissionId = "m1", Root = root, SourceRoot = root,
        Mode = "worktree", State = WorkspaceState.Ready,
    };

    /// <summary>
    /// A READ-ONLY SCOPE IS VISIBLE TO READERS. This is the whole point: the path guard, the check
    /// runner, the capability manifest and `repository_index` all resolve through `CurrentRoot`, and
    /// before this release a proposal-only project mission gave them nothing to resolve to.
    /// </summary>
    [Fact]
    public void AReadOnlySourceScope_IsTheRootEveryReaderResolvesTo()
    {
        using var scope = MissionWorkspaceScope.Enter(ReadOnlySource("/projects/acme"));

        Assert.Equal("/projects/acme", MissionWorkspaceScope.CurrentRoot);
        Assert.NotNull(MissionWorkspaceScope.Current);
    }

    /// <summary>
    /// AND INVISIBLE TO WRITERS. `CurrentWritable` must answer exactly as it answers outside any
    /// scope at all — the five consumers below already handle "no scope" correctly, and this makes
    /// a read-only scope indistinguishable from that case to them.
    /// </summary>
    [Fact]
    public void AReadOnlySourceScope_IsNotAWritableOne()
    {
        using var scope = MissionWorkspaceScope.Enter(ReadOnlySource("/projects/acme"));

        Assert.Null(MissionWorkspaceScope.CurrentWritable);
        Assert.Null(MissionWorkspaceScope.CurrentWritableRoot);
    }

    /// <summary>
    /// A WORKTREE IS UNCHANGED, and `Writable` defaults to true precisely so that is true without
    /// every existing construction site being edited. A default that had to be opted into would
    /// have made this release a sweep of every call site instead of one flag.
    /// </summary>
    [Fact]
    public void APreparedWorktree_IsStillWritable()
    {
        using var scope = MissionWorkspaceScope.Enter(Worktree("/tmp/anthill-workspaces/m1-w1"));

        Assert.Equal("/tmp/anthill-workspaces/m1-w1", MissionWorkspaceScope.CurrentRoot);
        Assert.Equal("/tmp/anthill-workspaces/m1-w1", MissionWorkspaceScope.CurrentWritableRoot);
        Assert.NotNull(MissionWorkspaceScope.CurrentWritable);
    }

    /// <summary>Outside any scope both answers are absent, which is what the writers already expect.</summary>
    [Fact]
    public void OutsideAnyScope_NeitherRootExists()
    {
        Assert.Null(MissionWorkspaceScope.CurrentRoot);
        Assert.Null(MissionWorkspaceScope.CurrentWritableRoot);
    }

    /// <summary>
    /// EVERY WRITE PATH ASKS THE WRITE QUESTION. A source guard, because the failure it prevents is
    /// silent: given a read-only scope, each of these produces a plausible wrong answer instead of
    /// an error, and a unit test would have to build a live git checkout to catch any of them.
    ///
    /// Keyed on the SHAPE — the call site reads the writable accessor — rather than on the five
    /// examples, so a sixth consumer that reaches for the ambient scope to write is refused here
    /// rather than discovered from a mission report.
    /// </summary>
    [Theory]
    [InlineData("Orchestration", "ExecutionService.cs", "var missionWorktree = Workspaces.MissionWorkspaceScope.CurrentWritableRoot;")]
    [InlineData("Orchestration", "ExecutionService.cs", "var workspace = Workspaces.MissionWorkspaceScope.CurrentWritable;")]
    [InlineData("Orchestration", "Queen.cs", "HarvestWorkspaceChanges(mission, Anthill.Core.Workspaces.MissionWorkspaceScope.CurrentWritable);")]
    [InlineData("Agents", "Ants.cs", "MissionWorkspaceScope.CurrentWritableRoot is { } actingTree")]
    [InlineData("Tools", "WorkspaceTools.cs", "var workspace = MissionWorkspaceScope.CurrentWritable;")]
    public void EveryWritePath_ReadsTheWritableScope(string dir, string file, string expected)
    {
        Assert.Contains(expected, Src("Anthill.Core", dir, file), StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE READ-ONLY SCOPE IS ONLY ENTERED FOR A PATH THAT EXISTS. Inventing a scope over a
    /// directory that is not there would turn every read into a filesystem error, which is worse
    /// than the fallback being fixed — so the Queen checks, and says which happened either way.
    /// </summary>
    [Fact]
    public void TheQueen_EntersASourceScopeOnlyForAPathThatExists_AndRecordsBoth()
    {
        var queen = Src("Anthill.Core", "Orchestration", "Queen.cs");

        Assert.Contains("Writable = false,", queen, StringComparison.Ordinal);
        Assert.Contains("exists = Directory.Exists(sourceRoot);", queen, StringComparison.Ordinal);
        Assert.Contains("EventTypes.MissionProjectSourceScope", queen, StringComparison.Ordinal);
        Assert.Contains("EventTypes.MissionProjectSourceMissing", queen, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tester names the tree it judged, and a read-only project scope is a third thing — not a
    /// mission workspace and not the configured fallback. Calling it either would misname the one
    /// tree whose identity the evidence depends on.
    /// </summary>
    [Fact]
    public void TheTesterNamesTheProjectTree_RatherThanCallingItAMissionWorkspace()
    {
        Assert.Contains("judged is { Writable: false } ? \"the project source tree (read-only)\"",
            Src("Anthill.Core", "Agents", "SpecialistAnts.cs"), StringComparison.Ordinal);
    }
}
