namespace Anthill.SDK.Artifacts;

/// <summary>
/// A statement that something was CHECKED, and how. ADR-004, v3.8.19.
///
/// Separate from <see cref="Artifact"/> because they answer different questions. An artifact is what
/// was produced; evidence is whether it holds up. Collapsing the two is how "the model said it
/// passed" becomes indistinguishable from "the test suite passed", which is the distinction the
/// whole colony's verification model rests on.
///
/// <see cref="Deterministic"/> is the load-bearing field. A compiler, a test runner and a hash
/// comparison are reproducible; a model's review is not. Both are worth recording and only one may
/// promote a mission — v2.26.0 established that rule and this makes it a property of the record
/// rather than a convention at each call site.
/// </summary>
public sealed record Evidence
{
    public required string Id { get; init; }

    /// <summary>
    /// What kind of check this was — <c>build</c>, <c>test_run</c>, <c>hash_match</c>,
    /// <c>model_review</c>. See <see cref="EvidenceKinds"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// True when the check is REPRODUCIBLE: run it again on the same inputs and it answers the same.
    /// A model's opinion is not, however confident it sounds. Only deterministic evidence may carry a
    /// mission to a verified outcome.
    /// </summary>
    public required bool Deterministic { get; init; }

    public required bool Passed { get; init; }

    /// <summary>The artifacts this check was performed ON. Empty evidence proves nothing about anything.</summary>
    public IReadOnlyList<string> ArtifactIds { get; init; } = Array.Empty<string>();

    /// <summary>Human-readable detail: the failing assertion, the compiler error, the reviewer's note.</summary>
    public string Detail { get; init; } = "";

    public required string MissionId { get; init; }
    public string? TaskId { get; init; }

    /* ------------------------------------------------------------------------------------------
     * WHICH TREE THIS JUDGED. v0.3.8.57.
     *
     * A check is a statement about a specific set of bytes, and until this release the evidence row
     * could not say which. The tree hash appeared only inside `Detail`, truncated to twelve
     * characters, in prose — readable by a person, useless to a query. So "does this build result
     * belong to the revision the verifier is about to promote?" had no answer the runtime could
     * compute, and the failure it guards against is silent: correct evidence attached to the wrong
     * source tree still reads as a pass.
     *
     * That is not hypothetical here. v3.8.22 shipped build verdicts computed against the primary
     * workspace instead of the patched sandbox — true statements about the wrong bytes — and it took
     * a release to notice. These three fields are what let the verifier reject that mechanically
     * rather than by inspection.
     *
     * NULLABLE, because plenty of evidence is legitimately not about a revision: a model review on
     * an informational mission, a tool outcome, a hash match over an artifact. Null means "not about
     * a materialized revision", which is different from "about one, unrecorded" — a consumer that
     * requires identity must refuse the null rather than assume it matches.
     * ------------------------------------------------------------------------------------------ */

    /// <summary>The materialized revision this check ran against, or null when it judged no tree.</summary>
    public string? RevisionId { get; init; }

    /// <summary>The patch set that produced that revision — WHAT WAS ASKED FOR.</summary>
    public string? PatchSetHash { get; init; }

    /// <summary>The tree that resulted — WHAT ACTUALLY LANDED. The two differ when a patch applies
    /// partially or the base moved, which is precisely when evidence must not be reused.</summary>
    public string? TreeHash { get; init; }

    /// <summary>Whether this row can be matched to a specific materialized revision.</summary>
    public bool IdentifiesARevision =>
        !string.IsNullOrWhiteSpace(RevisionId)
        && !string.IsNullOrWhiteSpace(PatchSetHash)
        && !string.IsNullOrWhiteSpace(TreeHash);

    /// <summary>
    /// Does this evidence judge the given revision? Compares the TREE as well as the id, because an
    /// id can be reused by a re-materialization and a tree hash cannot.
    /// </summary>
    public bool Judges(string revisionId, string treeHash) =>
        IdentifiesARevision
        && string.Equals(RevisionId, revisionId, StringComparison.Ordinal)
        && string.Equals(TreeHash, treeHash, StringComparison.Ordinal);

    public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();

    public static Evidence Create(
        string kind,
        bool deterministic,
        bool passed,
        string missionId,
        IReadOnlyList<string>? artifactIds = null,
        string detail = "",
        string? taskId = null,
        string? revisionId = null,
        string? patchSetHash = null,
        string? treeHash = null) => new()
        {
            Id = $"ev_{Guid.NewGuid():N}",
            Kind = kind,
            Deterministic = deterministic,
            Passed = passed,
            MissionId = missionId,
            ArtifactIds = artifactIds ?? Array.Empty<string>(),
            Detail = detail,
            TaskId = taskId,
            RevisionId = revisionId,
            PatchSetHash = patchSetHash,
            TreeHash = treeHash,
        };
}

/// <summary>
/// The check kinds, named once. Strings rather than an enum because a module may add a check the
/// core has never heard of — the same reasoning that keeps <c>ToolKind</c> narrow and the tool NAME
/// open.
/// </summary>
public static class EvidenceKinds
{
    // Deterministic — reproducible from the same inputs.
    public const string Build = "build";
    public const string TestRun = "test_run";
    public const string HashMatch = "hash_match";
    public const string SchemaValid = "schema_valid";
    public const string CommandCheck = "command_check";

    // Non-deterministic — recorded, never promoting.
    public const string ModelReview = "model_review";
    public const string OperatorJudgment = "operator_judgment";

    /// <summary>
    /// A READ-ONLY OBSERVATION of state: a directory listed, a file read, the workspace searched or
    /// indexed. v0.3.8.98.
    ///
    /// WHY IT IS NOT REPRODUCIBLE, even though repeating it on an unchanged tree gives the same
    /// answer. Reproducibility in this vocabulary means "bound to the bytes it judged" — every
    /// deterministic kind above is stamped with a revision and a tree hash, and that binding is what
    /// lets a later reader confirm the evidence is about the thing being promoted. An inspection of
    /// a live, unpatched workspace has no such identity: the tree it read can change underneath it
    /// with nothing recorded. Calling it deterministic would put "the ant looked at a file" in the
    /// same table as "the test suite passed" — the exact confusion the flag exists to prevent, and
    /// the reason this kind sits below the line rather than above it.
    ///
    /// WHY IT IS RECORDED AT ALL. An assessment mission runs no checks: its authority is `observe`,
    /// so the deterministic lane is empty BY DESIGN, and before this kind existed such a mission
    /// left the evidence store untouched no matter how much it read. "Nothing was inspected" and
    /// "an inspection happened and this vocabulary had no word for it" were indistinguishable —
    /// which is how mission 7afd85b2 could complete its tasks, read nothing, and be impossible to
    /// tell apart from one that had. An audit that asserts without inspecting is now detectable,
    /// and an inspection still cannot promote anything.
    /// </summary>
    public const string Inspection = "inspection";

    /// <summary>
    /// Which kinds are reproducible. Stated here so <c>Evidence.Deterministic</c> can be CHECKED
    /// against the kind rather than trusted from the caller — a "test_run" that claims to be
    /// non-deterministic, or a "model_review" that claims it is, is a mistake worth catching.
    /// </summary>
    public static readonly IReadOnlySet<string> Reproducible =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Build, TestRun, HashMatch, SchemaValid, CommandCheck };

    /// <summary>True when the kind's reproducibility matches what the record claims.</summary>
    public static bool AgreesWithKind(string kind, bool deterministic) =>
        Reproducible.Contains(kind) == deterministic;
}

/// <summary>
/// The artifact schemas ADR-004 names for v3.9.0. Declared now, with the store, so the vocabulary is
/// fixed before anything produces one — the alternative is each ant inventing its own name for the
/// same thing, which is the prose problem with extra steps.
/// </summary>
public static class ArtifactSchemas
{
    public const string RepositoryMap = "repository_map";
    public const string FileSet = "file_set";
    public const string UiMap = "ui_map";
    public const string ChangePlan = "change_plan";
    public const string PatchSet = "patch_set";
    public const string TestReport = "test_report";
    public const string SecurityReview = "security_review";
    public const string FailureDiagnosis = "failure_diagnosis";
    public const string VerificationBundle = "verification_bundle";
    public const string OperatorSummary = "operator_summary";
    public const string ReleaseNotes = "release_notes";
    public const string MemoryCandidate = "memory_candidate";

    /// <summary>
    /// What the operator asked for, and what the mission did about each of it. v0.3.8.98.
    ///
    /// Added because the SHAPE EXISTS and had no way to reach an operator: the specification gives
    /// each request an id at intake, the evaluator decides per id whether anything produced it, and
    /// before this the only trace of that decision was a sentence in a refusal. A ledger row says
    /// which task owned `d2`, whether the plan declared that or the runtime inferred it, and
    /// whether the task finished — which is the difference between "the answer looks thin" and
    /// "the step that owned your second question failed".
    /// </summary>
    public const string DeliverableLedger = "deliverable_ledger";

    /// <summary>
    /// An answer as CLAIMS, each with the retrieved source it rests on or the fact that it has
    /// none. v0.3.8.99.
    ///
    /// Added under the vocabulary's own rule — a schema the colony produces and the vocabulary did
    /// not name is a gap in the vocabulary — and only now that the colony produces it: the builder
    /// is ASKED for claims when a mission retrieved sources, so the structure is produced rather
    /// than imputed from prose. See <see cref="Artifacts.SourcedAnswer"/> for why `ResearchBrief`
    /// deliberately declined to type the builder, and what had to change first.
    /// </summary>
    public const string SourcedAnswer = "sourced_answer";

    /// <summary>
    /// The colony's OWN prior missions a task was shown, by id. v0.3.8.99.
    ///
    /// The internal analogue of <see cref="SourceSet"/>, and it exists for the same reason: a claim
    /// can only be traced to something that left a record of having been consulted. Without it, an
    /// answer drawn from the colony's own history has no citable identity and renders as
    /// `[UNSOURCED]` — which flattens "we could not attribute this" together with "this came from
    /// what we already knew", two different facts an operator needs to tell apart.
    /// </summary>
    public const string RecallSet = "recall_set";

    /// <summary>
    /// The records a claim may be cited against. v0.3.8.99.
    ///
    /// Named once because the builder LISTS them and `CitationIntegrity` RESOLVES against them, and
    /// a citable set that is spelled twice is one that eventually differs — offering a model a
    /// source the gate will not accept, which reads to an operator as the model inventing a
    /// citation it was handed.
    /// </summary>
    public static readonly IReadOnlySet<string> CitableRecords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SourceSet, RecallSet };

    /// <summary>
    /// A created deliverable — the content itself, its stated requirements traced or admitted
    /// unmet, and the identified inputs it rests on. v0.3.8.100.
    ///
    /// Added under the same rule as `sourced_answer` and only under the same condition: the
    /// builder is now ASKED for this structure when its task is typed as creation, so the record
    /// is produced rather than imputed from prose. See <see cref="Artifacts.CreatedArtifact"/> for
    /// what the record claims and — as importantly — what it deliberately does not.
    /// </summary>
    public const string CreatedArtifact = "created_artifact";

    /// <summary>
    /// v3.8.20 — added when the ant bridge was built, because the medic already emits
    /// <c>repair_recommendation</c> and five of the other six kinds ants emit mapped exactly onto
    /// the list above. A schema the colony already produces and the vocabulary did not name is a
    /// gap in the vocabulary, not a reason to rename what the ant emits.
    /// </summary>
    public const string RepairRecommendation = "repair_recommendation";

    /// <summary>
    /// The external sources a research task actually consulted. v3.8.21 — added because
    /// <c>WebResearchAnt</c> already builds and persists a <c>List&lt;SourceRecord&gt;</c>, which is
    /// genuinely structured data that had no way to reach the graph. A schema added because the
    /// colony produces the shape, not because the ADR imagined it.
    /// </summary>
    public const string SourceSet = "source_set";

    /// <summary>
    /// The tree a verification actually ran against. v3.8.23 — added because a verdict without one
    /// cannot be checked. "Build passed" is a claim about a specific set of bytes in a specific
    /// directory, and v3.8.22 recorded build verdicts whose directory was the primary workspace
    /// rather than the patched one: true statements about the wrong tree, indistinguishable in the
    /// store from true statements about the right one.
    /// </summary>
    public const string WorkspaceSnapshot = "workspace_snapshot";

    /// <summary>
    /// The typed record of one task failure, produced AT the failure boundary. Structural-repair
    /// release §2 — recovery consumes this, never re-inferring failure state from prose. See
    /// <see cref="Artifacts.FailureContext"/> for the payload shape.
    /// </summary>
    public const string FailureContext = "failure_context";

    /// <summary>
    /// A proposal to change DOCUMENTATION, requiring approval. v0.3.8.57.
    ///
    /// The scribe has emitted `docs_patch_set` since v3.8.20 and <see cref="ForAntKind"/> folded it
    /// onto <see cref="PatchSet"/> — but the two payloads have nothing in common. A patch set is
    /// `{ patch_set_id, summary, proposals[] }` and something materialises it; a docs proposal is
    /// `{ targets, source_mission, requires_approval }` and the scribe holds no apply permission.
    /// Every consumer that asked the store for "this mission's patch sets" — the soldier does
    /// exactly that, and reports how many it reviewed — was handed both and could not tell them
    /// apart. The vocabulary's own rule applies: a schema the colony already produces and the
    /// vocabulary did not name is a gap in the vocabulary, not a reason to rename what the ant emits.
    /// </summary>
    public const string DocsPatchSet = "docs_patch_set";

    /// <summary>
    /// The researcher's four declared sections, as data. v0.3.8.57.
    ///
    /// Added because the SHAPE ALREADY EXISTED — the researcher's prompt has demanded these
    /// headings since the ant was written, and the response was then flattened into a string. That
    /// is the vocabulary's stated rule again: a schema the colony already produces and the
    /// vocabulary did not name is a gap in the vocabulary. See <see cref="Artifacts.ResearchBrief"/>
    /// for why the BUILDER deliberately gets no equivalent.
    /// </summary>
    public const string ResearchBrief = "research_brief";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RepositoryMap, FileSet, UiMap, ChangePlan, PatchSet, TestReport, DeliverableLedger,
            SourcedAnswer, RecallSet, CreatedArtifact,
            SecurityReview, FailureDiagnosis, VerificationBundle, OperatorSummary,
            ReleaseNotes, MemoryCandidate, RepairRecommendation, SourceSet, WorkspaceSnapshot,
            FailureContext, DocsPatchSet, ResearchBrief,
        };

    /// <summary>
    /// Ant artifact kinds the bridge DELIBERATELY does not store as rows. v0.3.8.94.
    ///
    /// Until now these fell into <see cref="ForAntKind"/>'s null arm alongside typos and unknown
    /// kinds, and "deliberately not bridged" was indistinguishable from "nobody wrote the arm" —
    /// which is exactly how it read from outside: the coder has declared `patch_json` on every
    /// patch task since the execution framework, the bridge silently dropped it, and whether that
    /// was a decision or a gap could only be answered by archaeology. It was in fact correct both
    /// times, for reasons worth a name each:
    ///
    ///   * `text` — the narrative an operator reads. It IS the task result, stored on the task row;
    ///     a second copy as a schema-less artifact row would be the prose problem with extra steps.
    ///   * `patch_json` — the coder's raw patch JSON. `ExecutionService.RecordPatchArtifact` stores
    ///     the PARSED, validated patch set as the `patch_set` artifact — the authoritative record
    ///     the soldier reviews. Bridging the raw string too would store the same change twice under
    ///     two schemas, and every consumer asking "this mission's patch sets" would have to know
    ///     which copy is real.
    ///
    /// A kind in this set is a decision; a kind in neither this set nor the map is a gap the
    /// vocabulary test refuses.
    /// </summary>
    public static readonly IReadOnlySet<string> TransportOnly =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "patch_json" };

    /// <summary>
    /// What an ant's <c>AntArtifact.Kind</c> means in this vocabulary. v3.8.20.
    ///
    /// Ants have emitted typed artifacts since v2.19.0 — they were just serialised into a JSON blob
    /// on <c>task_results</c> and never became rows. Five of the seven kinds already matched a schema
    /// name exactly, which is the evidence that the vocabulary was drawn from the right place; the
    /// two that did not are mapped here rather than renamed at the ant, because the ant's name is the
    /// one an operator reads in a transcript.
    ///
    /// An unrecognised kind maps to NULL and is skipped rather than guessed at. A bridge that
    /// invented a schema for an unknown kind would fill the graph with rows whose type is a lie.
    /// A kind in <see cref="TransportOnly"/> maps to null ON PURPOSE — see that set for the two
    /// reasons.
    /// </summary>
    public static string? ForAntKind(string? antKind) => (antKind ?? "").ToLowerInvariant() switch
    {
        "failure_diagnosis" => FailureDiagnosis,
        "failure_context" => FailureContext,
        "memory_candidate" => MemoryCandidate,
        "security_review" => SecurityReview,
        "test_report" => TestReport,
        "ui_map" => UiMap,
        "repair_recommendation" => RepairRecommendation,
        "source_set" => SourceSet,
        "sourced_answer" => SourcedAnswer,
        "created_artifact" => CreatedArtifact,
        "recall_set" => RecallSet,
        "docs_patch_set" => DocsPatchSet,
        "research_brief" => ResearchBrief,
        "patch_set" => PatchSet,
        "repository_map" => RepositoryMap,
        "file_set" => FileSet,
        "change_plan" => ChangePlan,
        "operator_summary" => OperatorSummary,
        "release_notes" => ReleaseNotes,
        "verification_bundle" => VerificationBundle,
        "workspace_snapshot" => WorkspaceSnapshot,
        _ => null,
    };
}
