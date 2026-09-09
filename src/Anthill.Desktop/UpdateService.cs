using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using Anthill.Core.Configuration;
using Anthill.Core.Updates;

namespace Anthill.Desktop;

/// <summary>
/// UPDATES THAT DO NOT ASK. v0.3.8.149, at the operator's instruction: "users HATE having to click
/// through another installer. They shouldn't even need to give admin approval if anthill is
/// already installed."
///
/// WHAT THE OLD PROMPT WAS DOING, because it was doing two things and only one deserved to stay.
/// It asked for PERMISSION to install — which the operator granted once by installing Anthill, and
/// re-asking every release is the ritual this release removes. And it put a human in front of a
/// downloaded executable, which was the only thing standing between a compromised release channel
/// and code running on their machine.
///
/// The first job moves to Settings: `auto_update` is a setting with three honest values, and
/// changing it is the consent. The second job does NOT disappear — with nobody watching it matters
/// more, not less — so it is now done by machine: `UpdateStaging` verifies the download against the
/// SHA-256 the release workflow published beside it, and a payload that does not match is deleted
/// unrun. Removing the click without that would have traded a real protection for convenience.
///
/// WHY NO UAC PROMPT. Because as of this release Anthill installs per-user, under
/// %LOCALAPPDATA%\Programs\Anthill (see deploy/windows/anthill-setup.iss). A program that owns its
/// own directory can replace its own files, so the update needs no permission it was not already
/// given. An older machine-wide install in Program Files cannot be replaced without elevation by
/// anyone, so it is offered the move ONCE, in words, and never nagged again.
///
/// WHY IT APPLIES AT NEXT LAUNCH. A running program cannot replace its own files on Windows, and
/// an update that interrupts a mission to relaunch is a worse interruption than the prompt it
/// replaced. So the download happens quietly while the colony works, and the installer runs at the
/// next start, before the window opens — the operator's first sign of an update is being on the
/// new version.
///
/// Failure is quiet on the LAUNCH check (an offline machine must not see errors about a
/// convenience) and spoken on the EXPLICIT check (an operator who asked deserves an answer).
/// </summary>
internal static class UpdateService
{
    private const string Releases = "https://api.github.com/repos/Formicaria/Anthill/releases/latest";
    private static int _busy;   // one check/download at a time

    /// <summary>
    /// Applies a staged update, if one is waiting and verified. Called BEFORE the window exists —
    /// the installer replaces this program's own files, so it must run while nothing is using them.
    ///
    /// Returns true when it handed over to the installer, in which case the caller must exit and
    /// let setup relaunch the new version.
    /// </summary>
    public static bool ApplyStagedIfAny()
    {
        try
        {
            var site = InstallDetector.Detect();
            if (site.Shape is not (InstallShape.WindowsInstalled or InstallShape.WindowsPortable)) return false;

            var staged = UpdateStaging.Pending(site);
            if (staged is null) return false;

            if (site.Shape == InstallShape.WindowsPortable)
            {
                var applied = UpdateApplier.ApplyArchive(site, staged.PayloadPath);
                DesktopLog.Write($"update-apply (portable): {applied.Message}");
                UpdateStaging.Clear(site);
                return false;   // files are already swapped; this process keeps starting, now on them
            }

            DesktopLog.Write($"update-apply: running {staged.Asset} silently for v{staged.Version}.");
            RunInstaller(staged.PayloadPath);
            UpdateStaging.Clear(site);
            return true;
        }
        catch (Exception error)
        {
            DesktopLog.Write("update-apply: " + error.Message);
            return false;
        }
    }

    public static void CheckAndOffer(Form owner, NotifyIcon tray, bool announceUpToDate) =>
        new Thread(() =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                // THE SETTING THE DESKTOP WAS NOT READING. v0.3.8.151.
                //
                // `.149` shipped `auto_update` (silent | notify | off) and only the HEADLESS stager
                // ever consulted it. `ShellForm` called this method unconditionally at load, so on a
                // desktop the setting decided nothing at all: `off` still downloaded and installed.
                // A control that reads as a switch and reaches nothing is the defect this repository
                // names most often, and it shipped inside the release that introduced the switch.
                //
                // An EXPLICIT check is never suppressed. `announceUpToDate` is true only when the
                // operator picked "Check for updates…" from the tray; a person who asks is not
                // governed by a preference about what happens unasked.
                var policy = AnthillRuntime.AutoUpdate;
                if (!announceUpToDate && string.Equals(policy, "off", StringComparison.OrdinalIgnoreCase))
                {
                    DesktopLog.Write("update-check: skipped — auto_update is off.");
                    return;
                }

                var site = InstallDetector.Detect();
                var (latest, tag, asset) = QueryLatest(site);
                if (latest is null)
                {
                    if (announceUpToDate) owner.BeginInvoke(() => MessageBox.Show(owner,
                        "The update service could not be reached. Anthill keeps working; try again later.",
                        "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    return;
                }

                if (UpdateVersions.Compare(latest, AnthillRuntime.Version) <= 0)
                {
                    if (announceUpToDate) owner.BeginInvoke(() => MessageBox.Show(owner,
                        $"You are on v{AnthillRuntime.Version} — the latest release.",
                        "Anthill is up to date", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    return;
                }

                // A machine-wide install cannot be replaced without elevation by anybody, so it is
                // told once, plainly, and left alone. This is the only prompt this class can raise
                // on its own, and it exists because the alternative is silently doing nothing.
                if (IsMachineWide(site))
                {
                    owner.BeginInvoke(() => OfferMigration(owner, tray, latest, tag!, asset));
                    return;
                }

                if (asset is null)
                {
                    if (announceUpToDate) owner.BeginInvoke(() => System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(
                            $"https://github.com/Formicaria/Anthill/releases/tag/{tag}") { UseShellExecute = true }));
                    return;
                }

                // NOTIFY ASKS ONCE, AND "YES" MEANS DONE — not "yes, now read a license".
                //
                // This is the operator's own sentence for what the feature should be: it opens, it
                // asks whether to update, and confirming updates it. `silent` skips the asking;
                // `off` never got here. Either way the install itself is unattended, because the
                // consent being sought is consent to UPDATE, and a wizard is not a second consent —
                // it is the same one collected again with more clicks.
                if (!announceUpToDate && string.Equals(policy, "notify", StringComparison.OrdinalIgnoreCase))
                {
                    owner.BeginInvoke(() => OfferUpdateNow(owner, site, latest, asset.Value));
                    return;
                }

                var staged = StageQuietly(site, latest, asset.Value);
                if (announceUpToDate) owner.BeginInvoke(() => MessageBox.Show(owner,
                    staged.Staged
                        ? $"Anthill v{latest} has been downloaded and checked. It installs the next time "
                          + "you start Anthill — nothing to click, and your colony's memory is kept."
                        : $"Anthill v{latest} is available, but it was not installed: {staged.Message}",
                    staged.Staged ? "Update ready" : "Update not applied",
                    MessageBoxButtons.OK, staged.Staged ? MessageBoxIcon.Information : MessageBoxIcon.Warning));
            }
            catch (Exception error) { DesktopLog.Write("update-check: " + error.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        })
        { IsBackground = true, Name = "anthill-update-check" }.Start();

    /// <summary>
    /// Downloads the asset and its published digest, verifies, and records it for next launch.
    /// Nothing here can execute anything: staging writes files and a manifest, and the only code
    /// that runs a payload is <see cref="ApplyStagedIfAny"/>, which re-verifies first.
    /// </summary>
    private static UpdateStaging.StagingResult StageQuietly(InstallSite site, string version, Asset asset)
    {
        try
        {
            if (!site.CanSelfUpdate) return UpdateStaging.StagingResult.No(site.Explanation);

            Directory.CreateDirectory(site.StagingDirectory);
            var payload = Path.Combine(site.StagingDirectory, Path.GetFileName(asset.Name));

            using (var http = NewHttp(TimeSpan.FromMinutes(10)))
            {
                // The digest FIRST. A download with nothing to check it against is a download this
                // updater will not keep, and finding that out before spending the bytes is cheaper.
                var sidecar = TryGet(http, asset.Url + ".sha256") ?? TryGet(http, asset.DigestUrl);
                var expected = UpdateStaging.ParseDigest(sidecar);
                if (expected is null)
                    return UpdateStaging.StagingResult.No(
                        "this release publishes no SHA-256 checksum for its installer, so the download "
                      + "could not be verified and was not run. Update from the release page instead.");

                using (var download = http.GetStreamAsync(asset.Url).GetAwaiter().GetResult())
                using (var file = File.Create(payload))
                    download.CopyTo(file);

                if (!UpdateStaging.VerifyOrDelete(payload, expected, out var verdict))
                    return UpdateStaging.StagingResult.No(verdict);

                UpdateStaging.Record(site, new UpdateStaging.StagedUpdate(
                    version, asset.Name, expected, payload, site.Shape, DateTime.UtcNow));
                DesktopLog.Write($"update-stage: v{version} verified ({verdict}); applies at next launch.");
                return new UpdateStaging.StagingResult(true, $"v{version} is staged and applies at next launch.");
            }
        }
        catch (Exception error)
        {
            DesktopLog.Write("update-stage: " + error);
            return UpdateStaging.StagingResult.No($"the update could not be downloaded ({error.Message}).");
        }
    }

    /// <summary>
    /// The `notify` prompt: one question, and confirming installs. v0.3.8.151.
    ///
    /// Declining is remembered for this run only, deliberately. A preference already exists for
    /// "stop asking" — `auto_update: off` — and inventing a second, invisible one here would leave
    /// an operator with a colony that had quietly stopped updating and no setting saying so.
    /// </summary>
    private static void OfferUpdateNow(Form owner, InstallSite site, string latest, Asset asset)
    {
        var choice = MessageBox.Show(owner,
            $"Anthill v{latest} is available — you are on v{AnthillRuntime.Version}.\n\n"
          + "Update now? The download is checked against the release's published checksum and "
          + "installs without any further questions. Your colony's memory and settings are kept.",
            "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (choice != DialogResult.Yes) return;

        var staged = StageQuietly(site, latest, asset);
        if (!staged.Staged)
        {
            MessageBox.Show(owner, staged.Message, "Update not applied",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A portable copy is swapped in place at the next launch; there is no installer to run and
        // nothing to hand over to, so saying "restart to finish" is the honest end of this path.
        if (site.Shape == InstallShape.WindowsPortable)
        {
            MessageBox.Show(owner,
                $"Anthill v{latest} has been downloaded and checked. Restart Anthill to finish.",
                "Update ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UpdateStaging.Clear(site);
        RunInstaller(staged.Update!.PayloadPath);
        Application.Exit();
    }

    /// <summary>The one prompt left: a machine-wide install asking to become a per-user one.</summary>
    private static void OfferMigration(Form owner, NotifyIcon tray, string latest, string tag, Asset? asset)
    {
        var choice = MessageBox.Show(owner,
            $"Anthill v{latest} is available — you are on v{AnthillRuntime.Version}.\n\n"
          + "This copy was installed for all users, under Program Files, which Windows will not let "
          + "Anthill update on its own.\n\n"
          + "Move Anthill to a per-user install? Windows will ask for administrator approval once, "
          + "to remove the old copy. After that, updates install themselves silently and you will "
          + "not see this again. Your colony's memory and settings are kept either way.",
            "Update available",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (choice != DialogResult.Yes)
        {
            var item = new ToolStripMenuItem($"Update to v{latest}…");
            item.Click += (_, _) => OfferMigration(owner, tray, latest, tag, asset);
            if (!tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == item.Text))
                tray.ContextMenuStrip.Items.Insert(0, item);
            return;
        }

        if (asset is null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/Formicaria/Anthill/releases/tag/{tag}") { UseShellExecute = true });
            return;
        }

        // Elevation happens here, visibly, once — the setup package asks for it because removing
        // the Program Files copy needs it. It is not requested silently and never will be.
        var site = InstallDetector.Detect();
        var staged = StageQuietly(site with { CanSelfUpdate = true }, latest, asset.Value);
        if (!staged.Staged)
        {
            MessageBox.Show(owner, staged.Message, "Update not applied",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // SILENTLY, AND INTO THE PER-USER LOCATION — v0.3.8.151, and `.150` did neither.
        //
        // This line was a bare `Process.Start(payload)` with no arguments, so answering "yes" to the
        // one prompt this class raises launched the full Inno wizard: a license page, a destination
        // page, a tasks page. The operator reported it as "I thought silent updates were added and
        // they did not work", and they were right — `ApplyStagedIfAny` passed the silent switches
        // and this path passed nothing. Two launch sites for one installer, agreeing about nothing,
        // which is why there is now exactly one method that starts it.
        //
        // `/DIR` is what makes the migration a migration. Inno's `UsePreviousAppDir` is on by
        // default, so a copy already under Program Files would reinstall itself right back there —
        // the prompt would keep appearing every release, having promised it would not. Naming the
        // per-user directory is the whole point of answering yes.
        RunInstaller(staged.Update!.PayloadPath, PerUserProgramDirectory);
        Application.Exit();
    }

    /// <summary>Where a per-user install lives; the `[Setup] DefaultDirName` in `anthill-setup.iss`.</summary>
    private static string PerUserProgramDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", "Anthill");

    /// <summary>
    /// THE ONLY PLACE THIS PROGRAM STARTS AN INSTALLER. v0.3.8.151.
    ///
    /// There were two, and they disagreed: the apply path passed the silent switches and the
    /// migration path passed none, so the release that announced silent updates still showed a
    /// license page to anyone whose copy sat under Program Files. `DesktopShellTests` asked only
    /// that `/VERYSILENT` appear SOMEWHERE in this file, and it did — in the other method.
    ///
    /// The switches, and why each is here:
    ///   /VERYSILENT        no wizard at all, which is the entire promise of the feature.
    ///   /SUPPRESSMSGBOXES  no dialog can block a run nobody is watching.
    ///   /NORESTART         the machine is not ours to reboot.
    ///   /NOCANCEL          nothing half-applied.
    ///
    /// The restart-applications switch is deliberately GONE. `anthill-setup.iss` sets
    /// `RestartApplications=no`, so it asked the package for something the package had turned off —
    /// a statement of intent contradicted by the file it was addressed to, which is the same defect
    /// in miniature as the two launch sites. It is named in words rather than spelled here, because
    /// `DesktopShellTests` asserts the switch is absent and a comment quoting it would read as its
    /// presence — the mistake this whole method exists to stop being possible.
    /// </summary>
    private static void RunInstaller(string payloadPath, string? installDirectory = null)
    {
        var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL";
        if (!string.IsNullOrWhiteSpace(installDirectory))
            arguments += $" /DIR=\"{installDirectory}\"";

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(payloadPath)
        {
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    /// <summary>An install under Program Files: replaceable only with elevation, by anyone.</summary>
    private static bool IsMachineWide(InstallSite site)
    {
        if (site.Shape != InstallShape.WindowsInstalled) return false;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && UpdateApplier.IsInside(site.ProgramDirectory, root)) return true;
        }
        return false;
    }

    private readonly record struct Asset(string Name, string Url, string DigestUrl);

    private static (string? Latest, string? Tag, Asset? Asset) QueryLatest(InstallSite site)
    {
        try
        {
            using var http = NewHttp();
            var json = http.GetStringAsync(Releases).GetAwaiter().GetResult();
            var root = JsonDocument.Parse(json).RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latest)) return (null, null, null);

            // The asset this SHAPE consumes, named exactly — an installed copy takes the installer,
            // a portable copy takes the zip. Matching by the name the release workflow writes means
            // a release that is missing our asset produces no update rather than the wrong one.
            var wanted = site.AssetFor(latest);
            Asset? found = null;
            string? digestUrl = null;
            if (wanted is not null && root.TryGetProperty("assets", out var assets))
            {
                string? url = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                        url = asset.GetProperty("browser_download_url").GetString();
                    else if (string.Equals(name, wanted + ".sha256", StringComparison.OrdinalIgnoreCase))
                        digestUrl = asset.GetProperty("browser_download_url").GetString();
                }
                if (url is not null) found = new Asset(wanted, url, digestUrl ?? url + ".sha256");
            }
            return (latest, tag, found);
        }
        catch (Exception error)
        {
            DesktopLog.Write("update-query: " + error.Message);
            return (null, null, null);
        }
    }

    private static string? TryGet(HttpClient http, string url)
    {
        try
        {
            var response = http.GetAsync(url).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch { return null; }
    }

    private static HttpClient NewHttp(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnthillDesktop/" + AnthillRuntime.Version);
        return http;
    }
}
