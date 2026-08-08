namespace Anthill.SDK.Reasoning;

/// <summary>
/// Static metadata about a model provider the colony can talk to: how to reach it, whether it
/// needs a stored API key, and a curated starter model list for the console's dropdowns. This is
/// display/config metadata only — no secrets live here (see <c>provider_credentials</c> for keys).
/// </summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Kind,               // "free-local" | "paid" | "freemium"
    string Description,
    bool RequiresKey,
    string? DefaultEndpoint,
    string KeyHelpUrl,
    string DefaultModel,
    string[] Models);

/// <summary>
/// The fixed set of providers ANTHILL knows how to route to. Ollama is always available and
/// keyless (local); everything else needs a connection configured in Settings → Providers before
/// the core's router will route any role to it.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>
    /// Ollama has NO default model, and v3.8.33 stopped pretending otherwise.
    ///
    /// `DefaultModel: "llama3.1:8b"` was a guess about the operator's machine. A hosted provider can
    /// have a default because the provider owns the model list; Ollama serves whatever you pulled, so
    /// the only honest catalog answer is "ask the host". <c>LocalModelResolver</c> does that.
    /// </summary>
    public static readonly ProviderInfo Ollama = new(
        Id: "ollama", Name: "Ollama (local)", Kind: "free-local",
        Description: "Runs models on your own machine via Ollama. No API key and no per-token cost.",
        RequiresKey: false, DefaultEndpoint: null, KeyHelpUrl: "https://ollama.com",
        DefaultModel: "", Models: Array.Empty<string>());

    public static readonly ProviderInfo OpenAi = new(
        Id: "openai", Name: "OpenAI (ChatGPT)", Kind: "paid",
        Description: "GPT models via the OpenAI API. Pay-as-you-go, billed per token.",
        RequiresKey: true, DefaultEndpoint: "https://api.openai.com/v1/chat/completions",
        KeyHelpUrl: "https://platform.openai.com/api-keys",
        DefaultModel: "gpt-4o-mini",
        Models: new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "o4-mini" });

    public static readonly ProviderInfo Anthropic = new(
        Id: "anthropic", Name: "Anthropic (Claude)", Kind: "paid",
        Description: "Claude models via the Anthropic API. Pay-as-you-go, billed per token.",
        RequiresKey: true, DefaultEndpoint: "https://api.anthropic.com/v1/messages",
        KeyHelpUrl: "https://console.anthropic.com/settings/keys",
        DefaultModel: "claude-sonnet-4-5",
        Models: new[] { "claude-opus-4-1", "claude-sonnet-4-5", "claude-haiku-4-5" });

    public static readonly ProviderInfo Perplexity = new(
        Id: "perplexity", Name: "Perplexity", Kind: "paid",
        Description: "Perplexity's web-grounded Sonar models. Pay-as-you-go API access.",
        RequiresKey: true, DefaultEndpoint: "https://api.perplexity.ai/chat/completions",
        KeyHelpUrl: "https://www.perplexity.ai/settings/api",
        DefaultModel: "sonar",
        Models: new[] { "sonar", "sonar-pro", "sonar-reasoning" });

    public static readonly ProviderInfo OpenRouter = new(
        Id: "openrouter", Name: "OpenRouter", Kind: "paid",
        Description: "One key, many hosted models (including some free-tier options) from multiple labs.",
        RequiresKey: true, DefaultEndpoint: "https://openrouter.ai/api/v1/chat/completions",
        KeyHelpUrl: "https://openrouter.ai/keys",
        DefaultModel: "openai/gpt-4o-mini",
        Models: new[]
        {
            "openai/gpt-4o-mini", "anthropic/claude-sonnet-4.5",
            "meta-llama/llama-3.3-70b-instruct", "deepseek/deepseek-chat",
        });

    /*
     * v3.3.0 (ADR-006) — local OpenAI-compatible servers.
     *
     * These three are the claim in ADR-006 made good: they need no new client, because
     * OpenAiCompatibleClient already speaks /v1/chat/completions and that is exactly what they
     * serve. What each one needs is a base URL and a key policy.
     *
     * RequiresKey: false, and that is the substantive difference from the hosted providers. All
     * three run on the operator's own machine or network and accept any bearer token (commonly
     * none at all), so demanding a key would make a keyless local server unreachable through the
     * settings UI — the exact coupling this architecture exists to remove.
     *
     * Models is empty for all three for the same reason it is empty for Ollama: the list is
     * whatever the operator has loaded, so it is DYNAMIC and the capabilities endpoint reports it
     * as such rather than claiming the server offers nothing.
     */

    public static readonly ProviderInfo LmStudio = new(
        Id: "lmstudio", Name: "LM Studio (local)", Kind: "free-local",
        Description: "Local models served by LM Studio's OpenAI-compatible endpoint. No key, no per-token cost.",
        RequiresKey: false, DefaultEndpoint: "http://localhost:1234/v1/chat/completions",
        KeyHelpUrl: "https://lmstudio.ai",
        DefaultModel: "", Models: Array.Empty<string>());

    public static readonly ProviderInfo Vllm = new(
        Id: "vllm", Name: "vLLM (self-hosted)", Kind: "free-local",
        Description: "A vLLM server on your own hardware, through its OpenAI-compatible API.",
        RequiresKey: false, DefaultEndpoint: "http://localhost:8000/v1/chat/completions",
        KeyHelpUrl: "https://docs.vllm.ai",
        DefaultModel: "", Models: Array.Empty<string>());

    public static readonly ProviderInfo LlamaCpp = new(
        Id: "llamacpp", Name: "llama.cpp server (local)", Kind: "free-local",
        Description: "A llama.cpp server on your own machine, through its OpenAI-compatible API.",
        RequiresKey: false, DefaultEndpoint: "http://localhost:8080/v1/chat/completions",
        KeyHelpUrl: "https://github.com/ggml-org/llama.cpp",
        DefaultModel: "", Models: Array.Empty<string>());

    public static readonly IReadOnlyList<ProviderInfo> All =
        new[] { Ollama, OpenAi, Anthropic, Perplexity, OpenRouter, LmStudio, Vllm, LlamaCpp };

    public static readonly HashSet<string> KnownProviders =
        new(All.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>Providers that need a stored API key — everything except local Ollama.</summary>
    public static readonly HashSet<string> KeyedProviders =
        new(All.Where(p => p.RequiresKey).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

    public static ProviderInfo? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
