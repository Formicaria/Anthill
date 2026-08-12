using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using Anthill.Core.Configuration;

namespace Anthill.Desktop;

/// <summary>
/// Field report (installer batch) — the desktop app was "not easily updateable": the old check
/// only tucked a link into the tray menu. This asks GitHub once, and when a newer release exists
/// it PROMPTS: update now, and the setup program is downloaded and run with the operator watching;
/// or later, and the offer waits in the tray. Nothing ever downloads or installs without a yes.
///
/// The installer preserves the colony: data lives under %LOCALAPPDATA%\Anthill (Program.cs), which
/// no install, update, or uninstall touches — so the prompt can honestly say "your colony's memory
/// is kept."
///
/// Failure is quiet on the LAUNCH check (an offline machine must not see errors about a
/// convenience) and spoken on the EXPLICIT check (an operator who asked deserves an answer).
/// </summary>
internal static class UpdateService
{
    private const string Releases = "https://api.github.com/repos/Formicaria/Anthill/releases/latest";
    private static int _busy;   // one check/download at a time

    public static void CheckAndOffer(Form owner, NotifyIcon tray, bool announceUpToDate) =>
        new Thread(() =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var (latest, tag, assetUrl, assetName) = QueryLatest();
                if (latest is null)
                {
                    if (announceUpToDate) owner.BeginInvoke(() => MessageBox.Show(owner,
                        "The update service could not be reached. Anthill keeps working; try again later.",
                        "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    return;
                }

                if (!Version.TryParse(AnthillRuntime.Version, out var mine) || latest <= mine)
                {
                    if (announceUpToDate) owner.BeginInvoke(() => MessageBox.Show(owner,
                        $"You are on v{AnthillRuntime.Version} — the latest release.",
                        "Anthill is up to date", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    return;
                }

                owner.BeginInvoke(() => Offer(owner, tray, latest, tag!, assetUrl, assetName));
            }
            catch (Exception error) { DesktopLog.Write("update-check: " + error.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        })
        { IsBackground = true, Name = "anthill-update-check" }.Start();

    private static (Version? Latest, string? Tag, string? AssetUrl, string? AssetName) QueryLatest()
    {
        try
        {
            using var http = NewHttp();
            var json = http.GetStringAsync(Releases).GetAwaiter().GetResult();
            var root = JsonDocument.Parse(json).RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var latest)) return (null, null, null, null);

            // The installer asset, by shape. Falls back to null — the offer then opens the release
            // page instead of pretending a download exists.
            string? assetUrl = null, assetName = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("anthill-setup-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = name;
                        break;
                    }
                }
            return (latest, tag, assetUrl, assetName);
        }
        catch (Exception error)
        {
            DesktopLog.Write("update-query: " + error.Message);
            return (null, null, null, null);
        }
    }

    private static void Offer(Form owner, NotifyIcon tray, Version latest, string tag,
        string? assetUrl, string? assetName)
    {
        var choice = MessageBox.Show(owner,
            $"Anthill v{latest} is available — you are on v{AnthillRuntime.Version}.\n\n"
            + (assetUrl is not null
                ? "Update now? The installer downloads and runs; your colony's memory and settings are kept."
                : "Open the release page? (This release carries no installer asset, so the update is manual.)"),
            "Update available",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (choice != DialogResult.Yes)
        {
            // Later: the offer waits in the tray instead of nagging again this session.
            var item = new ToolStripMenuItem($"Update to v{latest}…");
            item.Click += (_, _) => Offer(owner, tray, latest, tag, assetUrl, assetName);
            if (!tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == item.Text))
                tray.ContextMenuStrip.Items.Insert(0, item);
            return;
        }

        if (assetUrl is null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/Formicaria/Anthill/releases/tag/{tag}") { UseShellExecute = true });
            return;
        }

        DownloadAndRun(owner, assetUrl, assetName!);
    }

    private static void DownloadAndRun(Form owner, string assetUrl, string assetName) =>
        new Thread(() =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), assetName);
                using (var http = NewHttp(TimeSpan.FromMinutes(5)))
                using (var download = http.GetStreamAsync(assetUrl).GetAwaiter().GetResult())
                using (var file = File.Create(path))
                    download.CopyTo(file);
                DesktopLog.Write($"Update downloaded to {path}; handing over to the installer.");

                owner.BeginInvoke(() =>
                {
                    // The installer replaces the app directory, so this instance steps aside. The
                    // colony's data is elsewhere (%LOCALAPPDATA%\Anthill) and unaffected.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    { UseShellExecute = true });
                    Application.Exit();
                });
            }
            catch (Exception error)
            {
                DesktopLog.Write("update-download: " + error);
                owner.BeginInvoke(() => MessageBox.Show(owner,
                    "The update could not be downloaded: " + error.Message
                    + "\n\nAnthill keeps running on the current version.",
                    "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        })
        { IsBackground = true, Name = "anthill-update-download" }.Start();

    private static HttpClient NewHttp(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnthillDesktop/" + AnthillRuntime.Version);
        return http;
    }
}
