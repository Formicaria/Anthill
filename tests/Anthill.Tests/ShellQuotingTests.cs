using Anthill.Api;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A verify command survives the shell with its quotes intact. v0.3.8.79 (PLAN.md §2 R2).
///
/// THE DEFECT, and it is the worst-shaped one this line has produced. `AutoApplyRunner.RunShell`
/// passed the whole command through `ProcessStartInfo.ArgumentList`, which .NET escapes by C-RUNTIME
/// rules — an argument containing a quote is wrapped, with inner `"` written as `\"`. That is right
/// for a program whose command line the C runtime parses. `cmd.exe` is not such a program: it has
/// its own rules and treats `\"` as a literal backslash and a quote. So
///
///     findstr /C:"aria-label" static\app.js
///
/// reached findstr as `/C:\"aria-label\"`, matched nothing, and exited 1. Auto-apply rolled back a
/// correctly applied patch and reported **"Verify FAILED"** — against a tree where the change was
/// present and correct.
///
/// WHY THAT SHAPE IS THE WORST ONE. It does not look like a configuration bug. The colony says
/// verification refused the change, so an operator debugs their patch, their build, their tests —
/// everything except the quoting of a command they wrote correctly. And it is silent for anyone
/// whose verify command happens to have no quotes, which is why it survived: the only verify command
/// any test used was scenario 3's `type docs\COLONY-NOTE.md`.
///
/// THE SECOND INSTANCE had no test at all. The auto-commit passes
/// `git -c user.name="ANTHILL Auto-Apply" … -m "{msg}"`, four quoted arguments, through the same
/// path — so a commit message with a space was being mangled wherever that path ran.
///
/// WHY THE TWO ARMS DIFFER, since a symmetric fix is the obvious instinct and would be wrong. On
/// Windows the child re-parses one command-line STRING, so `psi.Arguments` hands cmd the text an
/// operator wrote and cmd applies its own rules to it. On Unix there is no re-parsing: `ArgumentList`
/// becomes `argv` directly, `sh -c <command>` already received the command intact, and converting
/// that arm to a string would introduce exactly the re-quoting this removes.
/// </summary>
[Collection("specialist-gates")]   // AllowedWorkspaceRoot and the verify command are process-wide
public class ShellQuotingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _rootWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly string _verifyWas = AnthillRuntime.AutonomyAutoApplyVerifyCmd;

    public ShellQuotingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-shell-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        // A phrase WITH A SPACE, which is the only reason quoting is needed at all. A single word
        // would pass under the broken implementation too and prove nothing.
        File.WriteAllText(Path.Combine(_dir, "marker.txt"), "the colony roster\n");
        AnthillRuntime.AllowedWorkspaceRoot = _dir;
    }

    public void Dispose()
    {
        AnthillRuntime.AllowedWorkspaceRoot = _rootWas;
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = _verifyWas;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// THE ASSERTION THIS FILE EXISTS FOR. A quoted search phrase reaches the tool as one argument.
    ///
    /// Written per shell rather than once, because the bug is a property of how each shell parses
    /// what it is handed — a single cross-platform spelling would be asserting about .NET rather
    /// than about the thing that broke.
    /// </summary>
    [Fact]
    public void AQuotedPhrase_ReachesTheShellIntact()
    {
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = OperatingSystem.IsWindows()
            ? @"findstr /C:""the colony roster"" marker.txt"
            : @"grep -q ""the colony roster"" marker.txt";

        var result = AutoApplyRunner.RunVerify(_dir);

        Assert.True(result.Green,
            $"the quoted phrase did not survive the shell (exit {result.ExitCode}). The file contains "
          + "it, so a non-zero exit means the quotes were re-escaped on the way to the child and the "
          + "tool searched for something else. Output:\n" + result.Output);
    }

    /// <summary>
    /// And the negative, so the test above cannot pass by matching anything at all: a phrase that is
    /// NOT in the file must fail. Without this, an implementation that dropped the search term
    /// entirely — and so matched every line — would look green.
    /// </summary>
    [Fact]
    public void AQuotedPhraseThatIsAbsent_StillFails()
    {
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = OperatingSystem.IsWindows()
            ? @"findstr /C:""not in this file"" marker.txt"
            : @"grep -q ""not in this file"" marker.txt";

        var result = AutoApplyRunner.RunVerify(_dir);

        Assert.False(result.Green, "a phrase absent from the file was reported as verified");
    }

    /// <summary>An unquoted command still works — the fix must not require quoting.</summary>
    [Fact]
    public void AnUnquotedCommand_IsUnaffected()
    {
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = OperatingSystem.IsWindows()
            ? @"findstr colony marker.txt" : "grep -q colony marker.txt";

        Assert.True(AutoApplyRunner.RunVerify(_dir).Green);
    }

    /// <summary>
    /// The shell operators an operator relies on still work. `psi.Arguments` hands the string to cmd,
    /// which is what makes `&amp;&amp;` a shell operator rather than a literal — and the built-in
    /// default verify command is `dotnet build &amp;&amp; dotnet test`, so this is not hypothetical.
    /// </summary>
    [Fact]
    public void ShellOperators_StillCompose()
    {
        AnthillRuntime.AutonomyAutoApplyVerifyCmd = OperatingSystem.IsWindows()
            ? @"findstr colony marker.txt && findstr roster marker.txt"
            : "grep -q colony marker.txt && grep -q roster marker.txt";

        Assert.True(AutoApplyRunner.RunVerify(_dir).Green);
    }

    /// <summary>
    /// THE OPERATOR'S OWN SHELL, which the sweep for this defect class found second.
    ///
    /// `OperatorShell.Execute` backs the dashboard's shell box and had the identical implementation,
    /// so an admin typing `git commit -m "fix the thing"` had it delivered as `-m \"fix` plus two
    /// stray arguments. Worse than the auto-apply instance in one respect: a human typed a correct
    /// command, watched it come back wrong, and nothing in the output explained why.
    ///
    /// Asserted behaviourally rather than on source because this is a different call path with its
    /// own timeout, redirection and result type — `ShellSpawnTests` covers the shared rule.
    /// </summary>
    [Fact]
    public void TheOperatorShell_AlsoKeepsQuotedArgumentsIntact()
    {
        var result = OperatorShell.Execute(
            OperatingSystem.IsWindows()
                ? @"findstr /C:""the colony roster"" marker.txt"
                : @"grep -q ""the colony roster"" marker.txt",
            _dir);

        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0,
            $"the dashboard shell mangled a quoted argument (exit {result.ExitCode}).\n"
          + $"  stdout: {result.Stdout}\n  stderr: {result.Stderr}");
    }

    /// <summary>
    /// The arms are asymmetric ON PURPOSE, asserted on source so a later "tidy-up" to one spelling
    /// reintroduces the defect loudly instead of silently. A symmetric `ArgumentList` is what broke;
    /// a symmetric `Arguments` would break the Unix side the other way.
    /// </summary>
    [Fact]
    public void TheWindowsArmTakesAString_AndTheUnixArmTakesAList()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "AutoApplyRunner.cs")));

        Assert.Contains("if (isWindows) psi.Arguments = \"/c \" + command;", source);
        Assert.Contains("psi.ArgumentList.Add(\"-c\"); psi.ArgumentList.Add(command);", source);
        Assert.DoesNotContain("psi.ArgumentList.Add(\"/c\")", source);
    }
}
