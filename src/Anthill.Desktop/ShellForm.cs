using System.Net.Http;
using System.Windows.Forms;
using Anthill.Core.Configuration;
using Microsoft.Web.WebView2.WinForms;

namespace Anthill.Desktop;

/// <summary>
/// The window — open from the FIRST moment, narrating what the colony is doing, then becoming it.
/// v0.3.8.44: the first field failure was thirty blind seconds ending in silence; a window that
/// exists immediately and speaks cannot reproduce it.
///
/// Field report (installer batch): the loading screen wears the brand — the console's own dark
/// background, the ANTHILL wordmark in its weight and letterforms, the version in the queen's
/// amber — instead of a gray system label. Same palette as src/Anthill.UI/index.html's CSS vars;
/// change it there, change it here, or the two products drift apart at the front door.
///
/// The WebView2 user-data folder lives under %LOCALAPPDATA%\Anthill: the install directory may be
/// read-only (Program Files), and "beside the exe" is how packaged WebView2 apps break on first
/// run for non-admin users.
/// </summary>
internal sealed class ShellForm : Form
{
    // The console's palette (index.html: --bg, --anthill-text, --queen, --muted).
    private static readonly Color BrandBg = Color.FromArgb(9, 14, 23);        // --bg #090e17
    private static readonly Color BrandText = Color.FromArgb(234, 242, 255);  // --anthill-text #eaf2ff
    private static readonly Color BrandQueen = Color.FromArgb(251, 191, 36);  // --queen #fbbf24
    private static readonly Color BrandMuted = Color.FromArgb(122, 143, 173);

    private readonly Panel _loading = new() { Dock = DockStyle.Fill, BackColor = BrandBg };
    private readonly Label _wordmark = new()
    {
        Dock = DockStyle.None,
        AutoSize = true,
        // The wordmark: Segoe UI at the console's weight, letterspaced the way CSS does it with
        // .14em — WinForms has no tracking, so the spacing is IN the text. Honest approximation.
        Font = new Font("Segoe UI", 30f, FontStyle.Bold),
        ForeColor = BrandText,
        BackColor = BrandBg,
        Text = "A N T H I L L",
    };
    private readonly Label _version = new()
    {
        AutoSize = true,
        Font = new Font("Consolas", 11f),
        ForeColor = BrandQueen,
        BackColor = BrandBg,
        Text = $"v{AnthillRuntime.Version}",
    };
    private readonly Label _status = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 10.5f),
        ForeColor = BrandMuted,
        BackColor = BrandBg,
        Text = "Starting the colony…",
    };

    private readonly NotifyIcon _tray = new();
    private bool _trayBalloonShown;

    public ShellForm()
    {
        Text = $"Anthill v{AnthillRuntime.Version}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BrandBg;

        _loading.Controls.Add(_wordmark);
        _loading.Controls.Add(_version);
        _loading.Controls.Add(_status);
        _loading.Resize += (_, _) => CenterLoading();
        Controls.Add(_loading);
        CenterLoading();

        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = $"Anthill v{AnthillRuntime.Version}";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Anthill", null, (_, _) => RestoreFromTray());
        // Field report: the explicit update button. The launch check runs anyway; this is the
        // operator asking NOW and getting an honest answer either way.
        menu.Items.Add("Check for updates…", null, (_, _) => UpdateService.CheckAndOffer(this, _tray, announceUpToDate: true));
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

        // Field report: the update check PROMPTS on launch now (announceUpToDate: false — an
        // up-to-date machine hears nothing at startup; noise about a convenience is the old
        // failure shape). The colony boot starts in parallel.
        Load += (_, _) => { BeginBoot(); UpdateService.CheckAndOffer(this, _tray, announceUpToDate: false); };
    }

    private void CenterLoading()
    {
        var cx = _loading.Width / 2;
        var cy = _loading.Height / 2;
        _wordmark.Location = new Point(cx - _wordmark.Width / 2, cy - _wordmark.Height);
        _version.Location = new Point(cx - _version.Width / 2, cy + 6);
        _status.Location = new Point(cx - _status.Width / 2, cy + 40);
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        CenterLoading();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void BeginBoot() =>
        new Thread(() => Program.EnsureColony(
            status: s => BeginInvoke(() => SetStatus(s)),
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
            Controls.Remove(_loading);
            Controls.Add(web);
        }
        catch (Exception error)
        {
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
        // Failure keeps the brand background but switches to a readable log presentation.
        _wordmark.ForeColor = BrandMuted;
        _status.Font = new Font("Consolas", 9.5f);
        _status.MaximumSize = new Size(_loading.Width - 96, 0);
        _status.ForeColor = BrandText;
        SetStatus("Anthill could not start.\r\n\r\n" + why.Replace("\n", "\r\n"));
    }
}
