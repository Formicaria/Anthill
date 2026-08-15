namespace Anthill.SDK.Artifacts;

/// <summary>
/// Durable, append-only storage for artifacts and the graph they form. ADR-004, v3.8.19.
///
/// APPEND-ONLY IS THE CONTRACT, not an implementation detail. There is no Update and no Delete: a
/// revision is a new artifact citing the old one. "Update the change plan in place" destroys the
/// ability to ask what a decision was based on at the time it was made, which is the one question
/// this store exists to answer.
///
/// In the SDK because modules will produce artifacts — a reasoning module emitting a diagnosis, a
/// tools module emitting a file set — and a module may not see <c>SqliteMemory</c>. The core
/// implements it; <c>IModuleContext</c> can hand it over when something needs to.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Persist one. Returns the id. Storing the same payload twice yields two artifacts with the
    /// same <see cref="Artifact.ContentHash"/> and different ids — deduplication is a caller's
    /// decision, because two tasks independently reaching the same conclusion is a fact worth
    /// keeping, not a duplicate to collapse.
    /// </summary>
    string Put(Artifact artifact);

    Artifact? Get(string artifactId);

    /// <summary>Everything a mission produced, newest first.</summary>
    IReadOnlyList<Artifact> ForMission(string missionId, int limit = 200);

    /// <summary>
    /// Artifacts of one schema for a mission — "the change plans for this mission", "every test
    /// report". The query a consumer actually makes.
    /// </summary>
    IReadOnlyList<Artifact> ForMission(string missionId, string schema, int limit = 200);

    /// <summary>
    /// What this artifact was derived FROM, one hop. Walking further is the caller's business: the
    /// store returns edges, not opinions about depth.
    /// </summary>
    IReadOnlyList<Artifact> SourcesOf(string artifactId);

    /// <summary>
    /// What was derived from this artifact, one hop. The reverse edge, which is the half that makes
    /// "what consumed it" answerable — ADR-004's fifth verification item.
    /// </summary>
    IReadOnlyList<Artifact> ConsumersOf(string artifactId);

    /// <summary>
    /// Record that a role read a specific version of an artifact. v0.3.8.57.
    ///
    /// Distinct from <see cref="ConsumersOf"/>, which despite the name answers a question about
    /// ARTIFACTS — what was derived from this one, via SourceArtifactIds. A role that reads a patch
    /// set and writes prose creates no such edge, so "did the verifier read this, and which version"
    /// had no answer at all.
    ///
    /// IDEMPOTENT PER (artifact, role, task): a retried task reading the same artifact is one
    /// relationship observed twice. Implementations increment the read count rather than inserting
    /// a second row, which keeps this a ledger and not a log.
    /// </summary>
    void RecordConsumption(ArtifactConsumption consumption);

    /// <summary>Who read this artifact. The reverse edge that <see cref="ConsumersOf"/> is not.</summary>
    IReadOnlyList<ArtifactConsumption> ConsumptionsOf(string artifactId);

    /// <summary>Every read recorded for a mission, newest first.</summary>
    IReadOnlyList<ArtifactConsumption> ConsumptionsForMission(string missionId, int limit = 500);
}

/// <summary>
/// Durable storage for checks performed on artifacts. Separate interface from
/// <see cref="IArtifactStore"/> because the consumers differ: verification writes evidence and reads
/// artifacts, and a narrower view is a smaller thing to hand a module.
/// </summary>
public interface IEvidenceStore
{
    string Put(Evidence evidence);

    IReadOnlyList<Evidence> ForMission(string missionId, int limit = 200);

    /// <summary>Every check performed on one artifact.</summary>
    IReadOnlyList<Evidence> ForArtifact(string artifactId);

    /// <summary>
    /// Whether a mission has at least one PASSING DETERMINISTIC check. The question the promotion
    /// rule actually asks, expressed once here rather than re-derived by each caller — v2.26.0's
    /// "one verification authority" applied to the new store.
    /// </summary>
    bool HasDeterministicPass(string missionId);
}
