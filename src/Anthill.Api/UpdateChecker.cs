using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Api;

/// <summary>
/// Checks whether a newer ANTHILL release is published on the public GitHub repo. Compares the
/// running <see cref="AnthillRuntime.Version"/> against the latest release tag. Results are cached
/// so the header's periodic poll never hammers GitHub's API, and every failure mode (offline,
/// rate-limited, no releases yet) degrades to "unknown" rather than throwing.
/// </summary>
public static class UpdateChecker
{
    private const string Repo = "Formicaria/Anthill";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A FAILED CHECK IS NOT CACHED LIKE AN ANSWER. v0.3.8.129.
    ///
    /// Six seconds is not a timeout, it is a bet that the first TLS handshake of the session
    /// completes faster than most home connections manage — and the loss was charged at the
    /// success rate: one blocked poll pinned "unavailable" in the header for the full thirty
    /// minutes, long after the network came back. Twenty seconds to answer, five minutes to
    /// forgive.
    /// </summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(5);
    private static readonly object Gate = new();

    private static Dictionary<string, object?>? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    static UpdateChecker()
    {
        // GitHub's API requires a User-Agent; without it every request is rejected.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"ANTHILL/{AnthillRuntime.Version}");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Cached update status. Refreshes at most once per <see cref="CacheTtl"/>; force bypasses the cache.</summary>
    public static Dictionary<string, object?> Check(bool force = false)
    {
        lock (Gate)
        {
            var ttl = _cached is not null && _cached.TryGetValue("status", out var s) && (s as string) == "ok"
                ? CacheTtl : FailureTtl;
            if (!force && _cached is not null && DateTime.UtcNow - _cachedAt < ttl)
                return _cached;
        }

        var result = Fetch();
        lock (Gate) { _cached = result; _cachedAt = DateTime.UtcNow; }
        return result;
    }

    private static Dictionary<string, object?> Fetch()
    {
        var current = AnthillRuntime.Version;
        try
        {
            using var resp = Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest").GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return Unknown(current, $"GitHub returned {(int)resp.StatusCode}.");

            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var latest = tag.TrimStart('v', 'V');

            return new Dictionary<string, object?>
            {
                ["current"] = current,
                ["latest"] = latest,
                ["update_available"] = Compare(latest, current) > 0,
                ["release_name"] = name,
                ["release_url"] = url,
                ["checked_at"] = AnthillTime.NowUtc().ToIso(),
                ["status"] = "ok",
            };
        }
        catch (Exception ex)
        {
            return Unknown(current, Reason(ex));
        }
    }

    /// <summary>
    /// WHAT AN OPERATOR CAN ACT ON, not what the exception said.
    ///
    /// `ex.Message` for a lapsed timeout is "The request was canceled due to the configured
    /// HttpClient.Timeout of 6 seconds elapsing." — a sentence about this class's own field,
    /// rendered in the header of a colony that is working perfectly. It names no cause the reader
    /// owns and no action they can take. The check could not reach GitHub; that is the fact.
    /// </summary>
    private static string Reason(Exception ex) => ex switch
    {
        OperationCanceledException => $"no answer from github.com within {(int)Http.Timeout.TotalSeconds}s",
        HttpRequestException => "github.com could not be reached",
        _ => "the update check did not complete",
    };

    private static Dictionary<string, object?> Unknown(string current, string reason) => new()
    {
        ["current"] = current, ["latest"] = null, ["update_available"] = false,
        ["checked_at"] = AnthillTime.NowUtc().ToIso(), ["status"] = "unknown", ["reason"] = reason,
    };

    /// <summary>
    /// Dotted numeric version compare (1.8.14.3 vs 1.8.14). Returns &gt;0 if a is newer than b.
    /// Tolerates a leading "v"/"V" on either side so "v1.8.15" and "1.8.15" compare equal.
    /// </summary>
    internal static int Compare(string a, string b)
    {
        var pa = Parse((a ?? "").TrimStart('v', 'V'));
        var pb = Parse((b ?? "").TrimStart('v', 'V'));
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    private static int[] Parse(string v) =>
        (v ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
}
