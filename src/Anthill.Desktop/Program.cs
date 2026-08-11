using Anthill.Api;
using Anthill.Core.Configuration;

namespace Anthill.Desktop;

/// <summary>
/// v0.3.8.43 — the Windows desktop shell. One rule shapes everything here: this is a WINDOW onto
/// the colony, not a second colony and not a second console. The API host the CLI runs with
/// <c>--api</c> runs in-process on loopback, the embedded WebView renders the same `/ui` every
/// browser gets, and every feature the console gains is gained here for free.
///
/// Boot-or-attach: if an Anthill is already serving the configured port (a server install, or a
/// second launch racing the mutex), the shell ATTACHES to it rather than booting a rival colony
/// over the same database. Two Queens on one SQLite file is exactly the situation the worker-claim
/// machinery exists to survive, and surviving it is not a reason to create it.
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

        ApplicationConfiguration.Initialize();

        // Resolve the configured host/port before deciding whether to boot. Initialize() is what
        // ApiHost.Run() itself calls first; calling it here as well keeps the port read and the
        // boot decision consistent with what the host will actually bind.
        AnthillRuntime.Initialize();
        var listenHost = AnthillRuntime.ApiHost is "0.0.0.0" or "::" ? "127.0.0.1" : AnthillRuntime.ApiHost;
        var baseUrl = $"http://{listenHost}:{AnthillRuntime.ApiPort}";

        string? bootError = null;
        if (!ColonyProbe.IsAnthillServing(baseUrl))
        {
            // The same entry point the CLI uses — same composition, same modules, same security
            // posture. Background thread: the host's lifetime is the window's lifetime, and the
            // process exit that follows the last form closing is the same teardown Ctrl+C gives
            // the CLI.
            var api = new Thread(() =>
            {
                try { ApiHost.Run(args); }
                catch (Exception error) { bootError = error.Message; }
            })
            { IsBackground = true, Name = "anthill-api" };
            api.Start();

            if (!ColonyProbe.WaitUntilServing(baseUrl, TimeSpan.FromSeconds(30)))
            {
                // Truthful failure, not a blank window: say what was tried and what came back.
                System.Windows.Forms.MessageBox.Show(
                    $"The colony did not come up on {baseUrl} within 30 seconds."
                    + (bootError is null ? "" : $"\n\n{bootError}")
                    + "\n\nIf another service owns the port, set api_port in config.json.",
                    "Anthill could not start",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return 1;
            }
        }

        System.Windows.Forms.Application.Run(new ShellForm($"{baseUrl}/ui"));
        return 0;
    }
}
