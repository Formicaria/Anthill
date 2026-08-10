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
    /// The model name that used to be a built-in default, and is therefore not evidence of a choice.
    ///
    /// Removing the hardcoded default from the SOURCE was only half the job, and the half that does
    /// not help anyone who already ran Anthill. `SaveConfig()` serialises settings to
    /// `.anthill/config.json`, so every existing installation has `"ollama_model": "llama3.1:8b"`
    /// written to disk — put there by the default, not by the operator — and a config value looks
    /// exactly like a decision no matter where it came from.
    ///
    /// So on an upgraded install the colony would keep asking for a model the host may not have,
    /// with the same `model not found` on every call, and the release notes would say the hardcoding
    /// was removed. True of the code, false of the machine.
    /// </summary>
    public const string RetiredDefaultModel = "llama3.1:8b";

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
        var isRetiredDefault = string.Equals(configured, RetiredDefaultModel, StringComparison.OrdinalIgnoreCase);

        // A real choice is honoured without asking the host anything — the common case must not
        // depend on Ollama being up to answer.
        if (configured.Length > 0 && !isRetiredDefault)
            return new ModelChoice(ModelChoiceKind.Configured, configured,
                $"configured as '{configured}'", Array.Empty<string>());

        // The retired default is the ONE value that cannot be taken at face value, because it was
        // written by a default rather than chosen. It is honoured when the host actually has it —
        // plenty of people do run it deliberately — and treated as unchosen only when it is absent,
        // which is precisely the case where keeping it produces `model not found` forever.
        //
        // Scoped to this single string on purpose. Any OTHER configured model that is missing stays
        // configured and surfaces as "not installed": an explicit choice deserves an explicit error,
        // never a silent substitution.
        if (isRetiredDefault)
        {
            IReadOnlyList<string> present;
            try { present = lister(host) ?? Array.Empty<string>(); }
            catch
            {
                // Could not ask. Keep the operator's value rather than downgrading a possibly-real
                // choice on a transient outage; the reachability banner already covers this state.
                return new ModelChoice(ModelChoiceKind.Configured, configured,
                    $"configured as '{configured}'", Array.Empty<string>());
            }

            if (present.Any(m => string.Equals(m?.Trim(), configured, StringComparison.OrdinalIgnoreCase)))
                return new ModelChoice(ModelChoiceKind.Configured, configured,
                    $"configured as '{configured}'", Array.Empty<string>());
        }

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

        // Says WHY there is no chosen model, because "you never picked one" and "the one in your
        // config is a leftover default that is not installed" lead to different reactions.
        var lead = isRetiredDefault
            ? $"'{RetiredDefaultModel}' is in your configuration but is not installed at {host}, and it "
              + "was Anthill's own removed default rather than a choice you made, so it is being ignored"
            : "no model is configured";

        return models.Count switch
        {
            0 => new ModelChoice(ModelChoiceKind.NoneInstalled, "",
                $"{lead}, and {host} has no models installed. Pull one (`ollama pull <model>` — any "
                + "model Ollama can run will work), then pick it in Settings.", models),

            1 => new ModelChoice(ModelChoiceKind.SoleInstalled, models[0],
                $"{lead}; '{models[0]}' is the only model installed at {host}, so it is being used", models),

            _ => new ModelChoice(ModelChoiceKind.AmbiguousInstalled, "",
                $"{lead}, and {host} has {models.Count} models installed, so Anthill will not guess "
                + "which one should run the colony. Set ollama_model in Settings to one of: "
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
