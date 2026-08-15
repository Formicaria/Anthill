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

    /// <summary>True when this tool's outcome is worth recording as evidence at all.</summary>
    public static bool IsDeterministic(string? toolName) =>
        toolName is not null && DeterministicTools.ContainsKey(toolName);

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
        if (!DeterministicTools.TryGetValue(toolName ?? "", out var kind)) return null;
        if (string.IsNullOrWhiteSpace(missionId)) return null;

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
            detail: TextUtil.Truncate(detail ?? "", 500),
            taskId: taskId,
            revisionId: scope?.RevisionId,
            patchSetHash: scope?.PatchSetHash,
            treeHash: scope?.TreeHash);
    }
}
