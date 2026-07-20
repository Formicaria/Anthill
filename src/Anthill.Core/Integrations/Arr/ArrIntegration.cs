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
/// v2.5.1 Console Refit R1 — the typed widget payload shapes the *arr family publishes into
/// integration_state. One builder/parser pair so the sync provider, the repository compatibility
/// view, and the legacy-row migration can never drift apart on keys.
/// </summary>
public static class ArrWidgetPayloads
{
    public static string Health(string status, string version, int healthWarnings, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["status"]=status, ["version"]=version, ["health_warnings"]=healthWarnings, ["checked_at"]=checkedAt });

    public static string Queue(int total, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["total"]=total, ["checked_at"]=checkedAt });

    public static (string Status, string Version, int Warnings) ParseHealth(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("unknown", "", 0);
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            return (
                e.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown",
                e.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                e.TryGetProperty("health_warnings", out var w) && w.TryGetInt32(out var n) ? n : 0);
        }
        catch { return ("unknown", "", 0); }
    }

    public static int ParseQueueTotal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return -1;
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            return e.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : -1;
        }
        catch { return -1; }
    }
}

/// <summary>
/// v2.5.1 Console Refit R1 — the *arr family as the first IIntegrationDefinition implementations.
/// One class covers all seven kinds (they share the API design); SyncAsync fetches
/// status/health/queue through the GET-only ArrClient (allowlist + credential-store discipline
/// unchanged) and returns typed widget payloads for integration_state.
/// </summary>
public sealed class ArrIntegrationDefinition : IIntegrationDefinition
{
    private readonly (string Prefix, bool HasQueue) _meta;
    public string Kind { get; }
    public string Category => "media";
    public string AuthMode => "api_key";
    public IReadOnlyList<string> WidgetKinds { get; }

    public ArrIntegrationDefinition(string kind)
    {
        Kind = kind.ToLowerInvariant();
        _meta = ArrClient.Kinds[Kind];
        WidgetKinds = _meta.HasQueue ? new[] { "health", "queue" } : new[] { "health" };
    }

    /// <summary>Registers all seven *arr kinds in the catalog (idempotent — Register upserts).</summary>
    public static void RegisterAll()
    {
        foreach (var kind in ArrClient.Kinds.Keys)
            IntegrationCatalog.Register(new ArrIntegrationDefinition(kind));
    }

    public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(
        IntegrationContext context, CancellationToken ct)
    {
        using var client = new ArrClient(context.BaseUrl, context.TargetGuard, context.CredentialProvider);

        var status = await client.GetAsync($"{_meta.Prefix}/system/status", ct).ConfigureAwait(false);
        var version = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";

        // Health warnings (bazarr has no /health; skip quietly).
        var warnings = 0;
        if (!string.Equals(Kind, "bazarr", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var health = await client.GetAsync($"{_meta.Prefix}/health", ct).ConfigureAwait(false);
                if (health.ValueKind == JsonValueKind.Array) warnings = health.GetArrayLength();
            }
            catch { /* health endpoint optional per version — status alone still proves liveness */ }
        }

        var now = AnthillTime.NowUtc().ToIso();
        var payloads = new Dictionary<string, string> { ["health"] = ArrWidgetPayloads.Health("ok", version, warnings, now) };

        if (_meta.HasQueue)
        {
            var total = -1;
            try
            {
                var queue = await client.GetAsync($"{_meta.Prefix}/queue?page=1&pageSize=1", ct).ConfigureAwait(false);
                if (queue.ValueKind == JsonValueKind.Object && queue.TryGetProperty("totalRecords", out var t))
                    total = t.GetInt32();
            }
            catch { total = -1; }
            payloads["queue"] = ArrWidgetPayloads.Queue(total, now);
        }
        return payloads;
    }
}

/// <summary>
/// Deterministic scheduler job generalizing the v2.3.3 ArrSyncProvider: refreshes every enabled
/// integration instance whose kind is registered in IntegrationCatalog, publishing widget payloads
/// into integration_state. Plain C# — no LLM involvement (NORTH_STAR §3.2 rule 5). One failing
/// integration never fails the sweep. The job name stays "arr-sync" for homelab_meta continuity.
/// </summary>
public sealed class IntegrationSyncProvider
{
    private readonly HomelabRepository _repository;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string, string?> _credentialLookup; // credential id → secret (never logged)
    public string Name => "arr-sync";

    public IntegrationSyncProvider(HomelabRepository repository, IHomelabTargetGuard targetGuard, Func<string, string?> credentialLookup)
    {
        _repository = repository;
        _targetGuard = targetGuard;
        _credentialLookup = credentialLookup;
    }

    public async Task<HomelabProviderResult> RunAsync(CancellationToken ct)
    {
        var instances = _repository.ListIntegrationInstances()
            .Where(i => i.Enabled && IntegrationCatalog.Get(i.Kind) is not null).ToList();
        var ok = 0; var failed = 0;
        foreach (var instance in instances)
        {
            try { await SyncOneAsync(instance, ct).ConfigureAwait(false); ok++; }
            catch (Exception ex)
            {
                failed++;
                instance.Status = "error";
                instance.LastMessage = ex.GetBaseException().Message is { Length: > 200 } m ? m[..200] : ex.GetBaseException().Message;
                instance.LastChecked = AnthillTime.NowUtc().ToIso();
                _repository.UpsertIntegrationInstance(instance);
            }
        }
        return new HomelabProviderResult(failed == 0, $"integration sync: {ok} ok, {failed} failed of {instances.Count}");
    }

    // (System.Threading.Tasks fully qualified: Anthill.Core's GlobalUsings aliases bare Task to the domain entity.)
    public async System.Threading.Tasks.Task SyncOneAsync(IntegrationInstanceRecord instance, CancellationToken ct)
    {
        var definition = IntegrationCatalog.Get(instance.Kind)
            ?? throw new InvalidOperationException($"Unknown integration kind '{instance.Kind}'.");
        var context = new IntegrationContext(instance.Url, _targetGuard, () => _credentialLookup(instance.CredentialId));
        var payloads = await definition.SyncAsync(context, ct).ConfigureAwait(false);
        foreach (var (widgetKind, payload) in payloads)
            _repository.UpsertIntegrationState(instance.Id, widgetKind, payload);
        instance.Status = "ok";
        instance.LastMessage = "";
        instance.LastChecked = AnthillTime.NowUtc().ToIso();
        _repository.UpsertIntegrationInstance(instance);
    }
}
