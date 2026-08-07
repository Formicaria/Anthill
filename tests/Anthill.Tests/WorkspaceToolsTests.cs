using System.Diagnostics;
using Anthill.Core.Agents;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.5.0 — the two scoped workspace tools that a contract declared and nothing built.
///
/// The cost of that gap was concrete, not theoretical. <c>ToolAuthorization</c> short-circuits on
/// contract presence, so a role whose <c>AllowedTools</c> named only a phantom was authorized to
/// dispatch NOTHING: <c>ui_cartographer</c>, whose entire purpose is mapping a repository, and
/// <c>scribe</c>, which writes release notes. Both ran and produced no work — which in a transcript
/// reads as a weak model rather than as a missing tool.
/// </summary>
public class WorkspaceToolsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;

    public WorkspaceToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-wstools-" + Guid.NewGuid().ToString("N")[..10]);
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Directory.CreateDirectory(Path.Combine(_repo, "node_modules", "left-pad"));

        File.WriteAllText(Path.Combine(_repo, "src", "Program.cs"),
            "class Program\n{\n    static void Main() => Console.WriteLine(\"needle\");\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "Other.cs"), "// nothing here\n");
        File.WriteAllText(Path.Combine(_repo, "node_modules", "left-pad", "index.js"),
            "// needle needle needle\n");

        Git(_repo, "init -b main");
        Git(_repo, "config user.email test@anthill.local");
        Git(_repo, "config user.name Test");
        Git(_repo, "add -A");
        Git(_repo, "commit -m first");
    }

    public void Dispose()
    {
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

    private MissionWorkspace Workspace(string? root = null) => new()
    {
        Id = "ws1", MissionId = "m1", Root = root ?? _repo, SourceRoot = _repo,
        State = WorkspaceState.Active, Mode = "worktree",
        BaseRevision = Git(_repo, "rev-parse HEAD"),
    };

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    // ---- the roles are no longer blocked --------------------------------------------------------

    /// <summary>
    /// The headline outcome. Both names have moved from <c>Planned</c> to <c>Implemented</c>, which
    /// the inventory's own documentation calls "the whole point" — and the two roles that could
    /// dispatch nothing can now dispatch something.
    /// </summary>
    [Theory]
    [InlineData("ui_cartographer", "search_workspace")]
    [InlineData("scribe", "read_changed_files_summary")]
    public void ARoleBlockedOnAPhantomTool_CanNowDispatch(string role, string tool)
    {
        Assert.True(ToolInventory.Exists(tool), $"'{tool}' should now be implemented");
        Assert.DoesNotContain(tool, ToolInventory.Planned);
        Assert.True(ToolAuthorization.Evaluate(role, tool).Allowed);
    }

    /// <summary>
    /// SCRIBE specifically comes off the blocked list, and that is the measurable result.
    ///
    /// Stated precisely, because the first version of this test also asserted it for
    /// ui_cartographer — which was already true before the change and therefore proved nothing.
    /// The cartographer was never FULLY blocked: it also allows list_directory and read_text_file,
    /// and RolesBlockedByMissingTools only counts roles whose allowed tools ALL fail to exist. It
    /// was crippled rather than blocked — able to list and read, but unable to search, which is the
    /// difference between mapping a repository and guessing at one.
    ///
    /// Scribe's only allowed tool was the phantom, so it genuinely could dispatch nothing.
    ///
    /// v3.8.23: the list is now EMPTY, and the other three did not get their tools built. Their
    /// contracts stopped naming tools that never existed — soldier's PolicyScan is an in-process
    /// deterministic service and belongs out of a model's reach, medic needs orchestration to
    /// assemble a typed failure context rather than a tool to fetch one, and archivist's write path
    /// already runs through artifacts and would have been duplicated. Scribe remains the one role
    /// that came off this list by having something BUILT for it, which is what this test measures.
    /// </summary>
    [Fact]
    public void Scribe_ComesOffTheBlockedList()
    {
        var blocked = ToolInventory.RolesBlockedByMissingTools(AntExecutionCatalog.Contracts);

        Assert.DoesNotContain("scribe", blocked);
        Assert.Empty(blocked);

        // The control that keeps the assertion above meaningful: scribe is off the list because its
        // tool EXISTS, not because its contract stopped asking for one. Without this, emptying every
        // allowlist in the codebase would pass.
        Assert.Contains("read_changed_files_summary", AntExecutionCatalog.Contracts["scribe"].AllowedTools);
        Assert.True(ToolInventory.Exists("read_changed_files_summary"));
    }

    // ---- search ---------------------------------------------------------------------------------

    [Fact]
    public void Search_FindsALiteralWithItsFileAndLineNumber()
    {
        var tool = new SearchWorkspaceTool(new WorkspacePathGuard(_repo));

        var result = tool.Run(Args(("query", "needle")));

        Assert.True(result.Success);
        Assert.Contains("Program.cs", result.Output);
        Assert.Contains(":3:", result.Output);      // the line it is actually on
    }

    /// <summary>
    /// node_modules is skipped. Searching it is slow, and it floods the result with matches in
    /// dependencies the mission cannot change — the model then reads those as its own code.
    /// </summary>
    [Fact]
    public void Search_IgnoresDependencyDirectories()
    {
        var tool = new SearchWorkspaceTool(new WorkspacePathGuard(_repo));

        var result = tool.Run(Args(("query", "needle")));

        Assert.DoesNotContain("node_modules", result.Output);
        Assert.DoesNotContain("left-pad", result.Output);
    }

    [Fact]
    public void Search_SupportsRegexAndGlob()
    {
        var tool = new SearchWorkspaceTool(new WorkspacePathGuard(_repo));

        var regex = tool.Run(Args(("query", @"Console\.\w+"), ("regex", true)));
        Assert.True(regex.Success);
        Assert.Contains("Program.cs", regex.Output);

        var glob = tool.Run(Args(("query", "needle"), ("glob", "*.js")));
        Assert.DoesNotContain("Program.cs", glob.Output);
    }

    /// <summary>
    /// The model writes the pattern, so a bad one must come back as something it can FIX. Thrown, it
    /// would be an opaque tool failure; classified, the loop tells it to correct the arguments.
    /// </summary>
    [Fact]
    public void Search_AnInvalidRegex_IsAValidationFailure_NotACrash()
    {
        var tool = new SearchWorkspaceTool(new WorkspacePathGuard(_repo));

        var result = tool.Run(Args(("query", "([unclosed"), ("regex", true)));

        Assert.False(result.Success);
        Assert.Equal(Anthill.SDK.Contracts.FailureClass.ValidationFailure, result.Failure);
    }

    [Fact]
    public void Search_RequiresAQuery() =>
        Assert.Equal(Anthill.SDK.Contracts.FailureClass.ValidationFailure,
            new SearchWorkspaceTool(new WorkspacePathGuard(_repo)).Run(Args(("query", " "))).Failure);

    /// <summary>No matches is a SUCCESS with nothing found — not a failure.</summary>
    [Fact]
    public void Search_FindingNothing_Succeeds()
    {
        var result = new SearchWorkspaceTool(new WorkspacePathGuard(_repo))
            .Run(Args(("query", "definitely-not-in-this-repository")));

        Assert.True(result.Success);
        Assert.Contains("No matches", result.Output);
    }

    /// <summary>
    /// The search is confined by the same guard as every other file tool, so inside a mission it
    /// reads the workspace and cannot be pointed at the live checkout.
    /// </summary>
    [Fact]
    public void Search_CannotEscapeTheWorkspace()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "needle\n");

        var result = new SearchWorkspaceTool(new WorkspacePathGuard(_repo))
            .Run(Args(("query", "needle"), ("path", "../elsewhere")));

        Assert.False(result.Success);
        Assert.Equal(Anthill.SDK.Contracts.FailureClass.AuthorizationFailure, result.Failure);
    }

    // ---- change summary ---------------------------------------------------------------------------

    /// <summary>
    /// Where the manifest earns its keep: the diff is taken against the workspace's RECORDED base
    /// revision. Diffing against a moving HEAD would fold in commits this mission never made, which
    /// a scribe would then describe as this mission's work, in release notes, convincingly.
    /// </summary>
    [Fact]
    public void ChangeSummary_ReportsAgainstTheRecordedBaseRevision()
    {
        var baseRevision = Git(_repo, "rev-parse HEAD");
        File.WriteAllText(Path.Combine(_repo, "src", "Program.cs"), "class Program { }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "Added.cs"), "// new\n");

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = new ChangedFilesSummaryTool().Run(Args());

            Assert.True(result.Success);
            Assert.Contains(baseRevision, result.Output);
            Assert.Contains("Program.cs", result.Output);
            Assert.Contains("Added.cs", result.Output);      // untracked files are reported too
        }
    }

    /// <summary>
    /// Someone else committing during the mission must not appear as this mission's change.
    ///
    /// This one uses a REAL detached worktree rather than the repository directory. An earlier draft
    /// pointed the workspace at the repo itself, which made the assertion pass while proving nothing
    /// — the isolation being tested is a property of the worktree, so a test that does not take one
    /// is testing its own setup.
    /// </summary>
    [Fact]
    public void ChangeSummary_DoesNotIncludeCommitsMadeInTheSourceAfterwards()
    {
        var worktree = Path.Combine(_dir, "worktree");
        Git(_repo, $"worktree add --detach \"{worktree}\" HEAD");
        try
        {
            var workspace = Workspace(worktree);              // base revision pinned here

            // the operator commits something unrelated in the live checkout, mid-mission
            File.WriteAllText(Path.Combine(_repo, "unrelated.txt"), "someone else's work\n");
            Git(_repo, "add -A");
            Git(_repo, "commit -m unrelated");

            // and the mission changes one file of its own
            File.WriteAllText(Path.Combine(worktree, "src", "Program.cs"), "class Program { /* mine */ }\n");

            using (MissionWorkspaceScope.Enter(workspace))
            {
                var result = new ChangedFilesSummaryTool().Run(Args());

                Assert.True(result.Success);
                Assert.Contains("Program.cs", result.Output);
                Assert.DoesNotContain("unrelated.txt", result.Output);
            }
        }
        finally { Git(_repo, $"worktree remove --force \"{worktree}\""); }
    }

    /// <summary>
    /// Outside a mission it REFUSES rather than diffing the live checkout. Summarising the
    /// operator's own uncommitted work as "what this mission changed" would be a confident,
    /// plausible lie in a document meant to describe a release.
    /// </summary>
    [Fact]
    public void ChangeSummary_OutsideAMission_Refuses()
    {
        var result = new ChangedFilesSummaryTool().Run(Args());

        Assert.False(result.Success);
        Assert.Equal(Anthill.SDK.Contracts.FailureClass.UnsafeState, result.Failure);
        Assert.Contains("no mission workspace", result.Output + result.Error);
    }

    [Fact]
    public void ChangeSummary_CanIncludeThePatch()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "Program.cs"), "class Program { /* changed */ }\n");

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var result = new ChangedFilesSummaryTool().Run(Args(("include_patch", true)));

            Assert.True(result.Success);
            Assert.Contains("--- patch ---", result.Output);
            Assert.Contains("changed", result.Output);
        }
    }

    /// <summary>Both tools describe their arguments, so a model can be offered them.</summary>
    [Fact]
    public void BothToolsPublishAUsableSchema()
    {
        foreach (var tool in new ITool[]
                 { new SearchWorkspaceTool(new WorkspacePathGuard(_repo)), new ChangedFilesSummaryTool() })
        {
            var spec = ToolSchemaProjection.ToSpec(tool);
            Assert.Contains("\"type\":\"object\"", spec.ParametersJson.Replace(" ", ""));
            Assert.False(string.IsNullOrWhiteSpace(spec.Description));
        }
    }
}
