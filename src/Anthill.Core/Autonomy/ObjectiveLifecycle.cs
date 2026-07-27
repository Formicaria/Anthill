using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Autonomy;

/// <summary>Canonical reasons an objective's autonomous lifecycle ended (v1.8.16). Stored on the
/// objective's <c>end_reason</c> metadata so the console can explain why the Director stopped it.</summary>
public static class ObjectiveEndReason
{
    public const string CompletedSuccessfully = "completed_successfully";
    public const string StoppedNoFollowupRequired = "stopped_no_followup_required";
    public const string RetiredLooping = "retired_looping";
    public const string Failed = "failed";
    /// <summary>v2.22.0: the run budget ran out and NOT ONE run reached a verified success.
    /// Distinct from CompletedSuccessfully, which previously covered both — reporting exhaustion
    /// as completion turns a failed objective into an achievement in every summary that reads it.</summary>
    public const string ExhaustedWithoutSuccess = "exhausted_without_success";
    public const string ManuallyPaused = "manually_paused";
    public const string ManuallyStopped = "manually_stopped";

    /// <summary>Human label for a stored end-reason code.</summary>
    public static string Label(string? code) => code switch
    {
        CompletedSuccessfully => "Completed",
        StoppedNoFollowupRequired => "Stopped — no follow-up required",
        RetiredLooping => "Retired — looping",
        Failed => "Failed",
        ExhaustedWithoutSuccess => "Exhausted — never achieved",
        ManuallyPaused => "Paused by operator",
        ManuallyStopped => "Stopped by operator",
        _ => code ?? "",
    };
}

/// <summary>A decision to end an objective's autonomous lifecycle: the terminal status to move it
/// to, the canonical <see cref="ObjectiveEndReason"/> code, and a human-readable detail.</summary>
public sealed record ObjectiveEndDecision(ObjectiveStatus Status, string EndReason, string Detail);

/// <summary>
/// Phase 1 of the v1.8.16 roadmap — objective lifecycle hardening. Decides when a <em>successful</em>
/// objective should end cleanly (Completed / Stopped) instead of running until loop detection retires
/// it. This is the normal ending path for one-shot and verification-only objectives; loop detection
/// (<see cref="ObjectiveLearning.EvaluateRetirement"/>) is preserved strictly for true repeated loops.
///
/// The rules are conservative so broad standing objectives (max_runs 0 or &gt;1, no one-shot/verify
/// wording) keep running exactly as before:
/// <list type="bullet">
/// <item>An explicit one-shot objective (<c>max_runs == 1</c>, or one-shot wording in the charter)
///   that just succeeded ends as <c>completed_successfully</c>.</item>
/// <item>A verification-only / read-only / no-patch objective that succeeded and discovered no genuine
///   follow-up work ends as <c>stopped_no_followup_required</c> — there is nothing left to do.</item>
/// </list>
/// Returns null to keep the objective running (the caller then still consults loop/stale retirement).
/// </summary>
public static class ObjectiveLifecycle
{
    /// <summary>
    /// Evaluates clean completion after a run's outcome has been recorded. <paramref name="alreadyDone"/>
    /// is true when the run-budget rail (<c>max_runs</c>) already moved the objective to Done this run —
    /// in that case we only supply the end-reason label. Only successful runs can complete cleanly; a
    /// failed run falls through to the circuit-breaker / retirement rails.
    /// </summary>
    public static ObjectiveEndDecision? EvaluateCompletion(
        Objective o, bool success, int followUpsCreated, bool alreadyDone,
        ObjectiveProgress.Summary? progress = null)
    {
        // Respect the operator's opt-out. Disabled → behave exactly as pre-v1.8.16 (no clean completion;
        // objectives run until the run budget, circuit breaker, or loop detection stops them). The
        // run-budget end reason is still labelled even when disabled, since the status is already Done.
        if (!AnthillRuntime.AutonomyOneShotCompletion) return alreadyDone ? BudgetCompletion(o, success, progress) : null;

        // The run budget already moved it to Done — label the end reason by the final run's outcome.
        if (alreadyDone) return BudgetCompletion(o, success, progress);

        // Only a genuinely successful run with no newly-discovered work can end an objective cleanly.
        if (!success || followUpsCreated > 0) return null;

        var constraints = MissionConstraints.Parse(
            string.IsNullOrWhiteSpace(o.Charter) ? o.Title : $"{o.Title}. {o.Charter}");

        // Explicit one-shot: do the thing once, then stop. (max_runs==1 is handled by BudgetCompletion,
        // so this catches one-shot *wording* on an otherwise unbounded objective.)
        if (constraints.OneShot)
            return new ObjectiveEndDecision(ObjectiveStatus.Done, ObjectiveEndReason.CompletedSuccessfully,
                "One-shot objective completed successfully; nothing further to do.");

        // Verification / read-only / no-patch objective that succeeded and surfaced no follow-up work:
        // it has served its purpose, so stop cleanly rather than looping the same check forever.
        if (constraints.BlocksPatches)
            return new ObjectiveEndDecision(ObjectiveStatus.Done, ObjectiveEndReason.StoppedNoFollowupRequired,
                "Verification-only objective succeeded with no new work required; stopped cleanly.");

        return null;
    }

    /// <summary>
    /// v2.22.0: judged by the objective's EVIDENCE, not by whether the last run happened to
    /// succeed. An objective that succeeded on run 1 and failed on run 5 has still achieved its
    /// goal; one that failed all five has not, and must not be labelled Completed.
    ///
    /// <paramref name="progress"/> is null only for callers that have no run history to hand, in
    /// which case the final run's outcome is the best evidence available.
    /// </summary>
    private static ObjectiveEndDecision BudgetCompletion(Objective o, bool success,
        ObjectiveProgress.Summary? progress = null)
    {
        var achieved = progress?.Achieved ?? success;
        var reason = achieved ? ObjectiveEndReason.CompletedSuccessfully : ObjectiveEndReason.ExhaustedWithoutSuccess;
        var detail = progress is null
            ? (achieved ? "the final run succeeded" : "the final run did not succeed")
            : ObjectiveProgress.Explain(progress);
        return new(ObjectiveStatus.Done, reason,
            $"Reached its run budget ({o.RunCount}/{o.MaxRuns}): {detail}.");
    }
}
