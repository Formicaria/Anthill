using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Monitoring;

/// <summary>
/// v3.0.1 — Uptime-Kuma status integration (Homarr parity: the "uptimeKuma"/health widget). Uses
/// Uptime-Kuma's public status-page API, keyed by the page <c>slug</c> (which the credential store
/// holds — the same field Homarr stored). Discipline is identical to every other integration:
/// GET-only (no write method exists), the target host checked against the D1 allowlist before any
/// I/O, a strict timeout, deterministic parsing, no LLM. The slug is a page identifier, not a
/// secret, but it rides the credential slot so instances stay uniform with the rest of the catalog.
/// </summary>
public sealed class UptimeKumaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IHomelabTargetGuard _targetGuard;

    public UptimeKumaClient(string url, IHomelabTargetGuard targetGuard)
    {
        _base = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        _targetGuard = targetGuard;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<JsonElement> GetAsync(string relativePath, CancellationToken ct)
    {
        if (!_targetGuard.IsAllowed(_base.Host))
            throw new InvalidOperationException($"Refused: '{_base.Host}' is not on the homelab target allowlist.");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relativePath.TrimStart('/')));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} from {_base.Host} {relativePath}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Typed widget payloads Uptime-Kuma publishes into integration_state (one builder/parser pair).</summary>
public static class UptimeKumaWidgetPayloads
{
    public static string Status(int up, int down, int total, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["up"] = up, ["down"] = down, ["total"] = total, ["checked_at"] = checkedAt });

    public static string Health(string status, int up, int down, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["status"] = status, ["up"] = up, ["down"] = down, ["checked_at"] = checkedAt });

    public static (int Up, int Down, int Total) ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (-1, -1, -1);
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            int G(string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : -1;
            return (G("up"), G("down"), G("total"));
        }
        catch { return (-1, -1, -1); }
    }
}

/// <summary>
/// v3.0.1 — Uptime-Kuma as an <see cref="IIntegrationDefinition"/>. Read-only deterministic sync:
/// GET the public status-page heartbeat for the configured slug and count monitors up/down from the
/// latest heartbeat of each. Returns typed payloads for integration_state. No LLM, no writes.
/// </summary>
public sealed class UptimeKumaIntegrationDefinition : IIntegrationDefinition
{
    public string Kind => "uptimekuma";
    public string Category => "monitoring";
    public string AuthMode => "token"; // the status-page slug (not a secret; kept uniform with the catalog)
    public IReadOnlyList<string> WidgetKinds { get; } = new[] { "health", "status" };

    public static void RegisterAll() => IntegrationCatalog.Register(new UptimeKumaIntegrationDefinition());

    public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(
        IntegrationContext context, CancellationToken ct)
    {
        var slug = (context.CredentialProvider() ?? "").Trim();
        if (slug.Length == 0) throw new InvalidOperationException("Uptime-Kuma status-page slug is not configured.");

        using var client = new UptimeKumaClient(context.BaseUrl, context.TargetGuard);
        var now = AnthillTime.NowUtc().ToIso();

        var hb = await client.GetAsync($"api/status-page/heartbeat/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);

        // heartbeatList maps monitorId -> [heartbeats]; the last heartbeat's status is current
        // (1 = up, anything else = not up). Count monitors up/down from those.
        int up = 0, down = 0;
        if (hb.ValueKind == JsonValueKind.Object && hb.TryGetProperty("heartbeatList", out var list)
            && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var monitor in list.EnumerateObject())
            {
                if (monitor.Value.ValueKind != JsonValueKind.Array || monitor.Value.GetArrayLength() == 0) continue;
                var latest = monitor.Value[monitor.Value.GetArrayLength() - 1];
                var isUp = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("status", out var st)
                           && st.TryGetInt32(out var s) && s == 1;
                if (isUp) up++; else down++;
            }
        }
        var total = up + down;
        var overall = down == 0 ? "ok" : (up == 0 ? "down" : "degraded");

        return new Dictionary<string, string>
        {
            ["health"] = UptimeKumaWidgetPayloads.Health(overall, up, down, now),
            ["status"] = UptimeKumaWidgetPayloads.Status(up, down, total, now),
        };
    }
}
