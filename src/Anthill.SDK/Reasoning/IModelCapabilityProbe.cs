namespace Anthill.SDK.Reasoning;

/// <summary>
/// Asks a provider what its models can actually do, rather than inferring it from their names.
/// v3.8.5.
///
/// This exists because of a bug the colony already shipped. <c>ModelCapabilityCatalog</c> is a
/// hand-written name table — "a model called gemma:27b is text-only" — and v3.8.2 was released to
/// fix it reporting five roles as broken on every restart for a model that reports tool calling and
/// thinking. The lesson recorded then was that DISCOVERED capabilities beat declared ones.
///
/// The problem for this refactor is that discovery is inherently provider-specific: it means an
/// HTTP call to Ollama's <c>/api/show</c>. So the probe is an interface the core holds and a module
/// implements. When no module is registered the core falls back to the name table, which is exactly
/// the behaviour it had before discovery existed — degraded, documented, and not a crash.
/// </summary>
public interface IModelCapabilityProbe
{
    /// <summary>
    /// What this model actually supports, or <c>null</c> when this probe cannot describe it —
    /// a provider it does not serve, a host it cannot reach, a model it has never seen.
    ///
    /// Null rather than a default-valued <see cref="ModelCapabilities"/>, because "I don't know"
    /// and "it supports nothing" are different answers and the caller treats them differently: the
    /// first falls back to the name table, the second is believed.
    /// </summary>
    ModelCapabilities? For(string providerId, string model);

    /// <summary>
    /// Everything this probe currently knows about that provider, keyed by model. Empty when it
    /// serves a different provider or has not warmed yet. Used to answer "is there a tool-capable
    /// model available", never to make a routing decision on its own.
    /// </summary>
    IReadOnlyDictionary<string, ModelCapabilities> Snapshot(string providerId);

    /// <summary>
    /// Populate the cache ahead of first use. Called at startup, off the request path.
    ///
    /// v3.8.2's defect was an ORDERING one — the fitness report ran before the warm completed and
    /// judged every route against the fallback table — so anything reading capabilities at startup
    /// must wait for this rather than race it.
    /// </summary>
    void Warm();
}
