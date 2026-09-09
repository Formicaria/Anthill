using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Updates;

namespace Anthill.Api;

/// <summary>
/// THE HALF OF A SILENT UPDATE THAT HAPPENS WHILE THE COLONY WORKS. v0.3.8.146.
///
/// The desktop shell stages its own updates because it owns a window and a tray; a headless
/// colony — the LXC service, a portable copy run from a terminal — has neither, and it is exactly
/// the shape an operator is least likely to be sitting in front of. This is its equivalent: on a
/// timer, ask GitHub, and if a newer release exists, download it, verify it against the published
/// SHA-256, and leave it staged. Nothing is installed here. The swap happens at the next start,
/// by the systemd pre-start hook or the portable launch path, for the mechanical reason that a
/// running program cannot replace its own files.
///
/// WHAT MAKES THIS SAFE TO RUN UNATTENDED, stated once so a later reader does not have to
/// reconstruct it:
///
///   · it only ever WRITES a file into `.anthill/updates` and a manifest beside it — this class
///     starts no process and extracts no archive, so a bug here cannot execute anything;
///   · the payload is deleted unless it hashes to the digest the release published;
///   · it runs only when `auto_update` is `silent` and only where the shape can actually apply
///     one, so a container or an unidentified install downloads nothing at all;
///   · one attempt at a time, and one attempt per version — a colony that cannot reach GitHub, or
///     whose disk is full, retries on the next tick rather than in a loop.
/// </summary>
public static class UpdateStager
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static int _running;
    private static string? _lastAttempted;

    /// <summary>Starts the background check. Returns immediately; never throws into the caller.</summary>
    public static void Start(CancellationToken cancel = default)
    {
        var thread = new Thread(() =>
        {
            // A short delay so a colony's first seconds belong to the operator, not to us.
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromMinutes(2))) return;
            while (!cancel.IsCancellationRequested)
            {
                try { StageIfAvailable(); }
                catch (Exception error) { Console.Error.WriteLine($"[update] check failed: {error.Message}"); }
                if (cancel.WaitHandle.WaitOne(Interval)) return;
            }
        })
        { IsBackground = true, Name = "anthill-update-stager" };
        thread.Start();
    }

    /// <summary>One pass. Public so an operator action and a test can drive it directly.</summary>
    public static UpdateStaging.StagingResult StageIfAvailable()
    {
        if (!string.Equals(AnthillRuntime.AutoUpdate, "silent", StringComparison.OrdinalIgnoreCase))
            return UpdateStaging.StagingResult.No($"auto_update is '{AnthillRuntime.AutoUpdate}'; nothing was downloaded.");

        var site = InstallDetector.Detect();
        if (!site.CanSelfUpdate) return UpdateStaging.StagingResult.No(site.Explanation);

        if (Interlocked.Exchange(ref _running, 1) == 1)
            return UpdateStaging.StagingResult.No("an update check is already running.");
        try
        {
            var info = UpdateChecker.Check(force: false);
            if (info.GetValueOrDefault("status") as string != "ok")
                return UpdateStaging.StagingResult.No("the update service could not be reached.");
            if (info.GetValueOrDefault("update_available") is not true)
                return UpdateStaging.StagingResult.No("this colony is on the latest release.");

            var latest = info.GetValueOrDefault("latest") as string;
            if (string.IsNullOrWhiteSpace(latest)) return UpdateStaging.StagingResult.No("no version was reported.");
            latest = latest.TrimStart('v', 'V');

            // Already staged, or already tried this version this run. A release that fails to stage
            // is not retried every tick — that turns one bad download into a bandwidth loop.
            if (UpdateStaging.Pending(site) is { } already && UpdateVersions.Compare(already.Version, latest) >= 0)
                return new UpdateStaging.StagingResult(true, $"v{already.Version} is already staged.", already);
            if (string.Equals(_lastAttempted, latest, StringComparison.Ordinal))
                return UpdateStaging.StagingResult.No($"v{latest} was already attempted this run.");
            _lastAttempted = latest;

            return Stage(site, latest);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static UpdateStaging.StagingResult Stage(InstallSite site, string version)
    {
        var wanted = site.AssetFor(version);
        if (wanted is null) return UpdateStaging.StagingResult.No("this install shape consumes no release asset.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ANTHILL/{AnthillRuntime.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var release = http.GetStringAsync($"https://api.github.com/repos/Formicaria/Anthill/releases/tags/v{version}")
                              .GetAwaiter().GetResult();
            var root = JsonDocument.Parse(release).RootElement;

            string? assetUrl = null, digestUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                        assetUrl = asset.GetProperty("browser_download_url").GetString();
                    else if (string.Equals(name, wanted + ".sha256", StringComparison.OrdinalIgnoreCase))
                        digestUrl = asset.GetProperty("browser_download_url").GetString();
                }

            if (assetUrl is null)
                return UpdateStaging.StagingResult.No($"release v{version} publishes no '{wanted}'.");
            if (digestUrl is null)
                return UpdateStaging.StagingResult.No(
                    $"release v{version} publishes no checksum for '{wanted}', so it was not downloaded. "
                  + "Update this colony the way you installed it.");

            var expected = UpdateStaging.ParseDigest(http.GetStringAsync(digestUrl).GetAwaiter().GetResult());
            if (expected is null)
                return UpdateStaging.StagingResult.No($"the published checksum for '{wanted}' could not be read.");

            Directory.CreateDirectory(site.StagingDirectory);
            var payload = Path.Combine(site.StagingDirectory, wanted);
            using (var download = http.GetStreamAsync(assetUrl).GetAwaiter().GetResult())
            using (var file = File.Create(payload))
                download.CopyTo(file);

            if (!UpdateStaging.VerifyOrDelete(payload, expected, out var verdict))
            {
                Console.Error.WriteLine($"[update] {verdict}");
                return UpdateStaging.StagingResult.No(verdict);
            }

            var staged = new UpdateStaging.StagedUpdate(version, wanted, expected, payload, site.Shape, DateTime.UtcNow);
            UpdateStaging.Record(site, staged);
            Console.Error.WriteLine(
                $"[update] v{version} downloaded and verified ({verdict}). It installs at the next start.");
            return new UpdateStaging.StagingResult(true, $"v{version} is staged and installs at the next start.", staged);
        }
        catch (Exception error)
        {
            return UpdateStaging.StagingResult.No($"the update could not be downloaded ({error.Message}).");
        }
    }
}
