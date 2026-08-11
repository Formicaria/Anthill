using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.43 — the Windows desktop shell, pinned as WIRING rather than behaviour. The shell only
/// RUNS on Windows and this suite must pass everywhere, so what is tested is the arrangement that
/// keeps it honest: one window over the one console, the same composition root as the CLI, and a
/// build that cannot rot invisibly despite living outside Anthill.sln (the Anthill.UI rule — a
/// packaging artifact of the console does not tax every cross-platform build).
///
/// The lesson this encodes is v3.1.1's: present in the tree is not the same as built, and built
/// is not the same as reachable. Each assertion below names the mechanism that keeps one of those
/// gaps closed.
/// </summary>
public class DesktopShellTests
{
    private static string Root() => SourceText.RepoRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    [Fact]
    public void TheShell_IsOneWindowOverTheOneConsole()
    {
        var program = Read("src", "Anthill.Desktop", "Program.cs");

        // The same entry point the CLI uses — same composition, same modules, same security.
        Assert.Contains("ApiHost.Run(", program);
        // It renders the console, not a second UI.
        Assert.Contains("/ui", program);
        // Boot-or-attach: an already-serving colony is attached to, never doubled.
        Assert.Contains("IsAnthillServing", program);
        // One shell per machine; a second launch exits quietly.
        Assert.Contains("Mutex", program);
    }

    /// <summary>
    /// v0.3.8.44 — the first field failure, pinned so it cannot return. The report was "click and
    /// nothing happens": the runtime's default bind (0.0.0.0) was refused by the security posture,
    /// the refusal printed to a console a WinExe does not have, and the process waited blind then
    /// died faceless. Three mechanisms now stand where those three failures were.
    /// </summary>
    [Fact]
    public void TheFirstFieldFailure_CannotReturn()
    {
        var program = Read("src", "Anthill.Desktop", "Program.cs");

        // A local window binds loopback by default — the desktop twin of `--host 127.0.0.1`.
        // Config/env still win: only an UNSET host is defaulted.
        Assert.Contains("ANTHILL_HOST", program);
        Assert.Contains("127.0.0.1", program);

        // Everything the host prints survives to be read, and failures quote it.
        Assert.Contains("DesktopLog.Attach()", program);
        Assert.Contains("DesktopLog.Tail()", program);

        // No blind wait: a host that exits or crashes is reported NOW, with its exit code.
        Assert.Contains("if (!api.IsAlive)", program);

        // No silent death at any frame.
        Assert.Contains("FATAL", program);

        // And the window exists from the first moment, narrating the boot.
        var form = Read("src", "Anthill.Desktop", "ShellForm.cs");
        Assert.Contains("Starting the colony", form);
        Assert.Contains("ShowFailure", form);

        // The packaging half: WebView2's native loader cannot load from inside a single-file
        // bundle; self-extraction is what makes the published exe capable of opening a window.
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>",
            Read("src", "Anthill.Desktop", "Anthill.Desktop.csproj"));
    }

    [Fact]
    public void TheProbe_ChecksForAnthill_NotMerelyForAServer()
    {
        var probe = Read("src", "Anthill.Desktop", "ColonyProbe.cs");
        // A bare TCP/HTTP success would attach the shell to whatever owns the port.
        Assert.Contains("ANTHILL", probe);
    }

    [Fact]
    public void TheCsproj_MirrorsTheCliCompositionRoot_AndCrossTargets()
    {
        var csproj = Read("src", "Anthill.Desktop", "Anthill.Desktop.csproj");

        // Modules are wired in the exe, never referenced by core — the CLI's exact rule.
        foreach (var module in new[] { "Anthill.Modules.Reasoning", "Anthill.Modules.Homelab", "Anthill.Modules.Tools" })
            Assert.Contains(module, csproj);
        Assert.Contains("Anthill.Api", csproj);

        // A Linux host can compile and publish it; running it still needs Windows.
        Assert.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", csproj);
        Assert.Contains("net9.0-windows", csproj);
    }

    /// <summary>
    /// Outside the solution, so ONLY these call sites keep it building. Delete either and the
    /// shell can break with every suite green — which is the exact rot this test exists to stop.
    /// </summary>
    [Fact]
    public void TheBuild_CannotRotInvisibly()
    {
        Assert.Contains("src/Anthill.Desktop/Anthill.Desktop.csproj",
            Read(".github", "workflows", "ci.yml"));
        Assert.Contains("src/Anthill.Desktop/Anthill.Desktop.csproj",
            Read("scripts", "validate.ps1"));
        // v0.3.8.44: and the RELEASE ships it — the Windows archive carries the desktop app
        // beside the server binary, so "download Anthill for Windows" means both shapes.
        Assert.Contains("src/Anthill.Desktop/Anthill.Desktop.csproj",
            Read(".github", "workflows", "release.yml"));
        // And deliberately not in the solution — if it moves in, this test is the reminder to
        // remove the now-redundant explicit builds rather than run three.
        Assert.DoesNotContain("Anthill.Desktop", Read("Anthill.sln"));
    }

    /// <summary>The window claims a writable WebView2 profile — the install dir may be
    /// Program Files, and "beside the exe" is how packaged WebView2 apps break for non-admins.</summary>
    [Fact]
    public void TheWebViewProfile_LivesInLocalAppData()
    {
        var form = Read("src", "Anthill.Desktop", "ShellForm.cs");
        Assert.Contains("LocalApplicationData", form);
        Assert.Contains("UserDataFolder", form);
        // And a failed embedded browser names its fix instead of rendering a blank window.
        Assert.Contains("WebView2 Runtime", form);
    }
}
