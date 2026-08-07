using System.Security.Cryptography;
using System.Text;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// A unit of work a task produced, as data rather than prose. ADR-004, v3.8.19.
///
/// WHAT PROBLEM THIS SOLVES. Ants collaborate today by passing strings: <c>Task.Result</c> is a
/// <c>string?</c>, and the next ant reads the previous one's narrative. That works until it doesn't
/// — a fluent model wins over a correct one, nothing can be replayed, and "what was this decision
/// based on" has no answer beyond a transcript.
///
/// IMMUTABLE AND HASHED. An artifact is never edited. A revision is a NEW artifact citing the old
/// one in <see cref="SourceArtifactIds"/>, so the dependency graph falls out of provenance rather
/// than needing to be maintained beside it, and <see cref="ContentHash"/> detects an input that
/// changed under a consumer's feet.
///
/// SHIPPED WITHOUT A CONSUMER, DELIBERATELY. This release adds the store and nothing reads from it:
/// ants still pass prose. That is the phase-0 shape the refactor used — land the contract, prove it
/// persists, then move the consumers in a release whose blast radius is one thing. ADR-004 calls
/// replacing the output path "the largest behavioural change in V3", and it is not something to
/// bundle.
/// </summary>
public sealed record Artifact
{
    public required string Id { get; init; }

    /// <summary>
    /// What KIND of thing this is — <c>repository_map</c>, <c>change_plan</c>, <c>test_report</c>.
    /// The vocabulary ADR-004 names; see <see cref="ArtifactSchemas"/>.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Which version of that schema the payload conforms to. Separate from <see cref="Schema"/> so a
    /// consumer can refuse a shape it does not understand instead of guessing at it.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The role that produced it. Provenance is mandatory — that is the point.</summary>
    public required string ProducerRole { get; init; }

    public required string MissionId { get; init; }
    public string? TaskId { get; init; }
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// The artifacts this one was derived from. The dependency graph IS this field: an artifact that
    /// names its inputs makes "what did this decision rest on" answerable by traversal rather than by
    /// reading a transcript.
    /// </summary>
    public IReadOnlyList<string> SourceArtifactIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// SHA-256 of <see cref="Payload"/>. Computed at construction by <see cref="Create"/> rather than
    /// supplied, so it cannot disagree with what it hashes.
    /// </summary>
    public required string ContentHash { get; init; }

    public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();

    /// <summary>
    /// Who may see it. A first-class field because redaction belongs at the STORE boundary — one
    /// place that knows what is secret — rather than at each render site, where the first one that
    /// forgets is the leak.
    /// </summary>
    public ArtifactVisibility Visibility { get; init; } = ArtifactVisibility.Colony;

    /// <summary>The content itself, serialised per <see cref="Schema"/>.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Build one, hashing the payload. The only supported way to make an artifact: a constructor
    /// that let a caller supply <see cref="ContentHash"/> would let it supply the wrong one, and a
    /// hash that can be wrong detects nothing.
    /// </summary>
    public static Artifact Create(
        string schema,
        string producerRole,
        string missionId,
        string payload,
        string? taskId = null,
        string? workspaceId = null,
        IReadOnlyList<string>? sourceArtifactIds = null,
        ArtifactVisibility visibility = ArtifactVisibility.Colony,
        int schemaVersion = 1) => new()
        {
            Id = $"art_{Guid.NewGuid():N}",
            Schema = schema,
            SchemaVersion = schemaVersion,
            ProducerRole = producerRole,
            MissionId = missionId,
            TaskId = taskId,
            WorkspaceId = workspaceId,
            SourceArtifactIds = sourceArtifactIds ?? Array.Empty<string>(),
            ContentHash = HashOf(payload),
            Visibility = visibility,
            Payload = payload,
        };

    /// <summary>SHA-256, lowercase hex, prefixed so a bare hash is never mistaken for an id.</summary>
    public static string HashOf(string payload) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? ""))).ToLowerInvariant();

    /// <summary>
    /// Whether the payload still matches the hash recorded with it. The check ADR-004 asks for:
    /// "artifact hashes detect mutation or stale input".
    /// </summary>
    public bool IsIntact() => ContentHash == HashOf(Payload);
}

/// <summary>
/// Who may see an artifact. Ordered from most to least restricted so a comparison can express
/// "at least as open as".
/// </summary>
public enum ArtifactVisibility
{
    /// <summary>Contains secrets. Never rendered, never sent to a model, never in an API response.</summary>
    Secret = 0,

    /// <summary>Internal to the colony's own reasoning. Operators may see it; models may read it.</summary>
    Colony = 1,

    /// <summary>Intended for the operator — summaries, reports, release notes.</summary>
    Operator = 2,
}
