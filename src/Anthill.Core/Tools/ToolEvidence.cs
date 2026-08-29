using Anthill.SDK.Artifacts;

namespace Anthill.Core.Tools;

/// <summary>
/// Which tool outcomes are DETERMINISTIC EVIDENCE. v3.8.20 (ADR-004).
///
/// WHY THIS EXISTS, AND WHY HERE. v3.8.19 shipped the evidence store with no producer, and the
/// obvious candidate turned out to be a mirage: <c>VerificationRunner</c> — which owns
/// <c>BuildVerifier</c> and <c>TestVerifier</c>, both genuinely deterministic — has NO PRODUCTION
/// CALL SITE. It is constructed only by tests. The one bundle production does build,
/// <c>LearningRecorder.MissionEvidenceBundle</c>, declares <c>Deterministic: false</c>. So at
/// v3.8.19 the colony produced no deterministic evidence anywhere, and a store waiting for the
/// verification framework to be wired up would have waited indefinitely.
///
/// Where deterministic checks DO run in production is here: <c>run_allowlisted_check</c> is the
/// tester ant's only execution surface, it runs a declared command from a catalog with a fixed
/// argument list, and its exit code is a fact. Rerun it on the same tree and it answers the same.
/// That is the definition, so that is the evidence.
///
/// THE LIST IS SHORT AND CLOSED ON PURPOSE. A tool qualifies only if repeating it on unchanged
/// inputs must give the same answer. <c>web_search</c> does not — the internet changes.
/// <c>shell_command</c> does not — it runs whatever it was handed. <c>read_text_file</c> reports
/// state rather than testing a claim. Being generous here would put "the ant looked at a file" into
/// the same table as "the test suite passed", which is the confusion the whole deterministic flag
/// exists to prevent.
/// </summary>
public static class ToolEvidence
{
    /// <summary>
    /// Tools whose success or failure is a reproducible verdict, and the evidence kind each records.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DeterministicTools =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // A declared, allowlisted command with a fixed argument list and a hard timeout. The
            // exit code is the verdict; the catalog is what makes it repeatable.
            ["run_allowlisted_check"] = EvidenceKinds.CommandCheck,
        };

    /// <summary>
    /// Tools whose outcome is a READ-ONLY OBSERVATION rather than a verdict. v0.3.8.98.
    ///
    /// THE LIST ABOVE IS UNCHANGED, and that is the point. A verdict is a reproducible claim bound
    /// to the bytes it judged, and exactly one tool produces one; nothing here is being promoted
    /// into that lane. What is added is a second, lower lane for the fact that an inspection
    /// HAPPENED — recorded as <see cref="EvidenceKinds.Inspection"/>, always non-deterministic, so
    /// `HasDeterministicPass`, `EvidenceVerdict` and the promotion identity gate treat it exactly
    /// as they treat a model review: recorded, never promoting.
    ///
    /// WHY IT IS NEEDED. An assessment mission's authority is `observe`: it runs no checks, so the
    /// deterministic lane is empty by design and the store stayed empty however much the colony
    /// read. That made "this audit inspected nothing and asserted its findings" indistinguishable
    /// from "this audit read the repository", which is mission 7afd85b2's exact shape. These are the
    /// colony's whole read surface — four that read the REPOSITORY and one that reads the COLONY
    /// ITSELF — they are dispatched through this chokepoint, and the record costs one row per call.
    /// </summary>
    private static readonly IReadOnlySet<string> ObservationTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_directory", "read_text_file", "search_workspace", "repository_index",
            // v0.3.8.98: the colony reading its OWN state is an inspection by the same definition —
            // read-only, unbound to any tree, and the only receipt an audit of "what is enabled
            // right now" can leave. `system_info` stays out: reporting the OS and the process is
            // not evidence that anything about the colony was examined.
            "colony_state",
        };

    /// <summary>True when this tool's outcome is a reproducible VERDICT — the promotion lane.</summary>
    public static bool IsDeterministic(string? toolName) =>
        toolName is not null && DeterministicTools.ContainsKey(toolName);

    /// <summary>True when this tool's outcome is a read-only observation worth recording.</summary>
    public static bool IsObservation(string? toolName) =>
        toolName is not null && ObservationTools.Contains(toolName);

    /// <summary>True when this tool produces an evidence row of EITHER kind.</summary>
    public static bool Records(string? toolName) => IsDeterministic(toolName) || IsObservation(toolName);

    /// <summary>
    /// The evidence a completed tool call represents, or null when the tool does not produce any.
    ///
    /// Returns null rather than a non-deterministic record for everything else, deliberately. The
    /// store is not an audit log — the event stream already is one, and <c>tool_called</c> /
    /// <c>tool_completed</c> have carried that since v1. Evidence is specifically the set of claims
    /// something can be PROMOTED on, and widening it costs exactly the property that makes it useful.
    /// </summary>
    public static Evidence? For(string toolName, bool success, string missionId, string? taskId, string detail)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return null;

        if (!DeterministicTools.TryGetValue(toolName ?? "", out var kind))
        {
            // The observation lane. Detail names the TOOL as well as its outcome, because "an
            // inspection happened" is only useful if a reader can tell a directory listing from a
            // file read — and the identity fields are deliberately not stamped: an unpatched
            // workspace is not a revision, and labelling one would let an observation of the base
            // tree look like evidence about a candidate.
            if (!ObservationTools.Contains(toolName ?? "")) return null;
            return Evidence.Create(
                kind: EvidenceKinds.Inspection,
                deterministic: false,
                passed: success,
                missionId: missionId,
                detail: TextUtil.Truncate($"{toolName}: {detail ?? ""}", 2400),
                taskId: taskId);
        }


        // v0.3.8.57 — the TREE this check actually ran in.
        //
        // Structural repair §3 stamps the revision on the TASK (`RanRevisionId`) and that is what
        // MissionVerification pairs on. This row said nothing, and it is the one that matters
        // longest: a task object lives for a mission, while an evidence row is what a replay reads
        // and what `Evidence.Judges` was built to interrogate. So "the tester ran in revision B" was
        // answerable and "this passing command_check is about revision B's bytes" was not.
        //
        // Read from the AMBIENT SCOPE rather than passed in. The scope is what actually decided which
        // tree the command ran against — ExecutionService enters it around the dispatch — so taking
        // the identity from anywhere else would risk recording a revision the check did not run in,
        // which is precisely the "true statement about the wrong workspace" failure v3.8.22 shipped.
        //
        // Null outside a revision, and that is correct: an unpatched mission workspace is not a
        // revision, and labelling it as one would let evidence about the base tree satisfy a
        // candidate built from a patch.
        var scope = Workspaces.MissionWorkspaceScope.Current;

        return Evidence.Create(
            kind: kind,
            deterministic: true,
            passed: success,
            missionId: missionId,
            // v0.3.8.97: 500 → 2400. Five hundred characters was sized for a verdict line, and a
            // FAILED check's detail now carries its output tail — the part an operator reads to
            // learn WHY. A cap that re-destroys what the producer just started preserving would be
            // the same three-layer loss with a smaller knife.
            detail: TextUtil.Truncate(detail ?? "", 2400),
            taskId: taskId,
            revisionId: scope?.RevisionId,
            patchSetHash: scope?.PatchSetHash,
            treeHash: scope?.TreeHash);
    }
}
