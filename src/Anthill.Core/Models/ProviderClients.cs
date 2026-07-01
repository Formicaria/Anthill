using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
        _endpoint = endpoint;
        _apiKey = apiKey;
        _model = model;
        _extraHeaders = extraHeaders;
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
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                if (_extraHeaders is not null)
                    foreach (var (name, value) in _extraHeaders) request.Headers.TryAddWithoutValidation(name, value);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(request).GetAwaiter().GetResult();
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
            catch (TaskCanceledException)
            {
                lastError = $"ERROR: {_providerLabel} request timed out (attempt {attempt}/{retries}).";
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
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private readonly string? _apiKey;
    private readonly string _model;

    public AnthropicClient(string? apiKey, string model)
    {
        _apiKey = apiKey;
        _model = model;
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
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = Http.SendAsync(request).GetAwaiter().GetResult();
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
            catch (TaskCanceledException)
            {
                lastError = $"ERROR: Anthropic request timed out (attempt {attempt}/{retries}).";
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
