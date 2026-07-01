using System.Net;
using System.Net.Sockets;

namespace Anthill.Core.Common;

/// <summary>
/// Best-effort LAN-address discovery for container/appliance-style deployments (Docker, LXC,
/// Windows Service). ANTHILL always binds all interfaces by default now (see
/// <see cref="Anthill.Core.Configuration.AnthillRuntime.ApiHost"/>), which is correct for
/// listening but useless to print in a "open this URL" banner — "0.0.0.0" isn't browsable.
/// This resolves the concrete address a client on the LAN would actually use, so the console
/// banner, `/status`, and the UI can show a real, clickable URL without any manual config,
/// whether the host is Linux or Windows and regardless of which interface ends up being "the"
/// one (physical NIC, LXC veth, Docker host-network passthrough, etc.).
/// </summary>
public static class NetworkUtil
{
    /// <summary>
    /// Returns the IPv4 address the OS would use to reach the public internet, without sending
    /// any actual traffic (UDP "connect" just performs local route/interface selection — no
    /// packet leaves the machine). Returns null if no route exists (fully offline sandboxes,
    /// isolated networks) rather than throwing; callers should fall back to the raw bind host.
    /// </summary>
    public static string? GetLikelyLanIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return socket.LocalEndPoint is IPEndPoint endpoint ? endpoint.Address.ToString() : null;
        }
        catch
        {
            // No route to the internet (offline host, locked-down sandbox, IPv6-only network).
            // Fall through gracefully — this is a display convenience, never a hard requirement.
            return null;
        }
    }

    /// <summary>True for the wildcard hosts that mean "all interfaces" and therefore aren't a
    /// usable client-facing URL on their own.</summary>
    public static bool IsWildcardBindHost(string? host) =>
        host is "0.0.0.0" or "::" or "[::]" or "" or null;
}
