using System.Net;
using Anthill.Core.Common;

namespace Anthill.Core.Homelab.Security;

/// <summary>
/// Homelab Target Allowlist guard (v1.9.0, NORTH_STAR D1). Deterministic homelab providers may
/// only contact hosts the operator has explicitly allowlisted. Matching is purely local and
/// deterministic: exact hostname (case-insensitive), exact IP, or IPv4 CIDR containment — no DNS
/// resolution, so a spoofed record can never widen the list.
///
/// ISOLATION GUARANTEE: this class only ever ALLOWS what is on its own list for callers that ask
/// it directly. It does not touch, wrap, or weaken <see cref="UrlSafety"/> — the general SSRF
/// guard keeps blocking private/loopback/link-local targets for every LLM-directed tool
/// regardless of what is allowlisted here. Tests prove the isolation both ways.
/// </summary>
public sealed class HomelabTargetGuard : IHomelabTargetGuard
{
    private readonly IHomelabRepository _repository;

    public HomelabTargetGuard(IHomelabRepository repository) => _repository = repository;

    public bool IsAllowed(string hostOrIp)
    {
        var target = (hostOrIp ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (target.Length == 0) return false;

        foreach (var entry in _repository.ListAllowlist())
        {
            if (!entry.Enabled) continue;
            var pattern = entry.Target.Trim().TrimEnd('.').ToLowerInvariant();
            if (pattern.Length == 0) continue;

            // Exact hostname or exact IP match.
            if (string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase)) return true;

            // IPv4 CIDR containment (e.g. 10.0.0.0/24).
            if (pattern.Contains('/') && IPAddress.TryParse(target, out var ip) && InCidr(ip, pattern))
                return true;
        }
        return false;
    }

    internal static bool InCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        if (!int.TryParse(parts[1], out var prefix)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        if (prefix is < 0 or > 32) return false;
        var ipBits = BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray(), 0);
        var netBits = BitConverter.ToUInt32(network.GetAddressBytes().Reverse().ToArray(), 0);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (ipBits & mask) == (netBits & mask);
    }
}
