using System.Collections.Concurrent;
using Anthill.Core.Security;

namespace Anthill.Api;

/// <summary>One authenticated operator session: who they are, their role, and when it expires.</summary>
public sealed record AuthSession(string Username, string Role, DateTime ExpiresAt);

/// <summary>
/// In-memory session registry. Login mints an opaque bearer token bound to a user + role with a
/// sliding expiry; every authenticated request resolves and renews it. Sessions live only in
/// process memory, so a restart logs everyone out — which is the safe default for a local control
/// plane and means no session secret is ever written to disk.
/// </summary>
public static class AuthSessions
{
    private static readonly ConcurrentDictionary<string, AuthSession> _sessions = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    public static string Issue(string username, string role)
    {
        PruneExpired(); // opportunistic: abandoned tokens are otherwise only evicted on their own resolve
        var token = TokenSecurity.GenerateStrongToken(32);
        _sessions[token] = new AuthSession(username, role, DateTime.UtcNow.Add(Ttl));
        return token;
    }

    /// <summary>Drops every session whose sliding expiry has already lapsed. Cheap; called on login.</summary>
    private static void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
            if (kv.Value.ExpiresAt <= now)
                _sessions.TryRemove(kv.Key, out _);
    }

    /// <summary>Resolves a session token, renewing its sliding expiry. Null if missing or expired.</summary>
    public static AuthSession? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var s)) return null;
        if (s.ExpiresAt <= DateTime.UtcNow) { _sessions.TryRemove(token, out _); return null; }
        var renewed = s with { ExpiresAt = DateTime.UtcNow.Add(Ttl) };
        _sessions[token] = renewed;
        return renewed;
    }

    public static void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    /// <summary>Drops every session for a user — used when their password, role, or status changes.</summary>
    public static void RevokeUser(string username)
    {
        foreach (var kv in _sessions)
            if (string.Equals(kv.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                _sessions.TryRemove(kv.Key, out _);
    }
}
