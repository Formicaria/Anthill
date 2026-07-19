using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Arr;

/// <summary>
/// v2.3.3 — *arr-stack integrations (Homarr-style; https://homarr.dev referenced for the UX model).
/// One GET-only client covers the whole mainstream family because they share the same API design:
/// Sonarr + Radarr (api/v3), Lidarr + Readarr + Whisparr + Prowlarr (api/v1) — all authenticated
/// with an X-Api-Key header — plus Bazarr (api/, X-API-KEY). Discipline is identical to every
/// other integration: GET-only by construction (no POST/PUT/DELETE method exists on this client),
/// the API key lives in the credential store and is fetched per request, the target host must be
/// on the D1 homelab allowlist, strict timeout, secrets never logged.
/// </summary>
public sealed class ArrClient : IDisposable
{
    /// <summary>kind → (api prefix, has queue endpoint). The catalog of supported apps.</summary>
    public static readonly IReadOnlyDictionary<string, (string Prefix, bool HasQueue)> Kinds =
        new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["sonarr"] = ("api/v3", true),
            ["radarr"] = ("api/v3", true),
            ["lidarr"] = ("api/v1", true),
            ["readarr"] = ("api/v1", true),
            ["whisparr"] = ("api/v3", true),
            ["prowlarr"] = ("api/v1", false), // indexer manager — no download queue
            ["bazarr"] = ("api", false),      // subtitles — different surface, status only
        };

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _apiKeyProvider;

    public ArrClient(string url, IHomelabTargetGuard targetGuard, Func<string?> apiKeyProvider)
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
        // One header covers the whole family: HTTP header names are case-insensitive, so
        // sonarr/radarr/lidarr/readarr/prowlarr/whisparr (X-Api-Key) and bazarr (X-API-KEY)
        // all read this. Adding both spellings would send a comma-joined double value.
        req.Headers.TryAddWithoutValidation("X-Api-Key", key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} from {_base.Host} {relativePath}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Deterministic scheduler job: refreshes status/version/health/queue for every enabled *arr app.
/// Plain C# — no LLM involvement (NORTH_STAR §3.2 rule 5). One failing app never fails the sweep.
/// </summary>
public sealed class ArrSyncProvider
{
    private readonly HomelabRepository _repository;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string, string?> _credentialLookup; // credential id → secret (never logged)
    public string Name => "arr-sync";

    public ArrSyncProvider(HomelabRepository repository, IHomelabTargetGuard targetGuard, Func<string, string?> credentialLookup)
    {
        _repository = repository;
        _targetGuard = targetGuard;
        _credentialLookup = credentialLookup;
    }

    public async Task<HomelabProviderResult> RunAsync(CancellationToken ct)
    {
        var apps = _repository.ListArrApps().Where(a => a.Enabled).ToList();
        var ok = 0; var failed = 0;
        foreach (var app in apps)
        {
            try { await SyncOneAsync(app, ct).ConfigureAwait(false); ok++; }
            catch (Exception ex)
            {
                failed++;
                app.Status = "error";
                app.LastMessage = ex.GetBaseException().Message is { Length: > 0 } m && m.Length > 200 ? m[..200] : ex.GetBaseException().Message;
                app.LastChecked = AnthillTime.NowUtc().ToIso();
                _repository.UpsertArrApp(app);
            }
        }
        return new HomelabProviderResult(failed == 0, $"arr sync: {ok} ok, {failed} failed of {apps.Count}");
    }

    // (System.Threading.Tasks fully qualified: Anthill.Core's GlobalUsings aliases bare Task to the domain entity.)
    public async System.Threading.Tasks.Task SyncOneAsync(ArrAppRecord app, CancellationToken ct)
    {
        if (!ArrClient.Kinds.TryGetValue(app.Kind, out var meta))
            throw new InvalidOperationException($"Unknown *arr kind '{app.Kind}'.");
        using var client = new ArrClient(app.Url, _targetGuard, () => _credentialLookup(app.CredentialId));

        var status = await client.GetAsync($"{meta.Prefix}/system/status", ct).ConfigureAwait(false);
        app.Version = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";

        // Health warnings (bazarr has no /health; skip quietly).
        app.HealthWarnings = 0;
        if (!string.Equals(app.Kind, "bazarr", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var health = await client.GetAsync($"{meta.Prefix}/health", ct).ConfigureAwait(false);
                if (health.ValueKind == JsonValueKind.Array) app.HealthWarnings = health.GetArrayLength();
            }
            catch { /* health endpoint optional per version — status alone still proves liveness */ }
        }

        app.QueueCount = -1;
        if (meta.HasQueue)
        {
            try
            {
                var queue = await client.GetAsync($"{meta.Prefix}/queue?page=1&pageSize=1", ct).ConfigureAwait(false);
                if (queue.ValueKind == JsonValueKind.Object && queue.TryGetProperty("totalRecords", out var t))
                    app.QueueCount = t.GetInt32();
            }
            catch { app.QueueCount = -1; }
        }

        app.Status = "ok";
        app.LastMessage = "";
        app.LastChecked = AnthillTime.NowUtc().ToIso();
        _repository.UpsertArrApp(app);
    }
}
