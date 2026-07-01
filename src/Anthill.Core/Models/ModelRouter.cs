using System.Text;
using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Models;

/// <summary>Abstraction over a text-generation backend. Implementations are role-routed by <see cref="ModelRouter"/>.</summary>
public interface IModelClient
{
    string Generate(string prompt, int retries = 2);
}

/// <summary>
/// Local Ollama client. Talks to the Ollama HTTP API with bounded retries and turns
/// transport faults into the sentinel "ERROR:" strings the rest of the colony branches on,
/// rather than throwing across the ant boundary.
/// </summary>
public sealed class OllamaClient : IModelClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(185) };
    private readonly string _model;
    private readonly string _host;

    public OllamaClient(string? model = null, string? host = null)
    {
        _model = model ?? AnthillRuntime.OllamaModel;
        _host = (host ?? AnthillRuntime.OllamaHost).TrimEnd('/');
    }

    public string Generate(string prompt, int retries = 2)
    {
        var url = $"{_host}/api/generate";
        var payload = JsonSerializer.Serialize(new { model = _model, prompt, stream = false });
        var lastError = "";
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = Http.PostAsync(url, content).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var output = doc.RootElement.TryGetProperty("response", out var resp) ? resp.GetString()?.Trim() ?? "" : "";
                return string.IsNullOrEmpty(output) ? "Ollama returned an empty response." : output;
            }
            catch (HttpRequestException)
            {
                return "ERROR: Could not connect to Ollama. Make sure Ollama is running at http://localhost:11434.";
            }
            catch (TaskCanceledException)
            {
                lastError = $"ERROR: Ollama request timed out (attempt {attempt}/{retries}).";
            }
            catch (Exception error)
            {
                lastError = $"ERROR: Ollama request failed: {error.Message} (attempt {attempt}/{retries}).";
            }
        }
        return lastError;
    }
}

/// <summary>Provider placeholders kept for forward-compatible routing config. Each fails closed with a clear message.</summary>
public sealed class PlaceholderClient : IModelClient
{
    private readonly string _provider;
    public PlaceholderClient(string provider) => _provider = provider;
    public string Generate(string prompt, int retries = 2) =>
        $"ERROR: {_provider} provider placeholder is not implemented in this build.";
}

/// <summary>
/// Role-based model routing. Resolves a provider/model per role, caches clients, records
/// each call as an event, and reinforces or decays the model-route pheromone trail by outcome.
/// Faithful to the Python <c>ModelRouter</c>, including the softened generic-failure penalty.
/// </summary>
public sealed class ModelRouter
{
    private readonly SqliteMemory? _memory;
    private readonly Dictionary<string, IModelClient> _clients = new();
    private readonly object _lock = new();
    public int CallCount { get; private set; }

    public ModelRouter(SqliteMemory? memory = null) => _memory = memory;

    public (string Provider, string Model) GetRoute(string role)
    {
        var route = AnthillRuntime.ModelRouting.GetValueOrDefault(role)
                    ?? AnthillRuntime.ModelRouting.GetValueOrDefault("fallback")
                    ?? new Dictionary<string, string> { ["provider"] = AnthillRuntime.DefaultModelProvider, ["model"] = AnthillRuntime.OllamaModel };
        return (route.GetValueOrDefault("provider", AnthillRuntime.DefaultModelProvider),
                route.GetValueOrDefault("model", AnthillRuntime.OllamaModel));
    }

    private IModelClient GetClient(string provider, string model)
    {
        // Keyed providers (OpenAI/Anthropic/Perplexity/OpenRouter/...) are built fresh on every
        // call instead of cached: the API key lives in provider_credentials and can be rotated or
        // revoked from Settings → Providers at any time, and a cached client would keep using a
        // stale (or just-deleted) key until process restart. Construction itself is cheap — each
        // client shares one static HttpClient — so this costs nothing but an allocation.
        if (ProviderCatalog.KeyedProviders.Contains(provider))
            return BuildKeyedClient(provider, model);

        var key = $"{provider}:{model}";
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            IModelClient client = provider switch
            {
                "ollama" => new OllamaClient(model),
                _ => new OllamaClient(AnthillRuntime.OllamaModel),
            };
            _clients[key] = client;
            return client;
        }
    }

    /// <summary>Builds a client for a keyed external provider, resolving its API key and endpoint
    /// from <see cref="SqliteMemory"/> (see <c>SqliteMemory.Providers.cs</c>).</summary>
    private IModelClient BuildKeyedClient(string provider, string model)
    {
        var info = ProviderCatalog.Find(provider);
        var apiKey = _memory?.GetDecryptedApiKey(provider);
        var storedBaseUrl = _memory?.GetProviderBaseUrl(provider);
        var endpoint = string.IsNullOrWhiteSpace(storedBaseUrl) ? info?.DefaultEndpoint ?? "" : storedBaseUrl;
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? info?.DefaultModel ?? model : model;

        return provider switch
        {
            "openai" => new OpenAiCompatibleClient("OpenAI", endpoint, apiKey, effectiveModel),
            "perplexity" => new OpenAiCompatibleClient("Perplexity", endpoint, apiKey, effectiveModel),
            "openrouter" => new OpenAiCompatibleClient("OpenRouter", endpoint, apiKey, effectiveModel,
                new Dictionary<string, string> { ["HTTP-Referer"] = "https://anthill.local", ["X-Title"] = "ANTHILL" }),
            "anthropic" => new AnthropicClient(apiKey, effectiveModel),
            _ => new PlaceholderClient(provider),
        };
    }

    /// <summary>Builds a client for an ad-hoc connection test — the same routing used at mission
    /// time, but callable directly by the API's "Test Connection" action without a role/route.</summary>
    public IModelClient GetClientForProvider(string provider, string? model = null) =>
        GetClient(provider, model ?? ProviderCatalog.Find(provider)?.DefaultModel ?? "");

    public string Generate(string role, string prompt, string? missionId = null, string? taskId = null,
        string? antName = null, int retries = 2)
    {
        if (!AnthillRuntime.UseOllama && AnthillRuntime.DefaultModelProvider == "ollama")
            return "ERROR: Model routing requested Ollama, but USE_OLLAMA is False.";

        var (provider, model) = GetRoute(role);
        var client = GetClient(provider, model);
        var started = DateTime.UtcNow;
        var response = client.Generate(prompt, retries);
        var durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        var success = !response.StartsWith("ERROR:");
        var pheromoneDelta = success ? 0.01 : response.ToLowerInvariant().Contains("timed out") ? -0.02 : -0.01;

        lock (_lock) CallCount++;

        if (_memory is not null && missionId is not null)
        {
            _memory.LogEvent(missionId, "model_call", $"Model call for role {role}: {provider}/{model}",
                taskId: taskId, antName: antName ?? role,
                metadata: new()
                {
                    ["role"] = role, ["provider"] = provider, ["model"] = model, ["success"] = success,
                    ["duration_ms"] = durationMs, ["prompt_chars"] = prompt.Length, ["response_chars"] = response.Length,
                    ["pheromone_delta"] = pheromoneDelta,
                });
            _memory.UpdatePheromoneTrail($"model:{provider}:{model}:{role}", "model_route", success, pheromoneDelta,
                new()
                {
                    ["role"] = role, ["provider"] = provider, ["model"] = model, ["duration_ms"] = durationMs,
                    ["last_mission_id"] = missionId, ["last_task_id"] = taskId,
                });
        }
        return response;
    }

    public string FormatRoutes()
    {
        var lines = new List<string> { $"ANTHILL v{AnthillRuntime.Version} Model Routes" };
        foreach (var role in new[] { "planner", "researcher", "web", "coder", "builder", "verifier", "fallback" })
        {
            var (provider, model) = GetRoute(role);
            lines.Add($"{role}: provider={provider} | model={model}");
        }
        return string.Join("\n", lines);
    }

    public string FormatModels()
    {
        var active = AnthillRuntime.ModelRouting.Keys
            .Select(r => { var (p, m) = GetRoute(r); return $"{p}:{m}"; })
            .Distinct().OrderBy(x => x, StringComparer.Ordinal);
        var configuredProviders = _memory?.ListProviderConnections()
            .Where(c => c["configured"] is true)
            .Select(c => c["provider"]?.ToString() ?? "")
            .ToList() ?? new List<string>();
        return $"ANTHILL v{AnthillRuntime.Version} Model Router\n" +
               $"Routing Enabled: {(AnthillRuntime.EnableModelRouting ? "ON" : "OFF")}\n" +
               $"Default Provider: {AnthillRuntime.DefaultModelProvider}\n" +
               $"Ollama Host: {AnthillRuntime.OllamaHost}\n" +
               $"Total Model Calls This Session: {CallCount}\n" +
               $"Active Route Targets: {string.Join(", ", active)}\n" +
               $"Configured External Providers: {(configuredProviders.Count > 0 ? string.Join(", ", configuredProviders) : "none")}\n" +
               "Provider Support: Ollama (local, keyless), OpenAI, Anthropic (Claude), Perplexity, and OpenRouter — " +
               "connect API keys in Settings → Providers.";
    }
}
