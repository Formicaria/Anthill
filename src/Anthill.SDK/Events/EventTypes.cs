namespace Anthill.SDK.Events;

/// <summary>
/// Every event type the colony emits today, as constants.
///
/// This list was not designed — it was READ, out of the working tree, from the ~85
/// <c>LogEvent</c> call sites across Core. That provenance is the point: these are the events the
/// system actually produces, so a subscriber written against this file is written against reality
/// rather than against an intention.
///
/// Until now publishers and readers have traded raw string literals, which means a typo in
/// <c>ApiHost</c> or <c>app.js</c> produces a filter that silently matches nothing — the failure
/// mode being an empty panel with no error anywhere. Naming them once ends that class of bug.
///
/// When adding an event: add the constant here in the same change as the publisher, never after.
/// </summary>
public static class EventTypes
{
    // ---- mission lifecycle -------------------------------------------------

    public const string MissionCreated = "mission_created";
    public const string MissionStarted = "mission_started";
    public const string MissionClassified = "mission_classified";
    public const string MissionContextResolved = "mission_context_resolved";
    public const string MissionEvaluated = "mission_evaluated";
    public const string MissionOutcome = "mission_outcome";
    public const string ObjectiveVerificationFailed = "objective_verification_failed";

    // ---- task lifecycle ----------------------------------------------------

    public const string TaskCreated = "task_created";
    public const string TaskReady = "task_ready";
    public const string TaskStarted = "task_started";
    public const string TaskCompleted = "task_completed";
    public const string TaskCompletedWithWarnings = "task_completed_with_warnings";
    public const string TaskFailed = "task_failed";
    public const string TaskFailedTimeout = "task_failed_timeout";
    public const string TaskBlocked = "task_blocked";
    public const string TaskDrained = "task_drained";
    public const string TaskExecutionRecorded = "task_execution_recorded";
    public const string TaskOutcomeApplied = "task_outcome_applied";
    public const string TaskResultSummarized = "task_result_summarized";
    public const string TaskGraphValidationIssue = "task_graph_validation_issue";

    /// <summary>A result arrived for a task the scheduler had already closed out.</summary>
    public const string TaskLateResultIgnored = "task_late_result_ignored";
    public const string TaskLateErrorIgnored = "task_late_error_ignored";

    // ---- worker / attempt --------------------------------------------------

    public const string AttemptClaimRefused = "attempt_claim_refused";
    public const string AttemptCloseFailed = "attempt_close_failed";
    public const string HandoffAdmitted = "handoff_admitted";
    public const string HandoffRejected = "handoff_rejected";
    public const string WorkerPermissionAudited = "worker_permission_audited";
    public const string WorkerRuntimeDenied = "worker_runtime_denied";

    // ---- tools -------------------------------------------------------------

    public const string ToolCalled = "tool_called";
    public const string ToolDenied = "tool_denied";
    public const string ToolDefinitionUnreadable = "tool_definition_unreadable";
    public const string WebSearchAttempted = "web_search_attempted";
    public const string WebSearchBudgetExhausted = "web_search_budget_exhausted";

    // ---- reasoning / models ------------------------------------------------

    public const string ModelCall = "model_call";
    public const string AnswerSynthesisFailed = "answer_synthesis_failed";
    public const string BestOutputSelected = "best_output_selected";

    // ---- approvals and escalation ------------------------------------------

    public const string ApprovalRequestCreated = "approval_request_created";
    public const string ApprovalRequestDeduped = "approval_request_deduped";
    public const string AdaptiveEscalated = "adaptive_escalated";
    public const string EscalationRefused = "escalation_refused";

    // ---- patches and auto-apply --------------------------------------------

    public const string PatchSetCreated = "patch_set_created";
    public const string PatchSetEmpty = "patch_set_empty";
    public const string PatchProposalCreated = "patch_proposal_created";
    public const string PatchProposalParseFailed = "patch_proposal_parse_failed";
    public const string PatchAlternativeCreated = "patch_alternative_created";
    public const string PatchReverted = "patch_reverted";
    public const string PatchRevertFailed = "patch_revert_failed";
    public const string AutonomyAutoApplyApplied = "autonomy_autoapply_applied";
    public const string AutonomyAutoApplyRolledBack = "autonomy_autoapply_rolled_back";
    public const string AutonomyAutoApplyRollbackFailed = "autonomy_autoapply_rollback_failed";

    // ---- learning and memory -----------------------------------------------

    public const string PheromoneScored = "pheromone_scored";
    public const string SkillCandidateRegistered = "skill_candidate_registered";
    public const string SkillOutcomeRecorded = "skill_outcome_recorded";
    public const string LearningReset = "learning_reset";

    // ---- workspaces --------------------------------------------------------

    public const string WorkspaceReady = "workspace_ready";
    public const string WorkspaceUnavailable = "workspace_unavailable";
    public const string WorkspaceChangeSet = "workspace_change_set";
    public const string WorkspaceNoChanges = "workspace_no_changes";
    public const string WorkspaceHarvestFailed = "workspace_harvest_failed";

    // ---- shadow mode -------------------------------------------------------

    public const string ShadowObservationFailed = "shadow_observation_failed";
    public const string ShadowOutcomeRecorded = "shadow_outcome_recorded";
    public const string ShadowRecommendationRecorded = "shadow_recommendation_recorded";

    // ---- modules -----------------------------------------------------------

    /// <summary>v3.8.6 — a module contributed its capability at startup. The first event type in
    /// this file that was not read out of the existing tree, because module loading is new.</summary>
    public const string ModuleRegistered = "module_registered";

    // ---- diagnostics and health --------------------------------------------

    public const string ConfigHealthFinding = "config_health_finding";
    public const string InternalRuntimeDefect = "internal_runtime_defect";
    public const string ReadinessAttested = "readiness_attested";
    public const string SelfTestEvent = "selftest_event";
    public const string SelfTestProbe = "selftest_probe";
}
