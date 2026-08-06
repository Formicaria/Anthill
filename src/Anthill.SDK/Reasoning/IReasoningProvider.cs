namespace Anthill.SDK.Reasoning;

/// <summary>
/// Anything that can be asked to reason. v3.8.4.
///
/// This is <c>IModelClient</c>, moved and renamed rather than replaced — and the distinction
/// matters. The refactor plan called for defining a fresh reasoning interface in the SDK, but
/// `IModelClient` was already exactly that: typed request in, typed response out, covering tool
/// calling, structured output, vision parts, reasoning content and token accounting, with the wire
/// encoding kept outside it. Writing a second interface beside a correct one would have created
/// precisely the duplication this refactor exists to remove.
///
/// The rename is the substantive part. "Model client" describes a thing that talks to a model,
/// which invites the assumption that a model is what the colony needs. "Reasoning provider"
/// describes a CAPABILITY the colony may or may not have — and the core is required to work when
/// it has none. Ollama, OpenAI, Anthropic and anything added later are interchangeable
/// implementations of this and nothing more.
///
/// Implementations live in modules. Nothing in <c>Anthill.Core</c> may name one.
/// </summary>
public interface IReasoningProvider
{
    /// <summary>
    /// The typed call. THE primary method — every provider implements this one.
    ///
    /// Transport belongs to the implementation: bounded retries, the ambient cancellation token
    /// from <see cref="ModelCallScope"/>, the per-call deadline and the status classification all
    /// live in each provider. What is deliberately NOT here is what goes on the wire and what comes
    /// back off it, so that encoding stays pure and testable without a provider running.
    /// </summary>
    ModelResponse Send(ModelRequest request, int retries = 2);

    /// <summary>
    /// The string call, a thin caller of the typed one rather than the other way round.
    ///
    /// The DIRECTION is the whole lesson of the v3.2.0 ant migration. A shim that widens a string
    /// into a typed value has to invent the information the string never carried, which makes it
    /// permanent by construction — that is how <c>string Run(Task, Mission)</c> survived four
    /// releases. This one narrows a typed result to text at the outermost edge, for callers that
    /// only ever wanted text: it discards rather than fabricates, and it deletes cleanly the moment
    /// the last such caller moves.
    /// </summary>
    ModelCallResult Generate(string prompt, int retries = 2) =>
        Send(ModelRequest.FromPrompt(prompt), retries).ToCallResult();
}
