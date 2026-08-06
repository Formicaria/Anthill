using Anthill.Core.Shadow;   // v3.8.7: ShadowObservation/ShadowRecommendation stayed in the core
using Anthill.Modules.Homelab;
using Anthill.Modules.Homelab.Incidents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Skills;

// v3.8.7 — moved out of Anthill.Core.Shadow.
//
// This is the one component the homelab extraction could not leave where it was. It reads
// IncidentRecord (now a module type) and writes to SqliteMemory and the skill registry (core
// types), so it is a BRIDGE — and a bridge cannot live on either bank. In the core it made the
// core depend on a module, which is the one rule this refactor has; in the module it would have
// needed the colony's memory, which no module may hold.
//
// The composition root is where both sides legitimately exist, and it is where the only caller
// already was: ApiHost.InitHomelab passes it Queen.Memory.
namespace Anthill.Api;

/// <summary>
/// v2.24.0 Phase E: shadow mode observes REAL incidents.
///
/// Two releases built a recommendation engine and a fault-simulation harness that only ever ran
/// against replayed scenarios, because nothing called them and nothing stored what they produced.
/// Storage arrived first (`SqliteMemory.Shadow`); this is the caller.
///
/// The safety property that makes this shippable at all: **shadow mode never executes.** It watches
/// an incident open, records what it *would* have done, and stops. The recommendation is compared
/// later against what the operator actually did, and that comparison is the qualification evidence
/// V3 requires. Nothing here touches the incident, the subject, or any action pathway.
///
/// It is also gated off by default. An observer that silently begins writing recommendations about
/// production incidents is not something that should arrive with an upgrade.
/// </summary>
public static class LiveIncidentObserver
{
    /// <summary>
    /// Observe an incident and record what shadow mode would recommend. Returns the recommendation
    /// when one was made, or null when observation is disabled or the incident is unusable.
    ///
    /// Never throws: an incident is a bad moment to add a second failure, and shadow mode is an
    /// observer — if it cannot form an opinion, the incident proceeds exactly as it would have.
    /// </summary>
    public static ShadowRecommendation? Observe(
        SqliteMemory? memory, SkillRegistry? skills, IncidentRecord? incident)
    {
        if (!AnthillRuntime.EnableShadowObservation) return null;
        if (memory is null || incident is null || string.IsNullOrWhiteSpace(incident.Id)) return null;

        try
        {
            var observation = ToObservation(incident);
            var recommendation = ShadowOperator.Recommend(observation, skills ?? new SkillRegistry());

            memory.SaveShadowRecommendation(recommendation, AnthillTime.ParseIsoOrNull(incident.OpenedAt));
            memory.LogEvent(AnthillRuntime.SystemApiMissionId, "shadow_recommendation_recorded",
                $"Shadow observed incident '{incident.Title}' and would {(recommendation.WouldRecommendExecution ? "recommend" : "NOT recommend")} "
                + $"{recommendation.ProposedAction} (predicted {recommendation.PredictedOutcome}).",
                antName: "shadow",
                metadata: new()
                {
                    ["incident_id"] = incident.Id,
                    ["proposed_action"] = recommendation.ProposedAction,
                    ["predicted_outcome"] = recommendation.PredictedOutcome,
                    ["risk"] = recommendation.Risk?.Level,
                    ["would_recommend_execution"] = recommendation.WouldRecommendExecution,
                    ["executed"] = false,   // stated explicitly: shadow mode never acts
                });
            return recommendation;
        }
        catch (Exception ex)
        {
            // Observation is best-effort by design. A shadow failure must never become an incident.
            memory.LogEvent(AnthillRuntime.SystemApiMissionId, "shadow_observation_failed",
                $"Shadow could not form a recommendation for incident {incident.Id}: {ex.Message}",
                antName: "shadow", metadata: new() { ["incident_id"] = incident.Id, ["error"] = ex.GetType().Name });
            return null;
        }
    }

    /// <summary>
    /// Record what actually happened once the operator resolves the incident. This is the half that
    /// turns a recommendation into evidence — without it the recommendation is an unfalsifiable
    /// claim, which is why <see cref="SqliteMemory.LoadScoreablePairs"/> excludes unjudged ones.
    ///
    /// The judgments are deliberately the operator's, not inferred: whether the diagnosis was right
    /// and whether the proposed action matched are exactly the things ANTHILL cannot mark its own
    /// homework on.
    /// </summary>
    public static void RecordOperatorJudgment(
        SqliteMemory? memory, string incidentId, bool diagnosisCorrect, bool actionWasNeeded,
        bool actionMatched, bool wouldHaveSucceeded, string note = "", DateTime? resolvedAt = null)
    {
        if (memory is null || string.IsNullOrWhiteSpace(incidentId)) return;

        memory.SaveShadowOutcome(
            new ShadowOutcome(incidentId, diagnosisCorrect, actionWasNeeded, actionMatched, wouldHaveSucceeded, note),
            resolvedAt);
        memory.LogEvent(AnthillRuntime.SystemApiMissionId, "shadow_outcome_recorded",
            $"Operator judgment recorded for incident {incidentId}: diagnosis {(diagnosisCorrect ? "correct" : "wrong")}, "
            + $"action {(actionMatched ? "matched" : "differed")}.",
            antName: "shadow",
            metadata: new()
            {
                ["incident_id"] = incidentId, ["diagnosis_correct"] = diagnosisCorrect,
                ["action_was_needed"] = actionWasNeeded, ["action_matched"] = actionMatched,
                ["would_have_succeeded"] = wouldHaveSucceeded,
            });
    }

    /// <summary>
    /// Map a homelab incident onto the observation the recommender expects.
    ///
    /// The proposed operation is derived from the incident's own subject kind rather than guessed
    /// from its prose: a service incident proposes a restart, a VM incident a power action, storage
    /// a restore. Reading intent out of free text would make the recommendation a function of how
    /// the title happened to be worded, and the qualification score would then measure the wording.
    /// </summary>
    internal static ShadowObservation ToObservation(IncidentRecord incident) => new(
        IncidentId: incident.Id,
        Diagnosis: string.IsNullOrWhiteSpace(incident.RootCause) ? incident.Title : incident.RootCause,
        ProposedOperation: OperationFor(incident.SubjectKind),
        TargetKind: incident.SubjectKind,
        TargetId: incident.SubjectId,
        Environment: AnthillRuntime.EnvironmentFingerprint);

    private static string OperationFor(string? subjectKind) => (subjectKind ?? "").ToLowerInvariant() switch
    {
        "service" => "restart_service",
        "vm" or "container" => "restart_guest",
        "storage" or "backup" => "restore",
        "host" => "investigate_host",
        _ => "investigate",   // unknown subject: the least invasive operation there is
    };
}
