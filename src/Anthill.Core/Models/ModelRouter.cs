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
            // Link the mission's ambient token (so a timed-out/cancelled mission aborts this call)
            // with a hard per-call deadline — the wait is now bounded AND cancellable, never the
            // old up-to-185s-per-attempt block that could freeze the single-writer job queue.
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.ModelCallTimeoutSeconds));
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = Http.PostAsync(url, content, cts.Token).GetAwaiter().GetResult();
                // v2.4.3: a non-2xx is NOT a connection failure — report what Ollama actually said.
                // The classic trap: a 404 here almost always means the model is not pulled, which
                // used to masquerade as "could not connect" and sent operators chasing networking.
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var detail = errBody.Length > 0 && errBody.Length <= 300 ? $" — {errBody.Trim()}" : "";
                    return (int)response.StatusCode == 404
                        ? $"ERROR: Ollama at {_host} is reachable but model '{_model}' is not available{detail}. Run: ollama pull {_model} (an offline machine needs the model blobs copied in — it cannot pull)."
                        : $"ERROR: Ollama at {_host} answered HTTP {(int)response.StatusCode}{detail}.";
                }
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var output = doc.RootElement.TryGetProperty("response", out var resp) ? resp.GetString()?.Trim() ?? "" : "";
                return string.IsNullOrEmpty(output) ? "Ollama returned an empty response." : output;
            }
            catch (HttpRequestException error)
            {
                return $"ERROR: Could not connect to Ollama at {_host} ({error.GetBaseException().Message}). "
                    + "Check: is Ollama running there; if it is on another machine, is OLLAMA_HOST=0.0.0.0 set on it "
                    + "(Ollama binds only 127.0.0.1 by default) and does ANTHILL's ollama_host point at its IP, not localhost?";
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                // The mission itself was stopped (deadline reached or job cancelled) — abort cleanly
                // and do NOT retry; retrying would just re-hit the already-cancelled token.
                return "ERROR: Ollama request cancelled because the mission was stopped.";
            }
            catch (OperationCanceledException)
            {
                lastError = $"ERROR: Ollama request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).";
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
    private readonly ModelCircuitBreaker? _breaker;
    public int CallCount { get; private set; }

    /// <param name="breaker">Test seam. When null a default breaker is built from
    /// <see cref="AnthillRuntime"/> (or none, if the feature is disabled).</param>
    public ModelRouter(SqliteMemory? memory = null, ModelCircuitBreaker? breaker = null)
    {
        _memory = memory;
        _breaker = breaker ?? (AnthillRuntime.EnableModelCircuitBreaker
            ? new ModelCircuitBreaker(AnthillRuntime.ModelCircuitFailureThreshold, AnthillRuntime.ModelCircuitCooldownSeconds)
            : null);
    }

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
            "anthropic" => new AnthropicClient(apiKey, effectiveModel, storedBaseUrl),
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
        var routeKey = $"{provider}:{model}";
        var started = DateTime.UtcNow;

        // If this provider's breaker is open, fail fast without a network call — the whole point is to
        // stop a dead/slow provider from making every mission wait out a full timeout and pin the queue.
        var blockedReason = _breaker?.Blocked(routeKey);
        string response;
        ModelCallOutcome outcome;
        if (blockedReason is not null)
        {
            response = $"ERROR: {provider} temporarily unavailable — {blockedReason}. "
                     + "Fast-failed without a network call to keep the mission queue moving.";
            outcome = ModelCallOutcome.ConnectError;
        }
        else
        {
            var client = GetClient(provider, model);
            response = client.Generate(prompt, retries);
            outcome = ModelCallOutcomeExtensions.Classify(response);
            _breaker?.Record(routeKey, outcome.ToCircuitSignal());
        }

        var durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        var success = !response.StartsWith("ERROR:", StringComparison.Ordinal);
        var pheromoneDelta = success ? 0.01
            : outcome is ModelCallOutcome.Timeout or ModelCallOutcome.ConnectError ? -0.02 : -0.01;

        lock (_lock) CallCount++;

        if (_memory is not null && missionId is not null)
        {
            _memory.LogEvent(missionId, "model_call", $"Model call for role {role}: {provider}/{model}",
                taskId: taskId, antName: antName ?? role,
                metadata: new()
                {
                    ["role"] = role, ["provider"] = provider, ["model"] = model, ["success"] = success,
                    ["outcome"] = outcome.Name(), ["circuit_open"] = blockedReason is not null,
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

    /// <summary>
    /// Per-route circuit-breaker health for operator dashboards: which providers are healthy, which
    /// are open (cooling down after repeated transport faults), and which are half-open probing. Empty
    /// when the breaker is disabled or no route has been exercised yet.
    /// </summary>
    public List<Dictionary<string, object?>> ProviderHealth() =>
        _breaker is null
            ? new()
            : _breaker.Snapshot().Select(s => new Dictionary<string, object?>
            {
                ["route"] = s.Key,
                ["state"] = s.State,
                ["consecutive_faults"] = s.ConsecutiveFaults,
                ["seconds_until_close"] = s.SecondsUntilClose,
            }).ToList();

    public string FormatRoutes()
    {
        var lines = new List<string> { $"ANTHILL v{AnthillRuntime.Version} Model Routes" };
        foreach (var role in new[] { "planner", "researcher", "web", "coder", "builder", "verifier", "strategist", "fallback" })
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
               $"Per-Call Timeout: {AnthillRuntime.ModelCallTimeoutSeconds}s | " +
               $"Circuit Breaker: {(AnthillRuntime.EnableModelCircuitBreaker ? $"ON (opens after {AnthillRuntime.ModelCircuitFailureThreshold} faults, {AnthillRuntime.ModelCircuitCooldownSeconds}s cooldown)" : "OFF")}\n" +
               FormatProviderHealthLine() +
               "Provider Support: Ollama (local, keyless), OpenAI, Anthropic (Claude), Perplexity, and OpenRouter — " +
               "connect API keys in Settings → Providers.";
    }

    /// <summary>Plain-English live breaker state for the /models view — nothing to interpret: healthy,
    /// or exactly which route is cooling down and for how long.</summary>
    private string FormatProviderHealthLine()
    {
        if (!AnthillRuntime.EnableModelCircuitBreaker) return "";
        var health = ProviderHealth();
        var degraded = health.Where(h => (string?)h["state"] is "open" or "half_open").ToList();
        if (degraded.Count == 0)
            return $"Provider Health: all routes healthy ({health.Count} seen this session)\n";
        var parts = degraded.Select(h => (string?)h["state"] == "open"
            ? $"{h["route"]} cooling down ({h["seconds_until_close"]}s left)"
            : $"{h["route"]} probing (half-open)");
        return $"Provider Health: DEGRADED — {string.Join("; ", parts)}\n";
    }
}
