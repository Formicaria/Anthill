using System.Security.Cryptography;
using System.Text.Json;
using Anthill.Core.Configuration;

namespace Anthill.Core.Updates;

/// <summary>
/// THE VERIFICATION THAT REPLACES THE CLICK. v0.3.8.149.
///
/// Until this release the Windows updater asked "install v0.3.8.145?" and an operator pressed Yes.
/// That prompt was doing two different jobs and only one of them was worth keeping. It asked for
/// PERMISSION — which the operator has now given once, in Settings, and re-asking every release is
/// the ritual this feature removes. And it put a human in front of a downloaded executable, which
/// was the only thing standing between a compromised release channel and code running on their
/// machine. Removing the click without replacing the second job would be trading a real protection
/// for convenience.
///
/// So the digest is the replacement. The release workflow publishes `<asset>.sha256` beside every
/// artifact, generated in the same job from the bytes it archived; nothing here is executed,
/// extracted or moved into place until the file on disk hashes to the digest that sidecar names.
/// A mismatch is not a warning: the download is DELETED and the update refused, because a file
/// that fails its hash is either corrupt or hostile and there is no third possibility worth
/// keeping on disk.
///
/// WHAT THIS IS NOT. A hash fetched over the same channel as the file is not a signature — anyone
/// who can replace the asset can replace the sidecar. It defeats corruption, a bad mirror, a
/// truncated download and a tampered CDN object; it does not defeat a compromised GitHub account.
/// Authenticode signing is the control that does, it needs a certificate this project does not yet
/// have, and saying so plainly here is better than letting a later reader assume the stronger
/// property. See docs/DEPLOYMENT.md.
/// </summary>
public static class UpdateStaging
{
    /// <summary>The staged-update description, written beside the payload and read at next launch.</summary>
    public sealed record StagedUpdate(
        string Version,
        string Asset,
        string Sha256,
        string PayloadPath,
        InstallShape Shape,
        DateTime StagedAt)
    {
        public const string FileName = "staged.json";
    }

    /// <summary>What a staging attempt did, in words the console can show without translation.</summary>
    public sealed record StagingResult(bool Staged, string Message, StagedUpdate? Update = null)
    {
        public static StagingResult No(string why) => new(false, why);
    }

    /// <summary>
    /// Reads a `sha256sum`-shaped sidecar: "&lt;64 hex&gt;␠␠&lt;name&gt;". Only the digest is taken —
    /// the name in the file is informational, and trusting it to pick a path would let the sidecar
    /// choose what gets written.
    /// </summary>
    public static string? ParseDigest(string? sidecar)
    {
        if (string.IsNullOrWhiteSpace(sidecar)) return null;
        var first = sidecar.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(first)) return null;
        var token = first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64) return null;
        return token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
    }

    /// <summary>The file's SHA-256, lower-case hex — the same shape `sha256sum` prints.</summary>
    public static string DigestOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a downloaded payload against its expected digest and DELETES it on any mismatch.
    ///
    /// Deleting rather than quarantining is deliberate. A failed payload has no use — it cannot be
    /// installed, it cannot be diagnosed without the digest it failed against (which is recorded in
    /// the message), and leaving an executable of unknown provenance in a directory the updater
    /// later runs things from is precisely the hazard this method exists to prevent.
    /// </summary>
    public static bool VerifyOrDelete(string payloadPath, string expectedDigest, out string message)
    {
        string actual;
        try { actual = DigestOf(payloadPath); }
        catch (Exception error)
        {
            message = $"the downloaded update could not be read to verify it ({error.Message}); it was discarded";
            TryDelete(payloadPath);
            return false;
        }

        if (string.Equals(actual, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            message = $"verified sha256 {actual}";
            return true;
        }

        TryDelete(payloadPath);
        message = $"the downloaded update did not match its published checksum (expected {expectedDigest}, "
                + $"got {actual}) and was discarded. Nothing was installed.";
        return false;
    }

    /// <summary>
    /// Records a verified payload as the update to apply at next launch. Written last, and only
    /// after verification, so the presence of this file IS the statement that a checked update is
    /// waiting — a reader never has to ask whether the payload beside it was validated.
    /// </summary>
    public static void Record(InstallSite site, StagedUpdate update)
    {
        Directory.CreateDirectory(site.StagingDirectory);
        var path = Path.Combine(site.StagingDirectory, StagedUpdate.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(update, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The staged update waiting for this launch, or null. Re-verifies the payload on the way out:
    /// the manifest says a digest was checked at download time, and between then and now the file
    /// has been sitting on a disk anyone with write access could reach.
    /// </summary>
    public static StagedUpdate? Pending(InstallSite site)
    {
        var path = Path.Combine(site.StagingDirectory, StagedUpdate.FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var staged = JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllText(path));
            if (staged is null) return null;
            if (!File.Exists(staged.PayloadPath)) { Clear(site); return null; }

            // A staged update for a version we are already running is spent, not pending.
            if (UpdateVersions.Compare(staged.Version, AnthillRuntime.Version) <= 0) { Clear(site); return null; }

            if (!VerifyOrDelete(staged.PayloadPath, staged.Sha256, out var message))
            {
                Console.Error.WriteLine($"[update] staged payload rejected at launch: {message}");
                Clear(site);
                return null;
            }
            return staged;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[update] the staged update could not be read ({error.Message}); clearing it.");
            Clear(site);
            return null;
        }
    }

    /// <summary>Removes the staged payload and its manifest. Used after applying, and after any refusal.</summary>
    public static void Clear(InstallSite site)
    {
        try
        {
            var dir = site.StagingDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir)) TryDelete(file);
        }
        catch { /* the next attempt overwrites it anyway */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>
/// Version comparison, in ONE place. v0.3.8.149 — it used to be in two: `UpdateChecker.Compare`
/// (dotted, tolerant of a leading `v`, missing parts zero) and the desktop updater's
/// `System.Version.TryParse`, which fails outright on a one-part or five-part version and whose
/// failure fell toward "no update available". Two answers to "is this newer" is how a colony comes
/// to believe it is current while an update sits on the shelf.
/// </summary>
public static class UpdateVersions
{
    /// <summary>Negative when <paramref name="left"/> is older, 0 when equal, positive when newer.</summary>
    public static int Compare(string? left, string? right)
    {
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Parts(string? version) =>
        (version ?? "").TrimStart('v', 'V').Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
}
