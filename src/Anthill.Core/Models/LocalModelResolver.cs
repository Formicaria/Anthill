using Anthill.Core.Configuration;

namespace Anthill.Core.Models;

/// <summary>Why the effective local model is what it is — or why there isn't one.</summary>
public enum ModelChoiceKind
{
    /// <summary>The operator named it, in config or the environment.</summary>
    Configured,
    /// <summary>Nothing was configured and the host holds exactly one model, so there was no choice
    /// to make.</summary>
    SoleInstalled,
    /// <summary>Nothing configured and the host holds none.</summary>
    NoneInstalled,
    /// <summary>Nothing configured and the host holds several. Anthill refuses to pick.</summary>
    AmbiguousInstalled,
    /// <summary>Nothing configured and the host could not be asked.</summary>
    HostUnreachable,
}

/// <summary>
/// The effective local model, and the reason for it.
/// </summary>
/// <param name="Model">Empty whenever <see cref="Resolved"/> is false — never a guess.</param>
/// <param name="Available">What the host reported, for the operator to choose from.</param>
public sealed record ModelChoice(
    ModelChoiceKind Kind,
    string Model,
    string Reason,
    IReadOnlyList<string> Available)
{
    public bool Resolved => Kind is ModelChoiceKind.Configured or ModelChoiceKind.SoleInstalled;
}

/// <summary>
/// Which local model the colony runs on. v3.8.33.
///
/// WHY THIS EXISTS
/// ---------------
/// `llama3.1:8b` was hardcoded in three places — <c>AnthillConfig</c>, <c>AnthillRuntime</c> and
/// <c>ProviderCatalog</c> — as the default local model. On any machine that had not pulled that
/// exact tag, every ant call failed with `model 'llama3.1:8b' not found` while the console reported
/// Ollama as reachable, because reachability and model presence are different questions and only the
/// first one was being surfaced.
///
/// A built-in model name is a guess about someone else's machine. Ollama has no default model and
/// cannot have one: what you can run is whatever you chose to pull.
///
/// THE RULE
/// --------
/// Configured wins. With nothing configured:
///
/// <list type="bullet">
/// <item>exactly ONE model installed — use it; there was no choice to make</item>
/// <item>NONE installed — refuse, and name the host</item>
/// <item>SEVERAL installed — refuse, and list them</item>
/// </list>
///
/// Refusing on ambiguity is the same rule <see cref="Anthill.SDK.Common.PatchApply"/> applies when
/// `old_content` matches twice: when the system cannot know which one you meant, saying so beats
/// picking. It matters more here than it looks. An auto-pick would happily select an embedding model
/// or a 0.5B draft model, and the colony would not fail — it would run, produce weak output, and
/// record that outcome as evidence. A mission that fails loudly costs a config line; one that
/// silently reasons badly costs trust in every result it produced.
/// </summary>
public static class LocalModelResolver
{
    /// <summary>Lists installed models for a host. Injected so this is testable without a server.</summary>
    public delegate IReadOnlyList<string> ModelLister(string host);

    /// <summary>
    /// Resolve without touching the network when a model is already configured — the common case,
    /// and the one that must never depend on Ollama being up to answer.
    /// </summary>
    /// <param name="configuredModel">`ollama_model` from config or ANTHILL_OLLAMA_MODEL.</param>
    /// <param name="host">The Ollama base URL, for the message when discovery is needed.</param>
    /// <param name="lister">Asks the host what it holds. May return empty; may throw, which is
    /// treated as unreachable rather than as "no models".</param>
    public static ModelChoice Resolve(string? configuredModel, string host, ModelLister lister)
    {
        var configured = (configuredModel ?? "").Trim();
        if (configured.Length > 0)
            return new ModelChoice(ModelChoiceKind.Configured, configured,
                $"configured as '{configured}'", Array.Empty<string>());

        IReadOnlyList<string> installed;
        try { installed = lister(host) ?? Array.Empty<string>(); }
        catch (Exception error)
        {
            return new ModelChoice(ModelChoiceKind.HostUnreachable, "",
                $"no model is configured, and {host} could not be asked what it has ({error.Message}). "
                + "Set ollama_model in Settings, or start Ollama.",
                Array.Empty<string>());
        }

        // Ordered so the message is stable run to run. An unstable list reads as flapping.
        var models = installed.Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        return models.Count switch
        {
            0 => new ModelChoice(ModelChoiceKind.NoneInstalled, "",
                $"no model is configured and {host} has none installed. Pull one (for example "
                + "`ollama pull <model>`), then set it in Settings.", models),

            1 => new ModelChoice(ModelChoiceKind.SoleInstalled, models[0],
                $"no model is configured; '{models[0]}' is the only one installed at {host}", models),

            _ => new ModelChoice(ModelChoiceKind.AmbiguousInstalled, "",
                $"no model is configured and {host} has {models.Count} installed, so Anthill will not "
                + $"guess which one should run the colony. Set ollama_model in Settings to one of: "
                + string.Join(", ", models), models),
        };
    }

    /// <summary>
    /// The same resolution against the live runtime configuration.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT cached. The operator can pull a model or change the setting while the colony
    /// is running, and a cached "no model installed" would outlive the fix — which is the exact shape
    /// of the problem this class was written to end.
    /// </remarks>
    public static ModelChoice Current(ModelLister lister) =>
        Resolve(AnthillRuntime.OllamaModel, AnthillRuntime.OllamaHost, lister);
}
