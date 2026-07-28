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
public sealed partial class Queen : IDisposable
{
    public void Dispose() => Memory.Dispose();

    public SqliteMemory Memory { get; }
    public ModelRouter? Router { get; }
    public ToolRegistry Tools { get; }
    private readonly Planner _planner;
    private readonly PheromoneEngine _pheromones = new();
    private readonly PatchProposalParser _patchParser = new();
    private readonly object _executionLock = new();
    private readonly AdaptiveMissionController _adaptive = new();

    /// <summary>
    /// v2.21.0 Phase C: the skills registry, hydrated from the database rather than constructed
    /// empty. Before this the V2.12 evaluation model had no production instantiation at all — a
    /// skill could earn Certified and nothing anywhere would ever see it.
    /// </summary>
    private SkillRegistry Skills => _skills ??= Memory.LoadSkillRegistry();
    private SkillRegistry? _skills;
    private readonly Dictionary<string, BaseAnt> _ants;
    public string? LastMissionId { get; private set; }

    public Queen(SqliteMemory? memory = null)
    {
        AnthillRuntime.Initialize();
        Memory = memory ?? new SqliteMemory();
        Router = AnthillRuntime.EnableModelRouting ? new ModelRouter(Memory) : null;
        Tools = BuildToolRegistry();
        _planner = new Planner(AnthillRuntime.UseOllama, Router);
        _ants = new Dictionary<string, BaseAnt>
        {
            ["researcher"] = new ResearcherAnt(Memory, Tools, Router),
            ["web"] = new WebResearchAnt(Memory, Tools, Router),
            ["file"] = new FileAnt(Tools),
            ["coder"] = new CoderAnt(AnthillRuntime.UseOllama, Router),
            ["builder"] = new BuilderAnt(AnthillRuntime.UseOllama, Router),
            ["verifier"] = new VerifierAnt(AnthillRuntime.UseOllama, Router),
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
    }

    private ToolRegistry BuildToolRegistry()
    {
        var registry = new ToolRegistry(Memory);
        var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
        registry.Register(new SystemInfoTool());
        // Stage D-2: TesterAnt's ONLY execution surface — declared checks, never arbitrary commands.
        registry.Register(new RunAllowlistedCheckTool(AnthillRuntime.AllowedWorkspaceRoot));
        if (AnthillRuntime.EnableFileTools)
        {
            registry.Register(new DirectoryListTool(guard));
            registry.Register(new ReadTextFileTool(guard));
        }
        if (AnthillRuntime.EnableFileWriting)
            registry.Register(new WriteTextFileTool(guard));
        registry.Register(new WebSearchTool());
        registry.Register(new ShellCommandTool());
        registry.Register(new ApplyPatchTool(guard));
        return registry;
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
        // One token governs the whole mission: external cancel OR the deadline, whichever comes first.
        using var missionCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // v2.26.0 pre-V3 hardening: the mission DEADLINE cancels the token. Before this, timeout
        // was only a wall-clock check in the dispatch loop — in-flight model calls ran to their own
        // completion while the mission proceeded to finalization without them. MissionStopReason
        // checks the clock before the token, so a deadline cancellation still reports as timeout.
        missionCts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.MaxMissionSeconds));
        missionCts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.MaxMissionSeconds));
        using var modelScope = ModelCallScope.Enter(missionCts.Token);
        var mission = new Mission { Goal = goal, Status = MissionStatus.Running };
        LastMissionId = mission.Id;

        // Persist the mission row before any LogEvent calls so FK constraints on events(mission_id) are satisfied.
        Memory.SaveMission(mission);
        onMissionCreated?.Invoke(mission.Id);

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

        var memoryContext =
            $"Recent Memory:\n{Memory.FormatRecentMemory(AnthillRuntime.RecentMemoryLimit, AnthillRuntime.MemoryResultChars)}\n\n" +
            $"Relevant Memory:\n{Memory.FormatRelevantMemory(goal, AnthillRuntime.RelevantMemoryLimit, AnthillRuntime.MemoryResultChars)}";
        mission.Tasks = _planner.CreateTasks(goal, memoryContext, Tools.DescribeTools(), Memory.FormatPheromoneContext(8), SkillPlanningContext.Format(Skills));

        var constraints = MissionConstraints.Parse(goal);
        foreach (var task in mission.Tasks)
        {
            if (task.TaskType == "general") task.TaskType = TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description);
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                task.AssignedWorker = AntRegistry.DefaultWorkerFor(task.AssignedAnt, task.TaskType, $"{goal} {task.Title} {task.Description}")?.WorkerId;
            var selection = AntRegistry.ValidateTask(task, constraints);
            if (!selection.Allowed)
            {
                task.Status = TaskStatus.Failed;
                task.FailureType = "ant_permission_denied";
                task.FailureReason = selection.Reason;
                task.Result = $"Task rejected by ant registry: {selection.Reason}";
            }
        }
        // Spec-ingestion plans already carry explicit section→synthesis→verify wiring and
        // non-critical section flags; auto-wiring would only re-derive the same edges.
        if (AnthillRuntime.EnableAutoDependencyWiring && !isSpecIngestion) AutoWireDependencies(mission);

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
            ["parallel_execution"] = AnthillRuntime.EnableParallelExecution,
            ["max_parallel_workers"] = AnthillRuntime.MaxParallelWorkers,
            ["auto_dependency_wiring"] = AnthillRuntime.EnableAutoDependencyWiring,
        });
        Console.WriteLine($"Mission ID: {mission.Id}");
        Console.WriteLine($"Created {mission.Tasks.Count} tasks. Parallel execution: {(AnthillRuntime.EnableParallelExecution ? "ON" : "OFF")}\n");

        // Persist the planned DAG before execution so /graph (and the live colony canvas) can see
        // the mission's tasks while they run — not only after the mission finishes.
        Memory.SaveMission(mission);

        // The executors return WHY they stopped dispatching (mission_timeout / mission_cancelled), or
        // null if the plan ran to its natural end — the authoritative signal for the outcome below.
        var stopReason = AnthillRuntime.EnableParallelExecution
            ? ExecuteTasksParallel(mission, missionStartedAt, missionCts.Token)
            : ExecuteTasksSequential(mission, missionStartedAt, missionCts.Token);

        var evaluation = FinalizeMission(mission, stopReason);
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
        return ComposeCliResult(mission);
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

    private static void AutoWireDependencies(Mission mission)
    {
        var researcherFileIds = new List<string>();
        var preBuilderIds = new List<string>();
        var builderIds = new List<string>();
        foreach (var task in mission.Tasks)
        {
            if (task.DependsOn.Count > 0) { /* respect explicit deps */ }
            else if (task.AssignedAnt is "researcher" or "web" or "file") { /* sources have no upstream deps */ }
            else if (task.AssignedAnt == "coder") task.DependsOn = new List<string>(researcherFileIds);
            else if (task.AssignedAnt == "builder") task.DependsOn = new List<string>(preBuilderIds);
            else if (task.AssignedAnt == "verifier") task.DependsOn = preBuilderIds.Concat(builderIds).ToList();

            if (task.AssignedAnt is "researcher" or "web" or "file") { researcherFileIds.Add(task.Id); preBuilderIds.Add(task.Id); }
            else if (task.AssignedAnt == "coder") preBuilderIds.Add(task.Id);
            else if (task.AssignedAnt == "builder") builderIds.Add(task.Id);
        }
    }

    /// <summary>
    /// v1.8.18 Mission Composer plan preview: builds the task plan for a goal exactly as
    /// <see cref="RunMission(string)"/> would (planner → task-type inference → auto-dependency
    /// wiring), but WITHOUT creating, persisting, executing, or logging a mission. Powers
    /// <c>POST /missions/plan</c> so an operator can review the plan (and see the effect of
    /// verification-only / no-patch constraints) before approving dispatch. Read-only: the only
    /// external effect is the planner's model call, exactly as a real dispatch would make.
    /// </summary>
    public List<Task> PlanPreview(string goal)
    {
        var memoryContext =
            $"Recent Memory:\n{Memory.FormatRecentMemory(AnthillRuntime.RecentMemoryLimit, AnthillRuntime.MemoryResultChars)}\n\n" +
            $"Relevant Memory:\n{Memory.FormatRelevantMemory(goal, AnthillRuntime.RelevantMemoryLimit, AnthillRuntime.MemoryResultChars)}";
        var tasks = _planner.CreateTasks(goal, memoryContext, Tools.DescribeTools(), Memory.FormatPheromoneContext(8), SkillPlanningContext.Format(Skills));
        foreach (var task in tasks)
        {
            if (task.TaskType == "general") task.TaskType = TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description);
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                task.AssignedWorker = AntRegistry.DefaultWorkerFor(task.AssignedAnt, task.TaskType, $"{goal} {task.Title} {task.Description}")?.WorkerId;
        }
        // Mirror RunMission's auto-wiring so the preview shows the same dependency edges the
        // scheduler would actually run. Spec-ingestion plans carry their own explicit wiring.
        if (AnthillRuntime.EnableAutoDependencyWiring && !Planner.IsLongInput(goal))
        {
            var transient = new Mission { Goal = goal, Tasks = tasks };
            AutoWireDependencies(transient);
            tasks = transient.Tasks;
        }
        return tasks;
    }

    private string? ExecuteTasksSequential(Mission mission, DateTime missionStartedAt, CancellationToken missionToken)
    {
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        LogSchedulerIssues(mission, scheduler.Prepare());
        LogSchedulerTransitions(mission, scheduler);
        var taskIndex = mission.Tasks.Select((t, i) => (t.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);

        while (!scheduler.IsFinished())
        {
            if (MissionStopReason(missionStartedAt, missionToken) is { } stop)
            {
                scheduler.SkipRemaining(stop.Message, stop.ReasonType);
                LogSchedulerTransitions(mission, scheduler);
                return stop.ReasonType; // timed out / cancelled — the mission's "why it stopped"
            }
            var task = scheduler.NextReadyTask();
            LogSchedulerTransitions(mission, scheduler);
            if (task is not null)
            {
                var before = AdaptiveMissionController.Fingerprint(mission);
                RunSingleTask(task, mission, taskIndex.GetValueOrDefault(task.Id), mission.Tasks.Count, scheduler);
                LogSchedulerTransitions(mission, scheduler);
                // Assess after every task: this loop's "wave" is one task.
                if (ApplyAdaptiveDecision(mission, scheduler, before)) return "adaptive_stop";
                continue;
            }
            // Nothing ready. Before declaring dead dependencies, let the controller decide whether
            // a bounded delta plan or repair can supply what is missing.
            if (ApplyAdaptiveDecision(mission, scheduler, previousFingerprint: null)) return "adaptive_stop";
            if (scheduler.NextReadyTask() is not null) continue;   // the controller admitted work
            var blocked = mission.Tasks.Where(t => t.Status == TaskStatus.Blocked).ToList();
            if (blocked.Count > 0)
            {
                foreach (var b in blocked)
                    scheduler.MarkSkipped(b.Id, b.BlockedReason ?? "Task skipped because scheduler could not make progress.", "dead_dependency");
                LogSchedulerTransitions(mission, scheduler);
                return null;
            }
            break;
        }
        return null;
    }

    private string? ExecuteTasksParallel(Mission mission, DateTime missionStartedAt, CancellationToken missionToken)
    {
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        LogSchedulerIssues(mission, scheduler.Prepare());
        LogSchedulerTransitions(mission, scheduler);
        var running = new Dictionary<System.Threading.Tasks.Task, Task>();
        var taskIndex = mission.Tasks.Select((t, i) => (t.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var lastSweep = Stopwatch.StartNew();
        string? waveFingerprint = null;   // null on the first wave: nothing to compare against yet

        while (true)
        {
            if (MissionStopReason(missionStartedAt, missionToken) is { } stop)
            {
                lock (_executionLock)
                {
                    scheduler.SkipRemaining(stop.Message, stop.ReasonType);
                    LogSchedulerTransitions(mission, scheduler);
                }
                // v2.26.0: a terminal mission must never contain a running task. Cancellation has
                // already reached every task token (the mission token is linked into each); this
                // waits a bounded grace period for in-flight work to observe it, then marks any
                // non-terminating task with its cancellation reason. Nothing returns before every
                // task is terminal.
                DrainRunningTasks(mission, scheduler, running, stop.ReasonType);
                return stop.ReasonType;
            }

            if (lastSweep.Elapsed.TotalSeconds >= AnthillRuntime.TaskTimeoutSweepSeconds)
            {
                lastSweep.Restart();
                lock (_executionLock)
                    foreach (var runningTask in running.Values.ToList())
                        if (runningTask.Status == TaskStatus.Running && runningTask.StartedAt is { } startedAt &&
                            (AnthillTime.NowUtc() - startedAt).TotalSeconds > AnthillRuntime.MaxTaskSeconds)
                            MarkTaskTimeout(runningTask, mission, scheduler);
            }

            List<Task> toSubmit;
            lock (_executionLock)
            {
                scheduler.Evaluate();
                LogSchedulerTransitions(mission, scheduler);
                if (scheduler.IsFinished() && running.Count == 0) return null;
                var runningIds = running.Values.Select(t => t.Id).ToHashSet();
                var eligible = scheduler.ReadyTasks().Where(t => !runningIds.Contains(t.Id)).ToList();
                LogSchedulerTransitions(mission, scheduler);
                var openSlots = Math.Max(0, AnthillRuntime.MaxParallelWorkers - running.Count);
                toSubmit = eligible.Take(openSlots).ToList();
            }

            foreach (var task in toSubmit)
            {
                var captured = task;
                var future = System.Threading.Tasks.Task.Run(() =>
                    RunSingleTask(captured, mission, taskIndex.GetValueOrDefault(captured.Id), mission.Tasks.Count, scheduler));
                running[future] = task;
            }

            if (running.Count == 0)
            {
                lock (_executionLock)
                {
                    var blocked = mission.Tasks.Where(t => t.Status == TaskStatus.Blocked).ToList();
                    if (blocked.Count > 0 && scheduler.ReadyTasks().Count == 0)
                    {
                        foreach (var b in blocked)
                            scheduler.MarkSkipped(b.Id, b.BlockedReason ?? "Task skipped because scheduler could not make progress.", "dead_dependency");
                        LogSchedulerTransitions(mission, scheduler);
                        return null;
                    }
                }
                Thread.Sleep(50);
                continue;
            }

            var done = running.Keys.Where(f => f.IsCompleted).ToList();
            if (done.Count == 0) { Thread.Sleep(50); continue; }

            foreach (var future in done)
            {
                var task = running[future];
                running.Remove(future);
                if (future.IsFaulted)
                {
                    var error = future.Exception?.GetBaseException();
                    lock (_executionLock)
                    {
                        if (task.Status == TaskStatus.Running)
                        {
                            task.Result = $"Task failed with unhandled parallel error: {error?.Message}";
                            task.FinishedAt = AnthillTime.NowUtc();
                            if (task.StartedAt is { } st) task.ElapsedSeconds = Math.Round((task.FinishedAt.Value - st).TotalSeconds, 3);
                            scheduler.MarkFailed(task.Id, task.Result, "parallel_worker_error", false, task.FinishedAt, task.ElapsedSeconds);
                            FinalizeTaskResult(mission, task);
                            Memory.LogEvent(mission.Id, "task_failed", task.Result, task.Id, task.AssignedAnt,
                                new() { ["task_type"] = task.TaskType, ["error"] = error?.Message, ["elapsed_seconds"] = task.ElapsedSeconds });
                        }
                    }
                }
            }
            lock (_executionLock)
            {
                scheduler.Evaluate();
                LogSchedulerTransitions(mission, scheduler);
                // A "wave" here is the batch of futures that just completed. Assess once per wave
                // rather than per task, so parallel completions cannot each trigger their own
                // replan for the same unmet criterion.
                if (running.Count == 0 && ApplyAdaptiveDecision(mission, scheduler, waveFingerprint))
                    return "adaptive_stop";
                waveFingerprint = AdaptiveMissionController.Fingerprint(mission);
            }
        }
    }

    private void LogSchedulerIssues(Mission mission, List<TaskGraphIssue> issues)
    {
        foreach (var issue in issues)
            Memory.LogEvent(mission.Id, "task_graph_validation_issue", issue.Message, issue.TaskId, "scheduler",
                new() { ["code"] = issue.Code, ["dependency_id"] = issue.DependencyId });
    }

    private void LogSchedulerTransitions(Mission mission, TaskScheduler scheduler)
    {
        foreach (var transition in scheduler.ConsumeTransitions())
        {
            var task = mission.Tasks.FirstOrDefault(t => t.Id == transition.TaskId);
            if (task is null) continue;
            var metadata = new Dictionary<string, object?>
            {
                ["from_status"] = transition.FromStatus, ["to_status"] = transition.ToStatus, ["reason_type"] = transition.ReasonType,
                ["task_type"] = task.TaskType, ["attempt_count"] = task.AttemptCount, ["max_attempts"] = task.MaxAttempts,
            };
            if (transition.ToStatus == TaskStatus.Ready.Value())
                Memory.LogEvent(mission.Id, "task_ready", $"Task ready: {task.Title}", task.Id, "scheduler", metadata);
            else if (transition.ToStatus == TaskStatus.Blocked.Value())
                Memory.LogEvent(mission.Id, "task_blocked", transition.Reason ?? $"Task blocked: {task.Title}", task.Id, "scheduler", metadata);
            else if (transition.ToStatus == TaskStatus.Skipped.Value())
            {
                task.Result ??= transition.Reason ?? "Task skipped by scheduler.";
                task.SkippedReason ??= transition.Reason;
                FinalizeTaskResult(mission, task);
                var depSkip = transition.ReasonType is "failed_dependency" or "missing_dependency" or "dead_dependency";
                Memory.LogEvent(mission.Id, depSkip ? "task_skipped_dependency" : "task_skipped", task.Result, task.Id, task.AssignedAnt, metadata);
                Console.WriteLine(task.Result);
            }
        }
    }

    private static bool MissionTimedOut(DateTime missionStartedAt) =>
        (AnthillTime.NowUtc() - missionStartedAt).TotalSeconds > AnthillRuntime.MaxMissionSeconds;

    /// <summary>
    /// Reports why the mission must stop dispatching, or null to continue. The deadline is checked
    /// first so it is reported as <c>mission_timeout</c>; an external cancel (job cancelled) reached
    /// before the deadline is reported as <c>mission_cancelled</c>. Both leave the same cancelled
    /// token that already aborted any in-flight model call.
    /// </summary>
    private static (string Message, string ReasonType)? MissionStopReason(DateTime missionStartedAt, CancellationToken missionToken)
    {
        if (MissionTimedOut(missionStartedAt))
            return ("Task skipped because mission timed out.", "mission_timeout");
        if (missionToken.IsCancellationRequested)
            return ("Task skipped because the mission was cancelled.", "mission_cancelled");
        return null;
    }

    private void RunSingleTask(Task task, Mission mission, int index, int total, TaskScheduler? scheduler)
    {
        var taskStartedAt = AnthillTime.NowUtc();
        AntRuntimeSelection runtimeSelection;
        try
        {
            runtimeSelection = AntRuntime.Resolve(task, MissionConstraints.Parse(mission.Goal));
        }
        catch (Exception error)
        {
            lock (_executionLock)
            {
                task.Result = $"Task rejected by worker runtime: {error.Message}";
                task.FinishedAt = AnthillTime.NowUtc();
                task.ElapsedSeconds = Math.Round((task.FinishedAt.Value - taskStartedAt).TotalSeconds, 3);
                if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "worker_runtime_denied", false, task.FinishedAt, task.ElapsedSeconds);
                else { task.Status = TaskStatus.Failed; task.FailedAt = task.FinishedAt; task.FailureReason = task.Result; task.FailureType = "worker_runtime_denied"; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "worker_runtime_denied", task.Result, task.Id, task.AssignedWorker ?? task.AssignedAnt,
                    new() { ["assigned_ant"] = task.AssignedAnt, ["assigned_worker"] = task.AssignedWorker, ["error"] = error.Message });
                Console.WriteLine(task.Result);
            }
            return;
        }
        Task taskSnapshot;
        Mission missionSnapshot;
        lock (_executionLock)
        {
            if (scheduler is not null)
            {
                if (!scheduler.MarkRunning(task.Id)) return;
                taskStartedAt = task.StartedAt ?? taskStartedAt;
            }
            else
            {
                if (task.Status is not (TaskStatus.Pending or TaskStatus.Ready)) return;
                task.Status = TaskStatus.Running;
                task.AttemptCount += 1;
                task.StartedAt = taskStartedAt;
                task.FinishedAt = null;
                task.ElapsedSeconds = null;
            }
            var runtimeMetadata = AntRuntime.Metadata(runtimeSelection);
            Console.WriteLine($"Task {index}/{total} -> {runtimeSelection.RuntimeNodeId} worker via {task.AssignedAnt} ant: {task.Title}");
            Memory.SaveTask(mission.Id, task); // live status: the canvas/graph sees "running" now
            Memory.LogEvent(mission.Id, "worker_permission_audited", $"Worker permission boundary audited: {runtimeSelection.RuntimeNodeId}", task.Id, runtimeSelection.RuntimeNodeId,
                runtimeMetadata);
            Memory.LogEvent(mission.Id, "task_started", $"Task started: {task.Title}", task.Id, runtimeSelection.RuntimeNodeId, MergeMetadata(runtimeMetadata, new()
            {
                ["task_type"] = task.TaskType, ["index"] = index, ["parallel"] = AnthillRuntime.EnableParallelExecution,
                ["assigned_worker"] = task.AssignedWorker,
                ["max_task_seconds"] = AnthillRuntime.MaxTaskSeconds, ["attempt_count"] = task.AttemptCount,
                ["max_attempts"] = task.MaxAttempts, ["snapshot_context"] = true,
            }));
            taskSnapshot = AntRuntime.PrepareWorkerTaskSnapshot(task, runtimeSelection);
            missionSnapshot = mission.DeepCopy();
        }

        RecordAgentMessage(mission.Id, task.Id, "queen", runtimeSelection.RuntimeNodeId, "task_dispatch",
            $"Dispatch task: {task.Title}\nType: {task.TaskType}\nDescription: {TextUtil.Truncate(task.Description, 900, "...[description truncated]")}",
            MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
            {
                ["schema"] = AnthillRuntime.AgentMessageVersion, ["context_strategy"] = "locked_mission_snapshot+compact_context_packets",
                ["assigned_worker"] = task.AssignedWorker,
                ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds, ["parallel_execution"] = AnthillRuntime.EnableParallelExecution,
            }));

        if (!_ants.TryGetValue(runtimeSelection.ExecutorRoleId, out var ant))
        {
            lock (_executionLock)
            {
                task.Result = $"No ant found for role: {runtimeSelection.ExecutorRoleId}";
                task.FinishedAt = AnthillTime.NowUtc();
                task.ElapsedSeconds = Math.Round((task.FinishedAt.Value - taskStartedAt).TotalSeconds, 3);
                if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "missing_ant", false, task.FinishedAt, task.ElapsedSeconds);
                else { task.Status = TaskStatus.Failed; task.FailedAt = task.FinishedAt; task.FailureReason = task.Result; task.FailureType = "missing_ant"; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "task_failed", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
                    MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["reason"] = "missing_ant", ["elapsed_seconds"] = task.ElapsedSeconds }));
                Console.WriteLine(task.Result);
            }
            return;
        }

        try
        {
            string? result;
            AntExecutionResult execution;
            using (var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ModelCallScope.Current))
            {
                // Per-task deadline, layered under the mission's (ModelCallScope.Current is the mission
                // token here). A single task can no longer consume the whole mission budget: its model
                // calls abort at MaxTaskSeconds instead of only being flagged as over-limit after they
                // return. The linked source means a mission cancel/timeout still propagates through too.
                taskCts.CancelAfter(TimeSpan.FromSeconds(AnthillRuntime.MaxTaskSeconds));
                using var taskScope = ModelCallScope.Enter(taskCts.Token);
                // v2.19.0: the STRUCTURED contract. The ant declares its outcome; the orchestrator
                // no longer infers one from the absence of an exception. The narrative is kept for
                // the operator but carries no control meaning.
                execution = ant.Execute(taskSnapshot, missionSnapshot);
                result = execution.Narrative ?? execution.Summary;
            }
            var finishedAt = AnthillTime.NowUtc();
            var elapsed = Math.Round((finishedAt - taskStartedAt).TotalSeconds, 3);
            lock (_executionLock)
            {
                if (task.Status != TaskStatus.Running)
                {
                    Memory.LogEvent(mission.Id, "task_late_result_ignored",
                        $"Late result ignored for task already in terminal/non-running state: {task.Status.Value()}", task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["elapsed_seconds"] = elapsed, ["result_preview"] = TextUtil.Truncate(result ?? "", 500) }));
                    return;
                }
                task.Result = result;
                task.FinishedAt = finishedAt;
                task.ElapsedSeconds = elapsed;
                if (elapsed > AnthillRuntime.MaxTaskSeconds)
                {
                    task.Result = $"Task exceeded max runtime of {AnthillRuntime.MaxTaskSeconds} seconds. Elapsed: {elapsed} seconds.";
                    if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "timeout", false, finishedAt, elapsed);
                    else { task.Status = TaskStatus.Failed; task.FailedAt = finishedAt; task.FailureReason = task.Result; task.FailureType = "timeout"; }
                    FinalizeTaskResult(mission, task);
                    Memory.LogEvent(mission.Id, "task_failed_timeout", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["max_task_seconds"] = AnthillRuntime.MaxTaskSeconds }));
                    Console.WriteLine(task.Result);
                    return;
                }
                // Everything the ant reported is persisted BEFORE the status decision, so evidence
                // survives even when the task fails. Handoffs are recorded here and, on the
                // completion path below, admitted through HandoffGate as real follow-up tasks.
                PersistExecutionRecord(mission, task, runtimeSelection, execution, elapsed);

                var decision = TaskOutcomeMapper.Map(execution);
                if (decision.Action != TaskOutcomeAction.Complete)
                {
                    ApplyNonCompletingOutcome(mission, task, runtimeSelection, execution, decision, finishedAt, elapsed, scheduler);
                    return;
                }

                if (decision.Warnings.Count > 0)
                    Memory.LogEvent(mission.Id, "task_completed_with_warnings",
                        $"Task completed with {decision.Warnings.Count} warning(s): {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["warnings"] = decision.Warnings }));

                if (scheduler is not null) scheduler.MarkComplete(task.Id, result, finishedAt, elapsed);
                else { task.Status = TaskStatus.Complete; task.CompletedAt = finishedAt; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "task_completed", $"Task completed: {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
                    MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["status_code"] = execution.StatusCode, ["result_preview"] = TextUtil.Truncate(task.Result ?? "", 500) }));
                if (task.AssignedAnt == "coder") ProcessPatchProposals(mission, task);
                if (task.AssignedAnt == "archivist") IngestMemoryCandidates(mission, task, execution);
                IngestHandoffs(mission, task, execution, runtimeSelection, scheduler);
                RecordAgentMessage(mission.Id, task.Id, runtimeSelection.RuntimeNodeId, "queen", "task_result",
                    task.ResultSummary ?? TextUtil.CreateResultSummary(task.Result, AnthillRuntime.MaxResultSummaryChars),
                    MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["schema"] = AnthillRuntime.AgentMessageVersion, ["status"] = task.Status.Value(), ["result_chars"] = task.ResultChars, ["estimated_tokens"] = task.EstimatedTokens, ["elapsed_seconds"] = task.ElapsedSeconds }));
                Console.WriteLine($"Task complete: {task.Title} ({elapsed}s)");
            }
        }
        catch (Exception error)
        {
            var finishedAt = AnthillTime.NowUtc();
            var elapsed = Math.Round((finishedAt - taskStartedAt).TotalSeconds, 3);
            lock (_executionLock)
            {
                if (task.Status != TaskStatus.Running)
                {
                    Memory.LogEvent(mission.Id, "task_late_error_ignored",
                        $"Late error ignored for task already in terminal/non-running state: {task.Status.Value()}", task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["elapsed_seconds"] = elapsed, ["error"] = error.Message }));
                    return;
                }
                task.Result = $"Task failed with error: {error.Message}";
                task.FinishedAt = finishedAt;
                task.ElapsedSeconds = elapsed;
                var terminalFailure = true;
                if (scheduler is not null)
                    terminalFailure = scheduler.MarkFailed(task.Id, task.Result, "execution_error", true, finishedAt, elapsed);
                else { task.Status = TaskStatus.Failed; task.FailedAt = finishedAt; task.FailureReason = task.Result; task.FailureType = "execution_error"; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, terminalFailure ? "task_failed" : "task_retry_scheduled", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
                    MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["task_type"] = task.TaskType, ["error"] = error.Message, ["elapsed_seconds"] = elapsed, ["attempt_count"] = task.AttemptCount, ["max_attempts"] = task.MaxAttempts }));
                RecordAgentMessage(mission.Id, task.Id, runtimeSelection.RuntimeNodeId, "queen", terminalFailure ? "task_error" : "task_retry",
                    task.Result, MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["schema"] = AnthillRuntime.AgentMessageVersion, ["error"] = error.Message, ["elapsed_seconds"] = elapsed }));
                Console.WriteLine(task.Result);
            }
        }
    }

    /// <summary>
    /// v2.26.0: bounded shutdown for the parallel executor. The mission token is already
    /// cancelled (deadline or operator); in-flight tasks get MissionDrainGraceSeconds to observe
    /// it and record their own terminal state. Whatever is still Running after the grace period is
    /// marked cancelled/timed-out HERE, with a persisted cancellation reason — so the mission
    /// reaches finalization with every task terminal, and a straggler's late write is ignored by
    /// the existing late-result guard.
    /// </summary>
    private void DrainRunningTasks(Mission mission, TaskScheduler scheduler,
        Dictionary<System.Threading.Tasks.Task, Task> running, string reasonType)
    {
        if (running.Count == 0) return;
        try
        {
            System.Threading.Tasks.Task.WaitAll(
                running.Keys.ToArray(), TimeSpan.FromSeconds(AnthillRuntime.MissionDrainGraceSeconds));
        }
        catch { /* task-level failures were already handled inside RunSingleTask */ }

        lock (_executionLock)
        {
            foreach (var task in running.Values.Where(t => t.Status == TaskStatus.Running).ToList())
            {
                var now = AnthillTime.NowUtc();
                var cancelled = reasonType == "mission_cancelled";
                task.CancellationReason = cancelled
                    ? "cancelled: mission was cancelled while this task was still running"
                    : $"timed_out: mission stopped ({reasonType}) while this task was still running";
                task.Result = task.CancellationReason;
                task.FinishedAt = now;
                if (task.StartedAt is { } st) task.ElapsedSeconds = Math.Round((now - st).TotalSeconds, 3);
                scheduler.MarkFailed(task.Id, task.CancellationReason,
                    cancelled ? "cancelled" : "timeout", false, now, task.ElapsedSeconds);
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "task_drained", task.CancellationReason, task.Id, task.AssignedAnt,
                    new() { ["reason_type"] = reasonType, ["grace_seconds"] = AnthillRuntime.MissionDrainGraceSeconds });
            }
            LogSchedulerTransitions(mission, scheduler);
        }
    }

    private void MarkTaskTimeout(Task task, Mission mission, TaskScheduler? scheduler)
    {
        var now = AnthillTime.NowUtc();
        task.FinishedAt = now;
        if (task.StartedAt is { } st) task.ElapsedSeconds = Math.Round((now - st).TotalSeconds, 3);
        task.Result = $"Task exceeded max runtime of {AnthillRuntime.MaxTaskSeconds} seconds.";
        if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "timeout", false, now, task.ElapsedSeconds);
        else { task.Status = TaskStatus.Failed; task.FailedAt = now; task.FailureReason = task.Result; task.FailureType = "timeout"; }
        FinalizeTaskResult(mission, task);
        Memory.LogEvent(mission.Id, "task_failed_timeout", task.Result, task.Id, task.AssignedAnt,
            new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = task.ElapsedSeconds, ["max_task_seconds"] = AnthillRuntime.MaxTaskSeconds });
        Console.WriteLine(task.Result);
    }

    private void FinalizeTaskResult(Mission mission, Task task)
    {
        task.ResultChars = (task.Result ?? "").Length;
        task.EstimatedTokens = TextUtil.EstimateTokenCount(task.Result);
        task.ResultSummary = TextUtil.CreateResultSummary(task.Result, AnthillRuntime.MaxResultSummaryChars);
        Memory.SaveTask(mission.Id, task); // live status: terminal state visible to /graph immediately
        Memory.SaveTaskResultSummary(mission.Id, task);
        Memory.LogMessageMetric(mission.Id, task.Id, task.AssignedAnt, "task_result",
            (task.Description ?? "").Length, task.ResultChars,
            new() { ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["summary_chars"] = (task.ResultSummary ?? "").Length, ["context_packets_enabled"] = AnthillRuntime.EnableContextPackets });
        if (!string.IsNullOrWhiteSpace(task.AssignedWorker))
            Memory.LogMessageMetric(mission.Id, task.Id, task.AssignedWorker, "worker_task_result",
                (task.Description ?? "").Length, task.ResultChars,
                new() { ["assigned_ant"] = task.AssignedAnt, ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["summary_chars"] = (task.ResultSummary ?? "").Length });
        Memory.LogEvent(mission.Id, "task_result_summarized", $"Task result summarized for compact downstream context: {task.Title}", task.Id, task.AssignedAnt,
            new() { ["result_chars"] = task.ResultChars, ["summary_chars"] = (task.ResultSummary ?? "").Length, ["estimated_tokens"] = task.EstimatedTokens });
    }

    private void ProcessPatchProposals(Mission mission, Task task)
    {
        if (string.IsNullOrEmpty(task.Result)) return;
        try
        {
            var patchSet = _patchParser.Parse(task.Result, mission.Id, task.Id);
            Memory.SavePatchSet(patchSet);
            Memory.LogEvent(mission.Id, "patch_set_created", $"Patch set created with {patchSet.Proposals.Count} proposal(s).", task.Id, task.AssignedAnt,
                new() { ["patch_set_id"] = patchSet.Id, ["proposal_count"] = patchSet.Proposals.Count, ["summary"] = patchSet.Summary, ["saved"] = true });
            if (patchSet.Proposals.Count == 0)
            {
                Memory.LogEvent(mission.Id, "patch_set_empty", "CoderAnt returned a valid patch set with no proposals.", task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["summary"] = patchSet.Summary });
                Memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", true, 0.005,
                    new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["proposal_count"] = 0, ["reason"] = "valid_empty_patch_set" });
                return;
            }
            foreach (var proposal in patchSet.Proposals)
            {
                Memory.LogEvent(mission.Id, "patch_proposal_created", $"Patch proposal created for {proposal.FilePath}", task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value(), ["requires_approval"] = proposal.RequiresApproval, ["status"] = proposal.Status.Value() });
                // Autonomous objectives re-propose the same change run after run while the first
                // request sits unreviewed — don't stack identical approval requests.
                if (Memory.HasDuplicatePendingApproval(proposal))
                {
                    Memory.LogEvent(mission.Id, "approval_request_deduped",
                        $"Identical change for {proposal.FilePath} is already awaiting approval — no duplicate request created.", task.Id, "queen",
                        new() { ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value() });
                    continue;
                }
                var approval = CreatePatchApprovalRequest(mission, task, patchSet, proposal);
                Memory.SaveApprovalRequest(approval);
                Memory.LogEvent(mission.Id, "approval_request_created", $"Approval request created for patch proposal: {proposal.FilePath}", task.Id, "queen",
                    new() { ["approval_request_id"] = approval.Id, ["target_id"] = approval.TargetId, ["action_type"] = approval.ActionType.Value(), ["approval_status"] = approval.Status.Value() });
            }
            Memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", true, 0.03,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["proposal_count"] = patchSet.Proposals.Count, ["approval_requests_created"] = patchSet.Proposals.Count });
            Memory.UpdatePheromoneTrail("capability:approval_gate", "capability", true, 0.02,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["approval_requests_created"] = patchSet.Proposals.Count });
        }
        catch (Exception error)
        {
            Memory.LogEvent(mission.Id, "patch_proposal_parse_failed", $"Patch proposal parsing failed: {error.Message}", task.Id, task.AssignedAnt,
                new() { ["error"] = error.Message, ["raw_preview"] = TextUtil.Truncate(task.Result, 1000) });
            Memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", false, -0.03,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["error"] = error.Message });
        }
    }

    private static ApprovalRequest CreatePatchApprovalRequest(Mission mission, Task task, PatchSet patchSet, PatchProposal proposal) => new()
    {
        MissionId = mission.Id, TaskId = task.Id, ActionType = ApprovalActionType.PatchProposal, TargetId = proposal.Id,
        Title = $"Approve patch proposal for {proposal.FilePath}",
        Description = $"Patch proposal requires approval before application.\nFile: {proposal.FilePath}\nChange Type: {proposal.ChangeType.Value()}\n" +
                      $"Reason: {proposal.Reason}\nRisk: {proposal.Risk}\n\nApproval alone does not apply the patch. Use /apply <approval_id> after approval and after enabling write gates.",
        Metadata = new() { ["patch_set_id"] = patchSet.Id, ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value(), ["requires_approval"] = proposal.RequiresApproval, ["patch_application_enabled"] = AnthillRuntime.EnablePatchApplication, ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting },
    };

    private Outcomes.MissionEvaluation FinalizeMission(Mission mission, string? stopReason)
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
        var evaluation = Outcomes.MissionEvaluator.Evaluate(
            mission, stopReason, Memory.CountPatchProposalsForMission(mission.Id));
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
                Outcomes.ObjectiveVerification.Explain(mission, Memory.CountPatchProposalsForMission(mission.Id)),
                metadata: new() { ["goal"] = TextUtil.Truncate(mission.Goal, 300) });

        mission.SuccessScore = _pheromones.ScoreMission(mission);
        Memory.LogEvent(mission.Id, "pheromone_scored", $"Mission pheromone score calculated: {mission.SuccessScore}",
            metadata: new() { ["success_score"] = mission.SuccessScore, ["mission_status"] = mission.Status.Value() });
        Memory.UpdateMissionPheromones(mission, evaluation.OutcomeCode);
        CreditSkills(mission, evaluation);
        RegisterProceduralRoutes(mission, evaluation);
        mission.BestOutputTaskId = SelectBestOutputTaskId(mission);
        mission.UserResult = ComposeUserResult(mission);
        mission.DebugResult = ComposeDebugResult(mission);
        // v2.16.0: FinalResult is what the operator reads — a plain-English answer when synthesis
        // is on and the raw output warrants it. UserResult (raw best task) and DebugResult (full
        // trace) are untouched, so the detail behind the answer is always still there.
        mission.FinalResult = ComposeFinalAnswer(mission);
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

    /// <summary>
    /// v2.26.0: procedural route registration moved HERE from the per-task archivist path — where
    /// it resolved the mission outcome while status was still Running, always got a negative, and
    /// therefore never registered anything. (v2.23's feature was structurally dead in production;
    /// its tests passed because they called Register directly with a final outcome.) Candidates
    /// are rebuilt from the durable memory_candidate events the archivist path records.
    /// </summary>
    private void RegisterProceduralRoutes(Mission mission, Outcomes.MissionEvaluation evaluation)
    {
        var candidates = Memory.GetRecentEvents(200, eventType: Outcomes.MemoryCandidateIngest.EventType, missionId: mission.Id)
            .Select(row => Json.TryParseObject(row.GetValueOrDefault("metadata_json")?.ToString()))
            .Select(meta => new Outcomes.MemoryCandidateIngest.Candidate(
                MemoryClass: meta.GetValueOrDefault("memory_class")?.ToString() ?? "",
                Summary: meta.GetValueOrDefault("summary")?.ToString() ?? "",
                SourceMission: meta.GetValueOrDefault("source_mission")?.ToString() ?? "",
                Outcome: meta.GetValueOrDefault("outcome")?.ToString() ?? "",
                Confidence: meta.GetValueOrDefault("confidence")?.ToString() ?? "",
                AutoPromote: meta.GetValueOrDefault("auto_promote") is bool b && b))
            .Where(c => c.MemoryClass.Length > 0)
            .ToList();
        if (candidates.Count == 0) return;

        var routes = ProceduralCandidatePromotion.Register(Skills, candidates, evaluation.OutcomeCode);
        if (routes.Count == 0) return;
        foreach (var routeId in routes)
            if (Skills.Get(routeId) is { } registered) Memory.SaveSkill(registered);
        foreach (var id in routes)
            Memory.LogEvent(mission.Id, "skill_candidate_registered",
                $"Observed route registered as a skill candidate (usable for nothing until verified): {id}",
                antName: "archivist",
                metadata: new() { ["skill_id"] = id, ["mission_outcome"] = evaluation.OutcomeCode, ["status"] = "Candidate" });
    }

    /// <summary>
    /// v2.19.0: persist everything the ant reported, regardless of outcome.
    ///
    /// Artifacts, evidence, warnings, metrics and proposed handoffs used to be serialised into the
    /// result string (the old Compat helper) and were therefore unreadable by anything downstream.
    /// They are recorded here as a structured event BEFORE the status decision, so a failed task's
    /// evidence survives — which is what makes a later diagnosis or repair possible at all.
    ///
    /// Handoffs are recorded here as the proposal record; IngestHandoffs decides which of them
    /// become real tasks. A rejected handoff therefore still leaves a trace.
    /// </summary>
    private void PersistExecutionRecord(Mission mission, Task task, AntRuntimeSelection runtimeSelection,
        AntExecutionResult execution, double elapsed)
    {
        Memory.LogEvent(mission.Id, "task_execution_recorded",
            $"Structured result recorded: {execution.StatusCode}", task.Id, runtimeSelection.RuntimeNodeId,
            MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
            {
                ["status_code"] = execution.StatusCode,
                ["success"] = execution.Success,
                ["summary"] = TextUtil.Truncate(execution.Summary, 500),
                ["artifacts"] = execution.Artifacts.Select(a => new Dictionary<string, object?>
                {
                    ["kind"] = a.Kind, ["title"] = a.Title, ["path"] = a.Path,
                    ["chars"] = a.Content.Length,
                }).ToList(),
                ["evidence"] = execution.Evidence.Select(e => new Dictionary<string, object?>
                {
                    ["kind"] = e.Kind, ["value"] = e.Value, ["detail"] = e.Detail,
                }).ToList(),
                ["warnings"] = execution.Warnings,
                ["failure_class"] = execution.Failure?.Class.ToString(),
                ["failure_reason"] = execution.Failure?.Reason,
                ["failure_retryable"] = execution.Failure?.Retryable,
                ["handoffs_proposed"] = execution.Handoffs.Select(h => new Dictionary<string, object?>
                {
                    ["destination_role"] = h.DestinationRole, ["reason"] = h.Reason,
                    ["required_task_type"] = h.RequiredTaskType,
                }).ToList(),
                ["metrics"] = new Dictionary<string, object?>
                {
                    ["model_calls"] = execution.Metrics.ModelCalls, ["tool_calls"] = execution.Metrics.ToolCalls,
                    ["elapsed_seconds"] = elapsed, ["input_chars"] = execution.Metrics.InputChars,
                    ["output_chars"] = execution.Metrics.OutputChars, ["retry_count"] = execution.Metrics.RetryCount,
                    ["environment"] = execution.Metrics.EnvironmentFingerprint,
                },
            }));
    }

    /// <summary>
    /// v2.19.0: apply a non-completing decision. Before this release there was no such path for a
    /// normally-returned result — everything that did not throw was marked complete.
    /// </summary>
    private void ApplyNonCompletingOutcome(Mission mission, Task task, AntRuntimeSelection runtimeSelection,
        AntExecutionResult execution, TaskOutcomeDecision decision, DateTime finishedAt, double elapsed,
        TaskScheduler? scheduler)
    {
        task.Result = decision.Reason;
        if (decision.Action == TaskOutcomeAction.Skip)
        {
            if (scheduler is not null) scheduler.MarkSkipped(task.Id, decision.Reason, decision.FailureType);
            else { task.Status = TaskStatus.Skipped; task.SkippedAt = finishedAt; task.SkippedReason = decision.Reason; }
        }
        else
        {
            // The scheduler owns the retry decision: it knows the attempt budget. Retryable here
            // means "eligible", not "guaranteed".
            if (scheduler is not null)
                scheduler.MarkFailed(task.Id, decision.Reason, decision.FailureType, decision.Retryable, finishedAt, elapsed);
            else
            {
                task.Status = TaskStatus.Failed; task.FailedAt = finishedAt;
                task.FailureReason = decision.Reason; task.FailureType = decision.FailureType;
            }
        }

        FinalizeTaskResult(mission, task);
        Memory.LogEvent(mission.Id, "task_outcome_applied",
            $"Task did not complete ({execution.StatusCode}): {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
            MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
            {
                ["status_code"] = execution.StatusCode, ["action"] = decision.Action.ToString(),
                ["retryable"] = decision.Retryable, ["failure_type"] = decision.FailureType,
                ["reason"] = TextUtil.Truncate(decision.Reason, 500), ["elapsed_seconds"] = elapsed,
            }));
        Console.WriteLine($"Task {execution.StatusCode}: {task.Title} ({elapsed}s) — {TextUtil.Truncate(decision.Reason, 160)}");
    }

    /// <summary>
    /// v2.20.0: the archivist's memory candidates finally have a consumer. Each well-formed
    /// candidate becomes a durable memory_candidate event with provenance — queryable by reporting,
    /// never fed to planning, never certified here (auto_promote is recorded, not acted on).
    /// Runs only on the completion path: a blocked or failed archival produced no candidates.
    /// </summary>
    private void IngestMemoryCandidates(Mission mission, Task task, AntExecutionResult execution)
    {
        var candidates = Outcomes.MemoryCandidateIngest.Extract(execution);
        foreach (var candidate in candidates)
            Memory.LogEvent(mission.Id, Outcomes.MemoryCandidateIngest.EventType,
                $"Memory candidate [{candidate.MemoryClass}] {TextUtil.Truncate(candidate.Summary, 300)}",
                task.Id, task.AssignedAnt, Outcomes.MemoryCandidateIngest.EventMetadata(candidate));
        if (candidates.Count > 0)
            Console.WriteLine($"Archived {candidates.Count} memory candidate(s) for mission {mission.Id}.");

        // v2.23.0 Phase C4 route registration used to live here — and resolved the mission
        // outcome while status was still Running, so it ALWAYS read negative and never registered
        // anything. Moved to RegisterProceduralRoutes at finalization, where the one canonical
        // evaluation exists. (v2.26.0 pre-V3 hardening.)
    }

    /// <summary>
    /// v2.21.0 Phase A: turn a completed task's proposed handoffs into real follow-up tasks.
    ///
    /// Every admitted task passes the SAME gates as an initial-plan task — HandoffGate (depth,
    /// mission task budget, runtime eligibility, contract task-type support, dedupe) and then
    /// AntRegistry.ValidateTask, the identical authorization check CreateTasks applies. There is
    /// deliberately no admission path that skips them, and a handoff can never grant a capability:
    /// it can only ask for a role that is already runtime-eligible for a task type its contract
    /// already supports.
    ///
    /// Depth is computed from the SOURCE task's lineage, never from the handoff's self-reported
    /// Depth — see HandoffGate.NextDepthFrom for why that distinction is what actually bounds
    /// recursion.
    ///
    /// Rejections are logged with their reason. Nothing is dropped silently.
    /// </summary>
    /// <remarks>Internal rather than private so the admission path itself is testable — a source
    /// guard proving the call site exists is not the same as proving the gates actually run.</remarks>
    internal void IngestHandoffs(Mission mission, Task sourceTask, AntExecutionResult execution,
        AntRuntimeSelection runtimeSelection, TaskScheduler? scheduler)
    {
        if (!AnthillRuntime.EnableHandoffIngestion || execution.Handoffs.Count == 0) return;

        var depth = HandoffGate.NextDepthFrom(sourceTask);
        var constraints = MissionConstraints.Parse(mission.Goal);

        foreach (var proposed in execution.Handoffs)
        {
            var handoff = proposed with { Depth = depth };
            var admission = HandoffGate.Evaluate(handoff, mission);
            if (!admission.Accepted || admission.CreatedTask is null)
            {
                LogHandoffRejected(mission, sourceTask, handoff, admission.Reason, runtimeSelection);
                continue;
            }

            var created = admission.CreatedTask;
            created.ParentTaskIds = new List<string> { sourceTask.Id };

            if (TryAdmitDynamicTask(mission, scheduler, created, constraints) is { Length: > 0 } refusal)
            {
                LogHandoffRejected(mission, sourceTask, handoff, refusal, runtimeSelection);
                continue;
            }

            Memory.LogEvent(mission.Id, "handoff_admitted",
                $"Handoff admitted: {handoff.SourceRole} -> {handoff.DestinationRole} ({created.Title})",
                created.Id, handoff.DestinationRole,
                MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
                {
                    ["source_task_id"] = sourceTask.Id, ["destination_role"] = handoff.DestinationRole,
                    ["required_task_type"] = handoff.RequiredTaskType, ["depth"] = depth,
                    ["dedupe_key"] = handoff.DedupeKey, ["required"] = handoff.Required,
                    ["reason"] = handoff.Reason,
                }));
            Console.WriteLine($"Handoff admitted: {handoff.SourceRole} -> {handoff.DestinationRole} (depth {depth})");
        }
    }

    private void LogHandoffRejected(Mission mission, Task sourceTask, AntHandoff handoff, string reason,
        AntRuntimeSelection runtimeSelection) =>
        Memory.LogEvent(mission.Id, "handoff_rejected",
            $"Handoff refused: {handoff.SourceRole} -> {handoff.DestinationRole} — {reason}",
            sourceTask.Id, handoff.SourceRole,
            MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
            {
                ["destination_role"] = handoff.DestinationRole, ["required_task_type"] = handoff.RequiredTaskType,
                ["depth"] = handoff.Depth, ["dedupe_key"] = handoff.DedupeKey, ["rejection_reason"] = reason,
            }));

    /// <summary>
    /// The single admission path for every task created DURING a run — handoff, delta plan, or
    /// repair. ADR §6: "Every runtime-added task passes the SAME authorization, contract and
    /// permission gates as an initial-plan task. There is no admission path that skips them."
    /// Having exactly one function makes that checkable rather than aspirational.
    ///
    /// Returns null when admitted, or the refusal reason.
    /// </summary>
    private string? TryAdmitDynamicTask(Mission mission, TaskScheduler? scheduler, Task created,
        MissionConstraints constraints)
    {
        created.AssignedWorker ??= AntRegistry.DefaultWorkerFor(
            created.AssignedAnt, created.TaskType, $"{mission.Goal} {created.Title}")?.WorkerId;

        var selection = AntRegistry.ValidateTask(created, constraints);
        if (!selection.Allowed) return $"ant registry denied: {selection.Reason}";

        if (scheduler is not null && !scheduler.AddDynamicTask(created))
            return "scheduler refused the task (duplicate id)";

        // ALWAYS also add to mission.Tasks. TaskScheduler copies the list it is constructed with
        // (Tasks = tasks.ToList()), so scheduler admission alone leaves the task invisible to
        // everything that reads the mission: outcome grading, MissionVerification, the archivist —
        // and HandoffGate's dedupe check, which scans mission.Tasks and would otherwise re-admit
        // the same handoff on every later completion.
        mission.Tasks.Add(created);
        Memory.SaveTask(mission.Id, created);   // survives restart like any planned task
        return null;
    }

    /// <summary>
    /// v2.21.0 Phase B2: consult the adaptive controller after a wave and act on its decision.
    ///
    /// Budgets are derived by COUNTING the mission's own audit events rather than held in memory,
    /// so a restart cannot silently hand a mission a fresh allowance — the durability requirement
    /// comes free from the event log, with no schema change and a readable trail of every replan
    /// and repair the mission spent.
    ///
    /// Returns true when the mission should stop.
    /// </summary>
    private bool ApplyAdaptiveDecision(Mission mission, TaskScheduler? scheduler, string? previousFingerprint)
    {
        if (!AnthillRuntime.EnableAdaptiveMissionControl) return false;

        var budget = new AdaptiveBudget(
            ReplansUsed: Memory.GetRecentEvents(200, "adaptive_delta_plan", mission.Id).Count,
            RepairCyclesUsed: Memory.GetRecentEvents(200, "adaptive_repair", mission.Id).Count);

        var decision = _adaptive.Assess(mission, budget, previousFingerprint);
        if (decision.Action is AdaptiveAction.Continue or AdaptiveAction.Finish) return false;

        var constraints = MissionConstraints.Parse(mission.Goal);

        if (decision.Action == AdaptiveAction.Repair)
        {
            var broken = mission.Tasks.First(t => t.Critical && t.Status == TaskStatus.Failed);
            var repair = new Task
            {
                Title = $"Repair: {TextUtil.Truncate(broken.Title, 80)}",
                Description = $"Diagnose and route a bounded repair for the failed task '{broken.Title}': "
                            + $"{TextUtil.Truncate(broken.FailureReason ?? "no reason recorded", 400)} "
                            + $"[adaptive repair cycle:{budget.RepairCyclesUsed + 1}]",
                AssignedAnt = "medic",
                TaskType = "failure_diagnosis",
                Critical = false,   // the repair attempt must not itself fail the mission
                ParentTaskIds = new List<string> { broken.Id },
            };
            return !RecordAdaptiveAdmission(mission, scheduler, repair, constraints, "adaptive_repair", decision);
        }

        if (decision.Action == AdaptiveAction.DeltaPlan)
        {
            // Delta ONLY: the missing verification step, never a re-plan of work already done.
            // The ADR rejected free replanning precisely because it is unbounded task creation
            // under another name.
            if (mission.Tasks.Any(t => MissionVerification.IsVerificationTask(t) && t.Status != TaskStatus.Failed))
            {
                LogAdaptiveStop(mission, decision, "verification already present — a delta plan would duplicate it");
                return true;
            }
            var verify = new Task
            {
                Title = "Verify mission outcome",
                Description = $"Independently verify that the mission goal was met: {TextUtil.Truncate(mission.Goal, 400)} "
                            + $"[adaptive delta generation:{budget.ReplansUsed + 1}]",
                AssignedAnt = "verifier",
                TaskType = "verify",
                Critical = true,
            };
            return !RecordAdaptiveAdmission(mission, scheduler, verify, constraints, "adaptive_delta_plan", decision);
        }

        LogAdaptiveStop(mission, decision, decision.Reason);
        return true;   // Escalate
    }

    /// <summary>Admit an adaptive task and record it; returns false when it could not be admitted.</summary>
    private bool RecordAdaptiveAdmission(Mission mission, TaskScheduler? scheduler, Task created,
        MissionConstraints constraints, string eventType, AdaptiveDecision decision)
    {
        var refusal = TryAdmitDynamicTask(mission, scheduler, created, constraints);
        if (refusal is not null)
        {
            // A refused adaptive task must stop the mission, not be silently skipped: the
            // controller said work was required and the mission cannot supply it.
            LogAdaptiveStop(mission, decision, $"adaptive task refused: {refusal}");
            return false;
        }

        Memory.LogEvent(mission.Id, eventType, $"{decision.Action}: {created.Title}", created.Id, created.AssignedAnt,
            new()
            {
                ["action"] = decision.Action.ToString(), ["reason"] = decision.Reason,
                ["unmet_criteria"] = decision.UnmetCriteria, ["task_type"] = created.TaskType,
            });
        Console.WriteLine($"Adaptive {decision.Action}: {created.Title}");
        return true;
    }

    private void LogAdaptiveStop(Mission mission, AdaptiveDecision decision, string reason)
    {
        Memory.LogEvent(mission.Id, "adaptive_escalated", $"Mission stopped by the adaptive controller: {reason}",
            metadata: new()
            {
                ["action"] = decision.Action.ToString(), ["reason"] = reason,
                ["unmet_criteria"] = decision.UnmetCriteria,
            });
        Console.WriteLine($"Adaptive stop: {reason}");
    }

    /// <summary>
    /// v2.22.0 Phase C2: credit the skills a mission actually followed, closing the learning loop.
    ///
    /// v2.21.0 made skills durable and let certified procedures INFORM a plan; nothing recorded
    /// whether following one worked, so standing could only ever be earned in the shadow
    /// simulator. Tasks now carry the skill they were planned from, so a finished mission can be
    /// credited back.
    ///
    /// The rule is the same one everything else obeys: **only `completed_verified` is a positive
    /// outcome**. A mission that merely finished, or finished partially, records a non-verified
    /// outcome — which `RecordOutcome` counts as a failure, because a procedure that cannot be
    /// shown to have worked has not been shown to work. That is deliberately the same asymmetry
    /// v2.19.0 established: unverified success reinforces nothing, but it does not pretend the
    /// attempt never happened.
    ///
    /// Promotion and demotion both stay with <see cref="SkillRegistry.RecordOutcome"/>; this only
    /// reports what happened and persists the result.
    /// </summary>
    private void CreditSkills(Mission mission, Outcomes.MissionEvaluation evaluation)
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

        foreach (var skillId in followed)
        {
            // v2.26.0: the bundle below is built from the ACTUAL verifier task and is honestly
            // semantic (Deterministic: false). Promotable now intrinsically requires deterministic
            // evidence, so this bundle records a NEUTRAL observation, never a promotion — the old
            // path here fabricated promotable evidence out of a model's own verdict. Deterministic
            // task-level evidence (build/test/diff) will flow in once patch verification bundles
            // are attached at this site; until then, no evidence means no credit.
            var status = Skills.RecordOutcome(skillId, bundle, AnthillRuntime.EnvironmentFingerprint,
                verified ? null : $"mission {mission.Id} finished {mission.Status.Value()} without verified success");
            Memory.LogEvent(mission.Id, "skill_outcome_recorded",
                $"Skill '{skillId}' recorded {(verified ? "a verified success" : "an unverified outcome")} — now {status}.",
                metadata: new()
                {
                    ["skill_id"] = skillId, ["verified"] = verified, ["status"] = status.ToString(),
                    ["mission_status"] = mission.Status.Value(),
                });
        }
        // v2.26.0: persist ONLY the touched skills, row-atomically — a whole-registry save from
        // one mission's finalization was last-writer-wins against a concurrent mission's.
        foreach (var skillId in followed)
            if (Skills.Get(skillId) is { } touched) Memory.SaveSkill(touched);
    }

    // v2.24.0's MissionIsVerified was absorbed into Outcomes.MissionEvaluator in v2.26.0 — the
    // layered rule (interim gate as floor + objective deliverable) lives THERE, computed once and
    // persisted, so no second site can drift from it.

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

    private void RecordAgentMessage(string missionId, string? taskId, string sender, string recipient, string messageType,
        string content, Dictionary<string, object?> metadata)
    {
        if (!AnthillRuntime.EnableAgentCommunicationLedger) return;
        Memory.LogAgentMessage(missionId, sender, recipient, messageType, content, taskId, metadata);
    }

    private static Dictionary<string, object?> MergeMetadata(Dictionary<string, object?> first, Dictionary<string, object?> second)
    {
        foreach (var (key, value) in second) first[key] = value;
        return first;
    }
}
