using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// No launch site hands `cmd /c` its command through `ArgumentList`. v0.3.8.79.
///
/// THE RULE, stated once so it stops being rediscovered. `ProcessStartInfo.ArgumentList` escapes
/// each entry by C-RUNTIME rules: an argument containing a quote is wrapped, with inner `"` emitted
/// as `\"`. That is exactly right when the child is an ordinary program whose command line the C
/// runtime parses — `git`, `docker`, an agent binary, a declared check. It is exactly wrong when the
/// child is `cmd.exe` receiving `/c <command>`, because cmd re-parses that string by its OWN rules
/// and treats `\"` as a literal backslash and quote.
///
/// TWO LIVE INSTANCES, and the second is why this guard exists rather than just the fix.
/// v0.3.8.78 found the first in `AutoApplyRunner.RunShell`: a verify command written as
/// `findstr /C:"aria-label" file` matched nothing, exited 1, and auto-apply rolled back a correctly
/// applied patch while reporting "Verify FAILED" against a tree where the change was present.
/// Sweeping for the same shape then found `OperatorShell.Execute` — the DASHBOARD SHELL — where an
/// operator typing `git commit -m "fix the thing"` had it delivered as `-m \"fix` plus two stray
/// arguments. That one lands on a human who typed a correct command and watched it come back wrong.
///
/// Both were written the same way at different times by the same reasoning, which is the definition
/// of a defect class rather than a bug. `-c` for `/bin/sh` is deliberately NOT flagged: there is no
/// command-line re-parsing on that side — the list becomes `argv` directly — so the Unix arm was
/// always correct and converting it to a string would introduce the very re-quoting this removes.
/// </summary>
public class ShellSpawnTests
{
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(SourceText.RepoRoot(), "src"), "*.cs",
                                 SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// THE GUARD. `/c` never goes through `ArgumentList`.
    ///
    /// Keyed on `/c` rather than on `cmd.exe`, because the switch is what identifies the hazard: a
    /// file may legitimately name `cmd.exe` while building a raw `Arguments` string, and the thing
    /// that must never happen is the SWITCH being listed — which is only ever done to then list the
    /// command beside it.
    /// </summary>
    [Fact]
    public void NoLaunchSite_PassesCmdsSwitchThroughArgumentList()
    {
        var offenders = new List<string>();
        var listed = new Regex(@"ArgumentList\.Add\(\s*""/c""", RegexOptions.IgnoreCase);

        foreach (var path in SourceFiles())
        {
            var source = SourceText.CodeOnly(File.ReadAllText(path));
            if (listed.IsMatch(source))
                offenders.Add(Path.GetRelativePath(SourceText.RepoRoot(), path));
        }

        Assert.True(offenders.Count == 0,
            "these files hand cmd.exe its `/c` switch through ArgumentList: "
          + string.Join(", ", offenders)
          + ".\nArgumentList escapes by C-runtime rules and cmd.exe does not follow them, so every "
          + "double quote in the command is delivered as \\\" and the command silently does "
          + "something other than what was written. Use `psi.Arguments = \"/c \" + command` so cmd "
          + "applies its own rules to a string an operator wrote for cmd. The Unix `-c` arm keeps "
          + "ArgumentList: there is no re-parsing there, and a string would reintroduce the bug.");
    }

    /// <summary>
    /// The two known shell wrappers are BOTH fixed, named individually so a regression in one is not
    /// masked by the other still being right — the guard above passes as soon as neither lists `/c`,
    /// including if one of them stopped shelling out at all.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Api", "AutoApplyRunner.cs")]
    [InlineData("Anthill.Api", "OperatorShell.cs")]
    public void TheShellWrappers_HandWindowsARawString_AndUnixAList(string project, string file)
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", project, file)));

        Assert.Contains("psi.Arguments = \"/c \" + command;", source);
        Assert.Contains("psi.ArgumentList.Add(\"-c\"); psi.ArgumentList.Add(command);", source);
    }

    /// <summary>
    /// And the sites that legitimately use `ArgumentList` still do. This is the half that stops the
    /// guard above from being "obeyed" by converting every launch site to a raw string, which would
    /// break argument passing everywhere a real program is invoked — the opposite defect, arrived at
    /// by over-applying the fix.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Core", "Projects", "RepoOps.cs")]
    [InlineData("Anthill.Modules", "Anthill.Modules.Tools", "ShellAndWebTools.cs")]
    public void SitesInvokingARealProgram_KeepTheArgumentList(params string[] path)
    {
        var full = new[] { SourceText.RepoRoot(), "src" }.Concat(path).ToArray();
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(full)));

        Assert.Contains("ArgumentList.Add", source);
    }
}
