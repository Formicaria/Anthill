using System.Diagnostics;
using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Pheromones;
using Anthill.Core.Planning;
using Anthill.Core.Scheduling;
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
        };
    }

    private ToolRegistry BuildToolRegistry()
    {
        var registry = new ToolRegistry(Memory);
        var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
        registry.Register(new SystemInfoTool());
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

    public string RunMission(string goal)
    {
        Console.WriteLine($"Queen received mission: {goal}");
        var missionStartedAt = AnthillTime.NowUtc();
        var mission = new Mission { Goal = goal, Status = MissionStatus.Running };
        LastMissionId = mission.Id;

        // Persist the mission row before any LogEvent calls so FK constraints on events(mission_id) are satisfied.
        Memory.SaveMission(mission);

        var backupPath = FileSecurity.BackupDb(AnthillRuntime.DbPath, AnthillRuntime.BackupDir, AnthillRuntime.PathFromScript);
        Memory.LogEvent(mission.Id, backupPath is not null ? "db_backup_created" : "db_backup_skipped",
            backupPath is not null ? "Pre-mission DB backup created." : "Pre-mission DB backup skipped because no database file exists yet.",
            metadata: new() { ["backup_file"] = backupPath is not null ? Path.GetFileName(backupPath) : null });
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
        mission.Tasks = _planner.CreateTasks(goal, memoryContext, Tools.DescribeTools(), Memory.FormatPheromoneContext(8));

        foreach (var task in mission.Tasks)
            if (task.TaskType == "general") task.TaskType = TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description);
        // Spec-ingestion plans already carry explicit section→synthesis→verify wiring and
        // non-critical section flags; auto-wiring would only re-derive the same edges.
        if (AnthillRuntime.EnableAutoDependencyWiring && !isSpecIngestion) AutoWireDependencies(mission);

        foreach (var task in mission.Tasks)
            Memory.LogEvent(mission.Id, "task_created", $"Task created for {task.AssignedAnt}: {task.Title}", task.Id, task.AssignedAnt,
                new() { ["task_type"] = task.TaskType, ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds });

        Memory.LogEvent(mission.Id, "mission_started", "Mission execution started.", metadata: new()
        {
            ["mission_type"] = missionType,
            ["task_count"] = mission.Tasks.Count,
            ["planner_pattern"] = mission.Tasks.Select(t => t.AssignedAnt).ToList(),
            ["task_type_pattern"] = mission.Tasks.Select(t => t.TaskType).ToList(),
            ["parallel_execution"] = AnthillRuntime.EnableParallelExecution,
            ["max_parallel_workers"] = AnthillRuntime.MaxParallelWorkers,
            ["auto_dependency_wiring"] = AnthillRuntime.EnableAutoDependencyWiring,
        });
        Console.WriteLine($"Mission ID: {mission.Id}");
        Console.WriteLine($"Created {mission.Tasks.Count} tasks. Parallel execution: {(AnthillRuntime.EnableParallelExecution ? "ON" : "OFF")}\n");

        if (AnthillRuntime.EnableParallelExecution) ExecuteTasksParallel(mission, missionStartedAt);
        else ExecuteTasksSequential(mission, missionStartedAt);

        FinalizeMission(mission);
        Console.WriteLine($"Pheromone score: {mission.SuccessScore}");
        Memory.SaveMission(mission);
        Memory.LogEvent(mission.Id, "mission_saved", "Mission saved to ANTHILL memory.", metadata: new() { ["db_path"] = Memory.DbPath });
        Console.WriteLine("Mission saved to ANTHILL memory.");
        return ComposeCliResult(mission);
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

    private void ExecuteTasksSequential(Mission mission, DateTime missionStartedAt)
    {
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        LogSchedulerIssues(mission, scheduler.Prepare());
        LogSchedulerTransitions(mission, scheduler);
        var taskIndex = mission.Tasks.Select((t, i) => (t.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);

        while (!scheduler.IsFinished())
        {
            if (MissionTimedOut(missionStartedAt))
            {
                scheduler.SkipRemaining("Task skipped because mission timed out.", "mission_timeout");
                LogSchedulerTransitions(mission, scheduler);
                return;
            }
            var task = scheduler.NextReadyTask();
            LogSchedulerTransitions(mission, scheduler);
            if (task is not null)
            {
                RunSingleTask(task, mission, taskIndex.GetValueOrDefault(task.Id), mission.Tasks.Count, scheduler);
                LogSchedulerTransitions(mission, scheduler);
                continue;
            }
            var blocked = mission.Tasks.Where(t => t.Status == TaskStatus.Blocked).ToList();
            if (blocked.Count > 0)
            {
                foreach (var b in blocked)
                    scheduler.MarkSkipped(b.Id, b.BlockedReason ?? "Task skipped because scheduler could not make progress.", "dead_dependency");
                LogSchedulerTransitions(mission, scheduler);
                return;
            }
            break;
        }
    }

    private void ExecuteTasksParallel(Mission mission, DateTime missionStartedAt)
    {
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        LogSchedulerIssues(mission, scheduler.Prepare());
        LogSchedulerTransitions(mission, scheduler);
        var running = new Dictionary<System.Threading.Tasks.Task, Task>();
        var taskIndex = mission.Tasks.Select((t, i) => (t.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var lastSweep = Stopwatch.StartNew();

        while (true)
        {
            if (MissionTimedOut(missionStartedAt))
            {
                lock (_executionLock)
                {
                    scheduler.SkipRemaining("Task skipped because mission timed out.", "mission_timeout");
                    LogSchedulerTransitions(mission, scheduler);
                }
                return;
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
                if (scheduler.IsFinished() && running.Count == 0) return;
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
                        return;
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
            scheduler.Evaluate();
            LogSchedulerTransitions(mission, scheduler);
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

    private void RunSingleTask(Task task, Mission mission, int index, int total, TaskScheduler? scheduler)
    {
        var taskStartedAt = AnthillTime.NowUtc();
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
            Console.WriteLine($"Task {index}/{total} -> {task.AssignedAnt} ant: {task.Title}");
            Memory.LogEvent(mission.Id, "task_started", $"Task started: {task.Title}", task.Id, task.AssignedAnt, new()
            {
                ["task_type"] = task.TaskType, ["index"] = index, ["parallel"] = AnthillRuntime.EnableParallelExecution,
                ["max_task_seconds"] = AnthillRuntime.MaxTaskSeconds, ["attempt_count"] = task.AttemptCount,
                ["max_attempts"] = task.MaxAttempts, ["snapshot_context"] = true,
            });
            taskSnapshot = task.DeepCopy();
            missionSnapshot = mission.DeepCopy();
        }

        RecordAgentMessage(mission.Id, task.Id, "queen", task.AssignedAnt, "task_dispatch",
            $"Dispatch task: {task.Title}\nType: {task.TaskType}\nDescription: {TextUtil.Truncate(task.Description, 900, "...[description truncated]")}",
            new()
            {
                ["schema"] = AnthillRuntime.AgentMessageVersion, ["context_strategy"] = "locked_mission_snapshot+compact_context_packets",
                ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds, ["parallel_execution"] = AnthillRuntime.EnableParallelExecution,
            });

        if (!_ants.TryGetValue(task.AssignedAnt, out var ant))
        {
            lock (_executionLock)
            {
                task.Result = $"No ant found for role: {task.AssignedAnt}";
                task.FinishedAt = AnthillTime.NowUtc();
                task.ElapsedSeconds = Math.Round((task.FinishedAt.Value - taskStartedAt).TotalSeconds, 3);
                if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "missing_ant", false, task.FinishedAt, task.ElapsedSeconds);
                else { task.Status = TaskStatus.Failed; task.FailedAt = task.FinishedAt; task.FailureReason = task.Result; task.FailureType = "missing_ant"; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "task_failed", task.Result, task.Id, task.AssignedAnt,
                    new() { ["reason"] = "missing_ant", ["elapsed_seconds"] = task.ElapsedSeconds });
                Console.WriteLine(task.Result);
            }
            return;
        }

        try
        {
            var result = ant.Run(taskSnapshot, missionSnapshot);
            var finishedAt = AnthillTime.NowUtc();
            var elapsed = Math.Round((finishedAt - taskStartedAt).TotalSeconds, 3);
            lock (_executionLock)
            {
                if (task.Status != TaskStatus.Running)
                {
                    Memory.LogEvent(mission.Id, "task_late_result_ignored",
                        $"Late result ignored for task already in terminal/non-running state: {task.Status.Value()}", task.Id, task.AssignedAnt,
                        new() { ["elapsed_seconds"] = elapsed, ["result_preview"] = TextUtil.Truncate(result ?? "", 500) });
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
                    Memory.LogEvent(mission.Id, "task_failed_timeout", task.Result, task.Id, task.AssignedAnt,
                        new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["max_task_seconds"] = AnthillRuntime.MaxTaskSeconds });
                    Console.WriteLine(task.Result);
                    return;
                }
                if (scheduler is not null) scheduler.MarkComplete(task.Id, result, finishedAt, elapsed);
                else { task.Status = TaskStatus.Complete; task.CompletedAt = finishedAt; }
                FinalizeTaskResult(mission, task);
                Memory.LogEvent(mission.Id, "task_completed", $"Task completed: {task.Title}", task.Id, task.AssignedAnt,
                    new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["result_preview"] = TextUtil.Truncate(task.Result ?? "", 500) });
                if (task.AssignedAnt == "coder") ProcessPatchProposals(mission, task);
                RecordAgentMessage(mission.Id, task.Id, task.AssignedAnt, "queen", "task_result",
                    task.ResultSummary ?? TextUtil.CreateResultSummary(task.Result, AnthillRuntime.MaxResultSummaryChars),
                    new() { ["schema"] = AnthillRuntime.AgentMessageVersion, ["status"] = task.Status.Value(), ["result_chars"] = task.ResultChars, ["estimated_tokens"] = task.EstimatedTokens, ["elapsed_seconds"] = task.ElapsedSeconds });
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
                        $"Late error ignored for task already in terminal/non-running state: {task.Status.Value()}", task.Id, task.AssignedAnt,
                        new() { ["elapsed_seconds"] = elapsed, ["error"] = error.Message });
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
                Memory.LogEvent(mission.Id, terminalFailure ? "task_failed" : "task_retry_scheduled", task.Result, task.Id, task.AssignedAnt,
                    new() { ["task_type"] = task.TaskType, ["error"] = error.Message, ["elapsed_seconds"] = elapsed, ["attempt_count"] = task.AttemptCount, ["max_attempts"] = task.MaxAttempts });
                RecordAgentMessage(mission.Id, task.Id, task.AssignedAnt, "queen", terminalFailure ? "task_error" : "task_retry",
                    task.Result, new() { ["schema"] = AnthillRuntime.AgentMessageVersion, ["error"] = error.Message, ["elapsed_seconds"] = elapsed });
                Console.WriteLine(task.Result);
            }
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
        Memory.SaveTaskResultSummary(mission.Id, task);
        Memory.LogMessageMetric(mission.Id, task.Id, task.AssignedAnt, "task_result",
            (task.Description ?? "").Length, task.ResultChars,
            new() { ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["summary_chars"] = (task.ResultSummary ?? "").Length, ["context_packets_enabled"] = AnthillRuntime.EnableContextPackets });
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

    private void FinalizeMission(Mission mission)
    {
        // Only a CRITICAL task failure fails the whole mission. A non-critical failure/skip
        // (e.g. one spec-ingestion section) degrades the mission to Partial but never aborts it.
        var criticalFailed = mission.Tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical);
        var degraded = mission.Tasks.Any(t => t.Status == TaskStatus.Skipped
                                              || (t.Status == TaskStatus.Failed && !t.Critical));
        mission.Status = criticalFailed ? MissionStatus.Failed : degraded ? MissionStatus.Partial : MissionStatus.Complete;
        mission.SuccessScore = _pheromones.ScoreMission(mission);
        Memory.LogEvent(mission.Id, "pheromone_scored", $"Mission pheromone score calculated: {mission.SuccessScore}",
            metadata: new() { ["success_score"] = mission.SuccessScore, ["mission_status"] = mission.Status.Value() });
        Memory.UpdateMissionPheromones(mission);
        mission.BestOutputTaskId = SelectBestOutputTaskId(mission);
        mission.UserResult = ComposeUserResult(mission);
        mission.DebugResult = ComposeDebugResult(mission);
        mission.FinalResult = mission.UserResult;
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
    }

    private void RecordAgentMessage(string missionId, string? taskId, string sender, string recipient, string messageType,
        string content, Dictionary<string, object?> metadata)
    {
        if (!AnthillRuntime.EnableAgentCommunicationLedger) return;
        Memory.LogAgentMessage(missionId, sender, recipient, messageType, content, taskId, metadata);
    }
}
