using Anthill.SDK.Reasoning;

namespace Anthill.Core.Models;

/// <summary>
/// The provider you get when there is no provider. v3.8.5.
///
/// This is <c>PlaceholderClient</c>, widened. It used to answer only for provider ids the router
/// recognised but had no client for; now it also answers when NO reasoning module is registered at
/// all — which, after phase 2b, is a perfectly ordinary way to run the colony. Missions still plan,
/// tasks still dispatch, tools still run, and anything that wanted a model gets a clean typed
/// refusal instead of a NullReferenceException from a router that assumed one existed.
///
/// It returns rather than throws, and that is the contract this type exists to keep: a client never
/// throws across the ant boundary. An ant receiving <see cref="ModelCallOutcome.Error"/> already
/// knows how to report a degraded generation, and the mission evaluator already refuses to mark a
/// mission verified on one. Throwing here would bypass all of that machinery to say the same thing
/// less usefully.
/// </summary>
public sealed class UnavailableProvider : IModelClient
{
    private readonly string _provider;
    private readonly string _reason;

    private UnavailableProvider(string provider, string reason)
    {
        _provider = provider;
        _reason = reason;
    }

    /// <summary>No reasoning module is registered — the colony is running without AI entirely.</summary>
    public static UnavailableProvider NoModuleRegistered(string provider) =>
        new(provider, "no reasoning module is registered in this build");

    /// <summary>A module is registered, but none of its factories serves this provider id.</summary>
    public static UnavailableProvider NotServed(string provider) =>
        new(provider, "no registered reasoning module serves this provider");

    /// <summary>
    /// The provider is available but no MODEL could be chosen. v3.8.33.
    ///
    /// Carries the resolver's own sentence, because "which model" is a question only the operator can
    /// settle and a generic refusal would send them looking in the wrong place. The alternative — a
    /// built-in default model name — is what this release removed: it turned "you have not chosen a
    /// model" into `model 'llama3.1:8b' not found` on every machine that had pulled something else.
    /// </summary>
    public static UnavailableProvider NoModelChosen(string provider, string reason) =>
        new(provider, reason);

    // Error, deliberately, not ConfigError: this classified as the generic Error before the typed
    // boundary, and Error maps to CircuitSignal.Neutral. Promoting it to ConfigError would make it
    // Healthy and start CLEARING a provider's breaker — a behaviour change smuggled in under a
    // refactor. The status recorded here is the one this path already had.
    public ModelResponse Send(ModelRequest request, int retries = 2) =>
        new()
        {
            Status = ModelCallOutcome.Error,
            Content = $"ERROR: {_provider} is unavailable — {_reason}.",
            Provider = _provider,
            Model = request.Model,
        };
}
