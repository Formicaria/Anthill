using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthill.Core.Configuration;

namespace Anthill.Core.Models;

/// <summary>
/// Chat-completions client for OpenAI-shaped APIs: OpenAI itself, Perplexity, and OpenRouter all
/// accept the same <c>{model, messages}</c> request body and return
/// <c>choices[0].message.content</c>, differing only in base URL and a couple of optional headers
/// (e.g. OpenRouter's attribution headers). One implementation covers all three so a new
/// OpenAI-compatible provider is just a catalog entry, not a new class.
/// </summary>
public sealed class OpenAiCompatibleClient : IModelClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _providerLabel;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly Dictionary<string, string>? _extraHeaders;

    public OpenAiCompatibleClient(string providerLabel, string endpoint, string? apiKey, string model,
        Dictionary<string, string>? extraHeaders = null)
    {
        _providerLabel = providerLabel;
        _endpoint = NormalizeEndpoint(endpoint);
        _apiKey = apiKey;
        _model = model;
        _extraHeaders = extraHeaders;
    }

    /// <summary>
    /// A "Base URL" override is conventionally just the host+version prefix — OpenAI's own client
    /// libraries define <c>base_url</c> exactly as e.g. "https://api.openai.com/v1" and append the
    /// request path themselves — so that's what operators naturally type into Settings → Providers
    /// even though the field's placeholder shows the full endpoint. Accept both forms: if the
    /// configured value doesn't already end with the chat-completions path, append it, rather than
    /// sending the request straight to the bare prefix and getting a 404 back from the provider.
    /// </summary>
    /// <summary>Public (rather than private) so it's directly unit-testable without a network call
    /// — pure string normalization, no side effects, nothing sensitive about exposing it.</summary>
    public static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = (endpoint ?? "").Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    /// <summary>
    /// v3.3.0 (ADR-006): typed, with the body now built by <see cref="ProviderWireFormat"/>.
    ///
    /// This is the client the substrate was built for. The hand-rolled anonymous object it replaces
    /// could only ever express a single user message; the projection carries messages, tools, a
    /// response schema and a per-call model — and it is unit-tested without a provider, which
    /// matters because every mistake it can make is silent (a tools array nested one level wrong is
    /// ignored, and the model simply answers without calling anything).
    ///
    /// Transport below is untouched: retries, the auth short-circuit, the ambient cancellation
    /// token and the per-call deadline all behave exactly as before.
    /// </summary>
    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        var model = request.Model ?? _model;
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Fail(ModelCallOutcome.ConfigError, model,
                $"ERROR: {_providerLabel} API key not configured. Add it in Settings → Providers.");

        // Trim the ask to what this provider/model pair can actually serve — once, here. A caller
        // may always request tools; whether they go on the wire is a property of the model.
        var negotiated = ModelCapabilityCatalog.Negotiate(
            request, ModelCapabilityCatalog.For(_providerLabel, model));
        var payload = ProviderWireFormat.OpenAiBody(negotiated, model).ToJsonString();
        var lastError = Fail(ModelCallOutcome.Empty, model, "");
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.ModelCallTimeoutSeconds));
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                if (_extraHeaders is not null)
                    foreach (var (name, value) in _extraHeaders) httpRequest.Headers.TryAddWithoutValidation(name, value);
                httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(httpRequest, cts.Token).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = Fail(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                            ? ModelCallOutcome.AuthError : ModelCallOutcome.HttpError, model,
                        $"ERROR: {_providerLabel} request failed ({(int)response.StatusCode}): {Truncate(body)}");
                    // Auth/permission failures will not heal on retry — surface immediately.
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        return lastError;
                    continue;
                }

                // Parsed by the tested projection: it also recovers tool calls and usage, which
                // the inline walk above could not see and therefore silently dropped.
                return ProviderWireFormat.ReadOpenAi(body, _providerLabel, model);
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                return Fail(ModelCallOutcome.Cancelled, model, $"ERROR: {_providerLabel} request cancelled because the mission was stopped.");
            }
            catch (OperationCanceledException)
            {
                lastError = Fail(ModelCallOutcome.Timeout, model, $"ERROR: {_providerLabel} request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).");
            }
            catch (HttpRequestException error)
            {
                return Fail(ModelCallOutcome.ConnectError, model, $"ERROR: Could not reach {_providerLabel}: {error.Message}");
            }
            catch (Exception error)
            {
                lastError = Fail(ModelCallOutcome.Error, model, $"ERROR: {_providerLabel} request failed: {error.Message} (attempt {attempt}/{retries}).");
            }
        }
        return lastError;
    }

    /// <summary>
    /// A failure envelope carrying which provider and model produced it. The operator-facing prose
    /// is byte for byte what it was — only the container changed.
    /// </summary>
    private ModelResponse Fail(ModelCallOutcome status, string model, string message) =>
        new() { Status = status, Content = message, Provider = _providerLabel, Model = model };

    private static string Truncate(string text, int max = 300) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// Anthropic Messages API client. Kept separate from <see cref="OpenAiCompatibleClient"/> because
/// Claude's request/response shape (top-level <c>max_tokens</c>, <c>x-api-key</c> header,
/// <c>content[]</c> block array) differs from the OpenAI-style contract.
/// </summary>
public sealed class AnthropicClient : IModelClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string _model;

    /// <summary>endpoint: optional Base URL override from Settings → Providers. Same normalization
    /// rationale as <see cref="OpenAiCompatibleClient"/> — accept the conventional "just the host
    /// prefix" form (e.g. "https://api.anthropic.com/v1") as well as the full path.</summary>
    public AnthropicClient(string? apiKey, string model, string? endpoint = null)
    {
        _apiKey = apiKey;
        _model = model;
        _endpoint = NormalizeEndpoint(endpoint);
    }

    /// <summary>Public for the same reason as <see cref="OpenAiCompatibleClient.NormalizeEndpoint"/>.</summary>
    public static string NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return DefaultEndpoint;
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/messages";
    }

    /// <summary>
    /// v3.3.0 (ADR-006): typed, with the body built by <see cref="ProviderWireFormat"/>.
    ///
    /// The projection is where Anthropic's two structural differences now live — the system prompt
    /// is a top-level field rather than a message, and tools use `input_schema` rather than
    /// `function.parameters`. The inline object it replaces could express neither, and would have
    /// sent a system message as user text.
    /// </summary>
    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        var model = request.Model ?? _model;
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Fail(ModelCallOutcome.ConfigError, model, "ERROR: Anthropic API key not configured. Add it in Settings → Providers.");

        var negotiated = ModelCapabilityCatalog.Negotiate(
            request, ModelCapabilityCatalog.For("anthropic", model));
        var payload = ProviderWireFormat.AnthropicBody(negotiated, model).ToJsonString();
        var lastError = Fail(ModelCallOutcome.Empty, model, "");
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.ModelCallTimeoutSeconds));
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                httpRequest.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                httpRequest.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(httpRequest, cts.Token).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = Fail(
                        response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                            ? ModelCallOutcome.AuthError : ModelCallOutcome.HttpError, model,
                        $"ERROR: Anthropic request failed ({(int)response.StatusCode}): {Truncate(body)}");
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        return lastError;
                    continue;
                }

                return ProviderWireFormat.ReadAnthropic(body, model);
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                return Fail(ModelCallOutcome.Cancelled, model, "ERROR: Anthropic request cancelled because the mission was stopped.");
            }
            catch (OperationCanceledException)
            {
                lastError = Fail(ModelCallOutcome.Timeout, model, $"ERROR: Anthropic request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).");
            }
            catch (HttpRequestException error)
            {
                return Fail(ModelCallOutcome.ConnectError, model, $"ERROR: Could not reach Anthropic: {error.Message}");
            }
            catch (Exception error)
            {
                lastError = Fail(ModelCallOutcome.Error, model, $"ERROR: Anthropic request failed: {error.Message} (attempt {attempt}/{retries}).");
            }
        }
        return lastError;
    }

    private static ModelResponse Fail(ModelCallOutcome status, string model, string message) =>
        new() { Status = status, Content = message, Provider = "anthropic", Model = model };

    private static string Truncate(string text, int max = 300) => text.Length <= max ? text : text[..max] + "…";
}
