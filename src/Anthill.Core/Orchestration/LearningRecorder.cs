using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Anthill.Core.Pheromones;
using Anthill.Core.Skills;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — what a finished mission teaches the colony.
///
/// Four things happen to a mission's result after it is graded: it is scored, its pheromone trails
/// are reinforced or weakened, the skills it followed are credited, and any observed route it
/// produced is registered as a candidate. All four were inline in <c>Queen.FinalizeMission</c>,
/// interleaved with result composition and event logging — so "what does this mission change about
/// future missions" had no single place to be read, reviewed, or turned off.
///
/// The safety rule the whole surface obeys, stated once: <b>only <c>completed_verified</c> is a
/// positive outcome</b>, and that fact is CONSUMED from the one canonical
/// <see cref="MissionEvaluation"/>, never re-derived here. A mission that merely finished, or
/// finished partially, records a non-verified outcome — which counts as a failure, because a
/// procedure that cannot be shown to have worked has not been shown to work.
///
/// The Queen still decides WHEN learning happens (after every task is terminal, after the one
/// evaluation exists, before completion is published). This owns only what is recorded.
/// </summary>
public interface ILearningRecorder
{
    /// <summary>
    /// Score the mission and record everything it teaches. Sets <see cref="Mission.SuccessScore"/>
    /// as a side effect — the score is part of the mission's persisted state, and the Queen saves
    /// the mission after this returns.
    /// </summary>
    void Record(Mission mission, MissionContext context, MissionEvaluation evaluation);
}

public sealed class LearningRecorder : ILearningRecorder
{
    private readonly SqliteMemory _memory;
    private readonly PheromoneEngine _pheromones;
    private readonly Func<SkillRegistry> _skills;

    public LearningRecorder(SqliteMemory memory, PheromoneEngine pheromones, Func<SkillRegistry> skills)
    {
        _memory = memory;
        _pheromones = pheromones;
        _skills = skills;
    }

    public void Record(Mission mission, MissionContext context, MissionEvaluation evaluation)
    {
        mission.SuccessScore = _pheromones.ScoreMission(mission);
        _memory.LogEvent(mission.Id, "pheromone_scored", $"Mission pheromone score calculated: {mission.SuccessScore}",
            metadata: new() { ["success_score"] = mission.SuccessScore, ["mission_status"] = mission.Status.Value() });
        _memory.UpdateMissionPheromones(mission, evaluation.OutcomeCode);
        CreditSkills(mission, context, evaluation);
        RegisterProceduralRoutes(mission, evaluation);
    }

    /// <summary>
    /// v2.22.0 Phase C2: credit the skills a mission actually followed, closing the learning loop.
    ///
    /// v2.21.0 made skills durable and let certified procedures INFORM a plan; nothing recorded
    /// whether following one worked, so standing could only ever be earned in the shadow simulator.
    /// Tasks now carry the skill they were planned from, so a finished mission can be credited back.
    ///
    /// Promotion and demotion both stay with <see cref="SkillRegistry.RecordOutcome"/>; this only
    /// reports what happened and persists the result.
    /// </summary>
    private void CreditSkills(Mission mission, MissionContext context, MissionEvaluation evaluation)
    {
        var followed = mission.Tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.SkillId))
            .Select(t => t.SkillId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (followed.Count == 0) return;

        // v2.26.0: verified-ness is CONSUMED from the one evaluation, never re-derived here.
        var verified = evaluation.IsPositive;

        // A promotable bundle is the ONLY thing RecordOutcome counts as a verified success, so an
        // unverified mission passes null rather than a bundle — it must not be able to promote.
        var bundle = verified ? MissionEvidenceBundle(mission) : null;
        var skills = _skills();

        foreach (var skillId in followed)
        {
            // v2.26.0: the bundle above is built from the ACTUAL verifier task and is honestly
            // semantic (Deterministic: false). Promotable now intrinsically requires deterministic
            // evidence, so this bundle records a NEUTRAL observation, never a promotion — the old
            // path here fabricated promotable evidence out of a model's own verdict. Deterministic
            // task-level evidence (build/test/diff) will flow in once patch verification bundles
            // are attached at this site; until then, no evidence means no credit.
            // v3.1.0: the environment a skill's coverage is proven against comes from the mission's
            // own context — the fingerprint recorded at intake, not one recomputed at finalization.
            var status = skills.RecordOutcome(skillId, bundle, context.EnvironmentFingerprint,
                verified ? null : $"mission {mission.Id} finished {mission.Status.Value()} without verified success");
            _memory.LogEvent(mission.Id, "skill_outcome_recorded",
                $"Skill '{skillId}' recorded {(verified ? "a verified success" : "an unverified outcome")} — now {status}.",
                metadata: new()
                {
                    ["skill_id"] = skillId, ["verified"] = verified, ["status"] = status.ToString(),
                    ["mission_status"] = mission.Status.Value(),
                    ["environment"] = context.EnvironmentFingerprint,
                    // Recorded, deliberately not enforced here: while break-glass is on, the
                    // installation is not V3-qualifiable and this credit is not qualifying evidence.
                    // v3.7.0 owns the enforcement point; v3.1.0 changes no behaviour.
                    ["qualifying"] = context.Profile.Verification.CanRecordVerifiedSuccess,
                });
        }
        // v2.26.0: persist ONLY the touched skills, row-atomically — a whole-registry save from
        // one mission's finalization was last-writer-wins against a concurrent mission's.
        foreach (var skillId in followed)
            if (skills.Get(skillId) is { } touched) _memory.SaveSkill(touched);
    }

    /// <summary>
    /// v2.26.0: procedural route registration lives at finalization, not on the per-task archivist
    /// path — where it resolved the mission outcome while status was still Running, always got a
    /// negative, and therefore never registered anything. (v2.23's feature was structurally dead in
    /// production; its tests passed because they called Register directly with a final outcome.)
    /// Candidates are rebuilt from the durable memory_candidate events the archivist path records.
    /// </summary>
    private void RegisterProceduralRoutes(Mission mission, MissionEvaluation evaluation)
    {
        var candidates = _memory.GetRecentEvents(200, eventType: MemoryCandidateIngest.EventType, missionId: mission.Id)
            .Select(row => Json.TryParseObject(row.GetValueOrDefault("metadata_json")?.ToString()))
            .Select(meta => new MemoryCandidateIngest.Candidate(
                MemoryClass: meta.GetValueOrDefault("memory_class")?.ToString() ?? "",
                Summary: meta.GetValueOrDefault("summary")?.ToString() ?? "",
                SourceMission: meta.GetValueOrDefault("source_mission")?.ToString() ?? "",
                Outcome: meta.GetValueOrDefault("outcome")?.ToString() ?? "",
                Confidence: meta.GetValueOrDefault("confidence")?.ToString() ?? "",
                AutoPromote: meta.GetValueOrDefault("auto_promote") is bool b && b))
            .Where(c => c.MemoryClass.Length > 0)
            .ToList();
        if (candidates.Count == 0) return;

        var skills = _skills();
        var routes = ProceduralCandidatePromotion.Register(skills, candidates, evaluation.OutcomeCode);
        if (routes.Count == 0) return;
        foreach (var routeId in routes)
            if (skills.Get(routeId) is { } registered) _memory.SaveSkill(registered);
        foreach (var id in routes)
            _memory.LogEvent(mission.Id, "skill_candidate_registered",
                $"Observed route registered as a skill candidate (usable for nothing until verified): {id}",
                antName: "archivist",
                metadata: new() { ["skill_id"] = id, ["mission_outcome"] = evaluation.OutcomeCode, ["status"] = "Candidate" });
    }

    /// <summary>
    /// The mission's own verification, expressed as a promotable bundle. Built from the verifier
    /// task that actually passed, so skill credit rests on the same evidence mission grading does
    /// rather than on a second, weaker opinion.
    /// </summary>
    private static Verification.VerificationBundle MissionEvidenceBundle(Mission mission)
    {
        var verifier = mission.Tasks.First(t => MissionVerification.IsVerificationTask(t)
                                                && t.Status == TaskStatus.Complete);
        return new Verification.VerificationBundle
        {
            Id = $"mission:{mission.Id}",
            TaskType = "mission_verification",
            Required = { "mission_verifier" },
            Results =
            {
                new Verification.VerificationResult("mission_verifier", Passed: true, Deterministic: false,
                    TextUtil.Truncate(verifier.ResultSummary ?? verifier.Result ?? "verified", 300),
                    new[] { new Verification.VerificationEvidence("task_id", verifier.Id) }),
            },
        };
    }
}
