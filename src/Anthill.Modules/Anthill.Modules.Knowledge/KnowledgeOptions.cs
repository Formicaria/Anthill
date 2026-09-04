namespace Anthill.Modules.Knowledge;

/// <summary>
/// Everything the knowledge module reads from configuration.
///
/// A snapshot type plus a <see cref="KnowledgeOptionsSource"/> delegate, following
/// <c>ConfiguredWebhookAdapter</c>: the delegate is invoked ON EVERY CALL and never captured, so an
/// operator who disables knowledge, or repoints the endpoint, takes effect on the next request
/// rather than the next restart. Capturing the snapshot at construction is the bug this shape
/// exists to prevent — the module is built once, at startup, and lives for the process.
///
/// The module cannot read <c>AnthillRuntime</c> (that is the core, which it may not reference), so
/// the composition root supplies the delegate. That is the same route <c>IToolRuntimeOptions</c>
/// takes, and it is why the module stays testable without a running colony.
/// </summary>
public sealed record KnowledgeOptions
{
    /// <summary>
    /// Master switch. OFF BY DEFAULT, and that default is load-bearing: an existing colony whose
    /// config has never heard of knowledge must start unchanged and register no knowledge tools.
    /// Rule 15 is enforced here first and everywhere else second.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL of the FORAGER service, e.g. <c>http://127.0.0.1:8790</c>.</summary>
    public string Endpoint { get; init; } = "http://127.0.0.1:8790";

    /// <summary>
    /// Bearer token, for operators who have put FORAGER behind an authenticating proxy. FORAGER
    /// itself has no authentication — it expects to own its loopback interface. Empty is the normal
    /// case and is not a warning.
    /// </summary>
    public string Token { get; init; } = "";

    /// <summary>
    /// Whether a non-loopback endpoint is permitted. OFF by default. FORAGER has no auth of its
    /// own, so pointing ANTHILL at one across a network is a decision with a real blast radius, and
    /// it should be one an operator makes on purpose rather than one a copied config makes for them.
    /// </summary>
    public bool AllowRemoteEndpoint { get; init; }

    /// <summary>Ceiling for a health probe. Short: it runs on console polls and must never be the slow thing.</summary>
    public int ProbeTimeoutMs { get; init; } = 2000;

    /// <summary>Ceiling for a search or a retrieval. Charged against the mission's clock, so it is bounded.</summary>
    public int RetrievalTimeoutMs { get; init; } = 5000;

    /// <summary>Ceiling for an ingestion control call. These return immediately by design — FORAGER
    /// queues and answers 202 — so this only has to cover the queueing round trip.</summary>
    public int IngestionTimeoutMs { get; init; } = 10000;

    public int DefaultTopK { get; init; } = 8;
    public int MaxContextChars { get; init; } = 12000;

    /// <summary>
    /// How long a retrieval result may be reused. Short by design. Cache keys include the resolved
    /// scope, so an entry can never be served across a project boundary — see
    /// <c>KnowledgeScope.CacheKey</c>, which exists for exactly this.
    /// </summary>
    public int CacheSeconds { get; init; } = 30;

    /// <summary>
    /// ANTHILL project id to FORAGER project id. This map IS the scope boundary: a mission whose
    /// project has no entry resolves to <c>KnowledgeScope.Unresolved</c> and retrieves nothing,
    /// rather than falling back to a default and reading someone else's knowledge base.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The FORAGER project used when a caller has no ANTHILL project — direct console queries and
    /// CLI use. Deliberately NOT a fallback for a mission whose project is unmapped: a mission that
    /// silently borrowed the default scope would be the cross-project leak Rule 12 forbids.
    /// </summary>
    public string DefaultProjectRef { get; init; } = "";

    /// <summary>
    /// Read knowledge from an exported package on disk instead of a live service. For air-gapped
    /// installs and for missions that must run against a pinned snapshot. Empty means live HTTP.
    /// </summary>
    public string PackagePath { get; init; } = "";

    /// <summary>Whether the loaded configuration can actually be used, and if not, why.</summary>
    public string? Unusable()
    {
        if (!Enabled) return "knowledge is disabled in configuration (knowledge_enabled)";
        if (PackagePath.Length > 0) return null;
        if (string.IsNullOrWhiteSpace(Endpoint)) return "no knowledge endpoint is configured (knowledge_forager_endpoint)";
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri))
            return $"the configured knowledge endpoint is not a valid absolute URL: {Endpoint}";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"the knowledge endpoint must be http or https, not '{uri.Scheme}'";
        if (!AllowRemoteEndpoint && !IsLoopback(uri))
            return $"the knowledge endpoint '{uri.Host}' is not loopback and knowledge_forager_allow_remote is false. "
                 + "FORAGER has no authentication of its own; enable this only behind a trusted proxy.";
        return null;
    }

    /// <summary>
    /// Loopback by parsed ADDRESS where the host is one, so <c>127.0.0.1</c>, <c>::1</c> and the
    /// whole 127/8 range all answer correctly rather than only the one spelling.
    ///
    /// The literal "localhost" is accepted as well, and that is a judgement rather than an
    /// oversight: it is what every default config and every README says, and refusing it would make
    /// the safe path the awkward one. The residual risk is a hosts file that points localhost
    /// somewhere else — which is a machine already under someone else's control, and not a threat
    /// this check could meaningfully survive anyway. ANY OTHER NAME IS NOT LOOPBACK: an unresolvable
    /// or unparseable host fails closed and needs knowledge_forager_allow_remote set deliberately.
    /// </summary>
    internal static bool IsLoopback(Uri uri)
    {
        if (System.Net.IPAddress.TryParse(uri.Host, out var address))
            return System.Net.IPAddress.IsLoopback(address);
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// How the module reads its options. Invoked per call; never cached by the caller.
/// </summary>
public delegate KnowledgeOptions KnowledgeOptionsSource();
