using System.Diagnostics;
using Anthill.Core.Sandbox;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// V2.10.0 validation: the sandbox NEVER touches the live checkout (writes stay inside, dispose
/// destroys everything, harvest cannot traverse out), and the bounded loop stops for every budget
/// with an explicable reason — no loop can run unbounded.
/// </summary>
public class SandboxExecutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_sbx_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string ThrowawayGitRepo()
    {
        var repo = Path.Combine(_dir, "repo"); Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "hello.txt"), "original");
        Run(repo, "init");
        Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
        Run(repo, "add ."); Run(repo, "commit -m init --no-gpg-sign");
        return repo;
    }

    private static void Run(string wd, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = wd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.WaitForExit(30_000);
    }

    // ---- Isolation -----------------------------------------------------------------------------

    [Fact]
    public void WorktreeSandbox_WritesNeverTouchTheSource_AndDisposeCleansUp()
    {
        var repo = ThrowawayGitRepo();
        string sandboxRoot;
        using (var sbx = SandboxWorkspace.Create(repo))
        {
            sandboxRoot = sbx.Root;
            Assert.Equal("worktree", sbx.Mode);
            Assert.True(File.Exists(Path.Combine(sbx.Root, "hello.txt"))); // exact HEAD state
            File.WriteAllText(Path.Combine(sbx.Root, "hello.txt"), "agent changed this");
            File.WriteAllText(Path.Combine(sbx.Root, "new.txt"), "agent artifact");
            Assert.Contains("hello.txt", sbx.ChangeSummary()); // diff visible in-sandbox
        }
        Assert.Equal("original", File.ReadAllText(Path.Combine(repo, "hello.txt"))); // live untouched
        Assert.False(File.Exists(Path.Combine(repo, "new.txt")));
        Assert.False(Directory.Exists(sandboxRoot)); // destroyed
    }

    [Fact]
    public void NonGitSource_FallsBackToBoundedCopy_StillIsolated()
    {
        var src = Path.Combine(_dir, "plain"); Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "original");
        using var sbx = SandboxWorkspace.Create(src);
        Assert.Equal("copy", sbx.Mode);
        File.WriteAllText(Path.Combine(sbx.Root, "a.txt"), "changed");
        Assert.Equal("original", File.ReadAllText(Path.Combine(src, "a.txt")));
    }

    [Fact]
    public void Harvest_CopiesOnlyRequestedArtifacts_AndBlocksTraversal()
    {
        var repo = ThrowawayGitRepo();
        var outDir = Path.Combine(_dir, "out");
        using var sbx = SandboxWorkspace.Create(repo);
        File.WriteAllText(Path.Combine(sbx.Root, "report.md"), "artifact");
        var harvested = sbx.Harvest(new[] { "report.md", "../../outside.txt" }, outDir);
        Assert.Single(harvested);
        Assert.Contains("report.md", harvested[0]);
        Assert.False(File.Exists(Path.Combine(outDir, "outside.txt"))); // traversal refused
    }

    // ---- Bounded loop --------------------------------------------------------------------------

    [Fact]
    public void Loop_CompletesWhenAgentSaysDone()
    {
        var o = BoundedAgentLoop.Run(new LoopBudget(), t => new LoopStep(Done: t == 3, ActionKey: $"step{t}", ToolCallsUsed: 1));
        Assert.True(o.Completed);
        Assert.Equal("completed", o.StopReason);
        Assert.Equal(3, o.Turns);
    }

    [Fact]
    public void Loop_StopsAtTurnBudget()
    {
        var o = BoundedAgentLoop.Run(new LoopBudget(MaxTurns: 4), t => new LoopStep(false, $"s{t}", 0));
        Assert.False(o.Completed);
        Assert.Equal("max_turns", o.StopReason);
        Assert.Equal(4, o.Turns);
    }

    [Fact]
    public void Loop_StopsAtToolBudget()
    {
        var o = BoundedAgentLoop.Run(new LoopBudget(MaxToolCalls: 5), t => new LoopStep(false, $"s{t}", ToolCallsUsed: 3));
        Assert.Equal("max_tool_calls", o.StopReason);
    }

    [Fact]
    public void Loop_StopsOnTimeout_WithInjectedClock()
    {
        var fake = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var o = BoundedAgentLoop.Run(new LoopBudget(MaxSeconds: 60),
            t => new LoopStep(false, $"s{t}", 0),
            now: () => { fake = fake.AddSeconds(45); return fake; });
        Assert.Equal("timeout", o.StopReason);
    }

    [Fact]
    public void Loop_DetectsRepeatedActions_AndStops()
    {
        var o = BoundedAgentLoop.Run(new LoopBudget(MaxRepeatedActions: 2), _ => new LoopStep(false, "same_thing", 1));
        Assert.Equal("repeated_action", o.StopReason);
        Assert.Contains("same_thing", o.Detail);
    }

    [Fact]
    public void Loop_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var o = BoundedAgentLoop.Run(new LoopBudget(), t => new LoopStep(false, $"s{t}", 0), cts.Token);
        Assert.Equal("cancelled", o.StopReason);
    }

    [Fact]
    public void Loop_StepFault_IsAStopReason_NeverAnUnboundedRetry()
    {
        var o = BoundedAgentLoop.Run(new LoopBudget(), _ => throw new InvalidOperationException("boom"));
        Assert.Equal("step_fault", o.StopReason);
        Assert.Contains("boom", o.Detail);
        Assert.Equal(1, o.Turns);
    }
}
