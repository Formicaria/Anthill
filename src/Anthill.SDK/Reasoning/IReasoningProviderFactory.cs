namespace Anthill.SDK.Reasoning;

/// <summary>
/// The settings a provider needs at CALL time, read live rather than captured. v3.8.5.
///
/// The distinction is load-bearing. <c>ModelCallTimeoutSeconds</c> is read on every request today,
/// so an operator lowering it from Settings takes effect on the next call. Snapshotting it into
/// <see cref="ReasoningProviderContext"/> at construction would have quietly broken that for the
/// one provider whose client is cached — the local Ollama one — and the symptom would be "the
/// timeout setting does nothing, but only for local models, and only until restart". An interface
/// keeps the read where it always was.
/// </summary>
public interface IReasoningRuntimeOptions
{
    /// <summary>Per-call deadline. Read on every request.</summary>
    int ModelCallTimeoutSeconds { get; }
}

/// <summary>
/// Fixed settings for a provider constructed outside the colony — chiefly tests, which build a
/// client directly against a stub server and have no runtime to read from.
///
/// The value matches <c>AnthillRuntime.ModelCallTimeoutSeconds</c>'s default, so a provider built
/// this way behaves as an unconfigured colony would. It does NOT track that constant: this is a
/// standalone default, and the composition root always supplies live options, so the two can only
/// disagree where nothing is reading configuration anyway.
/// </summary>
public sealed class DefaultReasoningRuntimeOptions : IReasoningRuntimeOptions
{
    public static readonly DefaultReasoningRuntimeOptions Instance = new();
    public int ModelCallTimeoutSeconds => 120;
}

/// <summary>
/// Everything a module needs to build one provider, and nothing it should have to look up.
///
/// The credentials and the endpoint are resolved by the CORE, which owns the encrypted store, and
/// handed over already resolved. A module that reached into the database for its own API key would
/// need the database, and the boundary would be gone at the first provider.
/// </summary>
/// <param name="ProviderId">Catalog id: "ollama", "openai", "anthropic", "openrouter", ...</param>
/// <param name="Model">The model to serve. Never empty by the time it reaches a factory.</param>
/// <param name="ApiKey">Decrypted, or null for a provider that needs none.</param>
/// <param name="Endpoint">The configured base URL, or the catalog default.</param>
/// <param name="Options">Live runtime settings — see <see cref="IReasoningRuntimeOptions"/>.</param>
public sealed record ReasoningProviderContext(
    string ProviderId,
    string Model,
    string? ApiKey,
    string Endpoint,
    IReasoningRuntimeOptions Options);

/// <summary>
/// Builds reasoning providers. THE inversion point of the Core/Modules split.
///
/// Before this, <c>ModelRouter</c> contained two switch statements naming <c>OllamaClient</c>,
/// <c>OpenAiCompatibleClient</c> and <c>AnthropicClient</c> directly — which meant the core could
/// not compile, let alone run, without every provider implementation present. That is the single
/// edge that made "the core runs with no AI provider" untrue, and this interface is what removes
/// it: the core asks for a provider by id and gets one, or gets nothing and degrades.
///
/// Implemented in <c>Anthill.Modules.Reasoning</c>. Nothing in <c>Anthill.Core</c> may name an
/// implementation.
/// </summary>
public interface IReasoningProviderFactory
{
    /// <summary>
    /// Whether this factory can serve that provider id. Asked before <see cref="Create"/>, so a
    /// factory never has to return null and a caller never has to check for one.
    /// </summary>
    bool CanServe(string providerId);

    /// <summary>
    /// Build a provider. Called only after <see cref="CanServe"/> returned true for the same id.
    ///
    /// Must not perform I/O — no connection test, no model list, no health probe. A provider is
    /// built on the mission hot path, and a factory that dialled a remote host here would put a
    /// network round-trip in front of every keyed call.
    /// </summary>
    IReasoningProvider Create(ReasoningProviderContext context);
}
