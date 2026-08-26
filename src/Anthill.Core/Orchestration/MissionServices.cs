using Anthill.Core.Domain;
using Anthill.Core.Outcomes;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — the mission authority, as a contract.
///
/// <see cref="Queen"/> implements this and remains the only implementation. The interface exists
/// because ADR-001 required the Queen's role to be *stateable*: it says what the mission authority
/// is responsible for, which is now a short list rather than a 1,365-line class.
///
/// The ADR's explicit prohibition was against decomposition producing two components that both
/// believe they finalise a mission. This is how that stays checkable — there is one coordinator,
/// and the services it delegates to (<see cref="IPlanningService"/>, <see cref="IExecutionService"/>,
/// <see cref="IMissionEvaluator"/>, <see cref="ILearningRecorder"/>, <see cref="IResultAssembler"/>)
/// deliberately have no lifecycle authority of their own.
/// </summary>
public interface IMissionCoordinator
{
    /// <summary>Run a mission to completion and return the operator-facing result.
    /// v0.3.8.95 — <paramref name="projectId"/> carries the owning project from the conversation
    /// that started the mission, so the mission's workspace can be a worktree of the project's own
    /// repository. Null for missions started outside a project; behaviour is then unchanged.</summary>
    string RunMission(string goal, Action<string>? onMissionCreated, CancellationToken cancel = default,
        Action<Queen.MissionOutcome>? onMissionFinished = null, string? projectId = null);

    /// <summary>The plan a dispatch would run, without creating a mission.</summary>
    MissionPlan PlanPreview(string goal);

    /// <summary>The capability set this coordinator was composed from.</summary>
    Configuration.RuntimeProfile Profile { get; }
}

/// <summary>
/// v3.1.0 (ADR-001) — grading a finished mission, behind an interface.
///
/// The rule this protects is v2.26.0's and has not changed: a mission is evaluated EXACTLY ONCE,
/// after every task is terminal, and the result is persisted before completion is published. Six
/// call sites once re-derived mission success independently and could disagree.
///
/// The interface adds one property that the static could not have: the evaluation becomes an
/// injected dependency, so a test can substitute a stub without touching global state, and the
/// composition root can see that the colony has exactly one grader. The implementation delegates
/// to the canonical <see cref="MissionEvaluator"/> — it is not a second set of rules, and
/// <see cref="CanonicalMissionEvaluator"/> deliberately contains no logic of its own.
/// </summary>
public interface IMissionEvaluator
{
    MissionEvaluation Evaluate(Mission mission, MissionContext context, string? stopReason, int patchProposalCount,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence = null);
}

/// <summary>
/// The one grader. A pass-through to <see cref="MissionEvaluator.Evaluate"/> by design: if this
/// type ever grows a rule of its own, there are two authorities again.
/// </summary>
public sealed class CanonicalMissionEvaluator : IMissionEvaluator
{
    public MissionEvaluation Evaluate(Mission mission, MissionContext context, string? stopReason,
        int patchProposalCount, IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence = null) =>
        MissionEvaluator.Evaluate(mission, stopReason, patchProposalCount,
            context.Constraints, context.Profile.Verification.ObjectiveVerification, evidence);
}
