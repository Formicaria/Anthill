using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// How an artifact came to exist — beyond WHO made it. v0.3.8.57.
///
/// <see cref="Artifact"/> has recorded the producer ROLE since v3.8.19, and that is the only thing it
/// has ever recorded about production. So "a coder wrote this patch set" was answerable and "with
/// which model, on which machine, running which version of the colony" was not — which makes a stored
/// artifact unreproducible in the only sense that matters. A 7B and a 70B both leave `producer_role:
/// coder` behind.
///
/// WHAT IS HERE IS WHAT CAN BE TRUTHFULLY STATED. Every field below has a real producer, and the one
/// that could not be filled honestly is absent rather than declared. That rule is the whole reason
/// this release keeps finding old defects: `RequiredInputArtifactTypes`, `EvidenceKinds.SchemaValid`
/// and `Task.InputArtifactIds` were all declared before anything populated them, and each sat unread
/// for releases while looking, to a reader, exactly like a working feature.
///
/// See ArtifactProvenanceTests for the facet-by-facet ledger of what the original brief asked for,
/// where each one actually lives, and which remain genuinely unproduced.
/// </summary>
public sealed record ArtifactProvenance
{
    /// <summary>The colony build that produced it. Reproduction starts by knowing what to run.</summary>
    [JsonPropertyName("colony_version")] public string? ColonyVersion { get; init; }

    /// <summary>OS family and runtime major — the same fingerprint FailureContext records.</summary>
    [JsonPropertyName("environment_fingerprint")] public string? EnvironmentFingerprint { get; init; }

    /// <summary>
    /// Which runtime executed the producing task — the local worker, or a remote node. Distinct from
    /// the ROLE: the role says what job it was doing, this says where the work happened.
    /// </summary>
    [JsonPropertyName("runtime_node")] public string? RuntimeNode { get; init; }

    /// <summary>
    /// The provider and model that ACTUALLY served the producing call, not the configured route.
    /// Null means no model was involved, which is a fact rather than a gap — see
    /// <see cref="ModelInvolved"/>.
    /// </summary>
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }

    /// <summary>The tool whose output this artifact came from, when a tool produced it.</summary>
    [JsonPropertyName("tool")] public string? Tool { get; init; }

    /// <summary>
    /// How many model and tool calls the producing execution made. Cheap to carry and it answers the
    /// question that decides how much to trust an artifact: was this computed or was it generated?
    /// </summary>
    [JsonPropertyName("model_calls")] public int ModelCalls { get; init; }
    [JsonPropertyName("tool_calls")] public int ToolCalls { get; init; }

    /// <summary>
    /// True when a model served the producing call. Stated EXPLICITLY rather than inferred from a
    /// null model, because those are different claims: "no model was involved" is a property of the
    /// work, while "the model is unknown" is a property of the record. Collapsing them would let a
    /// provenance gap read as a determinism guarantee.
    /// </summary>
    [JsonPropertyName("model_involved")] public bool ModelInvolved { get; init; }

    /// <summary>
    /// What the producing execution disclosed about its own output — "provider_failure", "low
    /// confidence sources", "changed_files_source: none". v0.3.8.57.
    ///
    /// These already existed as <c>AntExecutionResult.Warnings</c> and died with the execution. An
    /// artifact produced by a degraded run looked exactly like one produced by a clean run, and the
    /// caveat lived only in a transcript nobody queries. This is the ONE facet of the original
    /// provenance brief that turned out to have a real producer already — the others named there
    /// (assumptions, retention) still have none, and are recorded as gaps rather than as fields.
    /// </summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ArtifactProvenance? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ArtifactProvenance>(json!, Options); }
        // Unreadable provenance reads as ABSENT provenance, never as an exception on a read path.
        // An artifact whose origin cannot be parsed is still an artifact a worker may need.
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
