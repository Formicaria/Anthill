using Anthill.Core.Configuration;
using Anthill.SDK.Reasoning;

namespace Anthill.Core.Models;

/// <summary>
/// What reasoning capability this process has, if any. v3.8.5.
///
/// The composition root (<c>Anthill.Api</c>, or the CLI) registers a reasoning module at startup;
/// <see cref="ModelRouter"/> asks here for a provider. Nothing in the core names an implementation,
/// and an empty registry is a supported state, not an error — that is the whole point of the phase.
///
/// STATIC, which deserves a defence given ADR-001 spent a release moving the Queen's composition
/// off mutable statics. The difference is what is being held. ADR-001 was about per-Queen
/// CONFIGURATION — gates two Queens could disagree about, which is why a profile is passed in. This
/// holds which assemblies were loaded into the process, which every Queen in a process necessarily
/// agrees about. Threading it through <c>Queen</c> → <c>ModelRouter</c> as a parameter would have
/// meant every one of the several hundred tests that construct a bare <c>new Queen()</c> silently
/// switching to no-provider behaviour, which is a large behavioural change wearing a refactor's
/// clothes.
/// </summary>
public static class ReasoningProviders
{
    private static readonly List<IReasoningProviderFactory> Factories = new();
    private static readonly object Gate = new();
    private static IModelCapabilityProbe? _probe;

    // No `Any` property and no `Reset()`. Both existed in the first draft of this class and both
    // were removed before merge, for the reasons this repository's own audit tests exist to catch:
    // `Any` had no caller outside a test that asserted on it, and `Reset()` was actively harmful —
    // it invites exactly the process-global mutation that ResolveFrom exists to avoid, and a test
    // reaching for it opens a window where a parallel collection sees a colony with no providers.
    // If a status surface later needs to report whether reasoning is available, it should be added
    // then, with the endpoint that reads it.

    /// <summary>
    /// Discovers what models actually support, when a module supplies one. Null means the core
    /// falls back to <see cref="ModelCapabilityCatalog"/>'s name table — degraded but functional,
    /// and exactly the behaviour that predates capability discovery.
    /// </summary>
    public static IModelCapabilityProbe? Capabilities
    {
        get { lock (Gate) return _probe; }
    }

    /// <summary>
    /// Register a module's factory. Called once per module at startup, before any mission runs.
    /// </summary>
    public static void Register(IReasoningProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            if (!Factories.Contains(factory)) Factories.Add(factory);
        }
    }

    /// <summary>Register the capability probe. Last registration wins.</summary>
    public static void RegisterProbe(IModelCapabilityProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (Gate) _probe = probe;
    }

    private static LocalModelResolver.ModelLister? _localModels;

    /// <summary>
    /// Lists the models a LOCAL host currently holds, when a composition root supplies a way to ask.
    /// v3.8.33.
    ///
    /// Registered rather than implemented here for the same reason provider construction is:
    /// <c>Anthill.Core</c> does not make HTTP calls to providers (ADR-007). The core needs the
    /// ANSWER — to resolve "which model" when the operator has not chosen one — not the transport.
    ///
    /// Unregistered is a real state and resolves to "cannot ask", which becomes a refusal that names
    /// the host. It must never resolve to a built-in model name; that is precisely the hardcoding
    /// v3.8.33 removed.
    /// </summary>
    public static void RegisterLocalModelLister(LocalModelResolver.ModelLister lister)
    {
        ArgumentNullException.ThrowIfNull(lister);
        lock (Gate) _localModels = lister;
    }

    /// <summary>
    /// Ask the registered lister what <paramref name="host"/> holds. Throws when none is registered,
    /// which <see cref="LocalModelResolver"/> reports as "the host could not be asked" — distinct
    /// from "the host has no models", because they need different fixes.
    /// </summary>
    public static IReadOnlyList<string> ListLocalModels(string host)
    {
        LocalModelResolver.ModelLister? lister;
        lock (Gate) lister = _localModels;

        return lister is null
            ? throw new InvalidOperationException("no model discovery is registered in this runtime")
            : lister(host);
    }

    /// <summary>
    /// Build a provider, or an <see cref="UnavailableProvider"/> that explains why not.
    ///
    /// Never null and never throws. A routing decision has already been made by the time this is
    /// called — the mission is running, the ant is mid-task — so "there is no provider" has to
    /// arrive as a result the ant can report, not as an exception unwinding the task.
    ///
    /// First factory whose <see cref="IReasoningProviderFactory.CanServe"/> answers true wins, in
    /// registration order. Ordered rather than "one factory per id" because the useful override —
    /// a local mock, a recording proxy — is registered after the real one and should take
    /// precedence, and a dictionary keyed by id would make that a silent overwrite instead.
    /// </summary>
    public static IModelClient Resolve(string providerId, string model, string? apiKey, string endpoint)
    {
        List<IReasoningProviderFactory> snapshot;
        lock (Gate) snapshot = new List<IReasoningProviderFactory>(Factories);
        return ResolveFrom(snapshot, providerId, model, apiKey, endpoint);
    }

    /// <summary>
    /// The resolution itself, over an EXPLICIT factory list. Internal, and it exists for testing —
    /// but not as a convenience.
    ///
    /// The obvious way to test the no-provider path would be to empty the registry and then assert.
    /// That is a trap here: this registry is process-global and xUnit runs collections in
    /// PARALLEL, so a test emptying it opens a window in which unrelated tests resolving a provider
    /// see no module and fail somewhere else entirely — intermittently, and with a symptom pointing
    /// nowhere near the cause. Passing the list in removes the shared state from the test rather
    /// than trying to synchronise around it, and still exercises the real code path.
    /// </summary>
    internal static IModelClient ResolveFrom(IReadOnlyList<IReasoningProviderFactory> factories,
        string providerId, string model, string? apiKey, string endpoint)
    {
        if (factories.Count == 0) return UnavailableProvider.NoModuleRegistered(providerId);

        var context = new ReasoningProviderContext(providerId, model, apiKey, endpoint, RuntimeOptions.Instance);
        for (var i = factories.Count - 1; i >= 0; i--)
        {
            if (!factories[i].CanServe(providerId)) continue;
            var provider = factories[i].Create(context);
            // Adapt: the SDK contract is IReasoningProvider; the core's cache and the API surface
            // still speak IModelClient. The alias interface adds no members, so this wrapper is
            // pure plumbing and disappears when IModelClient is deleted a release from now.
            return provider as IModelClient ?? new ReasoningProviderAdapter(provider);
        }
        return UnavailableProvider.NotServed(providerId);
    }

    /// <summary>
    /// Live runtime settings for providers. Reads through to <see cref="AnthillRuntime"/> on every
    /// access rather than capturing, so an operator changing the call timeout mid-run affects the
    /// next call — including on the cached local client.
    /// </summary>
    private sealed class RuntimeOptions : IReasoningRuntimeOptions
    {
        public static readonly RuntimeOptions Instance = new();
        public int ModelCallTimeoutSeconds => AnthillRuntime.ModelCallTimeoutSeconds;
    }

    /// <summary>Presents an <see cref="IReasoningProvider"/> under the core's older alias.</summary>
    private sealed class ReasoningProviderAdapter : IModelClient
    {
        private readonly IReasoningProvider _inner;
        public ReasoningProviderAdapter(IReasoningProvider inner) => _inner = inner;
        public ModelResponse Send(ModelRequest request, int retries = 2) => _inner.Send(request, retries);
        public ModelCallResult Generate(string prompt, int retries = 2) => _inner.Generate(prompt, retries);
    }
}
