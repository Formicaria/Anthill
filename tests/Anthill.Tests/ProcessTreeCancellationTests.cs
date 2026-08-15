using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Stop means stop: no subprocess outlives the thing that started it. v0.3.8.57.
///
/// THE DEFECT THIS FOUND, in five places at once. Every git call in the core did this:
///
///     process.WaitForExit(60_000);
///     return (process.ExitCode == 0, ...);
///
/// `WaitForExit(ms)` returns FALSE on timeout and execution carried straight on, so git and every
/// child it had spawned kept running with nobody holding a handle to them. And `ExitCode` throws on
/// a process that has not exited — so the timeout surfaced as an exception message from the catch
/// block rather than as a timeout, which is why nobody had noticed: the call "failed" with a
/// plausible-looking error while the process it was waiting for was still going.
///
/// Five sites: SandboxWorkspace, WorkspaceTools, MissionWorkspaceManager, WorkspaceChangeSet and
/// PatchSetMaterializer. The last one differed only in being honest about the result — `&&`
/// short-circuited past ExitCode — and abandoned the process just the same.
///
/// The sweep below is the durable half. The five fixes are one release; a detector that fails when
/// a sixth site appears is what stops this being found again in a year.
/// </summary>
public class ProcessTreeCancellationTests
{
    /// <summary>
    /// Production files that launch a child process and wait for it. Discovered, not listed — a
    /// hand-maintained list is exactly what let five sites share one defect unnoticed.
    /// </summary>
    private static IEnumerable<(string Path, string Code)> WaitingCallers()
    {
        var root = Path.Combine(SourceText.RepoRoot(), "src");

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var code = SourceText.CodeOnly(File.ReadAllText(path));

            // Any ARGUMENT form, not just a literal. The first draft matched `WaitForExit(\d` and
            // therefore missed `WaitForExit(TimeoutMs)` and `WaitForExit(timeoutSeconds * 1000)` —
            // four of the twelve real sites, including the shell tool and the agent CLI, which are
            // the two most likely to leave something expensive running. A detector with a hole in it
            // reports a clean sweep, which is worse than no sweep.
            //
            // Parameterless `WaitForExit()` is deliberately excluded: it blocks until the process
            // exits, so it has no timeout path to get wrong.
            if (Regex.IsMatch(code, @"WaitForExit\(\s*[^)\s]")) yield return (path, code);
        }
    }

    /// <summary>
    /// Every site that waits with a timeout must kill the whole tree when that timeout expires.
    ///
    /// The assertion is deliberately about the FILE rather than the exact statement: several of these
    /// wrap the wait in a helper, and pinning a shape would make this a formatting check. What cannot
    /// be true is a file that bounds a wait and contains no tree kill at all — that is a site which,
    /// on timeout, walks away from a running process.
    /// </summary>
    [Fact]
    public void EverySiteThatWaitsWithATimeout_KillsTheWholeProcessTree()
    {
        var abandoning = new List<string>();

        foreach (var (path, code) in WaitingCallers())
        {
            // Both spellings of the same overload: `Kill(entireProcessTree: true)` and `Kill(true)`.
            var killsTree = Regex.IsMatch(code, @"Kill\(\s*(entireProcessTree\s*:\s*)?true\s*\)");
            if (!killsTree) abandoning.Add(Path.GetFileName(path));
        }

        Assert.True(abandoning.Count == 0,
            "these files wait on a child process with a timeout and never kill its tree, so on timeout "
          + $"the process and its children survive: {string.Join(", ", abandoning)}. "
          + "Kill(entireProcessTree: true) on the timeout path — a bare Kill() leaves grandchildren, "
          + "which for `dotnet test` or an agent CLI is most of what was actually running.");
    }

    /// <summary>
    /// The five git sites handle the timeout BEFORE reading `ExitCode`.
    ///
    /// Asserted by name rather than by sweep, and that is a correction. The first draft swept for
    /// "a discarded bounded wait in a file that also mentions ExitCode" and flagged `OperatorShell`
    /// and `AutoApplyRunner` — both of which are CORRECT. Their match was the short drain wait after
    /// `Kill`, which is exactly the right thing to do and looks identical to the defect at file
    /// granularity. A check that fails on correct code teaches people to weaken it.
    ///
    /// So this pins the shape at the sites that had the defect: the wait's result decides the path,
    /// and `ExitCode` is only reached once the process is known to have exited.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Core/Sandbox/SandboxWorkspace.cs")]
    [InlineData("Anthill.Core/Tools/WorkspaceTools.cs")]
    [InlineData("Anthill.Core/Workspaces/MissionWorkspaceManager.cs")]
    [InlineData("Anthill.Core/Workspaces/WorkspaceChangeSet.cs")]
    [InlineData("Anthill.Core/Verification/PatchSetMaterializer.cs")]
    public void TheGitSites_DecideOnTheWaitBeforeReadingExitCode(string relativePath)
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Matches(@"if\s*\(\s*!\s*\w+\.WaitForExit\(", code);
        Assert.Matches(@"Kill\(\s*entireProcessTree\s*:\s*true\s*\)", code);
    }

    /// <summary>
    /// The sweep is looking at something. A discovery-based guard that silently matches nothing is
    /// the quietest way for a check to stop checking — and this one would then pass forever.
    /// </summary>
    [Fact]
    public void TheSweep_ActuallyFindsTheProcessLaunchingSites()
    {
        var found = WaitingCallers().Select(w => Path.GetFileName(w.Path)).ToList();

        Assert.True(found.Count >= 10,
            $"only {found.Count} bounded-wait site(s) found, and there were twelve; the pattern has "
          + "stopped matching the code "
          + "and this guard is no longer guarding anything.");

        // The five that shared the defect, named so a rename cannot quietly drop one from the sweep.
        foreach (var expected in new[]
                 {
                     "SandboxWorkspace.cs", "WorkspaceTools.cs", "MissionWorkspaceManager.cs",
                     "WorkspaceChangeSet.cs", "PatchSetMaterializer.cs",
                 })
            Assert.Contains(expected, found);
    }
}
