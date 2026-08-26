namespace Anthill.Core.Agents;

/// <summary>
/// What an <see cref="AntEvidence"/> row's <c>Kind</c> may be — a closed vocabulary, extracted from
/// every emission site in the tree. v0.3.8.94.
///
/// WHY THIS EXISTS, and it is defect class 9 twice over. Two consumers filtered ant evidence on
/// <c>Kind == "tool"</c> — `FailureContext.Tool` and the persisted `TaskResult.Tool` — and NOTHING
/// in the colony has ever emitted that kind: both fields have been null for every task of every
/// mission since they were written, and a filter that cannot match looks exactly like "no tool was
/// involved". Beside them, `deterministic_work_completed` tested ant evidence against
/// `EvidenceKinds.Reproducible` — build / test_run / hash_match — a vocabulary that belongs to the
/// VERIFICATION store and that ant evidence has never used, so half of that expression was dead the
/// day it was written.
///
/// Two vocabularies exist ON PURPOSE and this file is the boundary between them:
/// `Anthill.SDK.Artifacts.EvidenceKinds` names what the verification store records (verifier
/// verdicts, promotable evidence); THIS names what an ant reports about its own execution. A
/// consumer reading ant evidence with the store's vocabulary is asking the wrong witness — that is
/// the exact mistake this replaces, so the two lists are deliberately disjoint.
///
/// The sweep in `AntEvidenceVocabularyTests` holds emissions to these constants — a bare string
/// literal at an emission or consumption site is how `"tool"` got promised and never produced.
/// </summary>
public static class AntEvidenceKinds
{
    /// <summary>An allowlisted check ran; Detail carries its outcome. The tester's kind.</summary>
    public const string Check = "check";

    /// <summary>
    /// A tool this task dispatched through the registry. v0.3.8.94 — the kind two consumers waited
    /// six releases for. Produced at the measurement boundary (`WithMeasuredMetrics`) from the
    /// registry's own dispatch record, for the same reason ToolCalls is: self-reporting produced
    /// zeros, and the chokepoint every dispatch passes is the honest witness.
    /// </summary>
    public const string Tool = "tool";

    /// <summary>A file the task examined or changed. Feeds FailureContext.AffectedPaths.</summary>
    public const string FilePath = "file_path";

    /// <summary>The workspace identity a specialist ran against (value "tree", detail the hash).</summary>
    public const string Workspace = "workspace";

    /// <summary>The materialized revision a check role actually judged.</summary>
    public const string Revision = "revision";

    /// <summary>A policy rule the soldier matched. Value is the rule id.</summary>
    public const string PolicyRule = "policy_rule";

    /// <summary>The failed task a medic diagnosed.</summary>
    public const string FailureId = "failure_id";

    /// <summary>The normalized signature of the failure a medic diagnosed.</summary>
    public const string FailureSignature = "failure_signature";

    /// <summary>The mission an archivist summarised.</summary>
    public const string MissionId = "mission_id";

    /// <summary>The verifier's verdict, as data beside its prose.</summary>
    public const string VerificationVerdict = "verification_verdict";

    /// <summary>Where the verifier's verdict came from — the evidence store, or its absence.</summary>
    public const string VerdictSource = "verdict_source";

    /// <summary>The model's verdict was overridden by stored evidence, and by what.</summary>
    public const string ModelVerdictOverridden = "model_verdict_overridden";

    /// <summary>Every kind an ant may report.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Check, Tool, FilePath, Workspace, Revision, PolicyRule,
        FailureId, FailureSignature, MissionId,
        VerificationVerdict, VerdictSource, ModelVerdictOverridden,
    };

    /// <summary>
    /// The kinds that record DETERMINISTIC WORK — reproducible actions, not observations or
    /// judgments. Deliberately narrow: a dispatched read tool is activity, a matched policy rule is
    /// a finding, but only a check that RAN is reproducible verification work. This is what
    /// `deterministic_work_completed` reads, replacing the half-dead expression that consulted the
    /// verification store's vocabulary about evidence the store never wrote.
    /// </summary>
    public static readonly IReadOnlySet<string> Deterministic =
        new HashSet<string>(StringComparer.Ordinal) { Check };
}
