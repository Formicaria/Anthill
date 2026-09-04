using System.Text.Json.Serialization;
using Anthill.SDK.Knowledge;

namespace Anthill.Modules.Knowledge;

// FORAGER's wire format, and the translation into the colony's vocabulary.
//
// EVERY FIELD NAME HERE WAS READ OFF A RUNNING INSTANCE, not off the documentation. The two agreed,
// but the habit is the point: this file is the one place in ANTHILL that knows what FORAGER's JSON
// looks like, so it is the one place that has to be right, and "the docs said so" is not a way to
// be right about a wire format.
//
// These types are internal. Nothing outside this module may name one — the moment a FORAGER DTO
// appears in a signature the core can see, the abstraction in Anthill.SDK.Knowledge has stopped
// being a boundary and started being a formality.
//
// Nullability is deliberately permissive: every reference field is nullable even where FORAGER
// always sends it. A DTO that asserts non-null is a DTO that throws on a version skew, and the
// failure lands in deserialization where there is no context to report. Mapping decides what is
// required; parsing does not.

internal sealed class ForagerHealth
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("schema_version")] public int? SchemaVersion { get; set; }
    [JsonPropertyName("database")] public string? Database { get; set; }
    [JsonPropertyName("model_provider")] public string? ModelProvider { get; set; }
    [JsonPropertyName("search_backend")] public string? SearchBackend { get; set; }
    [JsonPropertyName("migrations_applied")] public int? MigrationsApplied { get; set; }
}

/// <summary>FORAGER's uniform error envelope. <c>request_id</c> is the field that matters — it is
/// echoed in its log, so it is what turns "knowledge call failed" into a line an operator can find.</summary>
internal sealed class ForagerErrorEnvelope
{
    [JsonPropertyName("error")] public ForagerError? Error { get; set; }
}

internal sealed class ForagerError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

internal sealed class ForagerPage<T>
{
    [JsonPropertyName("items")] public List<T>? Items { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("page_size")] public int PageSize { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
}

internal sealed class ForagerKnowledgeItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("statement")] public string? Statement { get; set; }
    [JsonPropertyName("attribute_key")] public string? AttributeKey { get; set; }
    [JsonPropertyName("attribute_value")] public string? AttributeValue { get; set; }
    [JsonPropertyName("support")] public string? Support { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("entity_ids")] public List<string>? EntityIds { get; set; }
    [JsonPropertyName("effective_date")] public string? EffectiveDate { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("extractor_name")] public string? ExtractorName { get; set; }
    [JsonPropertyName("extractor_version")] public string? ExtractorVersion { get; set; }
    [JsonPropertyName("review_status")] public string? ReviewStatus { get; set; }
    [JsonPropertyName("superseded_by")] public string? SupersededBy { get; set; }
    [JsonPropertyName("evidence_count")] public int EvidenceCount { get; set; }

    // Present on the detail endpoint only. The list endpoint omits them, which is why mapping must
    // never assume their presence means "none" — see ForagerMapping.ToFact's comment on conflicts.
    [JsonPropertyName("conflict_ids")] public List<string>? ConflictIds { get; set; }
    [JsonPropertyName("entities")] public List<ForagerEntityRef>? Entities { get; set; }
    [JsonPropertyName("evidence")] public List<ForagerEvidence>? Evidence { get; set; }
    [JsonPropertyName("source_ids")] public List<string>? SourceIds { get; set; }
}

internal sealed class ForagerEntityRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("canonical_name")] public string? CanonicalName { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

internal sealed class ForagerEvidence
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("target_id")] public string? TargetId { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("chunk_id")] public string? ChunkId { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("excerpt")] public string? Excerpt { get; set; }
    [JsonPropertyName("content_hash")] public string? ContentHash { get; set; }
    [JsonPropertyName("extractor_name")] public string? ExtractorName { get; set; }
    [JsonPropertyName("extractor_version")] public string? ExtractorVersion { get; set; }
    [JsonPropertyName("model_name")] public string? ModelName { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("missing_excerpt")] public bool MissingExcerpt { get; set; }
    [JsonPropertyName("source_name")] public string? SourceName { get; set; }
    [JsonPropertyName("source_type")] public string? SourceType { get; set; }
}

internal sealed class ForagerEntity
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("canonical_name")] public string? CanonicalName { get; set; }
    [JsonPropertyName("aliases")] public List<ForagerAlias>? Aliases { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("mention_count")] public int MentionCount { get; set; }
    [JsonPropertyName("knowledge_count")] public int KnowledgeCount { get; set; }
}

internal sealed class ForagerAlias
{
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

internal sealed class ForagerConflict
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("attribute_key")] public string? AttributeKey { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("item_ids")] public List<string>? ItemIds { get; set; }
    [JsonPropertyName("source_ids")] public List<string>? SourceIds { get; set; }

    /// <summary>An OBJECT, not a string — <c>{ winner_id, reason }</c>. Getting this wrong would
    /// have deserialized to null silently and quietly dropped every suggestion.</summary>
    [JsonPropertyName("suggested_resolution")] public ForagerSuggestedResolution? SuggestedResolution { get; set; }

    [JsonPropertyName("resolution")] public ForagerResolution? Resolution { get; set; }
    [JsonPropertyName("items")] public List<ForagerKnowledgeItem>? Items { get; set; }
}

internal sealed class ForagerSuggestedResolution
{
    [JsonPropertyName("winner_id")] public string? WinnerId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class ForagerResolution
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("winner_id")] public string? WinnerId { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("actor")] public string? Actor { get; set; }
    [JsonPropertyName("decided_at")] public string? DecidedAt { get; set; }
}

internal sealed class ForagerSearchResponse
{
    [JsonPropertyName("query")] public string? Query { get; set; }
    [JsonPropertyName("knowledge")] public List<ForagerSearchHit>? Knowledge { get; set; }
    [JsonPropertyName("entities")] public List<ForagerEntity>? Entities { get; set; }
    [JsonPropertyName("took_ms")] public double TookMs { get; set; }
    [JsonPropertyName("backend")] public string? Backend { get; set; }
}

internal sealed class ForagerSearchHit
{
    [JsonPropertyName("item")] public ForagerKnowledgeItem? Item { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("snippet")] public string? Snippet { get; set; }
    [JsonPropertyName("why")] public string? Why { get; set; }
    [JsonPropertyName("matched_fields")] public List<string>? MatchedFields { get; set; }
}

internal sealed class ForagerJob
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("current_stage")] public string? CurrentStage { get; set; }
    [JsonPropertyName("cancel_requested")] public bool CancelRequested { get; set; }

    /// <summary>0..1. Arrives as a JSON number that is an integer when it is exactly 0 or 1, so it
    /// must be read as a double — declaring it int silently floors 0.4 to 0.</summary>
    [JsonPropertyName("progress")] public double Progress { get; set; }

    [JsonPropertyName("error")] public ForagerJobError? Error { get; set; }
    [JsonPropertyName("stages")] public List<ForagerJobStage>? Stages { get; set; }
    [JsonPropertyName("started_at")] public DateTime? StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }
}

internal sealed class ForagerJobError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("stage")] public string? Stage { get; set; }
}

internal sealed class ForagerJobStage
{
    [JsonPropertyName("stage")] public string? Stage { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("processed")] public int Processed { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
}

internal sealed class ForagerSource
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("source_type")] public string? SourceType { get; set; }
    [JsonPropertyName("content_hash")] public string? ContentHash { get; set; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("processing_status")] public string? ProcessingStatus { get; set; }
    [JsonPropertyName("document_date")] public string? DocumentDate { get; set; }
    [JsonPropertyName("authoritative")] public bool Authoritative { get; set; }
    [JsonPropertyName("duplicate_of")] public string? DuplicateOf { get; set; }
    [JsonPropertyName("superseded_by")] public string? SupersededBy { get; set; }
    [JsonPropertyName("chunk_count")] public int ChunkCount { get; set; }
}

/// <summary>
/// Wire vocabulary to colony vocabulary.
///
/// The rule every method here follows: an UNRECOGNISED value maps to the <c>Unknown</c> member, and
/// never to a plausible default. If FORAGER one day emits a support level this build has not heard
/// of, the honest outcome is a statement labelled UNKNOWN SUPPORT that a model will treat carefully
/// — not one silently promoted to DIRECT FACT because that was the first enum member.
/// </summary>
internal static class ForagerMapping
{
    public static KnowledgeSupport Support(string? wire) => wire switch
    {
        "direct_fact" => KnowledgeSupport.DirectFact,
        "supported_inference" => KnowledgeSupport.SupportedInference,
        "uncertain_inference" => KnowledgeSupport.UncertainInference,
        "unverified_claim" => KnowledgeSupport.UnverifiedClaim,
        _ => KnowledgeSupport.Unknown,
    };

    public static KnowledgeStatus Status(string? wire) => wire switch
    {
        "active" => KnowledgeStatus.Active,
        "superseded" => KnowledgeStatus.Superseded,
        "disputed" => KnowledgeStatus.Disputed,
        "unresolved" => KnowledgeStatus.Unresolved,
        "stale" => KnowledgeStatus.Stale,
        "archived" => KnowledgeStatus.Archived,
        _ => KnowledgeStatus.Unknown,
    };

    /// <summary>
    /// The confidentiality band. Anything unrecognised is <see cref="KnowledgeConfidentiality.Tenant"/>
    /// — NOT Unknown — because this value gates whether material may enter shared memory, and the
    /// safe reading of "I do not recognise this band" is the restrictive one.
    /// </summary>
    public static KnowledgeConfidentiality Confidentiality(string? wire) => wire switch
    {
        "general" => KnowledgeConfidentiality.General,
        _ => KnowledgeConfidentiality.Tenant,
    };

    public static KnowledgeFact ToFact(ForagerKnowledgeItem item, IReadOnlyList<string>? evidenceIds = null) => new()
    {
        KnowledgeId = item.Id ?? "",
        Statement = item.Statement ?? item.Title ?? "",
        Type = item.Type ?? "fact",
        Subject = item.Subject,
        Title = item.Title,
        AttributeKey = item.AttributeKey,
        AttributeValue = item.AttributeValue,
        Support = Support(item.Support),
        Status = Status(item.Status),
        Confidence = item.Confidence,
        Confidentiality = Confidentiality(item.Scope),
        EffectiveDate = item.EffectiveDate,
        SupersededBy = item.SupersededBy,

        // Evidence ids are passed IN rather than read off the item, because the list endpoint sends
        // only `evidence_count` and the detail endpoint sends the full array. Reading the array here
        // would make a list-sourced fact look evidence-free, which HasProvenance would then report
        // as a Rule 9 violation — a false alarm caused by the mapper, not by the data.
        EvidenceIds = evidenceIds ?? item.Evidence?.Select(e => e.Id ?? "").Where(id => id.Length > 0).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>(),

        EntityIds = item.EntityIds ?? (IReadOnlyList<string>)Array.Empty<string>(),
        ConflictIds = item.ConflictIds ?? (IReadOnlyList<string>)Array.Empty<string>(),
        Extractor = item.ExtractorName is null ? null : $"{item.ExtractorName}@{item.ExtractorVersion}",
    };

    public static KnowledgeEvidence ToEvidence(ForagerEvidence wire) => new()
    {
        EvidenceId = wire.Id ?? "",
        KnowledgeId = wire.TargetId ?? "",
        SourceId = wire.SourceId ?? "",
        SourceName = wire.SourceName,
        SourceType = wire.SourceType,
        Location = wire.Location,
        ChunkId = wire.ChunkId,
        Excerpt = wire.Excerpt,

        // FORAGER names the excerpt's own hash `content_hash` on an evidence row; the SOURCE's hash
        // is a different field on the source record. Keeping both names distinct on this side stops
        // a future reader from proving the wrong thing with it.
        ExcerptHash = wire.ContentHash,
        Extractor = wire.ExtractorName is null ? null : $"{wire.ExtractorName}@{wire.ExtractorVersion}",
        Model = wire.ModelName,
        Confidence = wire.Confidence,
        MissingExcerpt = wire.MissingExcerpt,
    };

    public static KnowledgeEntity ToEntity(ForagerEntity wire) => new()
    {
        EntityId = wire.Id ?? "",
        Name = wire.CanonicalName ?? "",
        Type = wire.Type ?? "unknown",
        Aliases = wire.Aliases?
            .Select(a => a.Alias ?? "")
            .Where(a => a.Length > 0 && !string.Equals(a, wire.CanonicalName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        MentionCount = wire.MentionCount,
        Confidence = wire.Confidence,
    };

    public static KnowledgeConflict ToConflict(ForagerConflict wire) => new()
    {
        ConflictId = wire.Id ?? "",
        Type = wire.Type ?? "unknown",
        AttributeKey = wire.AttributeKey,
        Status = wire.Status ?? "open",
        Description = wire.Description,
        KnowledgeIds = wire.ItemIds ?? (IReadOnlyList<string>)Array.Empty<string>(),
        SourceIds = wire.SourceIds ?? (IReadOnlyList<string>)Array.Empty<string>(),

        // Flattened to prose because that is how it is rendered to a model, and because the winner
        // id alone is useless without the reason — presenting "prefer ki_3943b4" to a reasoning
        // layer invites it to comply rather than to weigh.
        SuggestedResolution = wire.SuggestedResolution is null ? null
            : string.IsNullOrWhiteSpace(wire.SuggestedResolution.Reason)
                ? $"prefer {wire.SuggestedResolution.WinnerId}"
                : wire.SuggestedResolution.Reason,
        Resolution = wire.Resolution?.Action,
    };

    public static KnowledgeJob ToJob(ForagerJob wire) => new()
    {
        JobId = wire.Id ?? "",
        Status = wire.Status ?? "unknown",
        CurrentStage = wire.CurrentStage,
        Progress = wire.Progress,
        Stages = wire.Stages?.Select(s => new KnowledgeJobStage
        {
            Name = s.Stage ?? "",
            Status = s.Status ?? "",
            Processed = s.Processed,
            Skipped = s.Skipped,
            Failed = s.Failed,
            Warnings = s.Warnings?.Count ?? 0,
        }).ToList() ?? (IReadOnlyList<KnowledgeJobStage>)Array.Empty<KnowledgeJobStage>(),
        Warnings = wire.Stages?.SelectMany(s => s.Warnings ?? new List<string>()).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>(),
        Error = wire.Error?.Message,
        StartedAtUtc = wire.StartedAt,
        FinishedAtUtc = wire.FinishedAt,
    };

    public static KnowledgeSource ToSource(ForagerSource wire) => new()
    {
        SourceId = wire.Id ?? "",
        Name = wire.OriginalName ?? wire.Id ?? "",
        Type = wire.SourceType,
        ContentHash = wire.ContentHash,
        SizeBytes = wire.SizeBytes,
        ProcessingStatus = wire.ProcessingStatus,
        DocumentDate = wire.DocumentDate,
        Authoritative = wire.Authoritative,
        DuplicateOf = wire.DuplicateOf,
        SupersededBy = wire.SupersededBy,
        ChunkCount = wire.ChunkCount,
    };
}
