namespace Anthill.Core.Models;

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
/// <see cref="ModelRouter"/> will route any role to it.
/// </summary>
public static class ProviderCatalog
{
    public static readonly ProviderInfo Ollama = new(
        Id: "ollama", Name: "Ollama (local)", Kind: "free-local",
        Description: "Runs models on your own machine via Ollama. No API key and no per-token cost.",
        RequiresKey: false, DefaultEndpoint: null, KeyHelpUrl: "https://ollama.com",
        DefaultModel: "llama3.1:8b", Models: Array.Empty<string>());

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

    public static readonly IReadOnlyList<ProviderInfo> All = new[] { Ollama, OpenAi, Anthropic, Perplexity, OpenRouter };

    public static readonly HashSet<string> KnownProviders =
        new(All.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>Providers that need a stored API key — everything except local Ollama.</summary>
    public static readonly HashSet<string> KeyedProviders =
        new(All.Where(p => p.RequiresKey).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

    public static ProviderInfo? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
