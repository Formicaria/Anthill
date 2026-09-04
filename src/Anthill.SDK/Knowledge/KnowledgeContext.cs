using System.Text;

namespace Anthill.SDK.Knowledge;

// The canonical Anthill-side representation of retrieved knowledge.
//
// WHY THIS EXISTS AT ALL, since FORAGER already returns perfectly good JSON: handing a provider's
// wire format to the reasoning layer makes the provider's wire format an ANTHILL interface. Every
// prompt, every renderer and every test would then encode FORAGER's field names, and the second
// knowledge provider — or FORAGER's next schema version — becomes a rewrite instead of a class.
//
// So this is the shape the colony reasons about. It is DETERMINISTIC (same inputs, same render,
// same order) and INSPECTABLE (an operator can read it and see exactly what the model was told).
// Determinism is not aesthetic: a context that reorders itself between runs makes a misbehaving
// mission unreproducible, and prompt caching pointless.

/// <summary>
/// One statement the colony has been told, with everything needed to judge it.
/// </summary>
public sealed record KnowledgeFact
{
    /// <summary>FORAGER's stable knowledge id. The join key for evidence, conflicts and audit.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Short label for the ordinal the renderer assigns — FACT-1, FACT-2. Assigned at render, not stored.</summary>
    public required string Statement { get; init; }

    /// <summary>FORAGER's item kind: fact, decision, requirement, procedure, problem, lesson, ...</summary>
    public string Type { get; init; } = "fact";

    public string? Subject { get; init; }
    public string? Title { get; init; }

    /// <summary>The attribute this asserts, when it asserts one — <c>falcon|launch_date</c> = <c>2026-03-03</c>.
    /// This is what makes two statements comparable, and therefore what makes a conflict detectable.</summary>
    public string? AttributeKey { get; init; }
    public string? AttributeValue { get; init; }

    public required KnowledgeSupport Support { get; init; }
    public required KnowledgeStatus Status { get; init; }

    /// <summary>FORAGER's confidence, 0..1. Presented, never used as a threshold by ANTHILL — the
    /// decision of what is good enough belongs to the reasoning layer and to the operator's filters.</summary>
    public double Confidence { get; init; }

    public KnowledgeConfidentiality Confidentiality { get; init; } = KnowledgeConfidentiality.Unknown;

    /// <summary>When this was true, as the document said — not when the row was written.</summary>
    public string? EffectiveDate { get; init; }

    /// <summary>Set when <see cref="Status"/> is <see cref="KnowledgeStatus.Superseded"/>.</summary>
    public string? SupersededBy { get; init; }

    /// <summary>Evidence ids into <see cref="KnowledgeContext.Evidence"/>. May be empty ONLY when
    /// <see cref="Status"/> is <see cref="KnowledgeStatus.Unresolved"/> — see <see cref="HasProvenance"/>.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityIds { get; init; } = Array.Empty<string>();

    /// <summary>Conflict ids into <see cref="KnowledgeContext.Conflicts"/>. Non-empty means contested.</summary>
    public IReadOnlyList<string> ConflictIds { get; init; } = Array.Empty<string>();

    /// <summary>Which extractor produced this, and its version. Provenance of the EXTRACTION, as
    /// distinct from provenance of the claim.</summary>
    public string? Extractor { get; init; }

    /// <summary>
    /// Rule 9, as a predicate: a meaningful fact carries evidence, or is explicitly unresolved.
    /// There is no third state, and the assembler asserts this rather than hoping.
    /// </summary>
    public bool HasProvenance => EvidenceIds.Count > 0 || Status == KnowledgeStatus.Unresolved;

    /// <summary>Whether this fact is in an open conflict, and therefore must not be reported as settled.</summary>
    public bool IsContested => ConflictIds.Count > 0 || Status == KnowledgeStatus.Disputed;
}

/// <summary>
/// Why the colony believes something: a pointer into a real source, precise enough to re-read.
///
/// <see cref="Excerpt"/> plus <see cref="Location"/> is what makes the console's one-click
/// "why do you believe this?" possible, and what makes a fabricated citation detectable — an
/// excerpt that does not appear in the named source is a defect that shows.
/// </summary>
public sealed record KnowledgeEvidence
{
    public required string EvidenceId { get; init; }
    public required string KnowledgeId { get; init; }
    public required string SourceId { get; init; }

    /// <summary>The file as the operator knows it: <c>02-schedule-update.eml</c>.</summary>
    public string? SourceName { get; init; }
    public string? SourceType { get; init; }

    /// <summary>Where in the source: "Pages 1-2", "Sheet 2, rows 4-9", "message body", "Section: Schedule".</summary>
    public string? Location { get; init; }

    public string? ChunkId { get; init; }

    /// <summary>The actual quoted text. The load-bearing field.</summary>
    public string? Excerpt { get; init; }

    /// <summary>Hash of the excerpt, and of the whole source. Lets a consumer prove the text has not drifted.</summary>
    public string? ExcerptHash { get; init; }
    public string? ContentHash { get; init; }

    public string? Extractor { get; init; }
    public string? Model { get; init; }
    public double Confidence { get; init; }

    /// <summary>
    /// FORAGER could not locate the excerpt in the chunk any more. Surfaced, never hidden: an
    /// evidence link whose text cannot be found is the strongest possible signal that a claim needs
    /// re-checking, and dropping it would silently improve the apparent quality of the answer.
    /// </summary>
    public bool MissingExcerpt { get; init; }
}

/// <summary>A canonical entity, after FORAGER's resolution has merged its aliases.</summary>
public sealed record KnowledgeEntity
{
    public required string EntityId { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = "unknown";

    /// <summary>The other names this turned out to be. "Bob Smith" and "Robert Smith" are one person here.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    public int MentionCount { get; init; }
    public double Confidence { get; init; }
}

/// <summary>A typed edge between entities or items, carrying its own evidence.</summary>
public sealed record KnowledgeRelationship
{
    public required string RelationshipId { get; init; }
    public required string Type { get; init; }
    public string? FromId { get; init; }
    public string? ToId { get; init; }
    public string? Statement { get; init; }
    public KnowledgeSupport Support { get; init; } = KnowledgeSupport.Unknown;
    public double Confidence { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A disagreement between statements, presented rather than resolved.
///
/// Rule 10 lives here. The retrieval layer's job is to make sure the model KNOWS two sources
/// disagree; choosing between them is reasoning, and reasoning is not RAG's to do. A suggested
/// resolution is carried when FORAGER offered one, explicitly marked as unapplied, because
/// "the newer document says otherwise" is evidence the model should weigh, not a verdict.
/// </summary>
public sealed record KnowledgeConflict
{
    public required string ConflictId { get; init; }

    /// <summary>FORAGER's type: <c>attribute_mismatch</c>, <c>contradiction</c>, <c>duplicate_source</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Which attribute is contested, when the conflict is about one.</summary>
    public string? AttributeKey { get; init; }

    /// <summary><c>open</c> or a decided state. Open means nobody has ruled.</summary>
    public required string Status { get; init; }

    public string? Description { get; init; }

    /// <summary>The competing knowledge ids. Always at least two, and all of them are in the context.</summary>
    public IReadOnlyList<string> KnowledgeIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceIds { get; init; } = Array.Empty<string>();

    /// <summary>What FORAGER would suggest, and why. NOT applied, and rendered as unapplied.</summary>
    public string? SuggestedResolution { get; init; }

    /// <summary>What a reviewer actually decided, if anyone has. Null while open.</summary>
    public string? Resolution { get; init; }

    public bool IsOpen => string.Equals(Status, "open", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the retrieval cost and how complete it is. Metadata is not decoration: a context that was
/// truncated, or served while the knowledge base was degraded, is a context whose absences mean
/// something different, and the reasoning layer has to be able to tell.
/// </summary>
public sealed record RetrievalMetadata
{
    public required string Query { get; init; }
    public required KnowledgeScope Scope { get; init; }

    /// <summary>Which FORAGER search backend answered: <c>sqlite-fts5</c> or <c>sqlite-like</c>.
    /// The fallback backend has no stemming or ranking, so a thin result set means something
    /// different under it, and an operator debugging recall needs to know which they got.</summary>
    public string? Backend { get; init; }

    public int FactCount { get; init; }
    public int EvidenceCount { get; init; }
    public int ConflictCount { get; init; }
    public int OpenConflictCount { get; init; }

    /// <summary>Candidates FORAGER ranked, before evidence filtering and the top-k cut.</summary>
    public int CandidatesConsidered { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>True when the top-k or size budget cut material that matched. An absence the model must not read as "nothing else exists".</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Set when the context was assembled with something missing — evidence that could not be
    /// fetched, a conflict lookup that failed. The context is still usable; it is just not complete,
    /// and saying so is the difference between degraded and wrong.
    /// </summary>
    public string? Degradation { get; init; }

    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Everything the reasoning layer is given about what the organization knows, for one query.
///
/// Deterministic: facts are ordered by support then confidence then id, and every collection is
/// sorted by a stable key, so the same knowledge base and the same query render identically.
/// </summary>
public sealed record KnowledgeContext
{
    public IReadOnlyList<KnowledgeFact> Facts { get; init; } = Array.Empty<KnowledgeFact>();
    public IReadOnlyList<KnowledgeEvidence> Evidence { get; init; } = Array.Empty<KnowledgeEvidence>();
    public IReadOnlyList<KnowledgeEntity> Entities { get; init; } = Array.Empty<KnowledgeEntity>();
    public IReadOnlyList<KnowledgeRelationship> Relationships { get; init; } = Array.Empty<KnowledgeRelationship>();
    public IReadOnlyList<KnowledgeConflict> Conflicts { get; init; } = Array.Empty<KnowledgeConflict>();
    public required RetrievalMetadata Metadata { get; init; }

    /// <summary>
    /// The empty context. Distinguished from an UNAVAILABLE one — this means the knowledge base
    /// answered and had nothing, which is a real and useful answer.
    /// </summary>
    public static KnowledgeContext Empty(string query, KnowledgeScope scope) => new()
    {
        Metadata = new RetrievalMetadata { Query = query, Scope = scope },
    };

    public bool IsEmpty => Facts.Count == 0;

    /// <summary>Any open conflict anywhere in the context. The renderer leads with these.</summary>
    public bool HasOpenConflicts => Conflicts.Any(c => c.IsOpen);

    /// <summary>
    /// Rule 9 as a runtime check, not a comment. Returns the facts that carry neither evidence nor
    /// an unresolved marker. Non-empty is a DEFECT IN THE ASSEMBLER, and the tests assert it stays
    /// empty for every fixture — including the deliberately broken ones.
    /// </summary>
    public IReadOnlyList<KnowledgeFact> FactsWithoutProvenance() =>
        Facts.Where(f => !f.HasProvenance).ToList();

    /// <summary>
    /// Render for a model. Plain text, not JSON, and that is a considered choice: the reasoning
    /// layer's job is to weigh statements, and a labelled prose block with explicit support levels
    /// is read more reliably by every model class than a nested object — including the small local
    /// models this project targets, which is the case that actually decides it.
    ///
    /// The rendering rules that matter:
    ///   - Conflicts come FIRST when any are open. A model that reads the facts before it learns
    ///     they are contested has already formed an answer.
    ///   - Every fact carries its support level and status in words, uppercase, unmissable.
    ///   - Every fact names its evidence. A fact with none says so.
    ///   - Truncation and degradation are stated, so absence is never read as non-existence.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        text.Append("KNOWLEDGE CONTEXT\n");
        text.Append("Query: \"").Append(Metadata.Query).Append("\"\n");
        text.Append("Scope: ").Append(Metadata.Scope).Append('\n');

        if (Facts.Count == 0)
        {
            text.Append("\nNo organizational knowledge matched this query.\n");
            text.Append("This means the knowledge base was searched and had nothing — it does NOT mean\n");
            text.Append("the answer is unknown to the organization. Do not fill the gap by assumption.\n");
            return text.ToString();
        }

        // Ordinals are assigned here and used by both sections, so [FACT-2] in the conflict block
        // and [FACT-2] in the fact block are the same statement. A conflict that cites an ordinal
        // the reader cannot find is worse than no cross-reference at all.
        var ordinals = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Facts.Count; i++) ordinals[Facts[i].KnowledgeId] = $"FACT-{i + 1}";

        var open = Conflicts.Where(c => c.IsOpen).ToList();
        if (open.Count > 0)
        {
            text.Append("\nCONFLICTS DETECTED — the sources disagree. Do not report either side as settled.\n");
            for (var i = 0; i < open.Count; i++)
            {
                var conflict = open[i];
                text.Append('\n').Append('[').Append("CONFLICT-").Append(i + 1).Append("] ")
                    .Append(conflict.Type);
                if (!string.IsNullOrWhiteSpace(conflict.AttributeKey))
                    text.Append(" on ").Append(conflict.AttributeKey);
                text.Append(" — UNRESOLVED\n");
                if (!string.IsNullOrWhiteSpace(conflict.Description))
                    text.Append("  ").Append(conflict.Description).Append('\n');
                var cited = conflict.KnowledgeIds
                    .Select(id => ordinals.TryGetValue(id, out var o) ? o : id)
                    .ToList();
                if (cited.Count > 0)
                    text.Append("  Competing statements: ").Append(string.Join(", ", cited)).Append('\n');
                if (!string.IsNullOrWhiteSpace(conflict.SuggestedResolution))
                    text.Append("  Suggested (NOT APPLIED): ").Append(conflict.SuggestedResolution).Append('\n');
            }
        }

        text.Append("\nFACTS\n");
        var evidenceById = Evidence.ToDictionary(e => e.EvidenceId, StringComparer.Ordinal);
        foreach (var fact in Facts)
        {
            text.Append('\n').Append('[').Append(ordinals[fact.KnowledgeId]).Append("] ")
                .Append(fact.Statement).Append('\n');
            text.Append("  Support: ").Append(Words(fact.Support))
                .Append("   Confidence: ").Append(fact.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                .Append("   Status: ").Append(fact.Status.ToString().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(fact.EffectiveDate))
                text.Append("   Effective: ").Append(fact.EffectiveDate);
            text.Append('\n');

            if (fact.Status == KnowledgeStatus.Superseded && !string.IsNullOrWhiteSpace(fact.SupersededBy))
                text.Append("  Superseded by: ")
                    .Append(ordinals.TryGetValue(fact.SupersededBy, out var by) ? by : fact.SupersededBy)
                    .Append('\n');

            if (fact.EvidenceIds.Count == 0)
            {
                text.Append("  Evidence: NONE — this statement is UNRESOLVED. Its supporting text could not be\n");
                text.Append("            located. Do not rely on it without checking the source yourself.\n");
            }
            else
            {
                text.Append("  Evidence:\n");
                foreach (var id in fact.EvidenceIds)
                {
                    if (!evidenceById.TryGetValue(id, out var ev)) continue;
                    text.Append("    - ").Append(ev.SourceName ?? ev.SourceId);
                    if (!string.IsNullOrWhiteSpace(ev.Location)) text.Append(" (").Append(ev.Location).Append(')');
                    text.Append('\n');
                    if (!string.IsNullOrWhiteSpace(ev.Excerpt))
                        text.Append("      \"").Append(Trim(ev.Excerpt, 300)).Append("\"\n");
                    if (ev.MissingExcerpt)
                        text.Append("      [excerpt could not be located in the source]\n");
                }
            }

            if (fact.Support == KnowledgeSupport.UnverifiedClaim)
                text.Append("  NOTE: unverified claim — asserted by a source but supported by nothing.\n");
        }

        if (Entities.Count > 0)
        {
            text.Append("\nRELATED ENTITIES\n");
            foreach (var entity in Entities)
            {
                text.Append("  ").Append(entity.Name).Append(" (").Append(entity.Type).Append(')');
                if (entity.Aliases.Count > 0)
                    text.Append(" — also: ").Append(string.Join(", ", entity.Aliases));
                text.Append('\n');
            }
        }

        var sources = Evidence
            .Select(e => e.SourceName ?? e.SourceId)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (sources.Count > 0)
        {
            text.Append("\nSOURCES\n");
            foreach (var source in sources) text.Append("  ").Append(source).Append('\n');
        }

        if (Metadata.Truncated)
            text.Append("\nNOTE: this context was truncated. More matching knowledge exists than is shown;\n")
                .Append("absence from this list is not evidence of absence from the knowledge base.\n");

        if (!string.IsNullOrWhiteSpace(Metadata.Degradation))
            text.Append("\nNOTE: partial retrieval — ").Append(Metadata.Degradation).Append('\n');

        return text.ToString();
    }

    private static string Words(KnowledgeSupport support) => support switch
    {
        KnowledgeSupport.DirectFact => "DIRECT FACT",
        KnowledgeSupport.SupportedInference => "SUPPORTED INFERENCE",
        KnowledgeSupport.UncertainInference => "UNCERTAIN INFERENCE",
        KnowledgeSupport.UnverifiedClaim => "UNVERIFIED CLAIM",
        _ => "UNKNOWN SUPPORT",
    };

    private static string Trim(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
