using System.Text.Json;
using Anthill.Modules.Homelab;

namespace Anthill.Modules.Homelab.Integrations.Media;

/// <summary>
/// v3.0.1 — Plex media-server integration (Homarr parity: the "mediaServer" widget). Same discipline
/// as the *arr family: a GET-only client (no write method exists on it), the Plex token fetched
/// per request from the credential store, the target host checked against the D1 homelab allowlist
/// before any I/O, a strict timeout, and secrets never logged. Plex speaks XML by default, so the
/// client asks for JSON via the Accept header and authenticates with the X-Plex-Token header.
/// </summary>
public sealed class PlexClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _tokenProvider;

    public PlexClient(string url, IHomelabTargetGuard targetGuard, Func<string?> tokenProvider)
    {
        _base = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        _targetGuard = targetGuard;
        _tokenProvider = tokenProvider;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<JsonElement> GetAsync(string relativePath, CancellationToken ct)
    {
        if (!_targetGuard.IsAllowed(_base.Host))
            throw new InvalidOperationException($"Refused: '{_base.Host}' is not on the homelab target allowlist.");
        var token = _tokenProvider() ?? throw new InvalidOperationException("Plex token credential is not configured.");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relativePath.TrimStart('/')));
        req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json"); // Plex defaults to XML
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} from {_base.Host} {relativePath}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Typed widget payloads Plex publishes into integration_state (one builder/parser pair).</summary>
public static class PlexWidgetPayloads
{
    public static string MediaServer(int activeStreams, string version, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["active_streams"] = activeStreams, ["version"] = version, ["checked_at"] = checkedAt });

    public static string Health(string status, string version, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["status"] = status, ["version"] = version, ["checked_at"] = checkedAt });

    public static (int Streams, string Version) ParseMediaServer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (-1, "");
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            var s = e.TryGetProperty("active_streams", out var a) && a.TryGetInt32(out var n) ? n : -1;
            var v = e.TryGetProperty("version", out var vv) ? vv.GetString() ?? "" : "";
            return (s, v);
        }
        catch { return (-1, ""); }
    }
}

/// <summary>
/// v3.0.1 — Plex as an <see cref="IIntegrationDefinition"/>. Read-only deterministic sync: GET the
/// server root (liveness + version) and <c>/status/sessions</c> (active stream count), returning
/// typed payloads for integration_state. No LLM, no writes. The MediaContainer envelope Plex wraps
/// every response in is unwrapped here.
/// </summary>
public sealed class PlexIntegrationDefinition : IIntegrationDefinition
{
    public string Kind => "plex";
    public string Category => "media";
    public string AuthMode => "token";
    public IReadOnlyList<string> WidgetKinds { get; } = new[] { "health", "mediaServer" };

    public static void RegisterAll() => IntegrationCatalog.Register(new PlexIntegrationDefinition());

    public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(
        IntegrationContext context, CancellationToken ct)
    {
        using var client = new PlexClient(context.BaseUrl, context.TargetGuard, context.CredentialProvider);
        var now = AnthillTime.NowUtc().ToIso();

        // Plex wraps everything in { "MediaContainer": { ... } }.
        static JsonElement Container(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("MediaContainer", out var mc) ? mc : root;

        var root = Container(await client.GetAsync("/", ct).ConfigureAwait(false));
        var version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";

        var streams = 0;
        try
        {
            var sessions = Container(await client.GetAsync("status/sessions", ct).ConfigureAwait(false));
            if (sessions.ValueKind == JsonValueKind.Object && sessions.TryGetProperty("size", out var sz) && sz.TryGetInt32(out var n))
                streams = n;
        }
        catch { streams = 0; /* sessions optional — root alone proves liveness */ }

        return new Dictionary<string, string>
        {
            ["health"] = PlexWidgetPayloads.Health("ok", version, now),
            ["mediaServer"] = PlexWidgetPayloads.MediaServer(streams, version, now),
        };
    }
}
