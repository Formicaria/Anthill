using Anthill.Api;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A QUICK ACTION MUST TARGET SOMETHING THAT EXISTS. v0.3.8.124.
///
/// `ShellPlatform`'s own header states the rule it exists to keep: "an action is only shown when the
/// environment it targets can actually run it." It was written because the console offered four
/// systemd commands on every platform, so on Windows every one of them was a "command not found"
/// presented as a button that would work.
///
/// THE FILE THEN BROKE ITS OWN RULE, for seventy-five releases. The Windows set targeted a Windows
/// service named `Anthill` — `Get-Service Anthill`, `Get-EventLog -Source Anthill`,
/// `Restart-Service Anthill`. No such service exists. Nothing in this repository registers one
/// (`docs/DEPLOYMENT.md` still lists the Windows install script as unbuilt), and on Windows an
/// operator runs `AnthillDesktop.exe`. All three actions failed.
///
/// The name was never verified. It was written as a plausible convention — lowercase unit on Linux,
/// PascalCase service on Windows — beside a Linux set that was real, and it READ as symmetric
/// rather than as a guess. That is why this guard asserts against the artifacts the repository
/// actually builds rather than against a string: a second plausible-looking name would pass a test
/// that only checked spelling.
/// </summary>
public class ShellQuickActionTests
{
    /// <summary>
    /// The Windows actions target the desktop PROCESS, and name the executable this repository
    /// actually produces.
    ///
    /// Checked against `Anthill.Desktop.csproj`'s `AssemblyName` rather than against the literal
    /// "AnthillDesktop", so renaming the binary fails here instead of leaving three quick actions
    /// pointed at a process that no longer exists under that name — which is precisely the failure
    /// being fixed.
    /// </summary>
    [Fact]
    public void TheWindowsActions_TargetTheDesktopProcessThisRepositoryBuilds()
    {
        var csproj = File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Desktop", "Anthill.Desktop.csproj"));
        var assembly = System.Text.RegularExpressions.Regex
            .Match(csproj, @"<AssemblyName>([^<]+)</AssemblyName>").Groups[1].Value;

        Assert.False(string.IsNullOrWhiteSpace(assembly),
            "Anthill.Desktop.csproj declares no <AssemblyName>, so this guard has nothing to check "
          + "the quick actions against.");

        var windows = ShellPlatform.ActionsFor("windows");
        Assert.NotEmpty(windows);

        foreach (var action in windows.Where(a => a.Id is "service_status" or "restart_service"))
            Assert.Contains(assembly, action.Command, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND NOTHING ON WINDOWS ASKS A SERVICE MANAGER ABOUT A SERVICE NOBODY REGISTERS.
    ///
    /// The positive assertion above would pass on a command that named the process AND still called
    /// `Restart-Service`. This is the half that pins the actual defect closed: while no install
    /// script in this tree creates a Windows service, no quick action may talk to one.
    ///
    /// If a Windows service IS added later, this test is the right thing to change — and changing it
    /// means someone has to look at all three commands, which is the point.
    /// </summary>
    [Fact]
    public void NoWindowsAction_TalksToAServiceThisRepositoryNeverRegisters()
    {
        // The three directories that could plausibly install one, rather than a walk of the whole
        // tree: an `EnumerateFiles(repo, …, AllDirectories)` here would descend into .git, obj and
        // bin, which is slow, permission-dependent, and would eventually read a build artifact
        // containing the string and fail for a reason that has nothing to do with the rule.
        var repo = SourceText.RepoRoot();
        var registers = new[] { "deploy", "scripts", ".github" }
            .Select(d => Path.Combine(repo, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".iss", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Any(f => File.ReadAllText(f).Contains("New-Service", StringComparison.OrdinalIgnoreCase));

        Assert.False(registers,
            "Something in this repository now registers a Windows service. The quick actions were "
          + "changed to target the AnthillDesktop process precisely because nothing did — revisit "
          + "ShellPlatform's Windows set and this guard together.");

        foreach (var action in ShellPlatform.ActionsFor("windows"))
        {
            Assert.DoesNotContain("Restart-Service", action.Command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Get-Service", action.Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The Linux set is untouched and still targets the systemd unit — the platform where a service
    /// genuinely exists. Asserted so the fix above cannot be read as "services were wrong
    /// everywhere": they were wrong on exactly one platform, and that platform is the one with no
    /// installer.
    /// </summary>
    [Fact]
    public void TheLinuxActions_StillTargetTheSystemdUnit()
    {
        var linux = ShellPlatform.ActionsFor("linux");
        var restart = Assert.Single(linux, a => a.Id == "restart_service");

        Assert.Equal("systemctl restart anthill", restart.Command);
        Assert.True(restart.Danger, "restarting the colony is disruptive and must be confirmed");
    }

    /// <summary>
    /// An environment this colony cannot speak for gets NO actions, so the console shows a clear
    /// "nothing here" rather than another platform's buttons. The original rule, still held.
    /// </summary>
    [Fact]
    public void AnUnknownPlatform_IsOfferedNothing()
    {
        Assert.Empty(ShellPlatform.ActionsFor("unknown"));
        Assert.Empty(ShellPlatform.ActionsFor("plan9"));
    }
}
