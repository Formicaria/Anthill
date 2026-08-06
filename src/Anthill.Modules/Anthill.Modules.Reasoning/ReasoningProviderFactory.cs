using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// Builds every reasoning provider ANTHILL ships. v3.8.5.
///
/// This class is the two switch statements that used to live inside <c>ModelRouter</c>, moved to
/// where they belong. Nothing about the mapping changed — the same ids produce the same clients
/// with the same arguments — but the direction of the dependency inverted: the core no longer names
/// these types, it asks for a provider by id and this answers.
///
/// The OpenRouter attribution headers stay here rather than travelling in
/// <see cref="ReasoningProviderContext"/>, because they are a fact about how one provider wants to
/// be addressed. A context carrying per-provider header quirks would be the core knowing about
/// OpenRouter again, one field further away.
/// </summary>
public sealed class ReasoningProviderFactory : IReasoningProviderFactory
{
    private static readonly HashSet<string> Served = new(StringComparer.OrdinalIgnoreCase)
    {
        "ollama", "openai", "perplexity", "openrouter", "anthropic",
    };

    public bool CanServe(string providerId) => Served.Contains(providerId);

    public IReasoningProvider Create(ReasoningProviderContext c) => c.ProviderId.ToLowerInvariant() switch
    {
        "ollama" => new OllamaClient(c.Model, c.Endpoint, c.Options),
        "openai" => new OpenAiCompatibleClient("OpenAI", c.Endpoint, c.ApiKey, c.Model, c.Options),
        "perplexity" => new OpenAiCompatibleClient("Perplexity", c.Endpoint, c.ApiKey, c.Model, c.Options),
        "openrouter" => new OpenAiCompatibleClient("OpenRouter", c.Endpoint, c.ApiKey, c.Model, c.Options,
            new Dictionary<string, string> { ["HTTP-Referer"] = "https://anthill.local", ["X-Title"] = "ANTHILL" }),
        "anthropic" => new AnthropicClient(c.ApiKey, c.Model, c.Options, c.Endpoint),

        // Unreachable: Resolve() only calls Create() after CanServe() said yes. Throwing rather
        // than returning a placeholder because reaching here means the two lists have drifted —
        // a build-time mistake that should be loud, not a runtime condition to degrade through.
        _ => throw new InvalidOperationException(
            $"ReasoningProviderFactory.CanServe admitted '{c.ProviderId}' but Create has no case for it."),
    };
}
