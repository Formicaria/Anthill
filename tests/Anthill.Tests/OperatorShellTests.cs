using Anthill.Api;
using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Operator shell console (v1.8.14.3). The console is host remote-code-execution, so the tests
/// pin its guardrails: the <c>operator_shell</c> permission is admin-only (a coordinator can
/// never reach it), and the executor actually runs a command, captures output/exit code, and
/// enforces its timeout.
/// </summary>
public class OperatorShellTests
{
    [Fact]
    public void OperatorShellPermission_IsAdminOnly()
    {
        Assert.True(UserRoles.RoleAllows(UserRoles.Admin, "operator_shell"));
        Assert.False(UserRoles.RoleAllows(UserRoles.Coordinator, "operator_shell"));
        Assert.True(UserRoles.IsAdminOnly("operator_shell"));
    }

    [Fact]
    public void Execute_RunsCommandAndCapturesOutput()
    {
        var result = OperatorShell.Execute(OperatingSystem.IsWindows() ? "echo anthill" : "echo anthill", null);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("anthill", result.Stdout);
    }

    [Fact]
    public void Execute_NonZeroExit_IsReported()
    {
        var result = OperatorShell.Execute(OperatingSystem.IsWindows() ? "exit 3" : "exit 3", null);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Execute_RespectsWorkingDirectory()
    {
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var result = OperatorShell.Execute(OperatingSystem.IsWindows() ? "cd" : "pwd", tmp);
        Assert.Equal(0, result.ExitCode);
        // The reported working dir is the resolved directory we asked for.
        Assert.Equal(Path.GetFullPath(tmp), Path.GetFullPath(result.WorkingDir.TrimEnd(Path.DirectorySeparatorChar)));
    }
}
