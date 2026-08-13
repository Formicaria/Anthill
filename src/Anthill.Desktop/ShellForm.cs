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

    // v0.3.8.52 (field report: "the title bar is white") — the one part of the window WinForms
    // cannot paint is the non-client caption, and its default is light regardless of the form's
    // own colors. DWM owns it; these attributes ask DWM for the brand. 20 is
    // DWMWA_USE_IMMERSIVE_DARK_MODE (19 on pre-20H1 builds of Windows 10 — both are set, the
    // wrong one is ignored); 35/36 are Windows 11's caption/text color, COLORREF byte order
    // (0x00BBGGRR), which on Windows 10 return E_INVALIDARG and leave dark mode's dark gray —
    // still dark, still fine. Failure anywhere leaves a light title bar over a working colony,
    // which is why every call's result is deliberately discarded.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1;
            _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
            var caption = 0x00170E09;   // BrandBg   #090E17 as 0x00BBGGRR
            var text    = 0x00FFF2EA;   // BrandText #EAF2FF as 0x00BBGGRR
            _ = DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        }
        catch { /* a light title bar must never keep the window from opening */ }
    }

    public ShellForm()
    {
        Text = $"Anthill v{AnthillRuntime.Version}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BrandBg;

        // The Formicaria mark — the .ico the csproj embeds beside ApplicationIcon. ApplicationIcon
        // brands only the FILE (Explorer, Add/Remove Programs); the window, taskbar and tray read
        // Form.Icon, so the same .ico is loaded here, BEFORE the tray line below reads it. A failed
        // load falls through to the same SystemIcons fallback as before — a missing icon must
        // never keep the window from opening.
        try
        {
            using var iconStream = typeof(ShellForm).Assembly
                .GetManifestResourceStream("Anthill.Desktop.anthill.ico");
            if (iconStream is not null) Icon = new Icon(iconStream);
        }
        catch { /* unbranded but alive */ }

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
            // v0.3.8.52 — a target=_blank link (the Formicaria mark, an agent's Docs button) must
            // open in the operator's REAL browser. WebView2's default is an unbranded popup shell
            // with no tabs, no extensions and no password manager — a second UI, which the desktop
            // app exists to not be. Handled synchronously, so no popup ever flashes.
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch (Exception ex) { DesktopLog.Write("open external: " + ex.Message); }
            };
            // v0.3.8.52 — the REAL OS folder picker, for the console's Browse button. A web page
            // cannot learn an absolute path from the browser's own picker (by design), but the
            // desktop shell is a native app and can simply ask: the page posts "pick-folder",
            // the host shows FolderBrowserDialog, and the chosen absolute path comes back as one
            // JSON message. Browser shapes never send this — they fall back to the server-backed
            // directory browser (/fs/dirs), which browses the machine the workdir actually lives on.
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    if (e.TryGetWebMessageAsString() != "pick-folder") return;
                    using var dlg = new FolderBrowserDialog
                    {
                        Description = "Choose the project's working directory",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true,
                    };
                    var picked = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : "";
                    web.CoreWebView2.PostWebMessageAsJson(
                        System.Text.Json.JsonSerializer.Serialize(
                            new { type = "picked-folder", path = picked }));
                }
                catch (Exception ex) { DesktopLog.Write("pick-folder: " + ex.Message); }
            };
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
