using System.Text.Json;
using Anthill.Modules.Homelab;

namespace Anthill.Modules.Homelab.Integrations.MediaRequests;

/// <summary>
/// v3.0.1 — Overseerr / Jellyseerr media-request integration (Homarr parity: the "mediaRequests"
/// widgets). Same discipline as the *arr family it mirrors: a GET-only client (no write method
/// exists on it), the API key fetched per request from the credential store, the target host
/// checked against the D1 homelab allowlist before any I/O, a strict timeout, and secrets never
/// logged. Overseerr and Jellyseerr share the same <c>/api/v1</c> surface, so one client covers
/// both kinds (and the common "seerr" alias the operator's dashboard used).
/// </summary>
public sealed class OverseerrClient : IDisposable
{
    /// <summary>Registered kinds this client serves — Overseerr, its Jellyfin fork, and the alias.</summary>
    public static readonly IReadOnlyCollection<string> Kinds = new[] { "overseerr", "jellyseerr", "seerr" };

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _apiKeyProvider;

    public OverseerrClient(string url, IHomelabTargetGuard targetGuard, Func<string?> apiKeyProvider)
    {
        _base = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        _targetGuard = targetGuard;
        _apiKeyProvider = apiKeyProvider;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<JsonElement> GetAsync(string relativePath, CancellationToken ct)
    {
        if (!_targetGuard.IsAllowed(_base.Host))
            throw new InvalidOperationException($"Refused: '{_base.Host}' is not on the homelab target allowlist.");
        var key = _apiKeyProvider() ?? throw new InvalidOperationException("API key credential is not configured.");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relativePath.TrimStart('/')));
        req.Headers.TryAddWithoutValidation("X-Api-Key", key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} from {_base.Host} {relativePath}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Typed widget payloads Overseerr publishes into integration_state — one builder/parser
/// pair so the sync provider and the UI runtime never drift on keys (same discipline as ArrWidgetPayloads).</summary>
public static class OverseerrWidgetPayloads
{
    public static string Requests(int total, int pending, int approved, int available, int processing, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["total"] = total, ["pending"] = pending, ["approved"] = approved,
            ["available"] = available, ["processing"] = processing, ["checked_at"] = checkedAt,
        });

    public static string Health(string status, string version, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["status"] = status, ["version"] = version, ["checked_at"] = checkedAt });

    public static (int Total, int Pending) ParseRequests(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (-1, -1);
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            int G(string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : -1;
            return (G("total"), G("pending"));
        }
        catch { return (-1, -1); }
    }
}

/// <summary>
/// v3.0.1 — Overseerr/Jellyseerr as an <see cref="IIntegrationDefinition"/>. Read-only deterministic
/// sync: GET <c>/api/v1/status</c> (liveness + version) and <c>/api/v1/request/count</c> (the request
/// stats the media-requests widgets show), returning typed payloads for integration_state. No LLM,
/// no writes.
/// </summary>
public sealed class OverseerrIntegrationDefinition : IIntegrationDefinition
{
    public string Kind { get; }
    public string Category => "media";
    public string AuthMode => "api_key";
    public IReadOnlyList<string> WidgetKinds { get; } = new[] { "health", "requests" };

    public OverseerrIntegrationDefinition(string kind) => Kind = kind.ToLowerInvariant();

    /// <summary>Registers every Overseerr kind/alias in the catalog (idempotent — Register upserts).</summary>
    public static void RegisterAll()
    {
        foreach (var kind in OverseerrClient.Kinds)
            IntegrationCatalog.Register(new OverseerrIntegrationDefinition(kind));
    }

    public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(
        IntegrationContext context, CancellationToken ct)
    {
        using var client = new OverseerrClient(context.BaseUrl, context.TargetGuard, context.CredentialProvider);
        var now = AnthillTime.NowUtc().ToIso();

        var status = await client.GetAsync("api/v1/status", ct).ConfigureAwait(false);
        var version = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";

        int total = -1, pending = -1, approved = -1, available = -1, processing = -1;
        try
        {
            var count = await client.GetAsync("api/v1/request/count", ct).ConfigureAwait(false);
            if (count.ValueKind == JsonValueKind.Object)
            {
                int G(string k) => count.TryGetProperty(k, out var e) && e.TryGetInt32(out var n) ? n : -1;
                total = G("total"); pending = G("pending"); approved = G("approved");
                available = G("available"); processing = G("processing");
            }
        }
        catch { /* request/count is version-optional — status alone still proves liveness */ }

        return new Dictionary<string, string>
        {
            ["health"] = OverseerrWidgetPayloads.Health("ok", version, now),
            ["requests"] = OverseerrWidgetPayloads.Requests(total, pending, approved, available, processing, now),
        };
    }
}
