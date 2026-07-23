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

    public string Generate(string prompt, int retries = 2)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return $"ERROR: {_providerLabel} API key not configured. Add it in Settings → Providers.";

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[] { new { role = "user", content = prompt } },
        });
        var lastError = "";
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.ModelCallTimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                if (_extraHeaders is not null)
                    foreach (var (name, value) in _extraHeaders) request.Headers.TryAddWithoutValidation(name, value);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(request, cts.Token).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"ERROR: {_providerLabel} request failed ({(int)response.StatusCode}): {Truncate(body)}";
                    // Auth/permission failures will not heal on retry — surface immediately.
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        return lastError;
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                var content = "";
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c))
                    content = c.GetString()?.Trim() ?? "";
                return string.IsNullOrEmpty(content) ? $"{_providerLabel} returned an empty response." : content;
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                return $"ERROR: {_providerLabel} request cancelled because the mission was stopped.";
            }
            catch (OperationCanceledException)
            {
                lastError = $"ERROR: {_providerLabel} request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).";
            }
            catch (HttpRequestException error)
            {
                return $"ERROR: Could not reach {_providerLabel}: {error.Message}";
            }
            catch (Exception error)
            {
                lastError = $"ERROR: {_providerLabel} request failed: {error.Message} (attempt {attempt}/{retries}).";
            }
        }
        return lastError;
    }

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

    public string Generate(string prompt, int retries = 2)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return "ERROR: Anthropic API key not configured. Add it in Settings → Providers.";

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = 4096,
            messages = new[] { new { role = "user", content = prompt } },
        });
        var lastError = "";
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.ModelCallTimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(request, cts.Token).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"ERROR: Anthropic request failed ({(int)response.StatusCode}): {Truncate(body)}";
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        return lastError;
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                var text = new StringBuilder();
                if (doc.RootElement.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                    foreach (var block in blocks.EnumerateArray())
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                            && block.TryGetProperty("text", out var blockText))
                            text.Append(blockText.GetString());

                var result = text.ToString().Trim();
                return string.IsNullOrEmpty(result) ? "Anthropic returned an empty response." : result;
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                return "ERROR: Anthropic request cancelled because the mission was stopped.";
            }
            catch (OperationCanceledException)
            {
                lastError = $"ERROR: Anthropic request timed out after {AnthillRuntime.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).";
            }
            catch (HttpRequestException error)
            {
                return $"ERROR: Could not reach Anthropic: {error.Message}";
            }
            catch (Exception error)
            {
                lastError = $"ERROR: Anthropic request failed: {error.Message} (attempt {attempt}/{retries}).";
            }
        }
        return lastError;
    }

    private static string Truncate(string text, int max = 300) => text.Length <= max ? text : text[..max] + "…";
}
