using Anthill.Core.Tools;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A check runs in the tree it is judging. v0.3.8.70 — pending item #44, "bind Tester and Soldier to
/// the exact patched revision", found while surveying qualification scenario 3.
///
/// THE DEFECT. `RunAllowlistedCheckTool` chose its working directory with
/// <c>manifest.IsEmpty ? _workdir : manifest.Root</c>, which reads as "no workspace in scope, use the
/// configured directory" and does not mean that. The manifest is empty when the workspace ADAPTERS
/// DETECT NO PROJECT TYPE at the scoped root — a statement about the directory's contents, not about
/// whether a directory is in scope.
///
/// So the sequence was: `ExecutionService` materializes the patched revision, enters a
/// `MissionWorkspaceScope` bound to it, dispatches the tester inside that scope, and stamps
/// <c>task.RanRevisionId = revision.RevisionId</c> — unconditionally. Meanwhile the check ran against
/// `_workdir`, which is `AnthillRuntime.AllowedWorkspaceRoot`: the ORIGINAL, unpatched tree. The
/// record said the tester judged the revision; the process had run somewhere else.
///
/// WHY IT SURVIVED. On this repository it is invisible — ANTHILL is .NET, every materialized revision
/// contains `.csproj` files, the adapters detect it, the manifest is non-empty and `manifest.Root`
/// happens to be the scoped root. It bites on project types the adapters do not detect, and in every
/// test whose workspace is a bare temp directory. Which is also why it could not be caught by
/// asserting on ANTHILL's own tree: a test that ran here would pass either way.
///
/// SO THIS TEST DOES NOT ASSERT ON CODE. It builds two directories that differ only in which one
/// holds a marker file, scopes the mission to the one that has it, and runs a check that succeeds
/// only in a directory containing that marker. The check's exit code is the answer to "where did you
/// run" — evidence, not a reading of the branch.
/// </summary>
public class CheckWorkingDirectoryTests : IDisposable
{
    private const string Marker = "revision-marker.txt";
    private const string CheckId = "test_marker_present";

    private readonly string _base;
    private readonly string _original;   // stands in for AllowedWorkspaceRoot — no marker
    private readonly string _revision;   // the materialized patched tree — has the marker

    public CheckWorkingDirectoryTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "anthill-checkdir-" + Guid.NewGuid().ToString("N")[..10]);
        _original = Path.Combine(_base, "original");
        _revision = Path.Combine(_base, "revision");
        Directory.CreateDirectory(_original);
        Directory.CreateDirectory(_revision);

        // NEITHER directory holds a project the adapters recognise — no .csproj, no package.json.
        // That is the condition under which the defect fires, and it is the ordinary condition for
        // a docs-only patch, which is exactly what qualification scenario 3 is about.
        File.WriteAllText(Path.Combine(_original, "README.txt"), "the unpatched tree\n");
        File.WriteAllText(Path.Combine(_revision, "README.txt"), "the patched tree\n");
        File.WriteAllText(Path.Combine(_revision, Marker), "only the revision has this\n");

        // A declared check, from the catalog, with fixed arguments — the security property the
        // runner's header states is untouched: the command never comes from model output or task
        // text. It succeeds iff the marker file is readable from the process's WORKING DIRECTORY,
        // so its exit code reports where it ran.
        CheckCatalog.Register(OperatingSystem.IsWindows()
            ? new CheckDefinition(CheckId, "cmd.exe", $"/c type {Marker}", 30, true, "test: marker present in cwd")
            : new CheckDefinition(CheckId, "cat", Marker, 30, true, "test: marker present in cwd"));
    }

    public void Dispose()
    {
        // v0.3.8.70 gave the catalog a way back, so this fixture does not leave its check in a
        // process-global allowlist the way the four older ones do.
        CheckCatalog.Unregister(CheckId);
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static MissionWorkspace RevisionAt(string root) => new()
    {
        Id = "revision-test",
        MissionId = "m-checkdir",
        Root = root,
        SourceRoot = root,
        State = WorkspaceState.Active,
        RevisionId = "rev-1",
    };

    // -----------------------------------------------------------------------------------------------
    // The regression
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT, as a behaviour. Scoped to the revision, configured with the original: the check
    /// must find the marker, and it can only do that by running in the revision.
    ///
    /// This failed before the fix — the marker is not in `_original` and `cat`/`type` exited
    /// non-zero — and the failure is the whole point: the tester was reporting on the unpatched tree
    /// while the task row said otherwise.
    /// </summary>
    [Fact]
    public void AScopedMission_RunsItsCheckInTheRevision_EvenWhenNoProjectTypeIsDetected()
    {
        var tool = new RunAllowlistedCheckTool(_original);

        using var scope = MissionWorkspaceScope.Enter(RevisionAt(_revision));
        var result = tool.Run(new Dictionary<string, object?> { ["check_id"] = CheckId });

        Assert.True(result.Success,
            "the check ran outside the mission's materialized revision. ExecutionService stamps "
          + "RanRevisionId on the task regardless, so this is evidence about the UNPATCHED tree "
          + "recorded as evidence about the patched one. Error: " + result.Error);
        Assert.Contains("exit_code=0", result.Output);
    }

    /// <summary>
    /// And proved from the other side, so the fix cannot be "always succeed". Scoped to the tree
    /// WITHOUT the marker, the same check must fail — otherwise the assertion above would pass for a
    /// runner that ignored its working directory entirely.
    /// </summary>
    [Fact]
    public void TheSameCheck_FailsWhenTheScopedTreeLacksTheMarker()
    {
        var tool = new RunAllowlistedCheckTool(_revision);   // deliberately inverted

        using var scope = MissionWorkspaceScope.Enter(RevisionAt(_original));
        var result = tool.Run(new Dictionary<string, object?> { ["check_id"] = CheckId });

        Assert.False(result.Success,
            "the check passed while scoped to a tree with no marker, so it is reading the CONFIGURED "
          + "directory rather than the scoped one — the same defect with the arguments swapped.");
    }

    /// <summary>
    /// UNSCOPED IS UNCHANGED. Outside any mission the configured workdir is still the answer, which
    /// is the CLI's and the API's ordinary case. A fix that only worked inside a scope would have
    /// moved the defect rather than closed it.
    /// </summary>
    [Fact]
    public void OutsideAnyMissionScope_TheConfiguredDirectoryIsStillUsed()
    {
        var result = new RunAllowlistedCheckTool(_revision)
            .Run(new Dictionary<string, object?> { ["check_id"] = CheckId });

        Assert.True(result.Success, result.Error);
    }

    // -----------------------------------------------------------------------------------------------
    // The catalog can be put back the way it was found
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A registered check can be removed, and a BUILT-IN cannot.
    ///
    /// The asymmetry is the point and it is the rule `ToolRegistry.Unregister` already carries:
    /// registration composes what the colony may execute, so a call able to strip `dotnet_build` out
    /// of the allowlist would be a second, unaudited way to decide what gets verified. Removal
    /// exists for the ids tests and operators add, and for nothing else.
    /// </summary>
    [Fact]
    public void ARegisteredCheck_CanBeRemoved_AndABuiltInCannot()
    {
        CheckCatalog.Register(new CheckDefinition("test_temporary", "cmd", "", 5, true, "temporary"));
        Assert.NotNull(CheckCatalog.Get("test_temporary"));

        Assert.True(CheckCatalog.Unregister("test_temporary"));
        Assert.Null(CheckCatalog.Get("test_temporary"));

        Assert.False(CheckCatalog.Unregister("dotnet_build"),
            "a built-in check was removable, so the catalog is a second way to decide what the "
          + "colony verifies — the surface ToolRegistry.Unregister refuses for the same reason.");
        Assert.NotNull(CheckCatalog.Get("dotnet_build"));
    }
}
