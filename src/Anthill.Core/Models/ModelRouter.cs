using System.Text;
using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Models;

/// <summary>
/// Abstraction over a text-generation backend. Implementations are role-routed by
/// <see cref="ModelRouter"/>.
///
/// v3.2.0 (ROADMAP § v3.2.0, "no <c>ERROR:</c> prefix determines success"): a client returns a
/// TYPED <see cref="ModelCallResult"/>. It does not throw across the ant boundary — that contract
/// is unchanged — but it no longer encodes what went wrong into prose for someone downstream to
/// parse back out.
///
/// Why that round-trip had to go: every failure site in a client already knows exactly what
/// happened — a 404, a refused connection, the mission's token, the per-call deadline. It then
/// formatted that knowledge into a sentence, and <c>Classify</c> recovered it by substring match.
/// Editing one of those sentences — "timed out" to "exceeded its deadline", say — would silently
/// reclassify the fault, the circuit breaker would stop seeing a TransientFault, and it would stop
/// tripping. Nothing would fail; the protection would just quietly stop working. Status is now set
/// where it is known.
/// </summary>
public interface IModelClient
{
    /// <summary>
    /// v3.3.0 (ADR-003): the typed call. THE primary method — every client implements this one.
    ///
    /// Transport stays exactly where it was: bounded retries, the ambient cancellation token, the
    /// per-call deadline and the status classification described above are unchanged and still live
    /// in each client. What moved out is only the two ends — what goes on the wire, and what comes
    /// back off it — into <see cref="ProviderWireFormat"/>, where they are pure and testable
    /// without a provider.
    /// </summary>
    ModelResponse Send(ModelRequest request, int retries = 2);

    /// <summary>
    /// The string call, now a thin caller of the typed one rather than the other way round.
    ///
    /// The DIRECTION is the whole lesson of the v3.2.0 ant migration. A shim that widens a string
    /// into a typed value has to invent the information the string never carried, which makes it
    /// permanent by construction — that is how <c>string Run(Task, Mission)</c> survived four
    /// releases. This one narrows a typed result to text at the outermost edge, for callers that
    /// only ever wanted text: it discards rather than fabricates, and it deletes cleanly the moment
    /// the last such caller moves.
    /// </summary>
    ModelCallResult Generate(string prompt, int retries = 2) =>
        Send(ModelRequest.FromPrompt(prompt), retries).ToCallResult();
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

    /// <summary>
    /// v3.3.0: typed, still on /api/generate.
    ///
    /// The endpoint deliberately does NOT change in this increment. Ollama's /api/chat is where
    /// tool calling and real multi-turn live, and it is where this is going — but moving the wire
    /// AND the contract in one step would leave a broken local model call indistinguishable from a
    /// broken refactor. This step is structural only: identical request on the wire, identical
    /// bytes back, transport and error classification untouched.
    ///
    /// Messages are flattened with role labels. Lossy in principle, lossless in practice today —
    /// every caller sends a single user message — and tools cannot arrive here because the
    /// capability catalog gives the ollama PROVIDER no tool calling, so nothing offers them.
    /// </summary>
    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        var url = $"{_host}/api/generate";
        var prompt = Flatten(request);
        var model = request.Model ?? _model;
        var payload = JsonSerializer.Serialize(new { model, prompt, stream = false });
        // The operator-facing prose is unchanged throughout; only the STATUS is now carried
        // alongside it instead of being recoverable from it.
        var lastError = Fail(ModelCallOutcome.Empty, model, "");
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
                        ? Fail(ModelCallOutcome.NotAvailable, model,
                            $"ERROR: Ollama at {_host} is reachable but model '{model}' is not available{detail}. Run: ollama pull {model} (an offline machine needs the model blobs copied in — it cannot pull).")
                        : Fail(ModelCallOutcome.HttpError, model,
                            $"ERROR: Ollama at {_host} answered HTTP {(int)response.StatusCode}{detail}.");
                }
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var output = doc.RootElement.TryGetProperty("response", out var resp) ? resp.GetString()?.Trim() ?? "" : "";
                return string.IsNullOrEmpty(output)
                    ? Fail(ModelCallOutcome.Empty, model, "Ollama returned an empty response.")
                    : new ModelResponse
                    {
                        Status = ModelCallOutcome.Ok, Content = output,
                        Provider = "ollama", Model = model,
                        // Ollama reports token counts on the response; absent on older builds,
                        // which is UNKNOWN rather than zero.
                        Usage = new ModelUsage(
                            doc.RootElement.TryGetProperty("prompt_eval_count", out var pe) ? pe.GetInt32() : null,
                            doc.RootElement.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : null),
                    };
            }
            catch (HttpRequestException error)
            {
                return Fail(ModelCallOutcome.ConnectError, model, $"ERROR: Could not connect to Ollama at {_host} ({error.GetBaseException().Message}). "
                    + "Check: is Ollama running there; if it is on another machine, is OLLAMA_HOST=0.0.0.0 set on it "
                    + "(Ollama binds only 127.0.0.1 by default) and does ANTHILL's ollama_host point at its IP, not localhost?");
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                // The mission itself was stopped (deadline reached or job cancelled) — abort cleanly
                // and do NOT retry; retrying would just re-hit the already-cancelled token.
                return Fail(ModelCallOutcome.Cancelled, model, "ERROR: Ollama request cancelled because the mission was stopped.");
            }
            catch (OperationCanceledException)
            {
                lastError = Fail(ModelCallOutcome.Timeout, model, $"ERROR: Ollama request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).");
            }
            catch (Exception error)
            {
                lastError = Fail(ModelCallOutcome.Error, model, $"ERROR: Ollama request failed: {error.Message} (attempt {attempt}/{retries}).");
            }
        }
        return lastError;
    }

    /// <summary>
    /// A failure, carrying which provider and model produced it. The operator prose is byte for
    /// byte what it was — only the envelope changed — because these strings are what an operator
    /// reads when a local model will not answer, and a refactor is not a licence to reword them.
    /// </summary>
    private static ModelResponse Fail(ModelCallOutcome status, string model, string message) =>
        new() { Status = status, Content = message, Provider = "ollama", Model = model };

    /// <summary>
    /// Messages to a single prompt for /api/generate. Roles are labelled rather than dropped, so a
    /// multi-turn request degrades to something the model can still follow instead of silently
    /// losing who said what. A lone user message — every call the colony makes today — flattens to
    /// exactly the string that was previously sent, which is what keeps this step behaviour-neutral.
    /// </summary>
    private static string Flatten(ModelRequest request)
    {
        if (request.Messages.Count == 1) return request.Messages[0].Content ?? "";
        var sb = new StringBuilder();
        foreach (var m in request.Messages)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            if (m.Role != ModelMessage.User) sb.Append(m.Role.ToUpperInvariant()).Append(":\n");
            sb.Append(m.Content);
        }
        return sb.ToString();
    }
}

/// <summary>Provider placeholders kept for forward-compatible routing config. Each fails closed with a clear message.</summary>
public sealed class PlaceholderClient : IModelClient
{
    private readonly string _provider;
    public PlaceholderClient(string provider) => _provider = provider;
    // Error, deliberately, not ConfigError: this classified as the generic Error before the typed
    // boundary, and Error maps to CircuitSignal.Neutral. Promoting it to ConfigError would make it
    // Healthy and start CLEARING a provider's breaker — a behaviour change smuggled in under a
    // refactor. The status recorded here is the one this path already had.
    public ModelResponse Send(ModelRequest request, int retries = 2) =>
        new()
        {
            Status = ModelCallOutcome.Error,
            Content = $"ERROR: {_provider} provider placeholder is not implemented in this build.",
            Provider = _provider,
            Model = request.Model,
        };
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

    /// <summary>
    /// v2.11.2 — resolves the effective route for a role. Normally this is the configured route, but
    /// if that provider's circuit breaker is OPEN and a distinct configured <c>fallback</c> route is
    /// healthy, it fails over to the fallback so the mission keeps moving instead of erroring on a
    /// dead provider. The decision runs through the deterministic <see cref="ModelRoutingPolicy"/>
    /// (stability-preferring: the configured route is only abandoned when proven unhealthy). This is
    /// a no-op when the breaker is disabled or when no distinct fallback is configured.
    /// </summary>
    public (string Provider, string Model, string? RerouteReason) ResolveRoute(string role)
    {
        var primary = GetRoute(role);
        if (_breaker is null) return (primary.Provider, primary.Model, null);

        var fallback = GetRoute("fallback");
        if (fallback.Provider == primary.Provider && fallback.Model == primary.Model)
            return (primary.Provider, primary.Model, null); // nothing distinct to fail over to

        // Health straight from live breaker state: an open breaker == proven-unhealthy for this
        // decision. Unknown routes are left out of the map, which the policy reads as healthy.
        var stats = new Dictionary<string, RouteHealth>(StringComparer.Ordinal);
        void Mark((string Provider, string Model) r)
        {
            var key = ModelStats.Key(r.Provider, r.Model);
            if (_breaker!.Blocked(key) is not null)
                stats[key] = new RouteHealth(key, RouteHealth.MinCallsForVerdict, 0, 0d, 0d);
        }
        Mark(primary);
        Mark(fallback);

        var choice = ModelRoutingPolicy.Choose("high", primary, new[] { fallback }, stats);
        return choice.Provider == primary.Provider && choice.Model == primary.Model
            ? (primary.Provider, primary.Model, null)
            : (choice.Provider, choice.Model, choice.Reason);
    }

    /// <summary>
    /// v2.26.0 introduced this typed boundary; v3.2.0 made it authoritative. It used to call
    /// <c>Generate</c> and re-derive the status by parsing the prose that came back. Now the
    /// status travels with the result from the client that knew it, and the string-returning
    /// <c>Generate</c> is the thin projection instead of the other way round.
    /// </summary>
    public ModelCallResult GenerateTyped(string role, string prompt, string? missionId = null,
        string? taskId = null, string? antName = null, int retries = 2) =>
        GenerateCore(role, prompt, missionId, taskId, antName, retries);

    /// <summary>Content-only projection, for callers that have not yet moved to the typed result.</summary>
    public string Generate(string role, string prompt, string? missionId = null, string? taskId = null,
        string? antName = null, int retries = 2) =>
        GenerateCore(role, prompt, missionId, taskId, antName, retries).Content;

    private ModelCallResult GenerateCore(string role, string prompt, string? missionId, string? taskId,
        string? antName, int retries)
    {
        if (!AnthillRuntime.UseOllama && AnthillRuntime.DefaultModelProvider == "ollama")
            return new ModelCallResult(ModelCallOutcome.Error,
                "ERROR: Model routing requested Ollama, but USE_OLLAMA is False.");

        var (provider, model, rerouteReason) = ResolveRoute(role);
        var routeKey = $"{provider}:{model}";
        var started = DateTime.UtcNow;

        // If this provider's breaker is open, fail fast without a network call — the whole point is to
        // stop a dead/slow provider from making every mission wait out a full timeout and pin the queue.
        var blockedReason = _breaker?.Blocked(routeKey);
        ModelCallResult result;
        if (blockedReason is not null)
        {
            result = new ModelCallResult(ModelCallOutcome.ConnectError,
                $"ERROR: {provider} temporarily unavailable — {blockedReason}. "
                + "Fast-failed without a network call to keep the mission queue moving.");
        }
        else
        {
            // v3.2.0: the status arrives WITH the result. This used to be
            // Classify(response) — recovering, by substring match, what the client already knew.
            result = GetClient(provider, model).Generate(prompt, retries);
            _breaker?.Record(routeKey, result.Status.ToCircuitSignal());
        }
        var response = result.Content;
        var outcome = result.Status;

        var durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        // v3.2.0 BEHAVIOUR FIX, called out because it is one: success was
        // !response.StartsWith("ERROR:"), which disagreed with ModelCallResult.Ok — whose own
        // documentation already said "an Empty response is never Ok". A provider returning nothing
        // does not start with ERROR:, so it was counted as a successful call, REINFORCING the
        // route's pheromone trail and reporting success:true in telemetry. Two definitions of
        // success in one method, the exact disease this phase exists to cure. There is now one.
        var success = result.Ok;
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
                    ["reroute_reason"] = rerouteReason,
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
        return result;
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
