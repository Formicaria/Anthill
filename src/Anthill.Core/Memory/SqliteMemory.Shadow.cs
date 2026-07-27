using Anthill.Core.Common;
using Anthill.Core.SafeAction;
using Anthill.Core.Shadow;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.24.0 Phase E prerequisite: shadow recommendations become durable.
///
/// The Shadow Operations line shipped across two releases — a non-executing recommendation engine
/// (v2.17.0) and a sixteen-scenario fault catalog with a simulation harness (v2.18.0) — with **no
/// table, no production call site, and no endpoint**. `ShadowOperator.Recommend` is invoked only by
/// its own tests and the simulator. It is the sixth instance of this codebase's signature defect,
/// and the largest: a complete, well-tested subsystem that nothing calls.
///
/// That made "live-incident wiring" unbuildable as written. Shadow mode's entire purpose is to
/// accumulate a track record — recommend, wait, compare against what the operator actually did,
/// and score the difference. A recommendation that vanishes when the process exits cannot be
/// compared to anything, so the qualification scoreboard could only ever be computed over replayed
/// scenarios. Wiring live observation without storage would have produced a system that appeared to
/// be qualifying itself while measuring nothing.
///
/// Recommendations and outcomes are stored separately, because they arrive at different times: the
/// recommendation when the incident is observed, the outcome when a human later says what really
/// happened. Joining them is what produces a scoreable pair.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// Record a shadow recommendation. Shadow mode never executes, so this is the whole of its
    /// output — the bundle is the artifact, and storing it is what makes qualification possible.
    /// </summary>
    public void SaveShadowRecommendation(ShadowRecommendation rec, DateTime? observedAt = null)
    {
        if (rec is null || string.IsNullOrWhiteSpace(rec.IncidentId)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO shadow_recommendations (incident_id, diagnosis, proposed_action, chosen_skill_id,
                      chosen_skill_confidence, risk_level, risk_score, risk_requires_approval,
                      risk_reasons_json, predicted_outcome, verification_plan_json,
                      rollback_plan, rollback_reason, would_recommend_execution, observed_at)
                  VALUES (@id, @diag, @action, @skill, @conf, @risk, @rscore, @rapprove, @rreasons,
                          @pred, @plan, @rb, @rbr, @would, @at)
                  ON CONFLICT(incident_id) DO UPDATE SET
                      diagnosis=@diag, proposed_action=@action, chosen_skill_id=@skill,
                      chosen_skill_confidence=@conf, risk_level=@risk, risk_score=@rscore,
                      risk_requires_approval=@rapprove, risk_reasons_json=@rreasons,
                      predicted_outcome=@pred,
                      verification_plan_json=@plan, rollback_plan=@rb, rollback_reason=@rbr,
                      would_recommend_execution=@would, observed_at=@at",
                ("@id", rec.IncidentId), ("@diag", rec.Diagnosis), ("@action", rec.ProposedAction),
                ("@skill", rec.ChosenSkillId), ("@conf", rec.ChosenSkillConfidence),
                ("@risk", rec.Risk?.Level ?? ""), ("@rscore", rec.Risk?.Score ?? 0),
                // Persisted, not derived: the policy-violation invariant is counted from this flag,
                // and a missing column would have made it read as permanently satisfied.
                ("@rapprove", (rec.Risk?.RequiresApproval ?? false) ? 1 : 0),
                ("@rreasons", Json.SafeDumps(rec.Risk?.Reasons ?? Array.Empty<string>())),
                ("@pred", rec.PredictedOutcome),
                ("@plan", Json.SafeDumps(rec.VerificationPlan)),
                ("@rb", rec.RollbackPlan.ToString()), ("@rbr", rec.RollbackReason),
                ("@would", rec.WouldRecommendExecution ? 1 : 0),
                ("@at", (observedAt ?? AnthillTime.NowUtc()).ToIso()));
        }
        InvalidateCache();
    }

    /// <summary>
    /// Record what actually happened, as judged by the operator. Deliberately separate from the
    /// recommendation: shadow mode's claim is only meaningful once a human has said what was true,
    /// and that judgment arrives later — often much later — than the recommendation.
    /// </summary>
    public void SaveShadowOutcome(ShadowOutcome outcome, DateTime? resolvedAt = null)
    {
        if (outcome is null || string.IsNullOrWhiteSpace(outcome.IncidentId)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO shadow_outcomes (incident_id, diagnosis_correct, action_was_needed, action_matched,
                      would_have_succeeded, operator_note, resolved_at)
                  VALUES (@id, @dc, @needed, @matched, @would, @note, @at)
                  ON CONFLICT(incident_id) DO UPDATE SET
                      diagnosis_correct=@dc, action_was_needed=@needed, action_matched=@matched,
                      would_have_succeeded=@would, operator_note=@note, resolved_at=@at",
                ("@id", outcome.IncidentId), ("@dc", outcome.DiagnosisCorrect ? 1 : 0),
                ("@needed", outcome.ActionWasNeeded ? 1 : 0), ("@matched", outcome.ActionMatched ? 1 : 0),
                ("@would", outcome.WouldHaveSucceeded ? 1 : 0), ("@note", outcome.OperatorNote),
                ("@at", (resolvedAt ?? AnthillTime.NowUtc()).ToIso()));
        }
        InvalidateCache();
    }

    /// <summary>
    /// Recommendation/outcome pairs that can actually be scored — an INNER join, because a
    /// recommendation with no operator judgment yet proves nothing and must not dilute the
    /// scoreboard in either direction.
    /// </summary>
    public List<Dictionary<string, object?>> LoadScoreablePairs(int limit = 500) =>
        Query(@"SELECT r.incident_id, r.diagnosis, r.proposed_action, r.chosen_skill_id,
                       r.chosen_skill_confidence, r.risk_level, r.risk_score,
                       r.risk_requires_approval, r.risk_reasons_json,
                       r.rollback_plan, r.rollback_reason, r.predicted_outcome,
                       r.verification_plan_json, r.would_recommend_execution, r.observed_at,
                       o.diagnosis_correct, o.action_was_needed, o.action_matched,
                       o.would_have_succeeded, o.operator_note, o.resolved_at
                FROM shadow_recommendations r
                JOIN shadow_outcomes o ON o.incident_id = r.incident_id
                ORDER BY o.resolved_at DESC LIMIT @lim",
            ("@lim", Math.Clamp(limit, 1, 2000)));

    /// <summary>
    /// Rehydrate stored rows into the typed pairs <see cref="QualificationScoreboard.Compute"/>
    /// expects. Without this the scoreboard had no production call site at all: it could only ever
    /// be handed pairs built in memory by the simulator or by its own tests, which is precisely the
    /// defect this phase exists to close. Malformed rows are skipped rather than defaulted, because
    /// a fabricated pair would move a qualification metric without any evidence behind it.
    /// </summary>
    public List<(ShadowRecommendation Rec, ShadowOutcome Outcome)> LoadScoreableRecommendations(int limit = 500)
    {
        var typed = new List<(ShadowRecommendation, ShadowOutcome)>();
        foreach (var row in LoadScoreablePairs(limit))
        {
            var id = row.GetValueOrDefault("incident_id")?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            string S(string c) => row.GetValueOrDefault(c)?.ToString() ?? "";
            bool B(string c) => AsLong(row.GetValueOrDefault(c)) != 0;
            double D(string c)
            {
                var v = row.GetValueOrDefault(c);
                return v is null || v is DBNull ? 0d
                     : double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0d;
            }

            var risk = new RiskAssessment(
                S("risk_level"), (int)AsLong(row.GetValueOrDefault("risk_score")),
                Json.TryParseStringList(S("risk_reasons_json")), B("risk_requires_approval"));

            typed.Add((
                new ShadowRecommendation(
                    IncidentId: id, Diagnosis: S("diagnosis"), ProposedAction: S("proposed_action"),
                    ChosenSkillId: S("chosen_skill_id"),
                    ChosenSkillConfidence: D("chosen_skill_confidence"),
                    Risk: risk, PredictedOutcome: S("predicted_outcome"),
                    VerificationPlan: Json.TryParseStringList(S("verification_plan_json")),
                    // An unparseable rollback plan escalates to a human rather than resolving to
                    // something that sounds recoverable. There is no "no-op" recovery action.
                    RollbackPlan: Enum.TryParse<RecoveryAction>(S("rollback_plan"), out var rb)
                        ? rb : RecoveryAction.Escalate,
                    RollbackReason: S("rollback_reason"),
                    WouldRecommendExecution: B("would_recommend_execution")),
                new ShadowOutcome(
                    IncidentId: id, DiagnosisCorrect: B("diagnosis_correct"),
                    ActionWasNeeded: B("action_was_needed"), ActionMatched: B("action_matched"),
                    WouldHaveSucceeded: B("would_have_succeeded"),
                    OperatorNote: S("operator_note"))));
        }
        return typed;
    }

    /// <summary>Recommendations still awaiting an operator judgment — the qualification backlog.</summary>
    public int CountUnresolvedShadowRecommendations() =>
        (int)AsLong(Scalar(@"SELECT COUNT(*) FROM shadow_recommendations r
                             LEFT JOIN shadow_outcomes o ON o.incident_id = r.incident_id
                             WHERE o.incident_id IS NULL"));

    /// <summary>
    /// Elapsed seconds between observing an incident and the operator resolving it, per pair.
    /// The raw material for the phase's timing metrics — stored as timestamps rather than computed
    /// durations so a late correction to either end recomputes correctly.
    /// </summary>
    public List<double> ShadowResolutionSeconds(int limit = 500)
    {
        var spans = new List<double>();
        foreach (var row in LoadScoreablePairs(limit))
        {
            var observed = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("observed_at")?.ToString());
            var resolved = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("resolved_at")?.ToString());
            if (observed is null || resolved is null) continue;
            var seconds = (resolved.Value - observed.Value).TotalSeconds;
            if (seconds >= 0) spans.Add(seconds);   // a negative span is a clock artefact, not a measurement
        }
        return spans;
    }
}
