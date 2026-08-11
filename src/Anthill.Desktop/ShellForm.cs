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

    public ShellForm()
    {
        Text = $"Anthill v{AnthillRuntime.Version}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_status);
        Load += (_, _) => BeginBoot();
    }

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
