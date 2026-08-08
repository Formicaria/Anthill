using Anthill.SDK.Artifacts;

namespace Anthill.Core.Domain;

/// <summary>
/// The typed artifacts a role's predecessors produced, compiled into bounded context. v3.8.29.
///
/// Stage C, and the gap that has headed the plan's "known gaps" table since it was written: roles
/// pass PROSE. <c>Task.Result</c> is a <c>string?</c>, so the context packet a worker receives is
/// built from other workers' narrative summaries — and everything downstream that learns, verifies
/// or plans has been reading that.
///
/// The artifact store has held typed output since v3.8.20: <c>file_set</c> with the paths actually
/// read, <c>source_set</c> with the sources actually consulted, <c>ui_map</c>, <c>patch_set</c> with
/// real content, <c>workspace_snapshot</c>, <c>verification_bundle</c>. All of it queryable, none of
/// it reaching the roles that would use it.
///
/// This is ADDITIVE and that is deliberate. The prose stays — it is what a model reads best, and
/// ripping it out would trade a working channel for an unproven one in a single step. What changes
/// is that the typed record travels ALONGSIDE it, carrying artifact IDs, so a worker's inputs become
/// something a replay can reconstruct rather than something reassembled from summaries.
/// </summary>
public static class ArtifactContext
{
    /// <summary>
    /// Schemas worth putting in front of a worker, in the order they are most likely to matter.
    ///
    /// Deliberately a LIST rather than "everything in the store". A context packet is a budget, and
    /// spending it on every artifact a long mission accumulated would push out the ones that decide
    /// the work. The order is the priority when the budget runs short.
    /// </summary>
    private static readonly string[] Priority =
    {
        ArtifactSchemas.PatchSet,
        ArtifactSchemas.UiMap,
        ArtifactSchemas.FileSet,
        ArtifactSchemas.SourceSet,
        ArtifactSchemas.VerificationBundle,
        ArtifactSchemas.WorkspaceSnapshot,
        ArtifactSchemas.FailureDiagnosis,
        ArtifactSchemas.RepairRecommendation,
        ArtifactSchemas.TestReport,
        ArtifactSchemas.SecurityReview,
    };

    /// <summary>
    /// Compile the mission's typed artifacts into a bounded block, newest-relevant first.
    /// </summary>
    /// <param name="store">Null returns empty — every caller without a store keeps its previous
    /// behaviour exactly, which is what lets this land without touching the CLI or the tests.</param>
    /// <param name="maxTotalChars">The whole block's budget. Excerpts are trimmed to fit; the block
    /// never silently exceeds what the caller allowed.</param>
    /// <param name="maxItemChars">Per-artifact excerpt cap. A single large patch set must not be
    /// able to consume the entire budget and hide everything else.</param>
    public static string Compile(IArtifactStore? store, string missionId,
        int maxTotalChars, int maxItemChars = 1200)
    {
        if (store is null || maxTotalChars <= 0 || string.IsNullOrWhiteSpace(missionId)) return "";

        List<Artifact> artifacts;
        try { artifacts = store.ForMission(missionId).ToList(); }
        catch (Exception error)
        {
            // A context compiler that throws would take down every dispatch that uses it. The
            // mission proceeds on prose, which is what it did before this existed.
            Console.Error.WriteLine($"[artifact-context] unavailable for {missionId}: {error.Message}");
            return "";
        }

        if (artifacts.Count == 0) return "";

        var ordered = artifacts
            .Where(a => Array.IndexOf(Priority, a.Schema) >= 0)
            .OrderBy(a => Array.IndexOf(Priority, a.Schema))
            .ThenByDescending(a => a.CreatedAt)
            .ToList();

        if (ordered.Count == 0) return "";

        var lines = new List<string> { "TYPED ARTIFACTS (structured record; the prose above is the narrative)" };
        var used = lines[0].Length;

        foreach (var artifact in ordered)
        {
            // The ID is the load-bearing field, not the excerpt. It is what makes "a replay can
            // reconstruct every worker's inputs from artifact IDs" answerable — the excerpt is a
            // convenience for the model, the id is the provenance.
            var header = $"\n- id: {artifact.Id}  schema: {artifact.Schema}  producer: {artifact.ProducerRole}";
            // TextUtil moved to Anthill.SDK.Common at v3.8.14 and arrives through the global using —
            // the first draft here qualified it as `Common.TextUtil`, which resolves against
            // Anthill.Core.Common and does not exist. Bare, like every other call site.
            var excerpt = TextUtil.Truncate(artifact.Payload, maxItemChars, "...[artifact truncated]");
            var block = $"{header}\n  {excerpt.Replace("\n", "\n  ")}";

            if (used + block.Length > maxTotalChars)
            {
                // Say that something was left out. A silently truncated context is one where a
                // worker cannot tell the difference between "there was no patch set" and "the patch
                // set did not fit", and those lead to different work.
                lines.Add($"\n- [{ordered.Count - (lines.Count - 1)} further artifact(s) omitted for space]");
                break;
            }

            lines.Add(block);
            used += block.Length;
        }

        return string.Join("", lines);
    }
}
