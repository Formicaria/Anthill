using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anthill.SDK.Security;

namespace Anthill.SDK.Common;

/// <summary>
/// URL decoding, normalisation, and SSRF/local-target filtering.
///
/// ANTHILL records search-result URLs but never fetches them, so the filter is
/// deliberately DNS-free: it rejects non-http(s) schemes, localhost-style names,
/// and private/loopback/link-local/reserved IP literals before any agent sees them.
/// Direct port of the matching Python helpers.
///
/// v3.8.12 — moved from <c>Anthill.Core.Common</c>. Six of its seven methods were already pure;
/// only <see cref="IsBlockedOutboundUrl"/> read configuration, and it now takes an optional
/// <see cref="ISsrfPolicy"/> that defaults to <see cref="SafetyPolicy.Ssrf"/>. Every existing call
/// site passes nothing and behaves exactly as before.
/// </summary>
public static class UrlSafety
{
    public static string NormalizeHost(string? host) =>
        (host ?? "").Trim().ToLowerInvariant().Trim('[', ']').TrimEnd('.');

    public static bool IsLoopbackBindHost(string host)
    {
        var normalized = NormalizeHost(host);
        if (normalized is "localhost" or "::1") return true;
        return IPAddress.TryParse(normalized, out var ip) && IPAddress.IsLoopback(ip);
    }

    public static string DecodeSearchUrl(string? url)
    {
        var cleaned = (url ?? "").Trim();
        if (cleaned.Length == 0) return cleaned;
        if (cleaned.StartsWith("//")) cleaned = "https:" + cleaned;
        try
        {
            var parsed = new Uri(cleaned, UriKind.Absolute);
            // DuckDuckGo wraps the real destination in a ?uddg= redirect parameter.
            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq] == "uddg")
                    return WebUtility.UrlDecode(pair[(eq + 1)..]);
            }
        }
        catch
        {
            // Not an absolute URL we can decode; return as-is.
        }
        return cleaned;
    }

    public static string ExtractDomain(string url)
    {
        var cleaned = DecodeSearchUrl(url);
        try
        {
            var parsed = new Uri(cleaned, UriKind.Absolute);
            var domain = parsed.Host.ToLowerInvariant();
            if (domain.StartsWith("www.")) domain = domain[4..];
            return string.IsNullOrEmpty(domain) ? "unknown" : domain;
        }
        catch
        {
            var match = System.Text.RegularExpressions.Regex.Match((url ?? "").Trim(), "https?://([^/]+)");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "unknown";
        }
    }

    public static string NormalizeUrlForDedupe(string url)
    {
        var cleaned = DecodeSearchUrl(url).Trim();
        try
        {
            var parsed = new Uri(cleaned, UriKind.Absolute);
            var domain = parsed.Host.ToLowerInvariant();
            if (domain.StartsWith("www.")) domain = domain[4..];
            var path = parsed.AbsolutePath.TrimEnd('/');
            return $"{parsed.Scheme.ToLowerInvariant()}://{domain}{path}";
        }
        catch
        {
            return cleaned.ToLowerInvariant().TrimEnd('/');
        }
    }

    /// <param name="policy">
    /// The blocklist to check against. Null — which is every call site in the colony today — reads
    /// <see cref="SafetyPolicy.Ssrf"/> live, so an operator or test that adds a blocked host after
    /// this type was first touched is still honoured.
    /// </param>
    public static bool IsBlockedOutboundUrl(string url, ISsrfPolicy? policy = null)
    {
        try
        {
            var gates = policy ?? SafetyPolicy.Ssrf;
            if (!Uri.TryCreate(DecodeSearchUrl(url), UriKind.Absolute, out var parsed)) return true;
            var scheme = parsed.Scheme.ToLowerInvariant();
            if (scheme is not ("http" or "https")) return true;
            var host = NormalizeHost(parsed.Host);
            if (host.Length == 0) return true;
            if (gates.BlockedHostnames.Contains(host) ||
                gates.BlockedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (IPAddress.TryParse(host, out var ip))
            {
                return IsPrivateOrReserved(ip);
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10/8, 172.16/12, 192.168/16, 169.254/16 link-local, 0.0.0.0, 224/4 multicast, 240/4 reserved
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 0) return true;
            if (bytes[0] >= 224) return true;
            return false;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
            // Unique local fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            return false;
        }
        return false;
    }

    /// <summary>Deterministic src_&lt;24hex&gt; id so the same normalized URL maps to a stable source id.</summary>
    public static string SourceIdFromUrl(string url)
    {
        var normalized = NormalizeUrlForDedupe(url);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant()[..24];
        return $"src_{hex}";
    }
}
