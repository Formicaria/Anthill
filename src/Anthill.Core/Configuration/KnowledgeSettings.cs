namespace Anthill.Core.Configuration;

/// <summary>
/// The knowledge subsystem's settings, as one immutable value.
///
/// Modelled on <see cref="MissionReplayOptions"/> and for the same reason: this group gates a
/// capability with a real blast radius — it names the service the colony trusts as its source of
/// organizational fact, and it decides which knowledge each project may read — so it is replaced
/// wholesale by <c>ProjectConfig</c> rather than edited field by field after validation.
///
/// WHY THIS EXISTS AT ALL, when <c>Anthill.Modules.Knowledge</c> already has a <c>KnowledgeOptions</c>
/// that looks almost identical: the core may not reference a module. The module's type is the one it
/// reads at call time; this is the one the core projects from configuration. The composition root
/// owns the translation, which is exactly where a boundary crossing should be visible.
///
/// Holds the OFF state until a configuration is projected, so nothing can observe a window in which
/// knowledge appears configured because the config had not loaded yet.
/// </summary>
public sealed record KnowledgeSettings
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "";
    public string Token { get; init; } = "";
    public bool AllowRemote { get; init; }
    public int ProbeTimeoutMs { get; init; } = 2000;
    public int RetrievalTimeoutMs { get; init; } = 5000;
    public int IngestionTimeoutMs { get; init; } = 10000;
    public int DefaultTopK { get; init; } = 8;
    public int MaxContextChars { get; init; } = 12000;
    public int CacheSeconds { get; init; } = 30;

    /// <summary>
    /// ANTHILL project id to FORAGER project id. The scope boundary — see the note on
    /// <c>AnthillConfig.KnowledgeProjectMap</c>. Case-insensitive because project ids are compared
    /// that way everywhere else in the colony.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project for callers with no ANTHILL project. Never a fallback for an unmapped mission.</summary>
    public string DefaultProject { get; init; } = "";

    // ---- Managed engine (W3-03) ---------------------------------------------------------------
    // Attached mode uses Endpoint/Token above. Managed mode ignores them: the supervisor discovers
    // the endpoint from the engine's startup line and generates the credential per start.

    /// <summary>Whether the host starts and supervises its own FORAGER engine. Off is attached mode.</summary>
    public bool Managed { get; init; }

    /// <summary>Node runtime for the managed engine. Empty finds `node` on PATH. Managed mode only.</summary>
    public string RuntimePath { get; init; } = "";

    /// <summary>The bundled FORAGER server entry the managed engine runs. Managed mode only.</summary>
    public string EntryPath { get; init; } = "";

    /// <summary>The data directory the managed engine owns. Empty defaults under the colony state dir. Managed mode only.</summary>
    public string DataDir { get; init; } = "";

    /// <summary>The safe state. What every caller sees before a configuration has been projected.</summary>
    public static readonly KnowledgeSettings Off = new();

    /// <summary>
    /// Resolve the FORAGER project an ANTHILL project may read, or null when it is unmapped.
    ///
    /// NULL IS A REFUSAL, not an invitation to use <see cref="DefaultProject"/>. A mission whose
    /// project is unmapped must retrieve nothing: falling back to a default would mean a mission for
    /// project A quietly reading project B's knowledge, which is the one failure the scope model
    /// exists to make impossible.
    /// </summary>
    public string? ProjectRefFor(string? anthillProjectId)
    {
        if (string.IsNullOrWhiteSpace(anthillProjectId)) return null;
        return ProjectMap.TryGetValue(anthillProjectId, out var reference) && reference.Length > 0
            ? reference
            : null;
    }
}
