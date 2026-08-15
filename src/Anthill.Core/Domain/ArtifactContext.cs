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
        // v0.3.8.57 — its own schema now, so a consumer asking for patch sets no longer receives
        // documentation proposals. Ranked last: it is context, not the change under review.
        ArtifactSchemas.DocsPatchSet,
        // v0.3.8.57 — the researcher's brief, now typed. Ranked below the change artifacts and
        // above nothing: it is orientation, which matters most when the concrete artifacts are absent.
        ArtifactSchemas.ResearchBrief,
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
    /// <param name="declaredInputIds">
    /// The artifacts this task was authoritatively given. v0.3.8.57.
    ///
    /// When non-empty, the block is EXACTLY these — in the order declared, with no schema-priority
    /// filter and no other artifact admitted. That is the whole point: a task with declared inputs
    /// receives what it was created to consume rather than everything the mission happens to hold.
    /// A tester inserted because a patch set exists should see that patch set, not the `ui_map` a
    /// cartographer produced for a different step.
    ///
    /// When empty, behaviour is unchanged — the mission-wide priority-ordered block. Most tasks have
    /// no unambiguous producer to name, and narrowing those by guesswork would remove context a
    /// worker legitimately used, which is a worse failure than sending too much.
    ///
    /// A declared id that is not in the store is REPORTED rather than skipped: a worker told it was
    /// given an input it never received should be able to tell that apart from having been given
    /// nothing.
    /// </param>
    /// <param name="consumerRole">
    /// Who is reading. v0.3.8.57 — when supplied, every artifact that actually reaches the worker
    /// is recorded in the consumption ledger.
    ///
    /// HERE, and not at the call sites, because this is the only place that knows what was
    /// ACTUALLY delivered. A caller can see what it asked for; the budget decides what arrives, and
    /// an artifact omitted for space was not consumed. Recording at the call site would produce a
    /// ledger of intentions, which reads exactly like a ledger of facts and is not one.
    ///
    /// Null means do not record — the CLI, tests, and any caller with no role to name keep their
    /// previous behaviour, including no writes on a read path.
    /// </param>
    /// <param name="consumerTaskId">The task the read was on behalf of, when there is one.</param>
    public static string Compile(IArtifactStore? store, string missionId,
        int maxTotalChars, int maxItemChars = 1200,
        IReadOnlyList<string>? declaredInputIds = null,
        string? consumerRole = null, string? consumerTaskId = null)
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

        var declared = declaredInputIds is { Count: > 0 };

        // The empty-store shortcut applies only when NOTHING WAS DECLARED. v0.3.8.57 — this check
        // used to sit above the declared-inputs handling and returned "" first, which silently
        // cancelled the missing-input report in the one case where it matters most: a task told it
        // was given three artifacts, in a mission whose store holds none, learned nothing at all and
        // could not distinguish that from having been given nothing. The report exists precisely
        // because those two lead to different work.
        //
        // Found by a consumption-ledger test whose block came back empty. The declared-input tests
        // all happened to seed at least one artifact, so every one of them passed over this.
        if (artifacts.Count == 0 && !declared) return "";
        var missing = new List<string>();
        List<Artifact> ordered;

        if (declared)
        {
            // EXACTLY the declared inputs, in the order declared. No schema filter: a task told to
            // consume an artifact consumes it, whether or not its schema is on the priority list —
            // the list exists to RANK a mission-wide block, and applying it here would silently drop
            // an input the runtime deliberately supplied.
            var byId = artifacts.ToDictionary(a => a.Id, StringComparer.Ordinal);
            ordered = new List<Artifact>();
            foreach (var id in declaredInputIds!)
            {
                if (byId.TryGetValue(id, out var found)) ordered.Add(found);
                else missing.Add(id);
            }
        }
        else
        {
            ordered = artifacts
                .Where(a => Array.IndexOf(Priority, a.Schema) >= 0)
                .OrderBy(a => Array.IndexOf(Priority, a.Schema))
                .ThenByDescending(a => a.CreatedAt)
                .ToList();
        }

        if (ordered.Count == 0 && missing.Count == 0) return "";

        var lines = new List<string>
        {
            declared
                ? "DECLARED INPUTS (the artifacts this task was given; the prose above is the narrative)"
                : "TYPED ARTIFACTS (structured record; the prose above is the narrative)",
        };

        // Said before the artifacts, not after: a worker that stops reading early should still learn
        // that something it was promised is absent. "I was given nothing" and "I was given three
        // things and one could not be found" lead to different work.
        if (missing.Count > 0)
            lines.Add($"\n- [{missing.Count} declared input(s) NOT FOUND in the store: {string.Join(", ", missing)}]");
        // Sum, not lines[0]: the missing-inputs notice above is part of the block and must be
        // charged to the budget, or a long list of absent ids could push the block past its cap.
        var used = lines.Sum(l => l.Length);
        // Counted explicitly rather than derived from lines.Count: the header and the missing-inputs
        // notice are both lines, so `lines.Count - 1` would under-report how many artifacts were
        // dropped whenever an input was absent — a wrong number is worse than no number.
        var emitted = 0;

        foreach (var artifact in ordered)
        {
            // The ID is the load-bearing field, not the excerpt. It is what makes "a replay can
            // reconstruct every worker's inputs from artifact IDs" answerable — the excerpt is a
            // convenience for the model, the id is the provenance.
            var header = $"\n- id: {artifact.Id}  schema: {artifact.Schema}  producer: {artifact.ProducerRole}";

            // v0.3.8.57 — the READ boundary. A schema label is a promise to the consumer about
            // what it is about to read, and until this release nothing checked the promise at
            // either end. A worker handed a malformed payload under a type name does not fail —
            // it CONSUMES it, and produces confident work on a shape that was never there.
            //
            // Said inline against the artifact rather than collected at the top: a note that this
            // block contains a bad payload somewhere is not usable; a note attached to the one
            // that is bad is.
            var conformance = Anthill.SDK.Artifacts.ArtifactSchemaCheck.Validate(artifact.Schema, artifact.Payload);
            if (!conformance.Conforms)
                header += $"\n  [WARNING: this payload does not match its schema — {conformance.Reason}. Treat its contents as unstructured text.]";
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
                lines.Add($"\n- [{ordered.Count - emitted} further artifact(s) omitted for space]");
                break;
            }

            lines.Add(block);
            used += block.Length;
            emitted++;
            RecordConsumption(store, artifact, consumerRole, consumerTaskId);
        }

        return string.Join("", lines);
    }

    /// <summary>
    /// Note that this artifact reached this role. v0.3.8.57.
    ///
    /// NEVER THROWS. A ledger entry is a diagnostic, and a diagnostic that can fail the operation
    /// it describes is worse than none — this release already turned "the payload is the wrong
    /// shape" into "the artifact was never stored" once, by letting a log write take down a Put.
    /// A failed record leaves a gap in the ledger; a thrown record leaves a worker with no context.
    /// </summary>
    private static void RecordConsumption(IArtifactStore store, Artifact artifact,
        string? consumerRole, string? consumerTaskId)
    {
        if (string.IsNullOrWhiteSpace(consumerRole)) return;
        try
        {
            store.RecordConsumption(new ArtifactConsumption
            {
                ArtifactId = artifact.Id,
                // The hash AS READ. If the artifact is later found to hash differently, this row is
                // what makes that detectable — see ArtifactConsumption.StillMatches.
                ContentHash = artifact.ContentHash,
                Schema = artifact.Schema,
                MissionId = artifact.MissionId,
                ConsumerRole = consumerRole!,
                ConsumerTaskId = consumerTaskId,
            });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[artifact-context] could not record {consumerRole} reading {artifact.Id}: {error.Message}");
        }
    }
}
