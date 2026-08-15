using System.Text.Json;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// Whether a payload is the shape its schema label claims. v0.3.8.57.
///
/// WHAT WAS MISSING. <see cref="ArtifactSchemas"/> has fixed the vocabulary since v3.8.19 and
/// <see cref="Artifact.Create"/> hashes the payload — so the store can prove a payload has not
/// CHANGED, and could prove nothing about whether it was ever right. Any string could be stored
/// under any schema name. <c>EvidenceKinds.SchemaValid</c> was declared in the same release and
/// produced by nothing, which is the tell: the check was intended and never built.
///
/// The consequence is not theoretical. <c>ArtifactContext</c> hands a worker artifacts labelled by
/// schema and a consumer reads the label — so a payload of the wrong shape does not fail, it gets
/// consumed. That is worse than a missing artifact, because a missing one is visibly missing.
///
/// SHAPES ARE DRAWN FROM PRODUCERS, NOT FROM AMBITION. Every entry below was read off the code that
/// writes it. A test_report is KEY: VALUE lines because <c>TesterAnt</c> writes lines; declaring it
/// JSON because JSON would be nicer would be a check that fails on every correct artifact in the
/// store, which is a test dictating a spelling rather than checking a property. Where a schema has
/// no producer at all its shape is honestly <see cref="ShapeKind.Unfixed"/> — recorded as undecided
/// rather than guessed at, so the first producer has to decide rather than inherit a guess.
///
/// NOTHING HERE REFUSES A WRITE. A producer whose payload is off-shape has made a mistake worth
/// surfacing loudly; dropping its artifact would replace a recoverable wrong row with an
/// unrecoverable absent one, and the absence is the harder failure to notice. The write boundary
/// stores and reports; the read boundary tells the consumer what it is holding.
/// </summary>
public static class ArtifactSchemaCheck
{
    public enum ShapeKind
    {
        /// <summary>Free text. Any non-empty payload conforms — the shape IS prose.</summary>
        Narrative,

        /// <summary>A JSON object, with <see cref="Shape.RequiredKeys"/> present at the top level.</summary>
        JsonObject,

        /// <summary>A JSON array. Element shape is the producer's business.</summary>
        JsonArray,

        /// <summary>
        /// No producer exists, so no shape has been decided. Distinct from Narrative: "anything goes"
        /// and "nobody has chosen yet" are different states, and collapsing them would let the first
        /// producer inherit a default nobody argued for.
        /// </summary>
        Unfixed,
    }

    public enum Conformance
    {
        Valid,

        /// <summary>Payload is empty or whitespace. Never conforming — an artifact of nothing is not a record.</summary>
        Empty,

        /// <summary>Declared JSON, does not parse.</summary>
        Malformed,

        /// <summary>Parses, but is the wrong JSON kind or is missing a required key.</summary>
        WrongShape,

        /// <summary>The schema is not in <see cref="ArtifactSchemas.All"/>.</summary>
        UnknownSchema,

        /// <summary>The schema is known and its shape is deliberately undecided. Not a failure.</summary>
        ShapeUndecided,
    }

    public readonly record struct Result(Conformance Status, string Reason)
    {
        /// <summary>
        /// True when nothing is WRONG. <see cref="Conformance.ShapeUndecided"/> counts as conforming:
        /// a schema nobody produces yet cannot be violated, and reporting it as a violation on every
        /// read would train readers to ignore the report.
        /// </summary>
        public bool Conforms => Status is Conformance.Valid or Conformance.ShapeUndecided;
    }

    public sealed record Shape(ShapeKind Kind, IReadOnlyList<string> RequiredKeys, string Producer);

    // Prefixed, and not for style: helpers named `Array` and `Object` shadow System.Array and
    // System.Object inside this class, so `Array.Empty<string>()` in the line above resolved to the
    // helper itself and would not compile.
    private static Shape AsNarrative(string producer) => new(ShapeKind.Narrative, [], producer);
    private static Shape AsObject(string producer, params string[] keys) => new(ShapeKind.JsonObject, keys, producer);
    private static Shape AsArray(string producer) => new(ShapeKind.JsonArray, [], producer);
    private static Shape AsUnfixed(string why) => new(ShapeKind.Unfixed, [], why);

    /// <summary>
    /// Every schema in the vocabulary, with the shape its producer actually writes.
    ///
    /// Guarded by a test that this covers <see cref="ArtifactSchemas.All"/> exactly — a schema added
    /// to the vocabulary without a shape decision would otherwise validate as "unknown" forever,
    /// which is the silent-hole outcome the whole check exists to close.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Shape> Shapes =
        new Dictionary<string, Shape>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- structured, and something downstream reads the structure ----

            [ArtifactSchemas.PatchSet] =
                AsObject("ExecutionService.RecordPatchArtifact", "proposals"),

            [ArtifactSchemas.DocsPatchSet] =
                AsObject("ScribeAnt (docs_patch_proposal)", "targets"),

            [ArtifactSchemas.VerificationBundle] =
                AsObject("ExecutionService.RecordVerificationArtifact", "patch_set_id", "proposals"),

            [ArtifactSchemas.WorkspaceSnapshot] =
                AsObject("ExecutionService.RecordWorkspaceSnapshot", "patch_set_id", "applied_tree_hash"),

            [ArtifactSchemas.FileSet] =
                AsObject("FileAnt", "files"),

            [ArtifactSchemas.SourceSet] =
                AsObject("WebResearchAnt", "sources"),

            // v0.3.8.64 (S6): `{}` conformed, and a gate that accepts an empty object as a map is
            // an existence check wearing a schema check's name. The old comment worried an honest
            // map of a route-less repository would be invalid — but the cartographer has always
            // emitted these three keys unconditionally, as EMPTY ARRAYS when nothing was found.
            // An honest empty map says "routes: []"; only a fabricated or truncated one says
            // nothing at all.
            [ArtifactSchemas.UiMap] =
                AsObject("UiCartographerAnt", "files_examined", "routes", "api_calls"),

            // FailureContext.FromJson owns the field-level contract; this only asserts it is an
            // object at all. Two components enforcing the same field list is how they come to
            // disagree about it.
            [ArtifactSchemas.FailureContext] =
                AsObject("ExecutionService.RecordFailureContext"),

            [ArtifactSchemas.MemoryCandidate] =
                AsArray("ArchivistAnt"),

            // research_need is required and the other three are not: a researcher can honestly
            // have nothing under "pheromone guidance", but a brief that does not say what still
            // needs finding out has not done the job the section exists for.
            [ArtifactSchemas.ResearchBrief] =
                AsObject("ResearcherAnt (parsed from its declared sections)", "research_need"),

            // ---- narrative, and honestly so ----

            [ArtifactSchemas.TestReport] =
                AsNarrative("TesterAnt — KEY: VALUE lines, not JSON"),

            [ArtifactSchemas.SecurityReview] =
                AsNarrative("SoldierAnt — deterministic policy review, rendered as lines"),

            [ArtifactSchemas.FailureDiagnosis] =
                AsNarrative("MedicAnt"),

            [ArtifactSchemas.RepairRecommendation] =
                AsNarrative("MedicAnt — a compact 'role:task_type' route"),

            [ArtifactSchemas.ReleaseNotes] =
                AsNarrative("ScribeAnt"),

            // ---- named by ADR-004, produced by nothing yet ----

            [ArtifactSchemas.RepositoryMap] =
                AsUnfixed("named by ADR-004; no producer writes one, so no shape has been chosen"),

            [ArtifactSchemas.ChangePlan] =
                AsUnfixed("named by ADR-004; the coder proposes patches directly and no change_plan is written"),

            [ArtifactSchemas.OperatorSummary] =
                AsUnfixed("named by ADR-004; the scribe writes release_notes instead"),
        };

    /// <summary>
    /// Does this payload match what its schema promises? Pure, allocation-light, and safe on any
    /// input — it is called on the write path and must never be the reason a Put throws.
    /// </summary>
    public static Result Validate(string? schema, string? payload)
    {
        var name = schema ?? "";
        if (!Shapes.TryGetValue(name, out var shape))
            return new(Conformance.UnknownSchema,
                $"'{name}' is not in the artifact vocabulary. Add it to ArtifactSchemas and give it a "
              + "shape, or use an existing schema — a row whose type is not in the vocabulary is a row "
              + "no consumer can be written against.");

        if (shape.Kind == ShapeKind.Unfixed)
            return new(Conformance.ShapeUndecided, $"'{name}' has no producer yet: {shape.Producer}");

        if (string.IsNullOrWhiteSpace(payload))
            return new(Conformance.Empty, $"'{name}' payload is empty");

        if (shape.Kind == ShapeKind.Narrative) return new(Conformance.Valid, "");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload!);
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            return new(Conformance.Malformed, $"'{name}' declares JSON and the payload does not parse: {error.Message}");
        }

        if (shape.Kind == ShapeKind.JsonArray)
            return root.ValueKind == JsonValueKind.Array
                ? new(Conformance.Valid, "")
                : new(Conformance.WrongShape, $"'{name}' expects a JSON array, found {root.ValueKind}");

        if (root.ValueKind != JsonValueKind.Object)
            return new(Conformance.WrongShape, $"'{name}' expects a JSON object, found {root.ValueKind}");

        var missing = shape.RequiredKeys.Where(k => !root.TryGetProperty(k, out _)).ToList();
        return missing.Count == 0
            ? new(Conformance.Valid, "")
            : new(Conformance.WrongShape,
                $"'{name}' is missing required key(s): {string.Join(", ", missing)} "
              + $"(written by {shape.Producer})");
    }
}
