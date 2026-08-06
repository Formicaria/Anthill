using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// Presents <see cref="OllamaCapabilityCache"/> — a static, host-keyed cache — as the instance
/// contract the core holds. v3.8.5.
///
/// An adapter rather than a rewrite of the cache, for two reasons. The cache's statics are reached
/// directly by <see cref="OllamaClient"/> on the call path and by its own tests, and converting it
/// to an instance would have meant threading it through both in the same change that moves the
/// assembly. And the shapes genuinely differ: the cache is keyed by HOST because one process can
/// talk to several Ollama servers, while the core asks by PROVIDER because that is the only thing a
/// routing decision knows. This is where that translation belongs.
/// </summary>
public sealed class OllamaCapabilityProbe : IModelCapabilityProbe
{
    private const string ProviderId = "ollama";
    private readonly string _host;

    /// <param name="host">The Ollama base URL, supplied by the composition root — the module does
    /// not read the core's configuration.</param>
    public OllamaCapabilityProbe(string host) => _host = host;

    private static bool IsOllama(string providerId) =>
        string.Equals(providerId, ProviderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null for any provider this probe does not serve, so the core falls back to the name table
    /// instead of believing an empty capability set. "I don't know" and "it supports nothing" are
    /// different answers, and conflating them is how a tool-calling model gets reported as broken.
    /// </summary>
    public ModelCapabilities? For(string providerId, string model) =>
        IsOllama(providerId) ? OllamaCapabilityCache.For(_host, model) : null;

    public IReadOnlyDictionary<string, ModelCapabilities> Snapshot(string providerId) =>
        IsOllama(providerId) ? OllamaCapabilityCache.Snapshot() : new Dictionary<string, ModelCapabilities>();

    public void Warm() => OllamaCapabilityCache.Warm(_host);
}
