using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.SDK.Knowledge;

namespace Anthill.Api.Knowledge;

/// <summary>
/// RUN THE COLONY OVER A KNOWLEDGE BASE, ONCE PER DOCUMENT VERSION. v0.3.8.154.
///
/// The operator's own sentence, and it is the specification: "I should be able to click on one
/// knowledge base and have it then run through the anthill automated missions to build its memory
/// and pheromones."
///
/// WHAT THIS ACTUALLY BUILDS, stated plainly because the word "memory" invites a bigger claim than
/// the runtime makes. A seeded mission is an ordinary mission. When it finishes, the archivist
/// records ONE episodic `memory_candidate` event. If it is graded `completed_verified` it also
/// records a `procedural_candidate` and reinforces three pheromone trails — the planner, worker and
/// task-type routes it took. If it fails, it records a negative candidate and the route is weakened.
///
/// A candidate is a RECORD, not a promoted memory: `MemoryCandidateIngest` stores rows and
/// deliberately does not certify or promote them, and `auto_promote` is written false and read by
/// nobody. And a pheromone trail is a ROUTE, not a fact — seeding teaches the colony which pipeline
/// answers this shape of question about this knowledge base, not what the knowledge says. Both are
/// worth having and neither is "the colony has learned your documents", so the console says the same
/// thing in the same words.
///
/// ONE PASS, TWO CALLERS. `SeedOnce` is public and returns a typed result rather than logging and
/// returning void, because the operator's button and a future timer must be the same code —
/// `UpdateStager.StageIfAvailable` made that argument first and it holds here for the same reason: a
/// scheduled path that drifts from the manual one is two implementations of one rule.
///
/// IT ENUMERATES DOCUMENTS, NOT FACTS, and that is forced rather than chosen. `IKnowledgeProvider`
/// has no enumerate-all-knowledge call — every retrieval surface is query-driven, and the only
/// listings that exist are sources and ingestion jobs. A document is also the right unit: it is what
/// FORAGER assigns a durable id and a content hash to, so it is the only thing a watermark can
/// honestly key on today. When P8 lands a revision ledger, the key gains a real `revision_id` and
/// nothing else about this changes.
/// </summary>
public static class KnowledgeSeeder
{
    /// <summary>
    /// The action type in the stable key. A second kind of seeding (a per-entity sweep, say) would
    /// be a different action over the same documents, and must not collide with this one.
    /// </summary>
    public const string ActionType = "study_source";

    /// <summary>
    /// The automation-policy version §7 requires in the key. BUMP IT when the goal text or the unit
    /// of work changes in a way that should re-run over knowledge already seeded — that is the only
    /// honest way to say "seed it again", and it keeps the record of what the previous pass did
    /// instead of deleting it.
    /// </summary>
    public const string PolicyVersion = "v1";

    /// <summary>How many documents one pass will seed. A base with a thousand documents must not
    /// become a thousand queued missions because somebody clicked a button.</summary>
    public const int MaxPerPass = 25;

    public sealed record SeedResult(
        bool Ok, string Message, int Submitted, int AlreadySeeded, int Available, IReadOnlyList<string> JobIds)
    {
        public static SeedResult No(string message) =>
            new(false, message, 0, 0, 0, Array.Empty<string>());
    }

    /// <summary>
    /// One pass over one knowledge base. Never throws: a seeding pass that can fault the caller is a
    /// button that can take the console down.
    /// </summary>
    public static async Task<SeedResult> SeedOnce(
        SqliteMemory memory, ApiJobRegistry jobs, KnowledgeScope scope,
        string? anthillProjectId, CancellationToken cancel)
    {
        if (!scope.IsQueryable) return SeedResult.No("No knowledge base is mapped for this project.");

        var ingestion = ApiHost.KnowledgeHost.Ingestion;
        if (ingestion is null) return SeedResult.No("Knowledge is not configured.");

        try
        {
            // WHO WE ARE READING, ASKED BEFORE ANYTHING IS RECORDED. The instance and generation are
            // two of the five parts of every key this pass writes; without them a receipt cannot say
            // which FORAGER, or which history of it, the document came from — and a restored store
            // would read as "already seeded" for documents that no longer exist.
            var availability = await ApiHost.KnowledgeHost.Provider.ProbeAsync(cancel).ConfigureAwait(false);
            if (!availability.Usable)
                return SeedResult.No($"The knowledge base is not usable: {availability.Reason ?? "no reason given"}.");

            var instance = availability.InstanceId ?? "";
            var generation = availability.InstanceGeneration ?? "";

            var sources = await ingestion.ListSourcesAsync(scope, cancel).ConfigureAwait(false);
            if (!sources.Ok || sources.Value is null)
                return SeedResult.No($"The knowledge base did not list its sources: {sources.Reason ?? "no reason given"}.");

            var submitted = new List<string>();
            var already = 0;
            var considered = 0;

            foreach (var source in sources.Value)
            {
                if (cancel.IsCancellationRequested) break;

                // A SUPERSEDED OR DUPLICATE DOCUMENT IS NOT SEEDED. FORAGER already decided both —
                // ANTHILL classifies nothing about knowledge and must not start here by studying a
                // document its own producer has marked as replaced.
                if (!string.IsNullOrWhiteSpace(source.SupersededBy) || !string.IsNullOrWhiteSpace(source.DuplicateOf))
                    continue;

                considered++;
                if (submitted.Count >= MaxPerPass) continue;

                var key = SqliteMemory.SeedActionKey(
                    instance, generation, scope.ProjectRef ?? "", source.SourceId,
                    source.ContentHash, ActionType, PolicyVersion);

                var receipt = new SqliteMemory.KnowledgeSeedReceipt
                {
                    EventId = Guid.NewGuid().ToString(),
                    ActionKey = key,
                    ProjectRef = scope.ProjectRef ?? "",
                    AnthillProjectId = anthillProjectId,
                    SourceId = source.SourceId,
                    SourceName = source.Name,
                    LogicalContentHash = source.ContentHash,
                    ProducerInstanceId = instance,
                    ProducerGeneration = generation,
                    PublicationStatus = source.ProcessingStatus,
                };

                // RECEIPT FIRST, QUEUE SECOND — §7's ordering, and the reason it is that way round is
                // the crash between them: a `received` row with no job is recoverable by the next
                // pass, while a queued mission with no row is a duplicate nothing can detect.
                if (!memory.TryRecordSeedIntent(receipt)) { already++; continue; }

                var job = jobs.Submit(GoalFor(source), idempotencyKey: key, projectId: anthillProjectId);
                memory.MarkSeedSubmitted(key, job.Id);
                submitted.Add(job.Id);
            }

            var message = submitted.Count == 0
                ? already > 0
                    ? $"Nothing new to study — all {already} document(s) in this knowledge base have already been seeded."
                    : "This knowledge base has no documents to study yet."
                : $"Queued {submitted.Count} mission(s) over this knowledge base."
                  + (already > 0 ? $" {already} document(s) were already seeded." : "")
                  + (considered > submitted.Count + already
                        ? $" {considered - submitted.Count - already} more will follow on the next pass."
                        : "");

            return new SeedResult(true, message, submitted.Count, already, considered, submitted);
        }
        catch (OperationCanceledException)
        {
            return SeedResult.No("The seeding pass was cancelled.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[knowledge-seed] pass failed: {error}");
            return SeedResult.No($"The seeding pass failed: {error.Message}");
        }
    }

    /// <summary>
    /// The mission's goal, and it is deliberately a QUESTION rather than an instruction to write
    /// something.
    ///
    /// A question resolves through intake to a class whose authority ceiling is `Observe`, so a
    /// seeding pass cannot patch, write or shell whatever a planner decides — which matters more
    /// here than anywhere, because this is the one lane the operator starts in bulk with one click.
    /// Naming the document rather than pasting its content is the same discipline: the researcher
    /// holds the knowledge tools and the mission is already inside the knowledge scope, so it
    /// retrieves what it needs and the goal stays a sentence.
    /// </summary>
    public static string GoalFor(KnowledgeSource source) =>
        $"What does the knowledge base establish about \"{Trim(source.Name)}\"? "
      + "Summarise the facts it records, the support level behind each, and anything it leaves "
      + "contested or unresolved.";

    private static string Trim(string? value)
    {
        var text = (value ?? "").Replace('"', '\'').Trim();
        if (text.Length == 0) return "this document";
        return text.Length <= 120 ? text : text[..120];
    }
}
