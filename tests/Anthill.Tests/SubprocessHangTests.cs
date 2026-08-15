using System.Diagnostics;
using Anthill.Core.Projects;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE HANG, FOR REAL. v0.3.8.65, PLAN.md §1b S7 — the ladder's last open clause.
///
/// v0.3.8.59 fixed the shape (both pipes drained concurrently, the wait bounding the call, the
/// kill taking the tree) and `ShellConfinementTests` pinned the ORDER at the source level, saying
/// honestly that "proving the fix behaviourally needs a child that never exits and one that floods
/// both pipes, which is S7's own work". This is that work: a genuinely never-exiting git (a hook
/// that sleeps) proving the timeout fires, and children that write past the 64KB pipe buffer on
/// BOTH streams proving the sequential-read deadlock is gone. Real processes, real pipes — the
/// review's recurring lesson is that a scan of the source answers a question adjacent to "does it
/// survive a real hang", and only a real hang answers that one.
///
/// POSIX-only by early return: the children are shell scripts, and CI plus both operator gates run
/// them; a Windows run skips rather than fakes.
/// </summary>
[Collection("specialist-gates")]   // TimeoutMs is a static; serialize with the other togglers
public class SubprocessHangTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "anthill-hang-" + Guid.NewGuid().ToString("N")[..10]);

    public SubprocessHangTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        RepoOps.TimeoutMs = 8000;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static bool Posix => !OperatingSystem.IsWindows();

    // -------------------------------------------------------------------------------------------
    // RepoOps.Git: a git that never exits, and one that floods both pipes
    // -------------------------------------------------------------------------------------------

    private string GitRepo()
    {
        Run("git", _root, "init", "-q");
        Run("git", _root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "--allow-empty", "-q", "-m", "seed");
        return _root;
    }

    private void Hook(string name, string body)
    {
        var hooks = Path.Combine(_root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var path = Path.Combine(hooks, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        Run("chmod", _root, "+x", path);
    }

    /// <summary>
    /// The concrete v0.3.8.59 claim, finally exercised: a git that NEVER EXITS reaches the
    /// timeout, is killed with its tree, and returns in bounded time. Before the fix this call
    /// blocked forever in the first ReadToEnd — the timeout sat downstream of the hang.
    /// </summary>
    [Fact]
    public void AGitThatNeverExits_TimesOutAndReturns()
    {
        if (!Posix) return;
        GitRepo();
        Hook("pre-commit", "sleep 300");
        RepoOps.TimeoutMs = 700;

        var watch = Stopwatch.StartNew();
        var (ok, output) = RepoOps.Git(_root, "-c", "user.email=t@t", "-c", "user.name=t",
            "commit", "--allow-empty", "-m", "hangs");
        watch.Stop();

        Assert.False(ok);
        Assert.Contains("timed out", output);
        // Bounded: the timeout FIRED. Generous ceiling for a loaded CI box; the old code never returned.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"took {watch.Elapsed.TotalSeconds:F1}s — the timeout did not bound the call");
    }

    /// <summary>
    /// The deadlock case: a child that writes far past the 64KB pipe buffer on BOTH streams.
    /// The old sequential reads deadlocked here — git filling stderr while this side drained
    /// stdout, each waiting for the other, neither timed out.
    /// </summary>
    [Fact]
    public void AGitThatFloodsBothPipes_CompletesWithoutDeadlock()
    {
        if (!Posix) return;
        GitRepo();
        // ~130KB to each stream: comfortably past the pipe buffer on both sides.
        Hook("pre-commit", "i=0; while [ $i -lt 2000 ]; do echo \"stdout line $i padding padding padding padding\"; "
                         + "echo \"stderr line $i padding padding padding padding\" 1>&2; i=$((i+1)); done");
        RepoOps.TimeoutMs = 8000;

        var watch = Stopwatch.StartNew();
        var (ok, _) = RepoOps.Git(_root, "-c", "user.email=t@t", "-c", "user.name=t",
            "commit", "--allow-empty", "-m", "floods");
        watch.Stop();

        Assert.True(ok, "a both-pipe flood deadlocked or failed the commit");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8),
            $"took {watch.Elapsed.TotalSeconds:F1}s — the flood was not drained concurrently");
    }

    // -------------------------------------------------------------------------------------------
    // ShellCommandTool: the same claim through the production tool
    // -------------------------------------------------------------------------------------------

    private sealed class OpenShellGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => false;
        public bool FileWritingEnabled => false;
        public bool ShellToolEnabled => true;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => false;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".cs" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }

    /// <summary>
    /// A `find` whose stdout is far past the pipe buffer, through the real allowlisted tool.
    /// Thousands of files means ~200KB of paths; the old shape read stdout to EOF synchronously
    /// before waiting, so a flood plus any stderr contention was the deadlock. The tool must
    /// complete promptly and cap its output rather than hang or return everything.
    /// </summary>
    [Fact]
    public void AFindThatFloodsStdout_CompletesAndIsCapped()
    {
        if (!Posix) return;
        var forest = Path.Combine(_root, "forest");
        for (var d = 0; d < 40; d++)
        {
            var dir = Path.Combine(forest, $"dir-{d:D3}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 100; f++)
                File.WriteAllText(Path.Combine(dir, $"file-with-a-longish-name-{f:D4}.txt"), "x");
        }

        var tool = new ShellCommandTool(new WorkspacePathGuard(_root), new OpenShellGates());
        var watch = Stopwatch.StartNew();
        var result = tool.Run(new Dictionary<string, object?> { ["command"] = "find ." });
        watch.Stop();

        Assert.True(result.Success, result.Error ?? "");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"took {watch.Elapsed.TotalSeconds:F1}s — the stdout flood was not drained concurrently");
        // v0.3.8.59's cap: 4,000 files of paths do NOT travel whole into a ToolResult and a prompt.
        Assert.True(result.Output.Length <= 21_000,
            $"output is {result.Output.Length} chars — the cap did not hold");
    }

    private static void Run(string file, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
    }
}
