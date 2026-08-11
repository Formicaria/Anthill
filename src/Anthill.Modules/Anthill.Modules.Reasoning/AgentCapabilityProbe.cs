using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// What an installed CLI agent can do. v0.3.8.41.
///
/// Without this, routing an ant to Claude Code reported it as unfit for its own contract. The
/// fitness check asks <c>ModelCapabilityCatalog</c>, which matches model-name fragments and then a
/// provider table, and falls through to <c>TextOnly</c> — "assume the least" — for anything it has
/// never heard of. That is the right default for an unknown MODEL and the wrong answer for a full
/// coding agent: the boot log said `ui_cartographer` was routed to something "missing tool calling"
/// when the thing it was routed to is a tool-calling agent.
///
/// A probe rather than an entry in that table, because the table lives in Anthill.SDK and the agent
/// catalogue lives here. <c>ReasoningProviders.Capabilities</c> is consulted FIRST for exactly this
/// reason — a module knows things about its own providers that a shared name table cannot.
///
/// Null for anything that is not a catalogued agent, which the interface documents as the
/// difference that matters: "I don't know" falls back to the name table, "it supports nothing" is
/// believed. Answering for providers this probe does not serve would silently override Ollama's own
/// discovered capabilities with a guess.
/// </summary>
public sealed class AgentCapabilityProbe : IModelCapabilityProbe
{
    /// <summary>
    /// What every agent in the catalogue can do.
    ///
    /// Declared rather than discovered, and that is a deliberate limit worth stating: these tools
    /// have no capability endpoint to ask, so this is a claim about the class of thing they are,
    /// not a measurement. It is defensible because the catalogue only admits agents that edit
    /// files, call tools and return structured work — that is the bar for being in it at all, and
    /// an agent that could not would not belong.
    ///
    /// The context window is deliberately null. It varies by whichever model the operator has
    /// configured inside the agent, which Anthill does not know and must not invent — and null
    /// means unknown, which the requirement check treats as "do not warn", rather than a limit.
    /// </summary>
    private static readonly ModelCapabilities AgentCapabilities = new()
    {
        ToolCalling = true,
        StructuredOutput = true,
        Streaming = false,     // Send() is one shot: the process runs, then its output is read.
        Vision = false,        // Not offered through the CLI surface Anthill drives.
        Embeddings = false,
        Reasoning = true,
        ContextWindowTokens = null,
    };

    public ModelCapabilities? For(string providerId, string model) =>
        AgentCliCatalog.ById(providerId) is not null ? AgentCapabilities : null;

    /// <summary>
    /// Everything known about that provider, keyed by model. One entry for an agent — itself — and
    /// empty for anything else, so "is there a tool-capable model here?" gets a truthful answer
    /// without this probe claiming providers it does not serve.
    /// </summary>
    public IReadOnlyDictionary<string, ModelCapabilities> Snapshot(string providerId)
    {
        var agent = AgentCliCatalog.ById(providerId);
        return agent is null
            ? new Dictionary<string, ModelCapabilities>()
            : new Dictionary<string, ModelCapabilities> { [agent.DisplayName] = AgentCapabilities };
    }

    /// <summary>
    /// Nothing to warm, and that is a property rather than an omission.
    ///
    /// This probe answers from the catalogue, so it is already correct on the first call. The
    /// interface documents why Warm exists at all: v3.8.2's defect was an ORDERING one — the fitness
    /// report ran before Ollama's warm completed and judged every route against the fallback table.
    /// A probe with no cache cannot lose that race, so agents are reported correctly even by
    /// something that asks during startup.
    /// </summary>
    public void Warm() { }
}
