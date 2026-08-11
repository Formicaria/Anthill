using System.Windows.Forms;
using Anthill.Core.Configuration;
using Microsoft.Web.WebView2.WinForms;

namespace Anthill.Desktop;

/// <summary>
/// The window. Nothing more — the console inside it is the product, and every control this form
/// could grow (navigation, status, settings) already exists in there with tests over it.
///
/// The WebView2 user-data folder lives under %LOCALAPPDATA%\Anthill: the install directory may be
/// read-only (Program Files), and the default of "beside the exe" is how packaged WebView2 apps
/// break on first run for non-admin users.
/// </summary>
internal sealed class ShellForm : Form
{
    private readonly WebView2 _web = new();
    private readonly string _url;

    public ShellForm(string url)
    {
        _url = url;
        Text = $"Anthill v{AnthillRuntime.Version}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _web.Dock = DockStyle.Fill;
        _web.CreationProperties = new Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Anthill", "WebView2"),
        };
        Controls.Add(_web);
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            _web.CoreWebView2.Navigate(_url);
        }
        catch (Exception error)
        {
            // The one dependency this shell has that the CLI does not: the WebView2 runtime.
            // Windows 11 and updated Windows 10 ship it; the failure names the fix for the rest.
            Controls.Remove(_web);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "The embedded browser could not start.\n\n"
                     + "Install the Microsoft Edge WebView2 Runtime (aka.ms/webview2), then reopen Anthill.\n\n"
                     + error.Message,
            });
        }
    }
}
