using Anthill.Core.Common;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;

namespace Anthill.Core.Orchestration;

/// <summary>
/// Finalization happens once per mission evaluation, however many times it is invoked. v0.3.8.41.
///
/// THE FAILURE THIS PREVENTS. Pheromone strength, skill observation counts and reputation are all
/// CUMULATIVE. Running learning twice for one mission does not produce a slightly wrong answer that
/// decays — it produces a permanently wrong one, and there is no record afterwards distinguishing
/// "this route succeeded twice" from "this route succeeded once and was counted twice". The same is
/// true of the archivist: a second run writes a second set of memory candidates for the same
/// mission, and the promotion threshold that is supposed to require repeat evidence across
/// DIFFERENT missions is satisfied by one mission finalised twice.
///
/// Restart and recovery are the reason this is not hypothetical. A process that dies between
/// `SaveMissionEvaluation` and `_learning.Record` leaves a mission that is evaluated and unlearned,
/// and any recovery worth having will finalise it again.
///
/// THE LEDGER IS THE EVENT LOG, not a field and not an in-memory set. The process that ran the
/// mission is precisely the process a restart no longer has, so the claim has to be durable; and the
/// event log is already durable, already per-mission, already queried this way by the adaptive
/// budget, and needs no schema change. The key includes the EVALUATION, not only the mission: a
/// re-evaluation that legitimately produces a different canonical outcome is a different fact and
/// deserves to be learned, while replaying the same one is not.
///
/// This is a claim, not a lock. Two threads finalising one mission at the same instant is not a
/// state the runtime can reach — a mission is driven by one `RunMission` call — so the cost of a
/// true mutex is not warranted, and the honest name for what this does is "claim".
/// </summary>
public static class MissionFinalizationLedger
{
    public const string LearningEventType = "mission_learning_recorded";
    public const string ArchivistEventType = "mission_archivist_recorded";

    /// <summary>
    /// Claim the right to run learning for this mission evaluation. False means it already ran.
    /// </summary>
    public static bool TryClaimLearning(SqliteMemory memory, string missionId, MissionEvaluation evaluation) =>
        TryClaim(memory, LearningEventType, missionId, evaluation,
            "Learning, pheromones, skill credit and route registration recorded for this evaluation.");

    /// <summary>
    /// Claim the right to run the archivist for this mission evaluation. False means it already ran.
    /// </summary>
    public static bool TryClaimArchivist(SqliteMemory memory, string missionId, MissionEvaluation evaluation) =>
        TryClaim(memory, ArchivistEventType, missionId, evaluation,
            "Archivist memory extraction recorded for this evaluation.");

    /// <summary>
    /// Whether a step has already been recorded. Exposed so a recovery path can REPORT what it is
    /// about to skip rather than skipping silently — an operator investigating a mission that looks
    /// unlearned needs to be able to tell "it was skipped" from "it never happened".
    /// </summary>
    public static bool AlreadyRecorded(SqliteMemory memory, string eventType, string missionId,
        MissionEvaluation evaluation) =>
        Existing(memory, eventType, missionId).Contains(Fingerprint(evaluation));

    private static bool TryClaim(SqliteMemory memory, string eventType, string missionId,
        MissionEvaluation evaluation, string description)
    {
        if (memory is null || string.IsNullOrWhiteSpace(missionId) || evaluation is null) return false;

        var fingerprint = Fingerprint(evaluation);
        if (Existing(memory, eventType, missionId).Contains(fingerprint))
        {
            // SAID OUT LOUD. A silent skip is indistinguishable from a step that was never wired,
            // which is the defect class this repository keeps finding; a recorded skip is evidence.
            memory.LogEvent(missionId, $"{eventType}_skipped",
                $"Already recorded for evaluation {fingerprint} — not repeating (finalization is idempotent).",
                metadata: new() { ["evaluation_fingerprint"] = fingerprint, ["step"] = eventType });
            return false;
        }

        memory.LogEvent(missionId, eventType, description,
            metadata: new()
            {
                ["evaluation_fingerprint"] = fingerprint,
                ["outcome_code"] = evaluation.OutcomeCode,
                ["evaluator_version"] = evaluation.EvaluatorVersion,
            });
        return true;
    }

    /// <summary>
    /// What makes two finalizations "the same one".
    ///
    /// The canonical outcome plus the evaluator version. Deliberately NOT the evaluated-at instant:
    /// a replay of an unchanged mission produces a new timestamp and the same verdict, and keying on
    /// time would make every replay look like new learning — which is the behaviour this exists to
    /// stop.
    /// </summary>
    private static string Fingerprint(MissionEvaluation evaluation) =>
        $"{evaluation.EvaluatorVersion}:{evaluation.OutcomeCode}:{evaluation.VerificationStatus}:{evaluation.DeliverableStatus}";

    private static HashSet<string> Existing(SqliteMemory memory, string eventType, string missionId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in memory.GetRecentEvents(50, eventType, missionId))
        {
            var meta = Json.TryParseObject(row.GetValueOrDefault("metadata_json")?.ToString());
            if (meta.GetValueOrDefault("evaluation_fingerprint")?.ToString() is { Length: > 0 } fingerprint)
                seen.Add(fingerprint);
        }
        return seen;
    }
}
