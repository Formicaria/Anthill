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

    // ---- symbols: evidence, not authority -------------------------------------------------------

    /// <summary>
    /// The point of symbol entries: "where is this declared" answered without opening a file, with a
    /// path and line the agent can then go and read.
    /// </summary>
    [Fact]
    public void Symbols_LocateDeclarationsWithTheirLine()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        var program = Assert.Single(index.FindSymbol("Program", exact: true));
        Assert.Equal("src/Program.cs", program.Path);
        Assert.Equal("type", program.Symbol.Kind);
        Assert.Equal(1, program.Symbol.Line);
    }

    [Theory]
    [InlineData("csharp", "public sealed class Widget { }\n", "Widget", "type")]
    [InlineData("csharp", "public interface IThing { }\n", "IThing", "type")]
    [InlineData("csharp", "    public async Task<int> ComputeAsync(int x)\n", "ComputeAsync", "method")]
    [InlineData("typescript", "export class Service { }\n", "Service", "type")]
    [InlineData("typescript", "export function render(a) { }\n", "render", "function")]
    [InlineData("typescript", "export const handler = async (req) => { }\n", "handler", "function")]
    [InlineData("python", "class Widget:\n", "Widget", "type")]
    [InlineData("python", "async def fetch(url):\n", "fetch", "function")]
    [InlineData("go", "func Handle(w http.ResponseWriter) {\n", "Handle", "function")]
    [InlineData("rust", "pub fn compute(x: i32) -> i32 {\n", "compute", "function")]
    public void EachLanguage_YieldsItsDeclarations(string language, string source, string name, string kind)
    {
        var symbols = RepositoryIndexBuilder.ExtractSymbols(language, System.Text.Encoding.UTF8.GetBytes(source));

        var found = Assert.Single(symbols.Where(s => s.Name == name));
        Assert.Equal(kind, found.Kind);
    }

    /// <summary>
    /// A language with no declared patterns yields nothing rather than guessing. Markdown headings
    /// are not declarations, and inventing symbols for them would fill the index with noise an agent
    /// would then chase.
    /// </summary>
    [Fact]
    public void AnUnknownLanguage_YieldsNoSymbols() =>
        Assert.Empty(RepositoryIndexBuilder.ExtractSymbols("markdown",
            System.Text.Encoding.UTF8.GetBytes("# Heading\n## Another\n")));

    /// <summary>
    /// A binary that happens to carry a source extension produces NOTHING, not confident nonsense.
    /// Pattern matching over bytes finds "declarations" wherever the bytes happen to look right.
    /// </summary>
    [Fact]
    public void ABinaryFile_YieldsNoSymbols() =>
        Assert.Empty(RepositoryIndexBuilder.ExtractSymbols("csharp",
            new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01, 0x02 }));

    /// <summary>
    /// Bounded per file. A generated or minified file with forty thousand declarations is not a map,
    /// and letting one file dominate the index makes every other answer harder to find.
    /// </summary>
    [Fact]
    public void SymbolsAreCappedPerFile()
    {
        var source = string.Concat(Enumerable.Range(0, RepositoryIndexBuilder.MaxSymbolsPerFile + 200)
            .Select(i => $"class Type{i} {{ }}\n"));

        var symbols = RepositoryIndexBuilder.ExtractSymbols("csharp", System.Text.Encoding.UTF8.GetBytes(source));

        Assert.Equal(RepositoryIndexBuilder.MaxSymbolsPerFile, symbols.Count);
    }

    /// <summary>Symbol search is deterministic, like every other index answer.</summary>
    [Fact]
    public void SymbolSearch_IsOrderedAndRepeatable()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        Assert.Equal(index.FindSymbol("e").Select(x => (x.Path, x.Symbol.Line)),
                     index.FindSymbol("e").Select(x => (x.Path, x.Symbol.Line)));
    }

    /// <summary>
    /// The honesty requirement, asserted rather than trusted to a comment. An agent told
    /// "declared nowhere" stops looking; one told "these are all the callers" changes code on the
    /// strength of a list that was never complete. The tool must never let a symbol answer read as
    /// authoritative.
    /// </summary>
    [Fact]
    public void TheTool_SaysItsSymbolAnswersAreNotAuthoritative()
    {
        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = Tool().Run(new Dictionary<string, object?> { ["symbol"] = "Program" });

            Assert.True(result.Success);
            Assert.Contains("src/Program.cs:1", result.Output);
            Assert.Contains("not by a compiler", result.Output);
            Assert.Contains("not a complete or authoritative list", result.Output);
        }
    }

    /// <summary>A name nothing declares is an empty answer, still carrying the caveat.</summary>
    [Fact]
    public void TheTool_FindingNoDeclaration_DoesNotClaimItDoesNotExist()
    {
        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = Tool().Run(new Dictionary<string, object?> { ["symbol"] = "NoSuchThing" });

            Assert.True(result.Success);
            Assert.Contains("(0)", result.Output);
            Assert.Contains("not a complete or authoritative list", result.Output);
        }
    }

    // ---- references: the answer an agent most wants to over-trust ---------------------------------

    private RepositoryIndex Indexed() => RepositoryIndexBuilder.Build(Workspace());

    /// <summary>
    /// The useful case: one declaration, a distinctive name. The mention in another file is found,
    /// and the DECLARATION is not returned as its own first caller — which would inflate a count an
    /// agent may be using to judge how risky a change is.
    /// </summary>
    [Fact]
    public void References_FindMentionsAndExcludeTheDeclaration()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Widget.cs"), "class WidgetFactory { }\n");
        File.WriteAllText(Path.Combine(_root, "src", "Uses.cs"),
            "class Uses\n{\n    WidgetFactory factory = new WidgetFactory();\n}\n");

        var report = RepositoryReferences.Find(Indexed(), _root, "WidgetFactory");

        Assert.True(report.Attributable);
        Assert.Equal("", report.Caveat);
        Assert.All(report.References, r => Assert.Equal("src/Uses.cs", r.Path));
        Assert.NotEmpty(report.References);
    }

    /// <summary>
    /// The case that makes this feature dangerous if handled naively. A name declared in several
    /// places produces mentions that CANNOT be attributed to any one of them — and "what calls this"
    /// is the input to "what would my change break", so a list that looks authoritative and is not
    /// gets acted on.
    /// </summary>
    [Fact]
    public void References_ToAnAmbiguousName_AreNotAttributable()
    {
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class Handler { }\n");
        File.WriteAllText(Path.Combine(_root, "ui", "b.ts"), "export class Handler { }\n");

        var report = RepositoryReferences.Find(Indexed(), _root, "Handler");

        Assert.Equal(2, report.DeclarationCount);
        Assert.False(report.Attributable);
        Assert.Contains("declared in 2 places", report.Caveat);
        Assert.Contains("CANNOT be attributed", report.Caveat);
    }

    /// <summary>A short name matches everywhere for unrelated reasons, and says so.</summary>
    [Fact]
    public void References_ToAShortName_AreFlaggedAsProbablyUnrelated()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Id.cs"), "class Id { }\n");

        var report = RepositoryReferences.Find(Indexed(), _root, "Id");

        Assert.False(report.Attributable);
        Assert.Contains("short enough", report.Caveat);
    }

    /// <summary>
    /// A name the index found no declaration for is reported as exactly that — possibly external,
    /// possibly written in a shape the patterns miss. Never as "does not exist".
    /// </summary>
    [Fact]
    public void References_WithNoKnownDeclaration_SayTheDefinitionIsUnknown()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Calls.cs"), "var x = ExternalThing.Go();\n");

        var report = RepositoryReferences.Find(Indexed(), _root, "ExternalThing");

        Assert.Equal(0, report.DeclarationCount);
        Assert.False(report.Attributable);
        Assert.Contains("possibly external", report.Caveat);
    }

    /// <summary>
    /// Whole words only. Without boundaries, "User" matches "UserService", "Users" and
    /// "ParseUserInput", and the result stops meaning anything.
    /// </summary>
    [Fact]
    public void References_MatchWholeWordsOnly()
    {
        File.WriteAllText(Path.Combine(_root, "src", "User.cs"), "class UserAccount { }\n");
        File.WriteAllText(Path.Combine(_root, "src", "Other2.cs"),
            "class Other2\n{\n    UserAccountRepository repo;\n    UserAccount account;\n}\n");

        var report = RepositoryReferences.Find(Indexed(), _root, "UserAccount");

        // the line declaring UserAccountRepository must not count as a mention of UserAccount
        Assert.DoesNotContain(report.References, r => r.Text.Contains("Repository"));
        Assert.Contains(report.References, r => r.Text.Contains("account"));
    }

    [Fact]
    public void References_ToNothing_AreAnEmptyReport()
    {
        var report = RepositoryReferences.Find(Indexed(), _root, "   ");

        Assert.Empty(report.References);
        Assert.Equal(0, report.FilesScanned);
    }

    /// <summary>
    /// The tool puts the caveat FIRST. Placed after the list it reads as a footnote to an answer
    /// already accepted; placed first it frames what follows — and for the question these results
    /// feed, that ordering is the difference between an agent checking and an agent proceeding.
    /// </summary>
    [Fact]
    public void TheTool_LeadsWithTheCaveat_WhenReferencesCannotBeAttributed()
    {
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class Handler { }\n");
        File.WriteAllText(Path.Combine(_root, "ui", "b.ts"), "export class Handler { }\n");

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = Tool().Run(new Dictionary<string, object?> { ["references"] = "Handler" });

            Assert.True(result.Success);
            var caution = result.Output.IndexOf("CAUTION", StringComparison.Ordinal);
            var list = result.Output.IndexOf("--- mentions", StringComparison.Ordinal);
            Assert.True(caution >= 0 && caution < list, "the caveat must precede the list");
            Assert.Contains("not resolved references", result.Output);
        }
    }

    /// <summary>Even the trustworthy case refuses to claim resolution it did not perform.</summary>
    [Fact]
    public void TheTool_NeverClaimsToHaveResolvedReferences()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Widget.cs"), "class WidgetFactory { }\n");
        File.WriteAllText(Path.Combine(_root, "src", "Uses.cs"), "var f = new WidgetFactory();\n");

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = Tool().Run(new Dictionary<string, object?> { ["references"] = "WidgetFactory" });

            Assert.Contains("still text matches", result.Output);
            Assert.Contains("imports, overloads and scope are not resolved", result.Output);
        }
    }

    // ---- incremental, and durable -----------------------------------------------------------------

    /// <summary>
    /// Incremental on the EXPENSIVE half. Every file is still read and hashed — you cannot know a
    /// file is unchanged without looking at it — but symbol extraction, the regex pass over every
    /// line, is skipped when the content matches.
    /// </summary>
    [Fact]
    public void ARebuild_ReusesFilesWhoseContentHasNotChanged()
    {
        var first = RepositoryIndexBuilder.Build(Workspace("rev-one"));

        var second = RepositoryIndexBuilder.Build(Workspace("rev-one"), first);

        Assert.Equal(first.Files.Count, second.ReusedFiles);
        Assert.Equal(0, first.ReusedFiles);
    }

    /// <summary>An edited file is re-read; the rest are still reused.</summary>
    [Fact]
    public void ARebuild_RedoesOnlyWhatChanged()
    {
        var first = RepositoryIndexBuilder.Build(Workspace("rev-one"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class Renamed { }\n");

        var second = RepositoryIndexBuilder.Build(Workspace("rev-one"), first);

        Assert.Equal(first.Files.Count - 1, second.ReusedFiles);
        Assert.Contains(second.FindSymbol("Renamed", exact: true), x => x.Path == "src/Program.cs");
        Assert.Empty(second.FindSymbol("Program", exact: true));
    }

    /// <summary>
    /// A previous index of a DIFFERENT revision is not reused. Different revision means different
    /// content at the same paths, and reusing across that boundary produces an index describing a
    /// tree nobody has.
    /// </summary>
    [Fact]
    public void ARebuild_NeverReusesAcrossRevisions()
    {
        var first = RepositoryIndexBuilder.Build(Workspace("rev-one"));

        var second = RepositoryIndexBuilder.Build(Workspace("rev-two"), first);

        Assert.Equal(0, second.ReusedFiles);
    }

    /// <summary>
    /// The index survives a restart, which is what makes "durable" true rather than aspirational.
    /// Without it, every process start re-walks and re-parses the whole repository.
    /// </summary>
    [Fact]
    public void TheIndex_SurvivesAReopenedDatabase()
    {
        var db = Path.Combine(_dir, "memory.db");
        var built = RepositoryIndexBuilder.Build(Workspace("rev-one"));

        using (var memory = new Anthill.Core.Memory.SqliteMemory(db))
            memory.SaveRepositoryIndex(built);

        using var reopened = new Anthill.Core.Memory.SqliteMemory(db);
        var loaded = reopened.LoadRepositoryIndex("ws1", "rev-one");

        Assert.NotNull(loaded);
        Assert.Equal(built.Files.Count, loaded!.Files.Count);
        Assert.Equal(built.SymbolCount, loaded.SymbolCount);
        Assert.Equal(built.Find("src/Program.cs")!.ContentHash, loaded.Find("src/Program.cs")!.ContentHash);
    }

    /// <summary>
    /// A stored index is only ever returned for the revision it describes — the same rule the
    /// in-memory cache follows, written down where it survives a restart.
    /// </summary>
    [Fact]
    public void AStoredIndex_IsNotReturnedForAnotherRevision()
    {
        var db = Path.Combine(_dir, "memory.db");
        using var memory = new Anthill.Core.Memory.SqliteMemory(db);
        memory.SaveRepositoryIndex(RepositoryIndexBuilder.Build(Workspace("rev-one")));

        Assert.Null(memory.LoadRepositoryIndex("ws1", "rev-two"));
    }

    /// <summary>
    /// A MISS returns null, not an empty index. An empty index is a legitimate answer for an empty
    /// repository, and conflating the two would make the first mission after a restart believe the
    /// repository has no files at all.
    /// </summary>
    [Fact]
    public void ANoStoredIndex_IsNullRatherThanEmpty()
    {
        using var memory = new Anthill.Core.Memory.SqliteMemory(Path.Combine(_dir, "memory.db"));

        Assert.Null(memory.LoadRepositoryIndex("never-indexed", "rev"));
    }

    /// <summary>
    /// Saving replaces wholesale. A file DELETED since the last index must disappear from it —
    /// merging would keep answering with a path that no longer exists, and an agent sent to read it
    /// gets a confusing failure instead of a correct absence.
    /// </summary>
    [Fact]
    public void SavingAnIndex_DropsFilesThatNoLongerExist()
    {
        var db = Path.Combine(_dir, "memory.db");
        using var memory = new Anthill.Core.Memory.SqliteMemory(db);
        memory.SaveRepositoryIndex(RepositoryIndexBuilder.Build(Workspace("rev-one")));

        File.Delete(Path.Combine(_root, "src", "Helper.cs"));
        memory.SaveRepositoryIndex(RepositoryIndexBuilder.Build(Workspace("rev-one")));

        Assert.Null(memory.LoadRepositoryIndex("ws1", "rev-one")!.Find("src/Helper.cs"));
    }

    /// <summary>
    /// A small repository indexes symbols; the flag stays off. The control for the test below —
    /// without it, that test would pass even if symbols were never extracted at all.
    /// </summary>
    [Fact]
    public void ASmallRepository_IndexesSymbols()
    {
        var index = RepositoryIndexBuilder.Build(Workspace());

        Assert.False(index.InventoryOnly);
        Assert.True(index.SymbolCount > 0);
    }

    /// <summary>
    /// The exit gate: "a large repository degrades to file-inventory-only rather than failing".
    /// Symbols are the expensive part and, past a certain size, the least useful — a symbol search
    /// returning two thousand candidates is not an answer.
    /// </summary>
    [Fact]
    public void ALargeRepository_DegradesToInventoryOnly()
    {
        var many = Path.Combine(_dir, "big");
        Directory.CreateDirectory(many);
        for (var i = 0; i <= RepositoryIndex.MaxFilesForSymbols; i++)
            File.WriteAllText(Path.Combine(many, $"F{i}.cs"), "class T { }\n");

        var index = RepositoryIndexBuilder.Build(Workspace(root: many));

        Assert.True(index.InventoryOnly);
        Assert.NotEmpty(index.Files);          // it still knows what is THERE
        Assert.Equal(0, index.SymbolCount);    // it just did not parse it
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
