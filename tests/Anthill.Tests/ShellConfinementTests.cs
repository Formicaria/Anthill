using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md §1b S2 — shell tool confinement. v0.3.8.59.
///
/// THE FINDING. "With `shell_tool_enabled`, confinement is weaker still: `cat`, `find` and `grep`
/// accept unrestricted absolute paths, and setting the working directory does not sandbox a process."
///
/// That last clause is the whole of it, and it is easy to read past. `ProcessStartInfo.WorkingDirectory`
/// decides where RELATIVE paths resolve. It has no bearing on absolute ones and confines nothing —
/// so `cat /etc/passwd`, `grep -r secret /` and `find / -name '*.key'` ran exactly as written, and
/// the allowlist of nine commands was carrying a load it was never designed for. An allowlist says
/// WHICH PROGRAM may run. It says nothing about what that program is pointed at.
///
/// WHY THE FIX CHECKS ARGUMENTS RATHER THAN ADDING A REAL SANDBOX. A real sandbox — a container, a
/// seccomp profile, a job object — is the right answer and is not available inside this process on
/// three platforms. Argument containment is what CAN be enforced here, so it is enforced here.
///
/// v0.3.8.110 — AND `dotnet run` NO LONGER EXECUTES ARBITRARY CODE BY DESIGN. This paragraph used
/// to end by recording that residual: "`dotnet` remains on the allowlist and `dotnet run` executes
/// arbitrary code by design." It was true, it was governed only by the enable flag, and it made
/// `dotnet` the one entry on a nine-command READ allowlist that could run whatever the workspace
/// contained. The subcommand is now allowlisted — reporting verbs only — and the reasoning is in
/// `ShellCommandTool.DotnetSubcommandRefusal`. A real sandbox is still the right answer for the
/// rest and is still not available in-process on three platforms.
///
/// These tests DO NOT run any of those commands. They assert the refusal happens before a process
/// starts, which is the property that matters and the only one that is safe to test.
/// </summary>
public class ShellConfinementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "anthill-shell-" + Guid.NewGuid().ToString("N")[..10]);

    public ShellConfinementTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "ok.txt"), "inside");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>
    /// Gates for this tool only, with the shell OPEN.
    ///
    /// Opening it matters: with `ShellToolEnabled` false every test below would pass, refused by the
    /// enable flag before containment was consulted at all. A security test that passes because the
    /// feature is off is the purest form of the adjacent-question defect — it proves the gate works
    /// and says nothing about the thing behind it.
    /// </summary>
    private sealed class OpenShellGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => false;
        public bool FileWritingEnabled => false;
        public bool ShellToolEnabled => true;
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => false;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".cs", ".md" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }

    private ShellCommandTool Tool() =>
        new(new WorkspacePathGuard(_root), new OpenShellGates());

    private ToolResult Run(string command) =>
        Tool().Run(new Dictionary<string, object?> { ["command"] = command });

    // -------------------------------------------------------------------------------------------
    // Absolute paths outside the workspace
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FINDING, verbatim, for each of the three commands the review named.
    ///
    /// The refusal must be an AuthorizationFailure rather than a validation error: this is a denied
    /// request, not a malformed one, and the failure taxonomy is what the medic reads to decide
    /// whether a repair is worth attempting. Classifying a refusal as a bad argument invites a
    /// bounded repair cycle spent rewording something that was never going to be allowed.
    /// </summary>
    [Theory]
    [InlineData("cat /etc/passwd")]
    [InlineData("grep -r secret /")]
    [InlineData("find / -name *.key")]
    [InlineData("ls /var/log")]
    public void AnAbsolutePathOutsideTheWorkspace_IsRefused(string command)
    {
        var result = Run(command);

        Assert.False(result.Success, $"`{command}` was not refused. WorkingDirectory does not sandbox a "
                              + "process — an absolute path ignores it entirely.");
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
        Assert.Contains("outside the workspace", result.Error ?? "");
    }

    /// <summary>
    /// And traversal out of the workspace, which is the same escape spelled relatively. Without this
    /// the fix would only stop the obvious form and the interesting one would still work.
    /// </summary>
    [Theory]
    [InlineData("cat ../../../etc/passwd")]
    [InlineData("ls sub/../../..")]
    public void RelativeTraversalOutOfTheWorkspace_IsRefused(string command) =>
        Assert.False(Run(command).Success);

    // -------------------------------------------------------------------------------------------
    // …and the tool still works
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A path INSIDE the workspace is not refused. Proved from this side because a containment fix
    /// that also breaks legitimate use gets disabled, and a disabled guard protects nothing.
    ///
    /// Asserting on the REFUSAL rather than on success: whether `ls` exists and what it prints is the
    /// machine's business, and a test that shells out for real is a test that fails on somebody's
    /// laptop for reasons that have nothing to do with confinement.
    /// </summary>
    [Theory]
    [InlineData("ls sub")]
    [InlineData("cat sub/ok.txt")]
    [InlineData("grep -r inside .")]
    public void APathInsideTheWorkspace_IsNotRefusedByContainment(string command)
    {
        var result = Run(command);

        if (!result.Success)
            Assert.DoesNotContain("outside the workspace", result.Error ?? "");
    }

    /// <summary>
    /// A bare token is not a path and must not be treated as one. `grep -r secret .` searches for
    /// the WORD "secret"; a containment check that read every argument as a location would refuse
    /// the search term and make the tool useless in a way whose cause reads as a security bug.
    /// </summary>
    [Theory]
    [InlineData("echo hello")]
    [InlineData("grep -r secret .")]
    [InlineData("dotnet --version")]
    public void ABareTokenIsNotAPath_AndIsNotRefusedAsOne(string command)
    {
        var result = Run(command);

        if (!result.Success)
            Assert.DoesNotContain("outside the workspace", result.Error ?? "");
    }

    /// <summary>
    /// v0.3.8.110 — `dotnet` MAY REPORT AND MAY NOT RUN.
    ///
    /// The residual this closes is the one the class summary above used to record. Every other
    /// entry on the allowlist can only read; `dotnet` is an interpreter, and the allowlist matched
    /// the PROGRAM alone, so three separate roads to executing workspace-supplied code passed every
    /// check in the tool.
    ///
    /// The refusal is asserted by its REASON, not merely by failure — `Run` returns unsuccessfully
    /// for a disabled tool, a missing binary and a refused argument alike, and a test that accepted
    /// any of those would go green on a machine with no SDK installed while asserting nothing.
    /// </summary>
    [Theory]
    [InlineData("dotnet run")]
    [InlineData("dotnet exec payload.dll")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("dotnet tool run something")]
    [InlineData("dotnet payload.dll")]
    [InlineData("dotnet fsi script.fsx")]
    [InlineData("dotnet")]
    public void DotnetVerbsThatExecute_AreRefusedBeforeAProcessStarts(string command)
    {
        var result = Run(command);

        Assert.False(result.Success, $"'{command}' was not refused — dotnet can execute what the "
                                   + "workspace supplied, and the allowlist admits it only to report.");
        Assert.Contains("refused", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And the reporting verbs still pass the subcommand gate. Without this the change
    /// above would be indistinguishable from removing `dotnet` from the allowlist entirely, which
    /// is a different decision and was not the one taken.</summary>
    [Theory]
    [InlineData("dotnet --version")]
    [InlineData("dotnet --info")]
    [InlineData("dotnet --list-sdks")]
    public void DotnetReportingVerbs_PassTheSubcommandGate(string command)
    {
        var result = Run(command);

        if (!result.Success)
            Assert.DoesNotContain("is refused", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path hidden on the right of `--flag=value` is still a path. Checking the whole token finds
    /// no separator at the front and waves it through — the argument parser and the security check
    /// disagreeing about where the value starts.
    /// </summary>
    [Fact]
    public void APathInsideAFlagValue_IsStillChecked() =>
        Assert.False(Run("grep --file=/etc/passwd x .").Success);

    // -------------------------------------------------------------------------------------------
    // Flags that turn reading into writing
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// `find . -exec rm {} ;` passes every containment check above — the path IS the workspace. The
    /// damage is done by the flag, and the allowlist admits `find` as a way to LOOK.
    ///
    /// This is not in the review. It is the same question the review asked, applied to the arguments
    /// rather than the paths: the allowlist controls which program runs, and `find -exec` runs a
    /// different program than the one that was allowlisted.
    /// </summary>
    [Theory]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("find . -delete")]
    [InlineData("find . -execdir sh {} ;")]
    [InlineData("find . -fprintf out.txt %p")]
    public void FlagsThatExecuteOrDelete_AreRefused(string command)
    {
        var result = Run(command);

        Assert.False(result.Success, $"`{command}` was allowed. The path is inside the workspace, so no "
                              + "containment check fires — the flag is what runs the other program.");
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
    }

    // -------------------------------------------------------------------------------------------
    // The mission's tree, not the live checkout
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The command runs in the EFFECTIVE root. It used to run in `_guard.Root` — the configured root
    /// — so inside a mission, whose workspace is a disposable tree, every shell command was pointed
    /// at the live checkout the mission exists to stay out of.
    ///
    /// Source-level, because standing up a mission workspace scope to observe a child process's cwd
    /// would test the harness more than the fix.
    /// </summary>
    [Fact]
    public void TheCommandRunsInTheEffectiveRoot_NotTheConfiguredOne()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Modules", "Anthill.Modules.Tools", "ShellAndWebTools.cs")));

        Assert.Contains("WorkingDirectory = _guard.EffectiveRoot", source);
        Assert.DoesNotContain("WorkingDirectory = _guard.Root", source);
    }

    // -------------------------------------------------------------------------------------------
    // The timeout that could not fire (S7, unavoidably in the same method)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The streams are drained CONCURRENTLY and the wait bounds the whole thing.
    ///
    /// The old shape was `ReadToEnd()` on stdout, then stderr, then `WaitForExit(30_000)` — and a
    /// process that never exits blocks forever in the first read, so the timeout meant to bound it
    /// was downstream of the thing that hangs. Reading sequentially also deadlocks when the child
    /// fills its stderr pipe while this side drains stdout.
    ///
    /// Asserted at the source level and this is the honest limit of it: proving the fix behaviourally
    /// needs a child that never exits and one that floods both pipes, which is S7's own work. What is
    /// pinned here is the ORDER — that no synchronous read stands between starting the process and
    /// waiting for it — because that ordering is the entire defect.
    /// </summary>
    [Fact]
    public void TheProcessIsWaitedOnBeforeItsOutputIsRead()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Modules", "Anthill.Modules.Tools", "ShellAndWebTools.cs")));

        var start = source.IndexOf("Process.Start(psi)", StringComparison.Ordinal);
        var wait = source.IndexOf("WaitForExit(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && wait > start, "the shell tool no longer waits on its process");

        var betweenStartAndWait = source[start..wait];
        Assert.DoesNotContain("ReadToEnd()", betweenStartAndWait);
        Assert.Contains("ReadToEndAsync()", betweenStartAndWait);

        // And the kill takes the tree, so a timed-out command leaves no orphan holding the pipe.
        Assert.Contains("Kill(entireProcessTree: true)", source);
    }
}
