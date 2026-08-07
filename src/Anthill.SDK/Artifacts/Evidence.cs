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

    public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();

    public static Evidence Create(
        string kind,
        bool deterministic,
        bool passed,
        string missionId,
        IReadOnlyList<string>? artifactIds = null,
        string detail = "",
        string? taskId = null) => new()
        {
            Id = $"ev_{Guid.NewGuid():N}",
            Kind = kind,
            Deterministic = deterministic,
            Passed = passed,
            MissionId = missionId,
            ArtifactIds = artifactIds ?? Array.Empty<string>(),
            Detail = detail,
            TaskId = taskId,
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

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RepositoryMap, FileSet, UiMap, ChangePlan, PatchSet, TestReport,
            SecurityReview, FailureDiagnosis, VerificationBundle, OperatorSummary,
            ReleaseNotes, MemoryCandidate, RepairRecommendation, SourceSet, WorkspaceSnapshot,
        };

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
    /// </summary>
    public static string? ForAntKind(string? antKind) => (antKind ?? "").ToLowerInvariant() switch
    {
        "failure_diagnosis" => FailureDiagnosis,
        "memory_candidate" => MemoryCandidate,
        "security_review" => SecurityReview,
        "test_report" => TestReport,
        "ui_map" => UiMap,
        "repair_recommendation" => RepairRecommendation,
        "source_set" => SourceSet,
        "docs_patch_set" or "patch_set" => PatchSet,
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
