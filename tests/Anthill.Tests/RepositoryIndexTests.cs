using Anthill.Core.Tools;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.6.0 — the repository index: answer "where is this handled" from evidence, without reading the
/// repository into the context window.
///
/// The exit gates being proven:
///   - an index query returns the same answer for the same revision, or reports itself stale
///   - no indexing path can read outside the mission workspace boundary
///   - an agent asked a repository question calls a TOOL, not a pre-stuffed blob
///   - build size is bounded and reported; a large repository degrades rather than failing
/// </summary>
public class RepositoryIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;

    public RepositoryIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-idx-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "ui"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "left-pad"));

        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class Program\n{\n}\n");
        File.WriteAllText(Path.Combine(_root, "src", "Helper.cs"), "class Helper { }\n");
        File.WriteAllText(Path.Combine(_root, "ui", "app.ts"), "export const app = 1;\n");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "left-pad", "index.js"), "module.exports = 1;\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private MissionWorkspace Workspace(string revision = "abc123", string? root = null) => new()
    {
        Id = "ws1", MissionId = "m1", Root = root ?? _root, SourceRoot = _root,
        State = WorkspaceState.Active, Mode = "worktree", BaseRevision = revision,
    };

    // ---- what it records --------------------------------------------------------------------

    [Fact]
    public void TheIndex_RecordsPathLanguageAndSize()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        var program = index.Find("src/Program.cs");
        Assert.NotNull(program);
        Assert.Equal("csharp", program!.Language);
        Assert.True(program.Bytes > 0);
        Assert.Equal(4, program.Lines);              // three newlines -> four line starts
        Assert.NotEqual("", program.ContentHash);
    }

    /// <summary>
    /// node_modules is not this repository. Indexing it answers questions about dependencies, and
    /// makes every language breakdown a description of somebody else's code.
    /// </summary>
    [Fact]
    public void TheIndex_SkipsDependencyDirectories()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        Assert.DoesNotContain(index.Files, f => f.Path.Contains("node_modules"));
        Assert.Null(index.Find("node_modules/left-pad/index.js"));
    }

    /// <summary>The cheapest useful answer to "what is this repository" — no file read required.</summary>
    [Fact]
    public void TheIndex_SummarisesLanguages()
    {
        var counts = RepositoryIndexBuilder.Build(Workspace()).LanguageCounts;

        Assert.Equal(2, counts["csharp"]);
        Assert.Equal(1, counts["typescript"]);
        Assert.Equal(1, counts["markdown"]);
    }

    /// <summary>
    /// The exit gate: the same revision gives the same answer. Unmeetable if ordering depends on
    /// whatever the filesystem felt like returning, so paths are sorted.
    /// </summary>
    [Fact]
    public void TwoBuildsOfTheSameTree_AreIdentical()
    {
        var first = RepositoryIndexBuilder.Build(Workspace());
        var second = RepositoryIndexBuilder.Build(Workspace());

        Assert.Equal(first.Files.Select(f => f.Path), second.Files.Select(f => f.Path));
        Assert.Equal(first.Files.Select(f => f.ContentHash), second.Files.Select(f => f.ContentHash));
    }

    // ---- stale must be DETECTABLE, not merely old ---------------------------------------------

    /// <summary>
    /// The reason every entry carries a content hash rather than a timestamp. An mtime tells you an
    /// index is old; it cannot tell you whether the answer it would give is still true.
    /// </summary>
    [Fact]
    public void AnEditedFile_IsDetectableAsChanged()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());
        var path = Path.Combine(_root, "src", "Program.cs");

        Assert.False(index.FileChanged("src/Program.cs", RepositoryIndexBuilder.HashOf(path)));

        File.WriteAllText(path, "class Program { /* edited */ }\n");

        Assert.True(index.FileChanged("src/Program.cs", RepositoryIndexBuilder.HashOf(path)));
    }

    /// <summary>
    /// Staleness is per FILE, not per index. A mission editing three files must not throw away what
    /// the index knows about twenty thousand others — that would make the index useless precisely
    /// during the work it exists to support.
    /// </summary>
    [Fact]
    public void EditingOneFile_DoesNotInvalidateTheOthers()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "edited\n");

        Assert.False(index.FileChanged("src/Helper.cs",
            RepositoryIndexBuilder.HashOf(Path.Combine(_root, "src", "Helper.cs"))));
    }

    [Fact]
    public void TheIndex_KnowsWhichRevisionItDescribes()
    {
        var index = RepositoryIndexBuilder.Build(Workspace("rev-one"));

        Assert.True(index.DescribesRevision("rev-one"));
        Assert.False(index.DescribesRevision("rev-two"));
        Assert.False(index.DescribesRevision(null));
    }

    /// <summary>An index built with no revision cannot claim to describe one.</summary>
    [Fact]
    public void AnIndexWithoutARevision_DescribesNothing() =>
        Assert.False(RepositoryIndexBuilder.Build(Workspace(revision: "")).DescribesRevision(""));

    // ---- boundaries and bounds ------------------------------------------------------------------

    /// <summary>
    /// The exit gate: no indexing path reads outside the workspace. Enforced by resolving every path
    /// through the same guard every file tool uses — an indexer with its own traversal would be a
    /// second file-access path nothing else audits.
    /// </summary>
    [Fact]
    public void TheIndex_ContainsNothingOutsideTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_dir, "outside.cs"), "secret\n");

        var index = RepositoryIndexBuilder.Build(Workspace());

        Assert.All(index.Files, f =>
        {
            Assert.DoesNotContain("..", f.Path);
            Assert.False(Path.IsPathRooted(f.Path));
        });
        Assert.Null(index.Find("../outside.cs"));
    }

    /// <summary>An unusable workspace yields an EMPTY index rather than indexing something else.</summary>
    [Fact]
    public void AnUnusableWorkspace_YieldsAnEmptyIndex()
    {
        var index = RepositoryIndexBuilder.Build(Workspace() with { State = WorkspaceState.Cleaned });

        Assert.Empty(index.Files);
        Assert.False(index.Truncated);
    }

    /// <summary>Build cost is reported, so "the index is slow" is measurable rather than felt.</summary>
    [Fact]
    public void TheIndex_ReportsWhatItCost()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        Assert.True(index.BuildMilliseconds >= 0);
        Assert.True(index.TotalBytes > 0);
    }

    // ---- the tool -------------------------------------------------------------------------------

    private RepositoryIndexTool Tool() => new(RepositoryIndexBuilder.Build);

    /// <summary>
    /// The exit gate, directly: an agent asks by CALLING, and gets an answer rather than a
    /// repository. The summary costs one turn and no file reads.
    /// </summary>
    [Fact]
    public void TheTool_AnswersWhatIsInTheRepository()
    {
        using (MissionWorkspaceScope.Enter(Workspace("rev-one")))
        {
            var result = Tool().Run(new Dictionary<string, object?>());

            Assert.True(result.Success);
            Assert.Contains("rev-one", result.Output);      // traceable to a revision
            Assert.Contains("csharp: 2", result.Output);
        }
    }

    [Fact]
    public void TheTool_FindsFilesByNameAndLanguage()
    {
        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var byName = Tool().Run(new Dictionary<string, object?> { ["name"] = "program" });
            Assert.Contains("src/Program.cs", byName.Output);
            Assert.DoesNotContain("app.ts", byName.Output);

            var byLanguage = Tool().Run(new Dictionary<string, object?> { ["language"] = "typescript" });
            Assert.Contains("ui/app.ts", byLanguage.Output);
            Assert.DoesNotContain("Program.cs", byLanguage.Output);
        }
    }

    /// <summary>
    /// Outside a mission it REFUSES rather than describing the live checkout. An answer about a tree
    /// the mission may not touch would be worse than none: confidently irrelevant.
    /// </summary>
    [Fact]
    public void TheTool_OutsideAMission_Refuses()
    {
        var result = Tool().Run(new Dictionary<string, object?>());

        Assert.False(result.Success);
        Assert.Equal(Anthill.Core.Contracts.FailureClass.UnsafeState, result.Failure);
    }

    /// <summary>
    /// The cartographer — the role whose entire purpose is mapping a repository — is allowed to ask.
    /// </summary>
    [Fact]
    public void TheCartographerMayCallIt()
    {
        Assert.True(ToolInventory.Exists("repository_index"));
        Assert.True(ToolAuthorization.Evaluate("ui_cartographer", "repository_index").Allowed);
    }
}
