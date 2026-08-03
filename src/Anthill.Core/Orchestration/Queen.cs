using System.Diagnostics;
using Anthill.Core.Agents;
using Anthill.Core.Outcomes;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Pheromones;
using Anthill.Core.Planning;
using Anthill.Core.Scheduling;
using Anthill.Core.Skills;
using Anthill.Core.Security;
using Anthill.Core.Tools;

namespace Anthill.Core.Orchestration;

/// <summary>
/// The Queen is the central coordinator: plan, dispatch, verify, remember, and score.
/// She stays thin enough to orchestrate while the ants and tools carry specialised
/// behaviour and <see cref="TaskScheduler"/> owns all dependency/lifecycle decisions.
/// This partial holds construction and the mission-execution engine; approvals, patch
/// application, and the formatter/view surface live in <c>Queen.Views.cs</c>.
/// </summary>
public sealed partial class Queen : IMissionCoordinator, IDisposable
{
    public void Dispose() => Memory.Dispose();

    public SqliteMemory Memory { get; }
    public ModelRouter? Router { get; }
    public ToolRegistry Tools { get; }
    /// <summary>The capability set this Queen was composed from. Missions resolve their context
    /// against it, so a mission cannot be governed by configuration the Queen never saw.</summary>
    public RuntimeProfile Profile { get; }
    private readonly Planner _planner;
    /// <summary>v3.1.0 (ADR-001): planning behind an interface. The Queen decides WHEN a plan is
    /// made and owns the mission it belongs to; it no longer also implements how one is built.</summary>
    private readonly IPlanningService _planning;
    /// <summary>v3.1.0 (ADR-001): what a finished mission teaches the colony — scoring, pheromone
    /// reinforcement, skill credit, route registration — behind an interface. The Queen decides
    /// WHEN learning happens; this owns what gets recorded.</summary>
    private readonly ILearningRecorder _learning;
    /// <summary>v3.1.0 (ADR-001): the operator-facing accounts of a finished mission — raw output,
    /// full trace, and the synthesised answer — behind an interface.</summary>
    private readonly IResultAssembler _results;
    private readonly PheromoneEngine _pheromones = new();
    /// <summary>v3.1.0 (ADR-001): driving the task graph — dispatch, task lifecycle, the
    /// concurrency boundary, mid-run task admission — behind an interface. Internal rather than
    /// private so the admission path itself stays testable: a source guard proving a call site
    /// exists is not the same as proving the gates actually run.</summary>
    internal IExecutionService Execution { get; }
    /// <summary>v3.1.0 (ADR-001): the ONE grader, injected. A pass-through to the canonical
    /// evaluator — the interface exists so the composition root can see there is exactly one.</summary>
    private readonly IMissionEvaluator _evaluator = new CanonicalMissionEvaluator();

    /// <summary>
    /// v2.21.0 Phase C: the skills registry, hydrated from the database rather than constructed
    /// empty. Before this the V2.12 evaluation model had no production instantiation at all — a
    /// skill could earn Certified and nothing anywhere would ever see it.
    /// </summary>
    private SkillRegistry Skills => _skills ??= Memory.LoadSkillRegistry();
    private SkillRegistry? _skills;
    private readonly Dictionary<string, BaseAnt> _ants;
    public string? LastMissionId { get; private set; }

    /// <summary>
    /// v3.1.0 (ADR-001): the Queen's own construction is now composed from an immutable
    /// <see cref="RuntimeProfile"/> rather than read out of mutable statics.
    ///
    /// This is what makes the phase's exit gate reachable. Construction used to read
    /// <c>EnableModelRouting</c>, <c>UseOllama</c>, <c>EnableFileTools</c> and
    /// <c>EnableFileWriting</c> directly, which meant two Queens built at two different instants
    /// could disagree about their own shape — and it is why every gate-touching test had to
    /// serialise itself around the globals. A profile passed in makes the disagreement impossible
    /// and the serialisation unnecessary.
    ///
    /// <paramref name="profile"/> null captures the live runtime, preserving the existing
    /// single-instance behaviour for the CLI and the API host.
    /// </summary>
    public Queen(SqliteMemory? memory = null, RuntimeProfile? profile = null)
    {
        AnthillRuntime.Initialize();
        Memory = memory ?? new SqliteMemory();
        // Captured BEFORE anything is built, so every component below sees one consistent answer.
        var options = (profile ?? RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>())).Options;
        Router = options.ModelRouting ? new ModelRouter(Memory) : null;
        Tools = BuildToolRegistry(options);
        // The profile is re-resolved against the tools this run actually registered, so its grants
        // describe what was built rather than what the gates implied.
        Profile = RuntimeProfile.Resolve(options, Tools.Names);
        _planner = new Planner(options.UseOllama, Router);
        // The registry factory, not the registry: Skills hydrates lazily from the database and is
        // shared with the credit/promotion paths, so there must remain exactly one instance.
        _planning = new PlanningService(_planner, Memory, Tools, () => Skills);
        _learning = new LearningRecorder(Memory, _pheromones, () => Skills);
        _results = new ResultAssembler(Memory, Router);
        _ants = new Dictionary<string, BaseAnt>
        {
            ["researcher"] = new ResearcherAnt(Memory, Tools, Router),
            ["web"] = new WebResearchAnt(Memory, Tools, Router),
            ["file"] = new FileAnt(Tools),
            ["coder"] = new CoderAnt(options.UseOllama, Router),
            ["builder"] = new BuilderAnt(options.UseOllama, Router),
            ["verifier"] = new VerifierAnt(options.UseOllama, Router),
            // Stage D canary 1: handler registered unconditionally (implemented), but the role only
            // becomes executable/plannable when its rollout gates are open — the catalog and the
            // registry gate agree by construction.
            ["ui_cartographer"] = new UiCartographerAnt(Tools),
            ["tester"] = new TesterAnt(Tools),
            ["soldier"] = new SoldierAnt(),
            ["scribe"] = new ScribeAnt(),
            ["medic"] = new MedicAnt(),
            ["archivist"] = new ArchivistAnt(),
        };
        // Execution framework Stage C: validate the executor catalog at startup. Any problem keeps
        // the affected role unavailable (fail closed) and is loud, never silent.
        foreach (var problem in AntExecutorCatalog.Initialize(_ants.Keys.ToList()))
            Console.Error.WriteLine($"[startup-validation] {problem}");

        // v3.4.2: does each role's route actually do what its contract needs? Reported at startup
        // because EVERY mismatch here fails silently at runtime — a model that cannot call tools is
        // never shown them and answers from priors; one without structured output returns prose
        // where a schema was expected and parses to an empty result. Neither throws, neither opens a
        // breaker, and in a transcript both look like a weak model rather than a misconfiguration.
        //
        // A warning, not a refusal: the operator's routing is theirs, the capability data can be
        // incomplete for a model nothing has described yet, and refusing to start over a
        // fail-closed guess would be worse than running with a warning they can act on.
        if (Router is not null)
            foreach (var fitness in AntModelFitness.CheckAll(Router, AntExecutionCatalog.Contracts).Where(f => !f.Fit))
                Console.Error.WriteLine(
                    $"[model-fitness] role '{fitness.RoleId}' is routed to {fitness.Provider}:{fitness.Model}, "
                  + $"which is missing: {string.Join("; ", fitness.Unmet)}");

        // v3.5.0: reconcile recorded workspaces with what is on disk, before anything can be
        // dispatched into one. A row left claiming Active by a process that died would otherwise be
        // handed to an agent as a live workspace, and something would wait forever for the agent
        // that row implies is already working in it.
        Workspaces = new Anthill.Core.Workspaces.MissionWorkspaceManager(Memory, options.AllowedWorkspaceRoot);
        foreach (var note in Workspaces.Recover())
            Console.Error.WriteLine($"[workspace-recovery] {note}");
        Execution = new ExecutionService(Memory, _ants);
    }

    private ToolRegistry BuildToolRegistry(RuntimeOptions options)
    {
        var registry = new ToolRegistry(Memory);
        var guard = new WorkspacePathGuard(options.AllowedWorkspaceRoot);
        registry.Register(new SystemInfoTool());
        // Stage D-2: TesterAnt's ONLY execution surface — declared checks, never arbitrary commands.
        registry.Register(new RunAllowlistedCheckTool(options.AllowedWorkspaceRoot));
        if (options.FileTools)
        {
            registry.Register(new DirectoryListTool(guard));
            registry.Register(new ReadTextFileTool(guard));
        }
        if (options.FileWriting)
            registry.Register(new WriteTextFileTool(guard));
        registry.Register(new WebSearchTool());
        registry.Register(new ShellCommandTool());
        registry.Register(new ApplyPatchTool(guard));

        // v3.4.1: operator-defined tools join the SAME registry, last, and by the same Register call
        // every built-in uses. That ordering is deliberate — a definition is validated against
        // ToolInventory and cannot take a built-in's name, so arriving last can never displace one.
        //
        // Registering them here rather than through a parallel path is the entire exit gate: from
        // this line onwards nothing in the harness — projection, authorization, dispatch, failure
        // classification, /tools — knows or asks whether a tool was compiled in or declared.
        UserTools = UserToolRegistrar.Default().RegisterAll(registry, Memory.LoadToolDefinitions());
        foreach (var rejected in UserTools.Where(r => !r.Registered))
            Console.Error.WriteLine(
                $"[user-tools] '{rejected.Name}' not registered: {string.Join("; ", rejected.Problems)}");

        return registry;
    }

    /// <summary>
    /// The outcome of loading operator-defined tools for THIS run, rejections included. Held so the
    /// API can answer "why is my tool not there" — the one question a rejected definition provokes,
    /// and one that is unanswerable if the rejection only ever reached stderr.
    /// </summary>
    public IReadOnlyList<ToolRegistration> UserTools { get; private set; } = Array.Empty<ToolRegistration>();

    /// <summary>
    /// v3.5.0: disposable, attributable workspaces for code missions. Owned by the Queen because
    /// workspace lifecycle is deterministic orchestration — no model participates in deciding where
    /// an agent may write, for the same reason none picks its own tool authorization.
    /// </summary>
    public Anthill.Core.Workspaces.MissionWorkspaceManager Workspaces { get; private set; } = null!;

    /// <summary>
    /// Re-read the stored definitions and re-register them into the live registry.
    ///
    /// Called after an operator adds, edits or revokes a tool, so the change takes effect for the
    /// next mission rather than the next restart. It re-registers the WHOLE set rather than one
    /// definition, because the grant table is replaced wholesale — that is what stops a definition
    /// removed since the last load from staying granted.
    /// </summary>
    public void ReloadUserTools() =>
        UserTools = UserToolRegistrar.Default().RegisterAll(Tools, Memory.LoadToolDefinitions());

    /// <summary>
    /// Prepare a workspace for a mission that may write, and record the outcome on the mission's
    /// own event stream.
    ///
    /// Returns null rather than throwing when preparation fails — which it legitimately does when
    /// the workspace root is not a git checkout. A mission that cannot get an isolated workspace
    /// still runs, under the configured root exactly as it did before v3.5.0; refusing to run at all
    /// would make an isolation improvement into a breaking change for every non-git deployment.
    /// The event says which happened, because "my changes went to the live checkout" must never be
    /// something an operator has to infer.
    /// </summary>
    private Anthill.Core.Workspaces.MissionWorkspace? PrepareWorkspace(string missionId)
    {
        try
        {
            var workspace = Workspaces.Prepare(missionId);
            if (workspace.Usable)
            {
                Workspaces.Activate(workspace.Id);
                Memory.LogEvent(missionId, "workspace_ready",
                    $"Mission workspace {workspace.Id} prepared from {workspace.BaseRevision}", null, "queen",
                    new()
                    {
                        ["workspace_id"] = workspace.Id,
                        ["base_revision"] = workspace.BaseRevision,
                        ["root"] = workspace.Root,
                    });
                return workspace;
            }

            Memory.LogEvent(missionId, "workspace_unavailable",
                $"No isolated workspace: {workspace.Note}. File operations use the configured root.",
                null, "queen", new() { ["reason"] = workspace.Note });
            return null;
        }
        catch (Exception error)
        {
            Memory.LogEvent(missionId, "workspace_unavailable",
                $"Workspace preparation failed: {error.Message}", null, "queen");
            return null;
        }
    }

    public string RunMission(string goal) => RunMission(goal, onMissionCreated: null);

    /// <summary>
    /// Runs a mission and reports the new mission's id to <paramref name="onMissionCreated"/> as
    /// soon as the row is persisted. Callers running missions concurrently (Phase 3) must use
    /// this callback instead of <see cref="LastMissionId"/>, which is a last-writer-wins
    /// convenience kept for the single-mission CLI path.
    ///
    /// <paramref name="cancel"/> lets the caller (e.g. the API job runner) stop a mission mid-flight:
    /// it is linked with a hard <see cref="AnthillRuntime.MaxMissionSeconds"/> deadline into a single
    /// token that is (a) published to every model call via <see cref="ModelCallScope"/> so an
    /// in-flight generation aborts promptly and (b) checked between tasks so the scheduler stops
    /// dispatching. Without it a hung/slow model call could pin the single-writer queue for minutes.
    /// </summary>
    public string RunMission(string goal, Action<string>? onMissionCreated, CancellationToken cancel = default,
        Action<MissionOutcome>? onMissionFinished = null)
    {
        Console.WriteLine($"Queen received mission: {goal}");
        var missionStartedAt = AnthillTime.NowUtc();

        // v3.1.0 (ADR-001): configuration is captured ONCE, here, and the run's capability set is
        // resolved from it. Everything below reads the snapshot; nothing on the mission path
        // reaches for a mutable static again. Two Queens in one process therefore cannot leak
        // configuration into each other's missions — each captured its own at its own intake.
        var profile = Profile;
        var options = profile.Options;

        var mission = new Mission { Goal = goal, Status = MissionStatus.Running };
        LastMissionId = mission.Id;

        // v3.1.0 (ADR-002): the mission's governing facts, resolved once at intake and passed
        // explicitly from here on. Constraints are parsed exactly once; the deadline is an
        // ABSOLUTE instant anchored to the mission's own start, so a resumed run compares against
        // the same wall-clock boundary the original did instead of restarting its clock.
        var context = MissionContext.Create(mission, profile, missionStartedAt);

        // One token governs the whole mission: external cancel OR the deadline, whichever comes first.
        using var missionCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // v2.26.0 pre-V3 hardening: the mission DEADLINE cancels the token. Before this, timeout
        // was only a wall-clock check in the dispatch loop — in-flight model calls ran to their own
        // completion while the mission proceeded to finalization without them. MissionStopReason
        // checks the clock before the token, so a deadline cancellation still reports as timeout.
        // v3.1.0: armed from the context's absolute deadline rather than a fresh duration.
        missionCts.CancelAfter(context.Remaining(AnthillTime.NowUtc()));
        using var modelScope = ModelCallScope.Enter(missionCts.Token);

        // Persist the mission row before any LogEvent calls so FK constraints on events(mission_id) are satisfied.
        Memory.SaveMission(mission);
        onMissionCreated?.Invoke(mission.Id);

        // v3.5.0 — a mission permitted to WRITE gets its own workspace, and every file operation for
        // the rest of this mission is confined to it.
        //
        // Gated on the write capabilities rather than prepared for every mission: a read-only
        // research mission has nothing to isolate, and taking a git worktree for it would cost a
        // directory per question. The scope is entered even when preparation FAILS to produce a
        // usable workspace — in which case CurrentRoot is null and the guard keeps its configured
        // root, which is exactly the pre-v3.5.0 behaviour rather than a silent widening.
        var wantsWorkspace = AnthillRuntime.EnableFileWriting || AnthillRuntime.EnablePatchApplication;
        var missionWorkspace = wantsWorkspace ? PrepareWorkspace(mission.Id) : null;
        using var workspaceScope = Anthill.Core.Workspaces.MissionWorkspaceScope.Enter(missionWorkspace);
        Memory.LogEvent(mission.Id, "mission_context_resolved",
            "Mission constraints, capability grants, deadline and budgets resolved at intake.",
            metadata: context.Snapshot());

        // v2.26.0 backup policy: a full DB copy before EVERY mission does not scale — a read-only
        // question should not trigger a database-sized write once the colony has history. Backups
        // now run when the last one is older than BackupMinIntervalMinutes (schema migrations and
        // auto-apply runs take their own). Retention and permission hardening unchanged.
        var backupPath = FileSecurity.BackupDbIfDue(AnthillRuntime.DbPath, AnthillRuntime.BackupDir,
            AnthillRuntime.PathFromScript, TimeSpan.FromMinutes(AnthillRuntime.BackupMinIntervalMinutes));
        var (prunedBackups, freedBytes) = FileSecurity.PruneBackups(AnthillRuntime.BackupDir, AnthillRuntime.MaxDbBackups, AnthillRuntime.PathFromScript);
        Memory.LogEvent(mission.Id, backupPath is not null ? "db_backup_created" : "db_backup_skipped",
            backupPath is not null ? "Pre-mission DB backup created."
                : "Pre-mission DB backup skipped (a recent backup already exists, or no database file yet).",
            metadata: new() { ["backup_file"] = backupPath is not null ? Path.GetFileName(backupPath) : null,
                ["backups_pruned"] = prunedBackups, ["bytes_freed"] = freedBytes, ["keep"] = AnthillRuntime.MaxDbBackups });
        Memory.LogEvent(mission.Id, "mission_created", "Mission created.", metadata: new() { ["goal"] = goal });

        // Classify the request. Oversized specification/architecture documents are ingested
        // section-by-section instead of through a single broad analysis task.
        var isSpecIngestion = Planner.IsLongInput(goal);
        var missionType = isSpecIngestion ? "spec_ingestion" : "standard";
        Memory.LogEvent(mission.Id, "mission_classified", $"Mission classified as {missionType}.", metadata: new()
        {
            ["mission_type"] = missionType, ["goal_chars"] = goal.Length,
            ["long_input_threshold"] = AnthillRuntime.LongInputThreshold,
            ["spec_ingestion_enabled"] = AnthillRuntime.EnableSpecIngestion,
        });

        // v3.1.0 (ADR-001): planning is a service. The Queen says WHEN a plan is made and owns
        // everything that happens to it afterwards; it no longer also implements how one is built.
        mission.Tasks = _planning.CreatePlan(context);

        foreach (var task in mission.Tasks)
            Memory.LogEvent(mission.Id, "task_created", $"Task created for {task.AssignedAnt}: {task.Title}", task.Id, task.AssignedAnt,
                new() { ["task_type"] = task.TaskType, ["assigned_worker"] = task.AssignedWorker, ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds });

        Memory.LogEvent(mission.Id, "mission_started", "Mission execution started.", metadata: new()
        {
            ["mission_type"] = missionType,
            ["task_count"] = mission.Tasks.Count,
            ["planner_pattern"] = mission.Tasks.Select(t => t.AssignedAnt).ToList(),
            ["worker_path"] = mission.Tasks.Select(t => t.AssignedWorker ?? t.AssignedAnt).ToList(),
            ["task_type_pattern"] = mission.Tasks.Select(t => t.TaskType).ToList(),
            ["parallel_execution"] = options.ParallelExecution,
            ["max_parallel_workers"] = options.MaxParallelWorkers,
            ["auto_dependency_wiring"] = options.AutoDependencyWiring,
            ["correlation_id"] = context.CorrelationId,
            ["deadline"] = context.Deadline.ToIso(),
        });
        Console.WriteLine($"Mission ID: {mission.Id}");
        Console.WriteLine($"Created {mission.Tasks.Count} tasks. Parallel execution: {(options.ParallelExecution ? "ON" : "OFF")}\n");

        // Persist the planned DAG before execution so /graph (and the live colony canvas) can see
        // the mission's tasks while they run — not only after the mission finishes.
        Memory.SaveMission(mission);

        // The executors return WHY they stopped dispatching (mission_timeout / mission_cancelled), or
        // null if the plan ran to its natural end — the authoritative signal for the outcome below.
        // v3.1.0 (ADR-001): the executor returns WHY it stopped dispatching (mission_timeout /
        // mission_cancelled / adaptive_stop), or null if the plan ran to its natural end — the
        // authoritative signal the Queen grades against below.
        var stopReason = Execution.Execute(mission, context, missionCts.Token);

        var evaluation = FinalizeMission(mission, context, stopReason);
        Console.WriteLine($"Pheromone score: {mission.SuccessScore}");
        Memory.SaveMission(mission);
        // The evaluation is persisted AFTER the final SaveMission on purpose: SaveMission is an
        // INSERT OR REPLACE, and a row replacement erases columns it does not carry — writing the
        // evaluation first would silently destroy it (the restart test caught exactly that). It is
        // still persisted BEFORE completion is published anywhere: the outcome event, the
        // job callback, and every Director/auto-apply read all come after this line.
        Memory.SaveMissionEvaluation(evaluation);
        Console.WriteLine("Mission saved to ANTHILL memory.");

        // v2.7.0 (canonical since v2.26.0): the operator-facing "why it ended" derives from the
        // ONE persisted evaluation — the reason text is presentation; the code is authority.
        var outcome = ComputeOutcome(mission, stopReason) with { OutcomeCode = evaluation.OutcomeCode };
        Memory.LogEvent(mission.Id, "mission_outcome", outcome.Reason,
            metadata: new()
            {
                ["outcome"] = outcome.Outcome, ["reason"] = outcome.Reason,
                ["outcome_code"] = evaluation.OutcomeCode, ["mission_status"] = mission.Status.Value(),
                ["verification_status"] = evaluation.VerificationStatus,
                ["deliverable_status"] = evaluation.DeliverableStatus,
            });
        onMissionFinished?.Invoke(outcome);
        return _results.ComposeCliResult(mission);
    }

    /// <summary>Plain-English mission result the console surfaces on each job. Keyed status + a short reason.</summary>
    public sealed record MissionOutcome(string Outcome, string Reason, string OutcomeCode = "");

    /// <summary>
    /// Derives the operator-facing outcome from the executor's stop reason (authoritative for
    /// cancel/timeout) and the finalized mission/task state (for the completed/partial/failed split).
    /// </summary>
    internal static MissionOutcome ComputeOutcome(Mission mission, string? stopReason)
    {
        var total = mission.Tasks.Count;
        var done = mission.Tasks.Count(t => t.Status == TaskStatus.Complete);
        if (stopReason == "mission_cancelled")
            return new("cancelled", $"Cancelled by operator — {done}/{total} tasks finished before stopping.");
        if (stopReason == "mission_timeout")
            return new("timed_out", $"Timed out — exceeded the {AnthillRuntime.MaxMissionSeconds}s mission budget after {done}/{total} tasks.");

        var taskTimeouts = mission.Tasks.Count(t => t.FailureType == "timeout");
        var timeoutNote = taskTimeouts > 0 ? $" ({taskTimeouts} task{(taskTimeouts == 1 ? "" : "s")} hit the per-task limit)" : "";
        return mission.Status switch
        {
            MissionStatus.Complete => new("completed", $"Completed — {done}/{total} tasks succeeded{timeoutNote}."),
            MissionStatus.Partial => new("partial", $"Partial — {done}/{total} tasks succeeded; some were skipped or failed{timeoutNote}."),
            _ => new("failed",
                (mission.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Failed)?.FailureReason is { Length: > 0 } fr
                    ? $"Failed — {fr}"
                    : $"Failed — a critical task did not succeed{timeoutNote}.")),
        };
    }

    /// <summary>
    /// v1.8.18 Mission Composer plan preview: builds the task plan for a goal exactly as
    /// <see cref="RunMission(string)"/> would (planner → task-type inference → auto-dependency
    /// wiring), but WITHOUT creating, persisting, executing, or logging a mission. Powers
    /// <c>POST /missions/plan</c> so an operator can review the plan (and see the effect of
    /// verification-only / no-patch constraints) before approving dispatch. Read-only: the only
    /// external effect is the planner's model call, exactly as a real dispatch would make.
    /// </summary>
    public MissionPlan PlanPreview(string goal)
    {
        // v3.1.0: the preview resolves a context exactly as a dispatch would, over a transient
        // mission that is never persisted, and then asks the SAME planning service — including the
        // authorization verdict, which the old preview skipped. It returns the plan together with
        // the constraints it was built under, so the endpoint rendering it does not have to
        // reconstruct either. An operator approving a preview is approving the plan that will run.
        var context = MissionContext.Create(new Mission { Goal = goal }, Profile, AnthillTime.NowUtc());
        return new MissionPlan(_planning.CreatePlan(context), context.Constraints, Planner.IsLongInput(goal));
    }

    private Outcomes.MissionEvaluation FinalizeMission(Mission mission, MissionContext context, string? stopReason)
    {
        // Only a CRITICAL task failure fails the whole mission. A non-critical failure/skip
        // (e.g. one spec-ingestion section) degrades the mission to Partial but never aborts it.
        // v2.26.0 invariant: no task may reach finalization non-terminal. If one does, that is an
        // internal runtime defect — reported as such, and the mission fails CLOSED rather than
        // evaluating half-finished state as if it were finished.
        var nonTerminal = mission.Tasks
            .Where(t => t.Status is TaskStatus.Pending or TaskStatus.Ready or TaskStatus.Blocked or TaskStatus.Running)
            .ToList();
        foreach (var stuck in nonTerminal)
        {
            stuck.Result = $"INTERNAL RUNTIME DEFECT: task was still '{stuck.Status.Value()}' at mission finalization.";
            stuck.CancellationReason = stuck.Result;
            stuck.Status = TaskStatus.Failed;
            stuck.FailureReason = stuck.Result;
            stuck.FailureType = "internal_runtime_defect";
            stuck.FinishedAt = AnthillTime.NowUtc();
            Memory.LogEvent(mission.Id, "internal_runtime_defect", stuck.Result, stuck.Id, stuck.AssignedAnt,
                new() { ["invariant"] = "no_non_terminal_task_at_finalization" });
        }

        var criticalFailed = mission.Tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical);
        var degraded = mission.Tasks.Any(t => t.Status == TaskStatus.Skipped
                                              || (t.Status == TaskStatus.Failed && !t.Critical));
        mission.Status = criticalFailed ? MissionStatus.Failed : degraded ? MissionStatus.Partial : MissionStatus.Complete;

        // v2.26.0 pre-V3 hardening: the ONE evaluation. Computed exactly once, after every task is
        // terminal, PERSISTED before any learning/credit/completion consumer runs — so restored
        // state answers exactly what live state answered, and no consumer re-derives success.
        // v3.1.0: graded ONCE, through the one injected evaluator. Its inputs are the mission's
        // constraints and verification policy as resolved at intake, so the evaluation is
        // reproducible from the persisted record rather than dependent on what the statics happened
        // to say at the moment finalization ran.
        var evaluation = _evaluator.Evaluate(
            mission, context, stopReason, Memory.CountPatchProposalsForMission(mission.Id));
        // NB: persisted by RunMission AFTER the final SaveMission (INSERT OR REPLACE would erase
        // it here) and before anything publishes completion. In-process consumers below use this
        // same object, so they cannot disagree with what gets persisted.
        Memory.LogEvent(mission.Id, "mission_evaluated", evaluation.Explanation, metadata: new()
        {
            ["outcome_code"] = evaluation.OutcomeCode,
            ["verification_status"] = evaluation.VerificationStatus,
            ["deliverable_status"] = evaluation.DeliverableStatus,
            ["stop_reason"] = evaluation.StopReason,
            ["evaluator_version"] = evaluation.EvaluatorVersion,
        });
        if (evaluation.DeliverableStatus == Outcomes.MissionEvaluation.Deliverable.NotSatisfied)
            Memory.LogEvent(mission.Id, "objective_verification_failed",
                Outcomes.ObjectiveVerification.Explain(mission, context.Constraints,
                    Memory.CountPatchProposalsForMission(mission.Id)),
                metadata: new() { ["goal"] = TextUtil.Truncate(mission.Goal, 300) });

        // v3.1.0 (ADR-001): everything a finished mission teaches the colony — scoring, pheromone
        // reinforcement, skill credit, route registration — behind one interface. The Queen still
        // decides WHEN learning happens: after every task is terminal, after the ONE canonical
        // evaluation exists, and before completion is published anywhere.
        _learning.Record(mission, context, evaluation);
        // v3.1.0 (ADR-001): the three operator-facing accounts of a finished mission — raw best
        // output, full trace, and the plain-English answer — assembled behind one interface.
        _results.Assemble(mission, context);
        Memory.LogEvent(mission.Id, "best_output_selected", $"Best output task selected: {mission.BestOutputTaskId}",
            metadata: new() { ["best_output_task_id"] = mission.BestOutputTaskId });
        var eventType = mission.Status == MissionStatus.Complete ? "mission_completed" : mission.Status == MissionStatus.Partial ? "mission_partial" : "mission_failed";
        Memory.LogEvent(mission.Id, eventType, $"Mission finished with status: {mission.Status.Value()}", metadata: new()
        {
            ["success_score"] = mission.SuccessScore, ["task_count"] = mission.Tasks.Count,
            ["failed_tasks"] = mission.Tasks.Where(t => t.Status == TaskStatus.Failed).Select(t => t.Id).ToList(),
            ["skipped_tasks"] = mission.Tasks.Where(t => t.Status == TaskStatus.Skipped).Select(t => t.Id).ToList(),
            ["best_output_task_id"] = mission.BestOutputTaskId,
        });
        return evaluation;
    }


}
