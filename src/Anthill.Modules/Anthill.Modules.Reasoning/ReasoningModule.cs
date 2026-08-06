using Anthill.SDK.Events;
using Anthill.SDK.Modules;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// The reasoning capability, as a module. v3.8.5.
///
/// Ollama, OpenAI, Perplexity, OpenRouter and Anthropic enter the colony through here and nowhere
/// else. If this module is not composed in, the core still starts, still plans, still dispatches
/// and still runs tools — every model call returns <c>UnavailableProvider</c>'s typed refusal
/// instead of an answer. That is the success criterion for this phase, and it is only true because
/// nothing in <c>Anthill.Core</c> names a type in this assembly.
/// </summary>
public sealed class ReasoningModule : IAnthillModule
{
    public string Name => "reasoning";

    public string Version => "3.8.5";

    /// <summary>
    /// Registration does NO I/O, per the <see cref="IAnthillModule"/> contract, and here that
    /// matters concretely: warming the Ollama capability cache means an HTTP call to a host that
    /// may not be running. Doing it here would make an unreachable Ollama into a colony that will
    /// not boot — for a component the colony is explicitly allowed to run without.
    ///
    /// The composition root warms the probe separately, after startup, off the request path.
    /// </summary>
    public void Register(IModuleContext context)
    {
        context.Events.Publish(new ColonyEvent
        {
            EventType = "module_registered",
            Message = "Reasoning providers available: ollama, openai, perplexity, openrouter, anthropic.",
            Metadata = new Dictionary<string, object?> { ["module"] = Name, ["version"] = Version },
        });
    }
}
