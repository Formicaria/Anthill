namespace Anthill.SDK.Artifacts;

/// <summary>
/// A record that a role READ a specific version of an artifact. v0.3.8.57.
///
/// WHY THIS IS NOT ALREADY ANSWERABLE. The store records production in full — producer role, task,
/// mission, content hash — and <c>IArtifactStore.ConsumersOf</c> looks like the other half but is
/// not: it answers "what artifacts were DERIVED from this one", by walking
/// <c>SourceArtifactIds</c>. That is production lineage between artifacts. It cannot answer "did the
/// verifier read the patch set, and which version of it", because a role that reads something and
/// produces prose leaves no edge at all.
///
/// WHY THE HASH AND NOT JUST THE ID. Artifacts are immutable, so an id would be enough today — and
/// the reason to record the hash anyway is that it makes the claim self-checking. A consumption row
/// whose hash no longer matches the artifact it names is evidence that something violated the
/// append-only rule, which is exactly the failure the hash exists to detect and which no other
/// record in the system would notice.
///
/// COUNTED, NOT DUPLICATED. A retried task reads the same artifact again; that is one relationship
/// observed twice, not two relationships. <see cref="ReadCount"/> keeps the repetition visible
/// without turning a bounded ledger into a log.
/// </summary>
public sealed record ArtifactConsumption
{
    public required string ArtifactId { get; init; }

    /// <summary>The hash AS READ. See the class remarks — this is what makes the row falsifiable.</summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Denormalised from the artifact deliberately. "Which schemas does the tester actually read"
    /// should not require a join to a table the answer does not otherwise depend on.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// The mission that PRODUCED the artifact. v0.3.8.106 states that, having found it stated
    /// nowhere.
    ///
    /// It has always been written as <c>artifact.MissionId</c>, and the only caller until now read
    /// artifacts within one mission — so "who produced it" and "who read it" were the same value
    /// and the field meant both. That is not a design; it is a coincidence, and cross-mission
    /// consumption is exactly where it ends. See <see cref="ConsumerMissionId"/>.
    /// </summary>
    public required string MissionId { get; init; }

    /// <summary>
    /// The mission that DID the reading. v0.3.8.106.
    ///
    /// Null for every row written before this release and for every same-mission read, where it
    /// would only repeat <see cref="MissionId"/> — <see cref="ReadBy"/> resolves the two. It is
    /// populated when a mission reads an artifact ANOTHER mission produced, which is the only case
    /// in which the distinction carries information.
    ///
    /// WHY IT IS NOT PART OF THE LEDGER'S PRIMARY KEY, which is
    /// <c>(artifact_id, consumer_role, consumer_task_id)</c>: two missions reading the same
    /// artifact under the same role and NO task would collide on that key, and the second read
    /// would bump the first's counter instead of recording itself. Cross-mission reads always
    /// carry a task id — they happen at the tool dispatch chokepoint, which has one — so the
    /// collision is unreachable rather than merely unlikely. Widening the key would mean rebuilding
    /// the table, and doing that to close a hole nothing can fall into is the kind of migration
    /// that breaks a store to tidy it.
    /// </summary>
    public string? ConsumerMissionId { get; init; }

    /// <summary>Who read it, resolving the legacy case: the consumer when one was recorded,
    /// otherwise the producing mission, which is what a same-mission row has always meant.</summary>
    public string ReadBy => string.IsNullOrWhiteSpace(ConsumerMissionId) ? MissionId : ConsumerMissionId!;

    /// <summary>True when this row records one mission reading another mission's artifact.</summary>
    public bool IsCrossMission =>
        !string.IsNullOrWhiteSpace(ConsumerMissionId)
        && !string.Equals(ConsumerMissionId, MissionId, StringComparison.Ordinal);

    /// <summary>The role that read it — the question this ledger exists to answer.</summary>
    public required string ConsumerRole { get; init; }

    /// <summary>
    /// Null when the read was not on behalf of a specific task. Kept nullable rather than defaulted
    /// to an empty string so "no task" and "a task whose id was lost" stay distinguishable.
    /// </summary>
    public string? ConsumerTaskId { get; init; }

    public DateTime FirstReadAt { get; init; } = Common.AnthillTime.NowUtc();
    public DateTime LastReadAt { get; init; } = Common.AnthillTime.NowUtc();

    /// <summary>How many times this role/task read this artifact. See the class remarks.</summary>
    public int ReadCount { get; init; } = 1;

    /// <summary>
    /// Whether the artifact still hashes to what this row says was read. False means the
    /// append-only rule was broken between the read and now — the artifact a decision rested on is
    /// not the artifact in the store.
    /// </summary>
    public bool StillMatches(Artifact artifact) =>
        artifact is not null
        && string.Equals(artifact.Id, ArtifactId, StringComparison.Ordinal)
        && string.Equals(artifact.ContentHash, ContentHash, StringComparison.Ordinal);
}
