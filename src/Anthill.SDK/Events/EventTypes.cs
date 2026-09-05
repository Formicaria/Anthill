namespace Anthill.SDK.Events;

/// <summary>
/// Every event type the colony emits today, as constants.
///
/// This list was not designed — it was READ, out of the working tree, from the <c>LogEvent</c> call
/// sites. That provenance is the point: these are the events the system actually produces, so a
/// subscriber written against this file is written against reality rather than against an intention.
///
/// Publishers and readers used to trade raw string literals, which means a typo in <c>ApiHost</c> or
/// <c>app.js</c> produces a filter that silently matches nothing — the failure mode being an empty
/// panel with no error anywhere. Naming them once is what ends that class of bug.
///
/// When adding an event: add the constant here in the same change as the publisher, never after.
///
/// v0.3.8.86 — AND UNTIL THAT RELEASE, THE PARAGRAPH ABOVE WAS THE DEFECT IT DESCRIBES. The file
/// read as though the vocabulary were complete and consumed. It was neither: the runtime emitted 134
/// distinct event names and this file declared 69, so "written against reality" was true of half the
/// system — and the missing half was the operator-facing half, where a filter matching nothing looks
/// exactly like a quiet colony. Two of the declared constants were emitted by NOBODY, and both were
/// near-misses of real event names (<c>autonomy_autoapply_rolled_back</c> against the real
/// <c>autonomy_autoapply_batch_rolled_back</c>), which is worse than absent: a subscriber filtering
/// on the constant would match nothing while the real events streamed past it. Exactly the empty
/// panel this file exists to prevent, produced by this file.
///
/// `EventVocabularyTests` now enforces both directions, because the instruction one line above this
/// one had been in place the whole time and was followed for roughly half the events. A rule a
/// document states and nothing checks is a rule that describes the author's intention rather than
/// the tree.
///
/// TWO CHANNELS PUBLISH THESE, and the guard knows it: <c>Memory.LogEvent</c> writes the persisted
/// event log, and the event bus carries <c>EventType = EventTypes.X</c>. A constant is live if
/// either uses it — which is why <c>ModuleRegistered</c> counts, despite no <c>LogEvent</c> naming
/// it.
/// </summary>
public static class EventTypes
{
    // ---- mission lifecycle -------------------------------------------------

    public const string MissionCreated = "mission_created";
    public const string MissionStarted = "mission_started";
    public const string MissionClassified = "mission_classified";

    /// <summary>v0.3.8.118 — the pre-dispatch plan, persisted before any worker is invoked. This is
    /// the record later stages are measured against; without it a verifier can only review whatever
    /// narrative survived truncation.</summary>
    public const string MissionDispatchPlanned = "mission_dispatch_planned";

    /// <summary>v0.3.8.118 — the request named something the runtime cannot execute, so the mission
    /// stopped BEFORE dispatch. Emitted instead of silently substituting a plan the operator did not
    /// ask for, which is what used to happen.</summary>
    public const string MissionDispatchPlanRefused = "mission_dispatch_plan_refused";

    /// <summary>v0.3.8.122 — the planner could not use the plan it was asked for and substituted a
    /// static one. This was five `Console.Error.WriteLine` calls and nothing else: the substitution
    /// happened, nothing recorded it, and an operator saw a colony that had ignored their goal with a
    /// green run behind it. `reason` carries one of `PlanSubstitutions`' five codes.
    ///
    /// Only ever logged for a mission that EXISTS. `Queen.PlanPreview` plans over a transient mission
    /// that is never persisted, and an event row for it would violate the events→missions foreign
    /// key — correctly, because planning that is not attached to a mission has no mission history to
    /// be written into.</summary>
    public const string MissionPlanSubstituted = "mission_plan_substituted";

    /// <summary>v0.3.8.122 — a worker proposed a change to canonical knowledge. It has its own type
    /// because it was riding on <c>module_registered</c>, a one-time boot event: a proposal filed
    /// under it could not be found by anyone looking for proposals, and any future consumer would
    /// have had to distinguish the two by reading their metadata.</summary>
    public const string KnowledgeReviewProposed = "knowledge_review_proposed";

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
    // v0.3.8.90 — the three mission terminals, emitted from one ternary in `Queen.cs` and therefore
    // invisible to the literal sweep since the day they were written. Declared because a consumer
    // has to be able to name them: the console's notification centre filtered on `mission_complete`
    // (no trailing "d") and so has never announced a successful mission, while every other name in
    // its pattern worked. A vocabulary that cannot be referenced by name gets re-spelled by hand,
    // and a hand-spelled name is one keystroke from matching nothing.
    public const string MissionCompleted = "mission_completed";
    public const string MissionPartial = "mission_partial";
    public const string MissionFailed = "mission_failed";
    // v0.3.8.90, same reason as the patch pair below: emitted through a wrapper the literal sweep
    // cannot read, and needed by name so the failures panel can be built from the vocabulary.
    public const string ToolCompleted = "tool_completed";
    public const string ToolFailed = "tool_failed";
    public const string TaskDrained = "task_drained";

    /// <summary>
    /// v0.3.8.81 — the role RETURNED after the mission was stopped, and what it returned was
    /// discarded. Distinct from <see cref="TaskDrained"/>, which is the straggler still running when
    /// the grace period expires: this one finished, usually by degrading, and the outcome it reported
    /// was frequently a success. The two are separate event names because an operator asking "what
    /// happened when I pressed stop" needs to tell "it was still going" from "it finished and I threw
    /// the answer away", and the metadata carries the discarded outcome for the second.
    /// </summary>
    public const string TaskStoppedMidFlight = "task_stopped_mid_flight";
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

    /// <summary>
    /// The operator answered. v0.3.8.112 — DECLARED LATE, and found by the sweep rather than by
    /// anyone noticing.
    ///
    /// Both have been emitted by `Queen.ApproveRequest` and `Queen.RejectRequest` since approvals
    /// existed, and neither was in this vocabulary. `EventVocabularyTests` could not see them: its
    /// literal reader requires `LogEvent(&lt;no parens&gt;, "name")`, and both call sites pass
    /// `Str(updated, "mission_id")` as the first argument — a call, so the pattern rejected the
    /// whole line. Widening that reader to resolve arguments positionally surfaced them
    /// immediately, which is the clearest evidence available that the sweep was worth doing: two
    /// events an operator's UI could never have filtered on, in the approval lane, for the whole
    /// life of the feature.
    /// </summary>
    public const string ApprovalRequestApproved = "approval_request_approved";

    /// <summary>The rejection half of <see cref="ApprovalRequestApproved"/>. Same history.</summary>
    public const string ApprovalRequestRejected = "approval_request_rejected";
    public const string AdaptiveEscalated = "adaptive_escalated";
    public const string EscalationRefused = "escalation_refused";

    /// <summary>
    /// The other end of <see cref="EscalationRefused"/>. v0.3.8.110.
    ///
    /// Every prior release, a refusal was the last thing a paused mission ever said about the
    /// action it stopped for: the operator answered, a row changed, and no event marked the work
    /// resuming because no work resumed. This is the first event in the colony's history that can
    /// only be emitted because a person's decision caused something to happen.
    ///
    /// It carries the action, the task ids being replayed and the mission's previous status,
    /// because "resumed" alone tells a reader that state changed and not what changed.
    /// </summary>
    public const string MissionResumed = "mission_resumed";

    /// <summary>
    /// A replay that could not run, and it is deliberately NOT a failure of the approval.
    ///
    /// The operator's decision is recorded and stands whatever happens here — the resumption path
    /// catches everything precisely so a storage or dispatch fault cannot unwind a person's answer.
    /// A consumer filtering for trouble wants this one; a consumer auditing decisions must not read
    /// it as one being reversed.
    /// </summary>
    public const string MissionResumeFailed = "mission_resume_failed";

    // ---- patches and auto-apply --------------------------------------------

    public const string PatchSetCreated = "patch_set_created";
    public const string PatchSetEmpty = "patch_set_empty";
    public const string PatchProposalCreated = "patch_proposal_created";
    public const string PatchProposalParseFailed = "patch_proposal_parse_failed";
    // v0.3.8.90: emitted since the apply path existed and declared here for the first time. The
    // publication sweep could not see them — the event type reaches `LogEvent` through a wrapper
    // whose first argument contains a call, which `LoggedLiteral`'s `[^,()]+` rejects — and
    // `AnthillRuntime.FailureEventTypes` needed to name them through the vocabulary rather than as
    // loose strings, which is what let three of its seven arms drift onto names nothing emits.
    public const string PatchApplied = "patch_applied";
    public const string PatchApplyFailed = "patch_apply_failed";
    public const string PatchAlternativeCreated = "patch_alternative_created";
    public const string PatchReverted = "patch_reverted";
    public const string PatchRevertFailed = "patch_revert_failed";
    public const string AutonomyAutoApplyApplied = "autonomy_autoapply_applied";

    // ---- learning and memory -----------------------------------------------

    public const string PheromoneScored = "pheromone_scored";
    /// <summary>v0.3.8.93: a verified worker trail replaced the registry's declaration-order
    /// tie-break for a task whose text did not decide the worker — the pheromone layer's first
    /// deterministic decision, evented so the steering is auditable rather than silent.</summary>
    public const string WorkerSelectedByTrail = "worker_selected_by_trail";
    /// <summary>
    /// v0.3.8.98: the plan NAMED a worker whose contract serves none of the capabilities the
    /// mission declared, and a compatible one took the task. Evented for the same reason the trail
    /// selection is — a dispatch that silently differs from the plan an operator previewed is a
    /// divergence nobody can reconcile afterwards. Metadata carries both workers and the required
    /// capability set, so the repair can be judged rather than merely noticed.
    /// </summary>
    public const string WorkerRepairedByCapability = "worker_repaired_by_capability";

    /// <summary>v0.3.8.104 — a compiled plan was refused before execution, with every blocker
    /// named. Distinct from a failed mission: nothing ran, and a capability blocker will refuse
    /// identically on a retry.</summary>
    public const string MissionPreflightRefused = "mission_preflight_refused";

    /// <summary>v0.3.8.104 — a tool dispatch refused because the mission's authority ceiling does
    /// not reach it. Distinct from an escalation refusal: nobody was asked, because the answer
    /// would not have mattered.</summary>
    public const string AuthorityCeilingRefused = "authority_ceiling_refused";

    /// <summary>v0.3.8.105 — a task's worker was replaced at DISPATCH because it did not declare
    /// the capability the task requires. The sibling of <see cref="WorkerRepairedByCapability"/>,
    /// which repairs at plan time; this one reaches the tasks admitted after the plan was
    /// checked — handoffs, delta plans, repairs — which preflight never sees.</summary>
    public const string TaskRerouted = "task_rerouted";

    /// <summary>v0.3.8.105 — dispatch refused: no worker in the task's role declares the capability
    /// it requires. A capability block, not a failure — it will refuse identically on a retry.</summary>
    public const string TaskCapabilityUnserved = "task_capability_unserved";

    /// <summary>v0.3.8.105 — a side-effecting action was refused because NOBODY WAS ASKED, and a
    /// pending approval request was filed so the operator has something to answer. Distinct from
    /// <see cref="EscalationRefused"/>, which also covers an operator who said no.</summary>
    public const string OperatorDecisionRequested = "operator_decision_requested";
    public const string SkillCandidateRegistered = "skill_candidate_registered";
    public const string SkillOutcomeRecorded = "skill_outcome_recorded";
    public const string LearningReset = "learning_reset";

    // ---- workspaces --------------------------------------------------------

    public const string WorkspaceReady = "workspace_ready";
    public const string WorkspaceUnavailable = "workspace_unavailable";
    public const string WorkspaceChangeSet = "workspace_change_set";
    /// <summary>v0.3.8.93: a harvested change set saved WITHOUT the review pipeline — no completed
    /// task existed to attribute it to. The evidence survives; the gap is named, not hidden.</summary>
    public const string WorkspaceChangeSetUnanchored = "workspace_change_set_unanchored";
    public const string WorkspaceNoChanges = "workspace_no_changes";
    public const string WorkspaceHarvestFailed = "workspace_harvest_failed";
    /// <summary>v0.3.8.95: the acting coder's workspace diff was captured into the patch pipeline
    /// while the task graph was still open — reviewers can still be inserted to judge it.</summary>
    public const string WorkspaceDiffCaptured = "workspace_diff_captured";
    /// <summary>v0.3.8.95: finalization found this workspace's diff already captured mid-mission
    /// and did not harvest a duplicate. "Already captured" is not "found nothing".</summary>
    public const string WorkspaceAlreadyCaptured = "workspace_already_captured";
    /// <summary>v0.3.8.95: the acting coder's changes could not be captured into a patch set.
    /// The edits still exist in the worktree; the record names the failure instead of hiding it.</summary>
    public const string WorkspaceCaptureFailed = "workspace_capture_failed";

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

    // =============================================================================================
    // v0.3.8.86 — THE SIXTY-SEVEN THIS FILE DID NOT HAVE.
    //
    // The header above says these constants "were READ, out of the working tree, from the ~85
    // LogEvent call sites across Core", and that "a subscriber written against this file is written
    // against reality rather than against an intention". Both sentences were true of the events it
    // listed and false of the file as a whole: the runtime emits 134 distinct event names and this
    // file declared 69, so a subscriber written against it was written against half the system —
    // and the missing half is the operator-facing half, where an empty panel is what a wrong filter
    // looks like.
    //
    // Added as one block rather than merged into the sections above, because they were absent as a
    // block and a reader deserves to see which half of the vocabulary arrived late.
    // =============================================================================================

    // ---- autonomy and auto-apply — the operator-facing loop ----------------------
    public const string AutonomyAutoapplyApplyFailed = "autonomy_autoapply_apply_failed";
    public const string AutonomyAutoapplyBatchRolledBack = "autonomy_autoapply_batch_rolled_back";
    public const string AutonomyAutoapplyBreakGlass = "autonomy_autoapply_break_glass";
    public const string AutonomyAutoapplyCommitted = "autonomy_autoapply_committed";
    public const string AutonomyAutoapplyError = "autonomy_autoapply_error";
    public const string AutonomyAutoapplyGitFailed = "autonomy_autoapply_git_failed";
    public const string AutonomyAutoapplyHalted = "autonomy_autoapply_halted";
    public const string AutonomyAutoapplyIneligible = "autonomy_autoapply_ineligible";
    public const string AutonomyAutoapplyPreflightRefused = "autonomy_autoapply_preflight_refused";
    public const string AutonomyAutoapplyReverted = "autonomy_autoapply_reverted";
    public const string AutonomyAutoapplyRollbackIncomplete = "autonomy_autoapply_rollback_incomplete";
    public const string AutonomyAutoapplySkipped = "autonomy_autoapply_skipped";
    public const string AutonomyAutoapplyStaleEvidence = "autonomy_autoapply_stale_evidence";
    public const string AutonomyAutoapplyStarted = "autonomy_autoapply_started";
    public const string AutonomyError = "autonomy_error";
    public const string AutonomyIdle = "autonomy_idle";
    public const string AutonomyMissionFinished = "autonomy_mission_finished";
    public const string AutonomyMissionStarted = "autonomy_mission_started";
    public const string AutonomyResumed = "autonomy_resumed";
    public const string AutonomyStarted = "autonomy_started";
    public const string AutonomyStopped = "autonomy_stopped";

    // ---- patch verification and materialization ----------------------------------
    public const string PatchBypassBlocked = "patch_bypass_blocked";
    // v0.3.8.91 — the bypass lane's two outcomes, declared because one of them just became VISIBLE.
    //
    // Both have been emitted since v0.3.8.51, from a ternary
    // (`ok ? "patch_bypass_applied" : "patch_bypass_apply_refused"`), which is a shape the emitter
    // sweep cannot read — so neither was declared and nothing complained. Adding the promotion
    // gate's refusal put `patch_bypass_apply_refused` in a plain first-argument position, the sweep
    // saw it for the first time, and the vocabulary guard failed on a name the runtime had been
    // writing for forty releases.
    //
    // Exactly the blind spot PLAN.md already records: a detector that reads one syntactic shape
    // measures that shape, not the runtime. Declaring these does not fix the detector; it removes
    // two of the dozen or so names still hiding behind it.
    public const string PatchBypassApplied = "patch_bypass_applied";
    public const string PatchBypassApplyRefused = "patch_bypass_apply_refused";
    /// <summary>v0.3.8.97 — Skip-all-approvals application is waiting for the set's inserted
    /// tester/soldier reviews to complete; the attempt fires at the last review's completion.</summary>
    public const string PatchBypassDeferred = "patch_bypass_deferred";
    // The promotion gate's own refusal, new in v0.3.8.91 and declared with its producer.
    public const string PatchPromotionRefused = "patch_promotion_refused";
    // v0.3.8.91 — the set-level apply and its rollback, declared with their producer so the
    // vocabulary never trails the runtime again.
    public const string PatchSetApplied = "patch_set_applied";
    public const string PatchSetApplyRefused = "patch_set_apply_refused";
    public const string PatchSetRolledBack = "patch_set_rolled_back";
    public const string PatchSetRollbackIncomplete = "patch_set_rollback_incomplete";
    // v0.3.8.91 — startup reconciliation of an apply a crash interrupted.
    public const string PatchApplyReconciled = "patch_apply_reconciled";
    public const string PatchApplyUnreconciled = "patch_apply_unreconciled";
    public const string PatchSetMaterializationFailed = "patch_set_materialization_failed";
    public const string PatchSetVerificationFaulted = "patch_set_verification_faulted";
    public const string PatchSetVerified = "patch_set_verified";
    public const string PatchVerifiedApproved = "patch_verified_approved";
    public const string PatchVerifyFailed = "patch_verify_failed";
    public const string PatchVerifyRestoreFailed = "patch_verify_restore_failed";
    public const string PatchVerifyStarted = "patch_verify_started";

    // ---- policy-inserted reviews and verification --------------------------------
    public const string PolicyReviewInserted = "policy_review_inserted";
    public const string PolicyReviewRefused = "policy_review_refused";
    public const string PolicyReviewSkipped = "policy_review_skipped";
    public const string VerificationBoundToEvidence = "verification_bound_to_evidence";
    public const string VerificationInserted = "verification_inserted";
    public const string VerificationRefused = "verification_refused";
    public const string VerificationSkipped = "verification_skipped";

    // ---- the archivist, after finalization ---------------------------------------
    public const string ArchivistFailed = "archivist_failed";
    public const string ArchivistRan = "archivist_ran";
    public const string ArchivistSkipped = "archivist_skipped";

    // ---- operator actions on the dashboard ---------------------------------------
    public const string AntProfileCleared = "ant_profile_cleared";
    public const string AntProfileSaved = "ant_profile_saved";
    public const string DirectoryGateClosed = "directory_gate_closed";
    public const string DirectoryGateOpened = "directory_gate_opened";
    public const string JobsCancelAll = "jobs_cancel_all";
    public const string MaintenanceClearMissions = "maintenance_clear_missions";
    public const string MaintenanceFlush = "maintenance_flush";
    public const string MaintenanceResetConfig = "maintenance_reset_config";
    public const string ObjectiveRetired = "objective_retired";
    public const string ObjectiveSuggestionApproved = "objective_suggestion_approved";
    public const string ObjectivesCleared = "objectives_cleared";
    public const string OperatorFileCreated = "operator_file_created";
    public const string OperatorFileEdited = "operator_file_edited";
    public const string OperatorShellCommand = "operator_shell_command";
    public const string OperatorShellError = "operator_shell_error";
    public const string OperatorShellResult = "operator_shell_result";
    public const string UserToolRegistered = "user_tool_registered";

    // ---- agent runs and installs -------------------------------------------------
    public const string AgentInstallStarted = "agent_install_started";
    public const string AgentRunFinished = "agent_run_finished";
    public const string AgentRunStarted = "agent_run_started";

    // ---- everything else the runtime emits ---------------------------------------
    public const string ArtifactSchemaViolation = "artifact_schema_violation";
    public const string EvidenceFollowUpsCreated = "evidence_follow_ups_created";
    public const string FailureContextRecorded = "failure_context_recorded";
    public const string MissionReportUnavailable = "mission_report_unavailable";
    public const string MissionRevisionRegistered = "mission_revision_registered";
    public const string RequiredHandoffRefused = "required_handoff_refused";
    public const string TaskRanInRevision = "task_ran_in_revision";
    public const string UiChangeBlockedUnmapped = "ui_change_blocked_unmapped";

    // ---- v0.3.8.89: the four the v0.3.8.86 sweep could not see -------------------
    //
    // That release added sixty-seven names by reading every event literal handed DIRECTLY to
    // `LogEvent`. These four are emitted through a wrapper instead — `RecordAdaptiveAdmission` takes
    // the event type as a PARAMETER and its callers pass the literal, and the memory-candidate names
    // reach `LogEvent` the same way — so the detector matched nothing and reported the vocabulary
    // complete. A guard honest about its own scope, and a blind spot all the same.
    //
    // Found by looking at the CONSUMER side: `GetRecentEvents(limit, "name", ...)` names an event
    // type in an unambiguous position, and four of the eighteen names queried that way were declared
    // nowhere. `EventVocabularyTests.EveryEventTypeQueriedByName_IsDeclared` is that check.
    public const string AdaptiveDeltaPlan = "adaptive_delta_plan";
    public const string AdaptiveRepair = "adaptive_repair";
    public const string MemoryCandidate = "memory_candidate";
    //
    // `memory_candidate_archived` IS NOT HERE, and the absence is the finding. Five assertions
    // queried that name — including the cancellation harness's "no memory survives a stopped
    // mission", one of the five properties R3 rests on — and NOTHING has ever emitted it. The
    // ingest's own event type is `memory_candidate` (MemoryCandidateIngest.EventType).
    //
    // A near-miss of a real name, so every one of those assertions was checking that an event no
    // producer writes did not appear, and could not have failed. v0.3.8.85 came close: its comment
    // in Queen.cs says the property "held by luck rather than by design" because a stopped mission
    // usually gives the archivist nothing to propose. It was not luck. The filter matched nothing.

    // ---- v0.3.8.114: the fifteen the LogEvent sweep could never see -----------------------------
    //
    // THE SECOND EMISSION CHANNEL. Every sweep before this one read `LogEvent(...)` call sites, and
    // this codebase emits through TWO channels: the persisted log, and the event bus as
    // `Publish(new ColonyEvent { EventType = ... })`. Thirty-two sites use the second, and fifteen
    // names lived only there — declared nowhere, so nothing could filter on them.
    //
    // The asymmetry is the finding, and it was visible in this file's own guard the whole time:
    // `EveryDeclaredEvent_IsPublishedBySomething` has always counted the bus as publication ("two
    // channels count as publication, and conflating them would have produced a false finding
    // here"), while `EveryEventTheRuntimeLogs_IsDeclaredInTheVocabulary` read only `LogEvent`. One
    // direction knew about the bus and the other did not.
    //
    // Same shape as v0.3.8.112's `approval_request_approved` and `approval_request_rejected` — the
    // rule was right, the reader was narrower than the rule — at five times the scale. Two of them
    // are mission-lane events an operator's console could never have filtered on.
    public const string ProviderRun = "provider_run";
    public const string HealthCheckFailed = "health_check_failed";
    public const string IncidentCandidate = "incident_candidate";
    public const string IncidentOpened = "incident_opened";
    public const string IncidentPattern = "incident_pattern";
    public const string IncidentFixRecorded = "incident_fix_recorded";
    public const string RiskAnalysis = "risk_analysis";
    public const string CredentialUsed = "credential_used";
    public const string ProxmoxTaskFailed = "proxmox_task_failed";
    public const string HomelabStopEngaged = "homelab_stop_engaged";
    public const string BackupRecordUpserted = "backup_record_upserted";
    public const string MissionLearningRecorded = "mission_learning_recorded";
    public const string MissionArchivistRecorded = "mission_archivist_recorded";

    // ---- v0.3.8.114: the Micromound vocabulary, which lived outside this file entirely ----------
    //
    // THE SAME DEFECT AS THE BLOCK ABOVE, ONE DIRECTORY OVER. `MicromoundEvents` held twenty event
    // names as its own literals, and this file — the vocabulary a subscriber filters against — named
    // none of them. Nor could the sweep find them: `BusLiteral` reads `EventType = "..."` and the
    // module publishes `EventType = eventType` through one helper, so every name was invisible in
    // both directions at once. A colony that speaks a word no dictionary lists is a colony whose
    // console cannot show it.
    //
    // THE LITERALS LIVE HERE AND NOWHERE ELSE. `MicromoundEvents` is now an alias table over these
    // constants — `public const string CharterIssued = EventTypes.MicromoundCharterIssued;` — which
    // is what the previous block called "the real fix … declare it and compose against the
    // vocabulary rather than against a string", applied where it was cheap enough to do at once.
    // The module keeps its own short names, because `MicromoundEvents.CharterIssued` reads better at
    // a call site than the qualified one, and there is exactly one string behind both.
    public const string MicromoundEnrollmentTokenMinted = "micromound_enrollment_token_minted";
    public const string MicromoundMoundEnrolled = "micromound_mound_enrolled";
    public const string MicromoundEnrollmentRefused = "micromound_enrollment_refused";
    public const string MicromoundSyncAccepted = "micromound_sync_accepted";
    public const string MicromoundSyncRefused = "micromound_sync_refused";
    public const string MicromoundChainBroken = "micromound_chain_broken";
    public const string MicromoundStopInEffect = "micromound_stop_in_effect";
    public const string MicromoundStopCleared = "micromound_stop_cleared";
    public const string MicromoundCharterIssued = "micromound_charter_issued";
    public const string MicromoundCharterRefused = "micromound_charter_refused";
    public const string MicromoundConfigurationIssued = "micromound_configuration_issued";
    public const string MicromoundConfigurationRefused = "micromound_configuration_refused";
    public const string MicromoundMissionDispatched = "micromound_mission_dispatched";
    public const string MicromoundMissionRefused = "micromound_mission_refused";
    public const string MicromoundMissionApprovalRequired = "micromound_mission_approval_required";
    public const string MicromoundMissionReported = "micromound_mission_reported";
    public const string MicromoundEvidenceIngested = "micromound_evidence_ingested";
    public const string MicromoundActionDegraded = "micromound_action_degraded";
    public const string MicromoundDownlinkRefused = "micromound_downlink_refused";
    public const string MicromoundMoundQuiesced = "micromound_mound_quiesced";
    public const string MicromoundMoundUnlinked = "micromound_mound_unlinked";
}
