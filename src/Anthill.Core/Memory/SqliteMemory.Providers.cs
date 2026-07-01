using Anthill.Core.Common;
using Anthill.Core.Models;

namespace Anthill.Core.Memory;

/// <summary>
/// Model-provider connection storage: encrypted API keys for external providers (OpenAI,
/// Anthropic, Perplexity, OpenRouter, ...) that ants can be routed to alongside local Ollama.
/// Keys are sealed at rest with <see cref="_cipher"/> (AES-256-GCM, see <c>FieldCipher</c>) and
/// are never returned by <see cref="ListProviderConnections"/> — only
/// <see cref="GetDecryptedApiKey"/>, used internally by <c>ModelRouter</c> to build a live client,
/// ever sees plaintext.
/// </summary>
public sealed partial class SqliteMemory
{
    public static string NormalizeProvider(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// A secret-free connection status for every keyed provider ANTHILL knows about (see
    /// <see cref="ProviderCatalog.KeyedProviders"/>) — including providers with no row yet, reported
    /// as not configured, so the console can render a consistent card grid without special-casing
    /// "never connected".
    /// </summary>
    public List<Dictionary<string, object?>> ListProviderConnections()
    {
        var rows = Query(
                "SELECT provider, api_key, base_url, label, enabled, last_verified_at, last_verify_ok, " +
                "last_verify_message, created_at, updated_at FROM provider_credentials")
            .ToDictionary(r => NormalizeProvider(r.GetValueOrDefault("provider") as string));

        var result = new List<Dictionary<string, object?>>();
        foreach (var providerId in ProviderCatalog.KeyedProviders.OrderBy(x => x, StringComparer.Ordinal))
        {
            rows.TryGetValue(providerId, out var row);
            var configured = row is not null && !string.IsNullOrEmpty(row.GetValueOrDefault("api_key") as string);
            result.Add(new Dictionary<string, object?>
            {
                ["provider"] = providerId,
                ["configured"] = configured,
                ["base_url"] = row?.GetValueOrDefault("base_url"),
                ["label"] = row?.GetValueOrDefault("label"),
                ["enabled"] = row is null || AsLong(row.GetValueOrDefault("enabled")) == 1,
                ["last_verified_at"] = row?.GetValueOrDefault("last_verified_at"),
                ["last_verify_ok"] = row?.GetValueOrDefault("last_verify_ok") is { } lvo ? AsLong(lvo) == 1 : (bool?)null,
                ["last_verify_message"] = row?.GetValueOrDefault("last_verify_message"),
                ["created_at"] = row?.GetValueOrDefault("created_at"),
                ["updated_at"] = row?.GetValueOrDefault("updated_at"),
            });
        }
        return result;
    }

    /// <summary>
    /// Creates or updates a provider connection. When <paramref name="apiKey"/> is null/blank on an
    /// existing row, the stored key is left untouched — so the console can update just the base URL,
    /// label, or enabled flag without re-entering the secret. Returns an error message, or "" on
    /// success.
    /// </summary>
    public string UpsertProviderCredential(string provider, string? apiKey, string? baseUrl, bool enabled, string? label)
    {
        var p = NormalizeProvider(provider);
        if (!ProviderCatalog.KeyedProviders.Contains(p))
            return $"Unknown provider '{p}'. Expected one of: {string.Join(", ", ProviderCatalog.KeyedProviders.OrderBy(x => x))}.";

        var existing = Query(
            "SELECT api_key, created_at, last_verified_at, last_verify_ok, last_verify_message FROM provider_credentials WHERE provider = @p",
            ("@p", p)).FirstOrDefault();

        var hasNewKey = !string.IsNullOrWhiteSpace(apiKey);
        if (existing is null && !hasNewKey)
            return "An API key is required to add a new connection.";

        var sealedKey = hasNewKey ? _cipher.Protect(apiKey) : existing?.GetValueOrDefault("api_key") as string;
        var createdAt = existing?.GetValueOrDefault("created_at") as string ?? AnthillTime.NowUtc().ToIso();
        var now = AnthillTime.NowUtc().ToIso();

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO provider_credentials
                    (provider, api_key, base_url, label, enabled,
                     last_verified_at, last_verify_ok, last_verify_message, created_at, updated_at)
                  VALUES (@p, @k, @b, @l, @e, @lva, @lvo, @lvm, @c, @u)",
                ("@p", p), ("@k", sealedKey),
                ("@b", string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim()),
                ("@l", string.IsNullOrWhiteSpace(label) ? null : label.Trim()),
                ("@e", enabled ? 1 : 0),
                ("@lva", existing?.GetValueOrDefault("last_verified_at")),
                ("@lvo", existing?.GetValueOrDefault("last_verify_ok")),
                ("@lvm", existing?.GetValueOrDefault("last_verify_message")),
                ("@c", createdAt), ("@u", now));
        }
        InvalidateCache();
        return "";
    }

    public string DeleteProviderCredential(string provider)
    {
        var p = NormalizeProvider(provider);
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM provider_credentials WHERE provider = @p", ("@p", p));
        }
        InvalidateCache();
        return "";
    }

    /// <summary>
    /// Decrypts and returns the stored key for a provider, or null if none is configured, the row
    /// couldn't be decrypted, or the connection is disabled. Internal use only (ModelRouter) — never
    /// exposed over the API.
    /// </summary>
    public string? GetDecryptedApiKey(string provider)
    {
        var p = NormalizeProvider(provider);
        var row = Query("SELECT api_key, enabled FROM provider_credentials WHERE provider = @p", ("@p", p)).FirstOrDefault();
        if (row is null || AsLong(row.GetValueOrDefault("enabled")) != 1) return null;
        var stored = row.GetValueOrDefault("api_key") as string;
        return string.IsNullOrEmpty(stored) ? null : _cipher.Unprotect(stored);
    }

    /// <summary>The configured base URL override for a provider, if any (callers fall back to the catalog default when null).</summary>
    public string? GetProviderBaseUrl(string provider) =>
        Query("SELECT base_url FROM provider_credentials WHERE provider = @p", ("@p", NormalizeProvider(provider)))
            .FirstOrDefault()?.GetValueOrDefault("base_url") as string;

    /// <summary>Records the outcome of a live "Test Connection" probe against a provider.</summary>
    public void SetProviderVerification(string provider, bool ok, string message)
    {
        var p = NormalizeProvider(provider);
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE provider_credentials SET last_verified_at = @t, last_verify_ok = @ok, last_verify_message = @m WHERE provider = @p",
                ("@t", AnthillTime.NowUtc().ToIso()), ("@ok", ok ? 1 : 0), ("@m", message), ("@p", p));
        }
        InvalidateCache();
    }
}
