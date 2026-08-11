using Anthill.Api;
using Anthill.Core.Configuration;

namespace Anthill.Desktop;

/// <summary>
/// v0.3.8.43 — the Windows desktop shell. One rule shapes everything here: this is a WINDOW onto
/// the colony, not a second colony and not a second console. The API host the CLI runs with
/// <c>--api</c> runs in-process on loopback, the embedded WebView renders the same `/ui` every
/// browser gets, and every feature the console gains is gained here for free.
///
/// v0.3.8.44 — rebuilt from the first field failure, which taught three lessons at once:
///
/// 1. A WinExe has no console, so anything that PRINTS its refusal dies silently. The runtime's
///    default bind is 0.0.0.0, the security posture rightly refuses a public bind without a
///    token, and the refusal went to a console that does not exist. The shell is a local window:
///    it now binds loopback by default (config/env still win when set), and Console out/err are
///    redirected to a log file so every printed word survives to be read.
/// 2. Thirty blind seconds is indistinguishable from a hang. The window now opens IMMEDIATELY
///    with a starting state, and either navigates when the colony answers or shows the real
///    failure — including the host's own words from the log — in the window itself.
/// 3. Nothing in this process may die without a face. Main is guarded; any escape becomes a
///    message box and a log line, never a silent exit.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One shell per machine. A second launch exits quietly — the OS taskbar already has the
        // first one, and two windows over one colony answer no question an operator asks.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AnthillDesktopShell", out var firstInstance);
        if (!firstInstance) return 0;

        try
        {
            DesktopLog.Attach();   // Console.Out/Error → %LOCALAPPDATA%\Anthill\desktop.log
            ApplicationConfiguration.Initialize();
            System.Windows.Forms.Application.Run(new ShellForm());
            return 0;
        }
        catch (Exception error)
        {
            // The lesson of the first field failure, applied at the outermost frame: no silent death.
            DesktopLog.Write("FATAL: " + error);
            System.Windows.Forms.MessageBox.Show(
                error.Message + "\n\nDetails: " + DesktopLog.PathHint,
                "Anthill could not start",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// Boot-or-attach, reported through callbacks so the window can narrate it. If an Anthill is
    /// already serving the configured port (a server install, or a racing second launch), the
    /// shell ATTACHES rather than booting a rival colony over the same database.
    /// </summary>
    public static void EnsureColony(Action<string> status, Action<string> ready, Action<string> failed)
    {
        try
        {
            // The shell is a local window, so an UNSET host means loopback here — the desktop
            // twin of the quick start's `--api --host 127.0.0.1`. The runtime's env override is
            // the supported channel for exactly this, and an explicit config/env choice wins.
            if (Environment.GetEnvironmentVariable("ANTHILL_HOST") is null)
                Environment.SetEnvironmentVariable("ANTHILL_HOST", "127.0.0.1");

            AnthillRuntime.Initialize();
            var listenHost = AnthillRuntime.ApiHost is "0.0.0.0" or "::" ? "127.0.0.1" : AnthillRuntime.ApiHost;
            var baseUrl = $"http://{listenHost}:{AnthillRuntime.ApiPort}";

            if (ColonyProbe.IsAnthillServing(baseUrl))
            {
                status($"Attached to the colony already running at {baseUrl}.");
                ready($"{baseUrl}/ui");
                return;
            }

            status("Starting the colony…");
            var exitCode = -1; Exception? crashed = null;
            var api = new Thread(() =>
            {
                try { exitCode = ApiHost.Run(Array.Empty<string>()); }
                catch (Exception error) { crashed = error; }
            })
            { IsBackground = true, Name = "anthill-api" };
            api.Start();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (ColonyProbe.IsAnthillServing(baseUrl)) { ready($"{baseUrl}/ui"); return; }
                // A host that already gave up is reported NOW, with its own words — not after a
                // blind wait. The refusal it printed is in the log this shell redirected.
                if (crashed is not null) { failed(crashed.Message + "\n\n" + DesktopLog.Tail()); return; }
                if (!api.IsAlive) { failed($"The colony host exited (code {exitCode}).\n\n" + DesktopLog.Tail()); return; }
                Thread.Sleep(250);
            }
            failed($"The colony did not answer on {baseUrl} within 30 seconds.\n\n" + DesktopLog.Tail());
        }
        catch (Exception error)
        {
            DesktopLog.Write("EnsureColony: " + error);
            failed(error.Message + "\n\n" + DesktopLog.Tail());
        }
    }
}
