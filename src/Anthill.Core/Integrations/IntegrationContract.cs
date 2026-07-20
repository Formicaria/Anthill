using System.Text.Json.Serialization;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations;

/// <summary>
/// Console Refit Phase R1 (docs/CONSOLE_REFIT.md) — THE generic integration contract. Sonarr and
/// friends stop being special: every connected application (media, download clients, infra,
/// networking, monitoring, automation, dev, storage, auth, notify) is an IIntegrationDefinition
/// registered in <see cref="IntegrationCatalog"/>. Discipline is uniform and inherited from the
/// *arr implementation that pioneered it: GET-only clients unless the approval-gated action
/// pipeline fronts a write, API keys/tokens write-only in the credential store, the D1 target
/// allowlist checked before any I/O, strict timeouts, deterministic C# sync (never LLM), and
/// typed widget payloads so the UI widget runtime (Phase R2) can render any integration without
/// knowing what it is.
/// </summary>
public interface IIntegrationDefinition
{
    /// <summary>Stable kind id, lowercase (e.g. "sonarr", "qbittorrent", "uptime_kuma").</summary>
    string Kind { get; }
    /// <summary>media | download | infra | network | monitoring | automation | dev | storage | auth | notify.</summary>
    string Category { get; }
    /// <summary>api_key | token | basic | none — drives the add-integration form and client auth.</summary>
    string AuthMode { get; }
    /// <summary>Widget kinds this integration can feed (queue, health, statistics, disk_usage, ...).</summary>
    IReadOnlyList<string> WidgetKinds { get; }
    /// <summary>
    /// Deterministic read-only sync: fetch state and return one JSON payload per widget kind.
    /// Implementations receive a ready client context (base url + credential lookup + target
    /// guard) and MUST route every request through it — never construct their own HttpClient.
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(
        IntegrationContext context, CancellationToken ct);
}

/// <summary>Everything an integration may touch — nothing else. Built by the host per sync.</summary>
public sealed record IntegrationContext(
    string BaseUrl,
    IHomelabTargetGuard TargetGuard,
    Func<string?> CredentialProvider);

/// <summary>A configured instance of an integration kind (mirrors ArrAppRecord, generalized).</summary>
public sealed class IntegrationInstanceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("credential_id")] public string CredentialId { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("last_message")] public string LastMessage { get; set; } = "";
    [JsonPropertyName("last_checked")] public string LastChecked { get; set; } = "";
}

/// <summary>
/// The registry. Adding an integration to ANTHILL = implementing IIntegrationDefinition and
/// registering it here — no new tables, endpoints, or UI pages. R1 migrates the seven *arr kinds
/// onto this; the R5 waves (download clients, media servers, monitoring, infra, networking,
/// notify/auth/dev) each add definitions only.
/// </summary>
public static class IntegrationCatalog
{
    private static readonly Dictionary<string, IIntegrationDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IIntegrationDefinition definition) =>
        Definitions[definition.Kind] = definition;

    public static IIntegrationDefinition? Get(string kind) =>
        Definitions.GetValueOrDefault(kind);

    public static IReadOnlyCollection<IIntegrationDefinition> All => Definitions.Values;
}
