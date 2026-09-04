namespace Anthill.SDK.Knowledge;

// The contract between "the colony wants to know something" and "something knows it".
//
// THE POINT OF THE ABSTRACTION, stated plainly because it will be under pressure: nothing behind
// this interface is named in it. No FORAGER type, no HTTP concept, no JSON. The core and the tools
// speak this and only this, so a second knowledge source — or FORAGER's next schema — is an
// implementation rather than a migration.
//
// Every method takes a KnowledgeScope. Not "usually", not "unless it is a lookup by id": every
// one. Rule 12 is that knowledge never crosses a project boundary, and the cheapest way to keep a
// rule like that is to make the unscoped call impossible to write rather than possible and audited.
//
// No method throws for an unavailable backend. Availability is a RESULT, not an exception, because
// "the knowledge base is down" is an ordinary operating condition for a colony whose knowledge base
// is optional, and a mission must be able to carry on without it. See KnowledgeOutcome.

/// <summary>
/// Why a knowledge call did not produce what was asked for. Mirrors <c>FailureClass</c>'s reasoning
/// — the caller has to DECIDE what to do next, and the right move differs by kind — but stays in
/// knowledge terms so the provider never has to reason about tool dispatch.
/// </summary>
public enum KnowledgeFailure
{
    /// <summary>It worked.</summary>
    None = 0,

    /// <summary>Knowledge is switched off in configuration. Not an error, and not retryable.</summary>
    Disabled,

    /// <summary>Configured but not reachable — refused, DNS, no route. Retryable.</summary>
    Unavailable,

    /// <summary>Reachable but too slow. Retryable.</summary>
    Timeout,

    /// <summary>Reached, and it refused us — bad token, or a proxy said no. Not retryable without operator action.</summary>
    Unauthorized,

    /// <summary>The scope could not be resolved, so there was nothing legitimate to query. Never widen and retry.</summary>
    ScopeUnresolved,

    /// <summary>The id does not exist in this scope. Distinct from Unavailable: the answer IS "no such thing".</summary>
    NotFound,

    /// <summary>The arguments were wrong. The caller can fix them.</summary>
    Invalid,

    /// <summary>It answered with something this build cannot parse. Not retryable; a version mismatch or a defect.</summary>
    Malformed,

    /// <summary>It answered with an error of its own. Retryable; the message carries its request id.</summary>
    Upstream,
}

/// <summary>
/// The result of a knowledge call: a value, or a typed reason there isn't one.
///
/// A discriminated result rather than exceptions-for-failure, because the failure path here is
/// ordinary rather than exceptional, and because the tools have to turn it into a
/// <c>ToolResult</c> anyway — which is itself a value-or-reason. Two translations of the same shape
/// is one too many.
/// </summary>
public sealed record KnowledgeOutcome<T>
{
    public T? Value { get; init; }
    public KnowledgeFailure Failure { get; init; } = KnowledgeFailure.None;

    /// <summary>Operator-readable. Names what happened and, where it can, what to do about it.</summary>
    public string? Reason { get; init; }

    /// <summary>The upstream's own request id, when it gave one. The single most useful thing for
    /// correlating an ANTHILL failure with a line in FORAGER's log.</summary>
    public string? UpstreamRequestId { get; init; }

    public bool Ok => Failure == KnowledgeFailure.None && Value is not null;

    /// <summary>Whether repeating the identical call could plausibly work. Derived, never stored,
    /// so it cannot contradict <see cref="Failure"/>.</summary>
    public bool Retryable => Failure is KnowledgeFailure.Unavailable
        or KnowledgeFailure.Timeout or KnowledgeFailure.Upstream;

    public static KnowledgeOutcome<T> Success(T value) => new() { Value = value };

    public static KnowledgeOutcome<T> Failed(KnowledgeFailure failure, string reason, string? requestId = null) =>
        new() { Failure = failure, Reason = reason, UpstreamRequestId = requestId };
}

/// <summary>
/// Whether the knowledge subsystem can answer at all, and what it is. Cheap, cached briefly, and
/// safe to call on a UI poll — it must never be the thing that makes a console page slow.
/// </summary>
public sealed record KnowledgeAvailability
{
    public required bool Enabled { get; init; }
    public required bool Reachable { get; init; }

    /// <summary>The provider's own version, when it said. Null when unreachable.</summary>
    public string? Version { get; init; }

    /// <summary>Canonical schema version the backend is speaking. A mismatch is worth surfacing early.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Which search backend is live — <c>sqlite-fts5</c> or the unranked <c>sqlite-like</c> fallback.</summary>
    public string? SearchBackend { get; init; }

    /// <summary>What the backend uses for semantic extraction, or a phrase meaning none. Reported,
    /// never required: deterministic operation is a supported configuration, not a degraded one.</summary>
    public string? ModelProvider { get; init; }

    /// <summary>The configured endpoint, for the console. Never includes a token.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Why it is not usable, when it is not. Present exactly when <see cref="Usable"/> is false.</summary>
    public string? Reason { get; init; }

    public bool Usable => Enabled && Reachable;

    public static KnowledgeAvailability Off(string reason) =>
        new() { Enabled = false, Reachable = false, Reason = reason };
}

/// <summary>What to search for, and how much to bring back.</summary>
public sealed record KnowledgeSearchRequest
{
    public required string Query { get; init; }
    public required KnowledgeScope Scope { get; init; }

    public int Limit { get; init; } = 10;

    /// <summary>Restrict to these FORAGER item types (fact, decision, procedure, ...). Empty means all.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The weakest support level to return. Defaults to <see cref="KnowledgeSupport.UnverifiedClaim"/>
    /// — everything — deliberately. Filtering weak claims out at retrieval hides the fact that the
    /// organization holds them, and "we have only an unverified claim about this" is an answer the
    /// reasoning layer needs to be able to give.
    /// </summary>
    public KnowledgeSupport MinimumSupport { get; init; } = KnowledgeSupport.UnverifiedClaim;

    /// <summary>
    /// Include superseded and archived items. Off by default; ON is how a temporal question
    /// ("what was the procedure in March?") gets answered, so it is a first-class option rather
    /// than a debugging flag.
    /// </summary>
    public bool IncludeHistorical { get; init; }
}

/// <summary>One ranked candidate, with the backend's own explanation of why it matched.</summary>
public sealed record KnowledgeSearchHit
{
    public required string KnowledgeId { get; init; }
    public required string Statement { get; init; }
    public string? Title { get; init; }
    public string Type { get; init; } = "fact";
    public required KnowledgeSupport Support { get; init; }
    public required KnowledgeStatus Status { get; init; }
    public double Confidence { get; init; }
    public double Score { get; init; }
    public string? Snippet { get; init; }

    /// <summary>The backend's plain-language reason. Passed through verbatim — it is more honest
    /// than anything ANTHILL could reconstruct from a score.</summary>
    public string? Why { get; init; }

    public int EvidenceCount { get; init; }
    public bool IsContested { get; init; }
}

public sealed record KnowledgeSearchResult
{
    public IReadOnlyList<KnowledgeSearchHit> Hits { get; init; } = Array.Empty<KnowledgeSearchHit>();
    public IReadOnlyList<KnowledgeEntity> Entities { get; init; } = Array.Empty<KnowledgeEntity>();
    public required RetrievalMetadata Metadata { get; init; }
}

/// <summary>
/// What to assemble a context from. The difference from a search request is the difference between
/// "find me candidates" and "tell me what we know, with the evidence" — this one triggers the
/// evidence, entity and conflict expansion.
/// </summary>
public sealed record KnowledgeRetrievalRequest
{
    public required string Query { get; init; }
    public required KnowledgeScope Scope { get; init; }

    public int TopK { get; init; } = 8;

    public bool IncludeEvidence { get; init; } = true;
    public bool IncludeEntities { get; init; } = true;
    public bool IncludeRelationships { get; init; }

    /// <summary>
    /// Fetch conflicts touching the retrieved facts. Defaults ON and there is no supported way to
    /// turn it off from a tool call: Rule 10 says conflicts are never hidden, and an option to hide
    /// them is a way to hide them.
    /// </summary>
    public bool IncludeConflicts { get; init; } = true;

    public bool IncludeHistorical { get; init; }
    public KnowledgeSupport MinimumSupport { get; init; } = KnowledgeSupport.UnverifiedClaim;

    /// <summary>
    /// Rough ceiling on rendered characters. The assembler drops whole facts from the tail rather
    /// than truncating mid-statement, and sets <c>Metadata.Truncated</c> when it does — a half-quoted
    /// excerpt is a misquotation, and a silently shortened context is a lie by omission.
    /// </summary>
    public int MaxContextChars { get; init; } = 12000;
}

/// <summary>
/// A review action an agent may PROPOSE against canonical knowledge.
///
/// Proposing is not applying. This type exists so an agent's opinion about the knowledge base can
/// be recorded and routed to an operator; the mutation itself happens in FORAGER, after a human
/// decides. See Rule 8, and docs/KNOWLEDGE_SECURITY.md.
/// </summary>
public sealed record KnowledgeReviewProposal
{
    public required string KnowledgeId { get; init; }
    public required KnowledgeScope Scope { get; init; }

    /// <summary>One of <c>mark_reviewed</c>, <c>reject</c>, <c>restore</c>, <c>archive</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Why. Required — an unexplained proposal is not reviewable, so it is not acceptable.</summary>
    public required string Rationale { get; init; }

    /// <summary>Which mission and role proposed it. Attribution is the point of an audit trail.</summary>
    public string? MissionId { get; init; }
    public string? ProposedBy { get; init; }
}

/// <summary>
/// The colony's way of asking what the organization knows.
///
/// Implementations: the live FORAGER client, a package reader for air-gapped installs, and a null
/// provider for when knowledge is off. The null one is not a stub — it is the reason an existing
/// colony with no FORAGER configured keeps working, and it returns typed <c>Disabled</c> outcomes
/// so the difference between "nothing known" and "not asked" survives to the model.
/// </summary>
public interface IKnowledgeProvider
{
    /// <summary>Stable identifier for logs and the console: <c>forager-http</c>, <c>forager-package</c>, <c>none</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Can this answer, and what is it? Must be cheap and must not throw. Called by the health
    /// endpoint, the console, and the tools before they do real work.
    /// </summary>
    Task<KnowledgeAvailability> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Ranked candidates. Cheap; no evidence expansion.</summary>
    Task<KnowledgeOutcome<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The main retrieval path: candidates, then evidence, then entities, then conflicts, assembled
    /// into a context whose every fact carries provenance or an explicit unresolved marker.
    /// </summary>
    Task<KnowledgeOutcome<KnowledgeContext>> RetrieveAsync(
        KnowledgeRetrievalRequest request, CancellationToken cancellationToken);

    /// <summary>One item by id, within a scope. An id from another project is <c>NotFound</c>, not a leak.</summary>
    Task<KnowledgeOutcome<KnowledgeFact>> GetAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken);

    /// <summary>The evidence for one item — the "why do you believe this?" call.</summary>
    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEvidence>>> GetEvidenceAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken);

    /// <summary>Entities related to an item, for graph expansion during iterative retrieval.</summary>
    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> GetRelatedEntitiesAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken);

    /// <summary>An entity by name, so an agent can pivot from a name it read to what is known about it.</summary>
    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> FindEntitiesAsync(
        string name, KnowledgeScope scope, CancellationToken cancellationToken);

    /// <summary>Open conflicts in scope, whether or not a query surfaced them.</summary>
    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeConflict>>> GetConflictsAsync(
        KnowledgeScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Ingestion, kept deliberately separate from retrieval.
///
/// Two interfaces rather than one because the permission boundary runs between them: nearly every
/// role may retrieve, almost none may ingest, and a single interface would mean every retrieval-only
/// implementation had to stub out methods it must never perform. Splitting them makes "this provider
/// cannot ingest" expressible as a type rather than as a runtime refusal.
/// </summary>
public interface IKnowledgeIngestionProvider
{
    /// <summary>
    /// Register files and start processing. Returns as soon as the job is queued — never waits for
    /// parsing. Paths must ALREADY have been resolved through the workspace guard by the caller;
    /// this contract cannot check containment because it does not know what a workspace is.
    /// </summary>
    Task<KnowledgeOutcome<KnowledgeJob>> StartIngestionAsync(
        KnowledgeIngestionRequest request, CancellationToken cancellationToken);

    /// <summary>Real persisted job state. Never a synthesized or interpolated progress number.</summary>
    Task<KnowledgeOutcome<KnowledgeJob>> GetJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken);

    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeJob>>> ListJobsAsync(
        KnowledgeScope scope, int limit, CancellationToken cancellationToken);

    /// <summary>Request cancellation. Completed stages are kept; the job stops at the next boundary.</summary>
    Task<KnowledgeOutcome<KnowledgeJob>> CancelJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken);

    /// <summary>Retry a failed job from its checkpoints rather than from the beginning.</summary>
    Task<KnowledgeOutcome<KnowledgeJob>> RetryJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken);

    Task<KnowledgeOutcome<IReadOnlyList<KnowledgeSource>>> ListSourcesAsync(
        KnowledgeScope scope, CancellationToken cancellationToken);
}

public sealed record KnowledgeIngestionRequest
{
    public required KnowledgeScope Scope { get; init; }

    /// <summary>Absolute paths, already guard-resolved. Empty means "reprocess what is registered".</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>Reprocess even where the content hash is unchanged.</summary>
    public bool Force { get; init; }

    public string? RequestedBy { get; init; }
    public string? MissionId { get; init; }
}

/// <summary>An ingestion job as the backend actually persisted it.</summary>
public sealed record KnowledgeJob
{
    public required string JobId { get; init; }

    /// <summary><c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    public string? CurrentStage { get; init; }

    /// <summary>0..1, DERIVED by the backend from persisted stage rows. Reaches 1 only on completion.</summary>
    public double Progress { get; init; }

    public IReadOnlyList<KnowledgeJobStage> Stages { get; init; } = Array.Empty<KnowledgeJobStage>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }

    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";
}

public sealed record KnowledgeJobStage
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public int Processed { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Warnings { get; init; }
}

public sealed record KnowledgeSource
{
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public string? Type { get; init; }
    public string? ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public string? ProcessingStatus { get; init; }
    public string? DocumentDate { get; init; }
    public bool Authoritative { get; init; }
    public string? DuplicateOf { get; init; }
    public string? SupersededBy { get; init; }
    public int ChunkCount { get; init; }
}
