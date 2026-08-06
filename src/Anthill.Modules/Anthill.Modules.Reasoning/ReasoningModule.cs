using Anthill.SDK.Events;
using Anthill.SDK.Modules;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// The reasoning capability, as a module. v3.8.5, made real in v3.8.6.
///
/// Ollama, OpenAI, Perplexity, OpenRouter and Anthropic enter the colony through here and nowhere
/// else. If this module is not loaded, the core still starts, still plans, still dispatches and
/// still runs tools — every model call returns <c>UnavailableProvider</c>'s typed refusal instead of
/// an answer. That is the success criterion for phase 2, and it holds only because nothing in
/// <c>Anthill.Core</c> names a type in this assembly.
///
/// v3.8.5 shipped this class registered by nobody: the API reached past the module system and poked
/// the core's provider registry with a factory it built itself. It worked, and it left
/// <see cref="IAnthillModule"/> as a contract with no caller — the exact defect this repository's
/// call-site audit exists to catch, introduced by the refactor that was supposed to prevent it.
/// Registration now goes through <see cref="IModuleContext"/>.
/// </summary>
public sealed class ReasoningModule : IAnthillModule
{
    private readonly string _ollamaHost;

    /// <param name="ollamaHost">Supplied by the composition root. The module does not read the
    /// core's configuration — that import is what kept these files in the core until v3.8.5.</param>
    public ReasoningModule(string ollamaHost) => _ollamaHost = ollamaHost;

    public string Name => "reasoning";

    public string Version => "3.8.6";

    /// <summary>
    /// Registration does NO I/O, per the <see cref="IAnthillModule"/> contract, and here that
    /// matters concretely: warming the Ollama capability cache means an HTTP call to a host that may
    /// not be running. Doing it here would turn an unreachable Ollama into a colony that will not
    /// boot — for a component the colony is explicitly allowed to run without.
    ///
    /// The composition root warms the probe separately, after startup, off the request path.
    /// </summary>
    public void Register(IModuleContext context)
    {
        context.RegisterReasoningProvider(new ReasoningProviderFactory());
        context.RegisterCapabilityProbe(new OllamaCapabilityProbe(_ollamaHost));

        context.Events.Publish(new ColonyEvent
        {
            EventType = EventTypes.ModuleRegistered,
            Message = "Reasoning providers available: ollama, openai, perplexity, openrouter, anthropic.",
            Metadata = new Dictionary<string, object?> { ["module"] = Name, ["version"] = Version },
        });
    }
}
