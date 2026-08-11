using System.Net.Http;
using System.Windows.Forms;
using Anthill.Core.Configuration;
using Microsoft.Web.WebView2.WinForms;

namespace Anthill.Desktop;

/// <summary>
/// The window — open from the FIRST moment, narrating what the colony is doing, then becoming it.
/// v0.3.8.44: the first field failure was thirty blind seconds ending in silence; a window that
/// exists immediately and speaks ("Starting the colony…" → console, or the real failure with the
/// host's own logged words) cannot reproduce it.
///
/// The WebView2 user-data folder lives under %LOCALAPPDATA%\Anthill: the install directory may be
/// read-only (Program Files), and "beside the exe" is how packaged WebView2 apps break on first
/// run for non-admin users.
/// </summary>
internal sealed class ShellForm : Form
{
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f),
        Text = "Starting the colony…",
    };

    // v0.3.8.47: the tray. Minimize sends Anthill there instead of the taskbar; the icon's menu
    // reopens or quits; a balloon says where the window went the first time so nobody thinks the
    // colony died. Closing the WINDOW still quits — hijacking the X into "secretly keep running"
    // is desktop behaviour people rightly hate; the tray is where MINIMIZE goes, by choice.
    private readonly NotifyIcon _tray = new();
    private bool _trayBalloonShown;

    public ShellForm()
    {
        Text = $"Anthill v{AnthillRuntime.Version}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_status);

        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = $"Anthill v{AnthillRuntime.Version}";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Anthill", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) return;
            Hide();
            if (_trayBalloonShown) return;
            _trayBalloonShown = true;
            _tray.BalloonTipTitle = "Anthill is still running";
            _tray.BalloonTipText = "The colony keeps working in the tray. Double-click the ant to reopen.";
            _tray.ShowBalloonTip(4000);
        };
        FormClosed += (_, _) => _tray.Visible = false;

        Load += (_, _) => { BeginBoot(); BeginUpdateCheck(); };
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// v0.3.8.47 — the update check: ASK GitHub, TELL the operator, change NOTHING. One request at
    /// startup, comparing the latest release tag to this build; a newer one adds a tray menu item
    /// that opens the release page in the default browser. No download, no silent install — an
    /// update the operator did not choose is the desktop failure mode this product exists to avoid.
    /// Failure is silence: an offline machine must not see errors about a convenience.
    /// </summary>
    private void BeginUpdateCheck() =>
        new Thread(() =>
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AnthillDesktop/" + AnthillRuntime.Version);
                var json = http.GetStringAsync(
                    "https://api.github.com/repos/thexonexone/operation-anthill/releases/latest")
                    .GetAwaiter().GetResult();
                var tag = System.Text.Json.JsonDocument.Parse(json)
                    .RootElement.GetProperty("tag_name").GetString() ?? "";
                var latest = tag.TrimStart('v');
                if (string.IsNullOrWhiteSpace(latest)
                    || !Version.TryParse(latest, out var them)
                    || !Version.TryParse(AnthillRuntime.Version, out var us)
                    || them <= us) return;

                BeginInvoke(() =>
                {
                    var item = new ToolStripMenuItem($"Update available: v{latest} — open release page");
                    item.Click += (_, _) => System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(
                            $"https://github.com/thexonexone/operation-anthill/releases/tag/{tag}")
                        { UseShellExecute = true });
                    _tray.ContextMenuStrip!.Items.Insert(0, item);
                    _tray.BalloonTipTitle = $"Anthill v{latest} is available";
                    _tray.BalloonTipText = "You are on v" + AnthillRuntime.Version
                        + ". Right-click the tray icon to open the release page. Nothing installs itself.";
                    _tray.ShowBalloonTip(6000);
                });
            }
            catch (Exception error) { DesktopLog.Write("update-check: " + error.Message); }
        })
        { IsBackground = true, Name = "anthill-update-check" }.Start();

    private void BeginBoot() =>
        // The boot runs off the UI thread; the window narrates it. BeginInvoke marshals every
        // report back, so the form never touches cross-thread state.
        new Thread(() => Program.EnsureColony(
            status: s => BeginInvoke(() => _status.Text = s),
            ready: url => BeginInvoke(() => ShowConsole(url)),
            failed: why => BeginInvoke(() => ShowFailure(why))))
        { IsBackground = true, Name = "anthill-boot" }.Start();

    private async void ShowConsole(string url)
    {
        var web = new WebView2
        {
            Dock = DockStyle.Fill,
            CreationProperties = new Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Anthill", "WebView2"),
            },
        };
        try
        {
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Navigate(url);
            Controls.Remove(_status);
            Controls.Add(web);
        }
        catch (Exception error)
        {
            // The one dependency this shell has that the CLI does not: the WebView2 runtime.
            // Windows 11 and updated Windows 10 ship it; the failure names the fix for the rest.
            DesktopLog.Write("WebView2: " + error);
            web.Dispose();
            ShowFailure("The embedded browser could not start.\n\n"
                + "Install the Microsoft Edge WebView2 Runtime (aka.ms/webview2), then reopen Anthill.\n\n"
                + error.Message
                + $"\n\nThe colony itself is running — a normal browser can open {url} right now.");
        }
    }

    private void ShowFailure(string why)
    {
        _status.Font = new Font("Consolas", 9.5f);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(24);
        _status.Text = "Anthill could not start.\r\n\r\n" + why.Replace("\n", "\r\n");
    }
}
