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
using Anthill.Core.Workers;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — running a mission's task graph.
///
/// This is the colony's concurrency boundary, and the reason it is one type rather than several:
/// every rule here is about ordering, and ordering rules that live in different places stop
/// agreeing. A task's status transition, its late-result guard, the per-task deadline, the
/// bounded drain after cancellation, and the admission of work discovered mid-run all read and
/// write the same task objects from the scheduler thread and from worker threads. They are
/// serialised by ONE lock, held here, and moving any of them out would mean either exporting the
/// lock or duplicating it.
///
/// The invariants this type exists to hold, each of which was a real defect first:
///
/// <list type="bullet">
/// <item><b>No task result is applied twice, and none is applied late.</b> A worker that returns
/// after its task reached a terminal state records the fact and drops the result.</item>
/// <item><b>A terminal mission contains no running task.</b> Cancellation reaches every task
/// token; whatever has not observed it within the drain grace period is marked terminal HERE,
/// with a persisted reason (v2.26.0).</item>
/// <item><b>Every task created mid-run passes the same gates as a planned one.</b> Handoffs,
/// delta plans and repairs all go through one admission path, so "there is no path that skips
/// the gates" stays checkable rather than aspirational.</item>
/// <item><b>Evidence survives failure.</b> What an ant reported is persisted BEFORE the status
/// decision, which is what makes a later diagnosis possible at all.</item>
/// </list>
///
/// The Queen remains the mission authority: it decides that a mission runs, and it alone
/// finalises one. This decides only how the graph is driven while it does.
/// </summary>
public interface IExecutionService
{
    /// <summary>
    /// Drive the mission's task graph to completion. Returns WHY dispatch stopped
    /// (<c>mission_timeout</c>, <c>mission_cancelled</c>, <c>adaptive_stop</c>), or null if the
    /// plan ran to its natural end — the authoritative signal the Queen grades against.
    /// </summary>
    string? Execute(Mission mission, MissionContext context, CancellationToken missionToken);

    /// <summary>
    /// Turn a completed task's proposed handoffs into real follow-up tasks. Public on the
    /// interface because it is a genuine operation of the execution surface with its own
    /// admission rules — not merely an internal step of <see cref="Execute"/>.
    /// </summary>
    /// <summary>
    /// Turn an ant's declared memory candidates into durable events. v3.8.26 — on the interface
    /// because the ARCHIVIST is no longer only reachable as a task: it runs post-finalization, from
    /// the Queen, outside the task graph. The ingest is the same either way, and a second copy of it
    /// beside the first is how two write paths for one fact begin.
    /// </summary>
    void IngestMemoryCandidatesFor(Mission mission, Task task, AntExecutionResult execution);

    void IngestHandoffs(Mission mission, MissionContext context, Task sourceTask,
        AntExecutionResult execution, AntRuntimeSelection runtimeSelection, TaskScheduler? scheduler);

    /// <summary>
    /// v0.3.8.93 — run a change set harvested from a mission workspace through the SAME patch
    /// pipeline a coder's structured proposal takes: save, artifact, verification, approval cards,
    /// bypass gate. On the interface because the Queen calls it at finalization, outside the task
    /// graph — the same reason <see cref="IngestMemoryCandidatesFor"/> is here, and the same rule:
    /// a second copy of the pipeline beside the first is how two write paths for one fact begin.
    /// </summary>
    void ProcessHarvestedPatchSet(Mission mission, Task anchorTask, PatchSet patchSet);
}

public sealed class ExecutionService : IExecutionService
{
    private readonly SqliteMemory _memory;
    private readonly IReadOnlyDictionary<string, BaseAnt> _ants;
    private readonly PatchProposalParser _patchParser = new();
    // v3.8.21 — the verification framework's first production call site. See VerifyPatchSet.
    private readonly Verification.VerificationRunner _verification = new();
    private readonly AdaptiveMissionController _adaptive = new();

    /// <summary>
    /// The single lock serialising every read-modify-write of task state. One lock, not one per
    /// concern: the status transition, the late-result guard, the timeout sweep and the drain all
    /// race over the same fields, and separate locks would only make the race harder to see.
    /// </summary>
    private readonly object _executionLock = new();

    /// <summary>
    /// v3.8.0 — the live attempt for each running task, so the terminal path can close the one the
    /// dispatch path opened.
    ///
    /// Keyed by task rather than kept in a local, because the claim happens in
    /// <see cref="RunSingleTask"/> and the verdict is reached in <see cref="FinalizeTaskResult"/> —
    /// a different method, called from eleven places. Threading an attempt id through all of them is
    /// how one path gets missed, and a missed path leaves an attempt Running with a live lease,
    /// blocking every retry of that task until the lease lapses.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _liveAttempts = new();

    /// <summary>
    /// How long a claim survives without renewal.
    ///
    /// Comfortably longer than any single task may run, because the lease exists to detect a DEAD
    /// worker rather than a slow one. Too tight and it reclaims work still in progress, so the colony
    /// does it twice; too generous and a real crash takes longer to notice. The second is the
    /// cheaper mistake, so this errs that way.
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);

    /// <summary>
    /// v3.8.26: the tool registry is optional and exists here for ONE reason — reading the per-task
    /// dispatch count it maintains, so <c>AntMetrics.ToolCalls</c> stops being zero. Optional
    /// because a dozen tests construct this service with no registry and none of them care about
    /// metrics; a required dependency would have rewritten those call sites to gain a number they
    /// do not read.
    /// </summary>
    public ExecutionService(SqliteMemory memory, IReadOnlyDictionary<string, BaseAnt> ants,
        Tools.ToolRegistry? tools = null, Models.ModelRouter? router = null,
        Func<string, string, Verification.SetApplyOutcome>? applyPatchSet = null)
    {
        _memory = memory;
        _ants = ants;
        _tools = tools;
        _router = router;
        _applyPatchSet = applyPatchSet;
    }

    /// <summary>
    /// v0.3.8.91 — apply a whole patch set inside one durable transaction, injected by the Queen.
    ///
    /// Null (tests and CLI shapes without a Queen) degrades to the manual card, the same safe
    /// direction the per-patch delegate it replaced degraded in. What it does NOT degrade to
    /// is the old per-proposal loop: a set that cannot be applied atomically is not applied.
    /// </summary>
    private readonly Func<string, string, Verification.SetApplyOutcome>? _applyPatchSet;

    // v0.3.8.51's per-patch approve-and-apply delegate was REMOVED in v0.3.8.91. It existed so the
    // bypass lane could apply proposals one at a time, which is the behaviour that release deleted:
    // a set is applied as a unit or not at all. `Queen.ApproveAndApplyPatch` is unchanged and still
    // serves the operator's Apply button; nothing injects it here any more.

    private readonly Tools.ToolRegistry? _tools;

    /// <summary>v3.8.31: the router, solely to read its per-task model-call count. Optional for the
    /// same reason the registry is — a colony with no provider has none to read.</summary>
    private readonly Models.ModelRouter? _router;

    /// <summary>
    /// Fill in the metrics the RUNTIME can measure, over whatever the ant reported.
    ///
    /// `AntMetrics` has existed since the execution framework with every counter at zero except
    /// `OutputChars`, which two of twelve ants set. The metric was self-reported, and self-reporting
    /// is why Stage F has no evidence to qualify a role on.
    ///
    /// These two are measured rather than asked for: elapsed time is what the executor already timed,
    /// and the tool count is what the dispatch chokepoint already saw. The ant's own values are kept
    /// wherever it actually supplied one — this fills gaps, it does not overwrite work.
    /// </summary>
    /// <param name="environmentFingerprint">Taken from the MISSION CONTEXT, not from
    /// <c>AnthillRuntime</c>. The first draft read the static and
    /// <c>RuntimeCompositionTests.TheMissionExecutionPath_ReadsNoMutableFeatureGate</c> rejected it —
    /// correctly. ADR-001 requires this path to read what was resolved at intake, so a mission cannot
    /// be described by configuration that changed while it was running. `RuntimeOptions` has carried
    /// the fingerprint since v3.1.0; it just had to be asked.</param>
    private AntExecutionResult WithMeasuredMetrics(AntExecutionResult execution, Task task, double elapsed,
        string environmentFingerprint)
    {
        var dispatches = _tools?.TakeDispatchCount(task.Id) ?? 0;
        var modelCalls = _router?.TakeModelCallCount(task.Id) ?? 0;
        var reported = execution.Metrics;

        return execution with
        {
            Metrics = reported with
            {
                ToolCalls = reported.ToolCalls > 0 ? reported.ToolCalls : dispatches,
                ModelCalls = reported.ModelCalls > 0 ? reported.ModelCalls : modelCalls,
                ElapsedSeconds = reported.ElapsedSeconds > 0 ? reported.ElapsedSeconds : elapsed,
                RetryCount = reported.RetryCount > 0 ? reported.RetryCount : Math.Max(0, task.AttemptCount - 1),
                EnvironmentFingerprint = reported.EnvironmentFingerprint ?? environmentFingerprint,
            },
        };
    }

    public string? Execute(Mission mission, MissionContext context, CancellationToken missionToken) =>
        context.Options.ParallelExecution
            ? ExecuteTasksParallel(mission, context, missionToken)
            : ExecuteTasksSequential(mission, context, missionToken);

    private string? ExecuteTasksSequential(Mission mission, MissionContext context, CancellationToken missionToken)
    {
        var scheduler = new TaskScheduler(mission.Tasks, mission.Id);
        LogSchedulerIssues(mission, scheduler.Prepare());
        LogSchedulerTransitions(mission, scheduler);
        var taskIndex = mission.Tasks.Select((t, i) => (t.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);

        while (!scheduler.IsFinished())
        {
            if (MissionStopReason(context, missionToken) is { } stop)
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
                RunSingleTask(task, mission, context, taskIndex.GetValueOrDefault(task.Id), mission.Tasks.Count, scheduler);
                LogSchedulerTransitions(mission, scheduler);
                // Assess after every task: this loop's "wave" is one task.
                if (ApplyAdaptiveDecision(mission, context, scheduler, before)) return AdaptiveStopReason;
                continue;
            }
            // Nothing ready. Before declaring dead dependencies, let the controller decide whether
            // a bounded delta plan or repair can supply what is missing.
            if (ApplyAdaptiveDecision(mission, context, scheduler, previousFingerprint: null)) return AdaptiveStopReason;
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

    private string? ExecuteTasksParallel(Mission mission, MissionContext context, CancellationToken missionToken)
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
            if (MissionStopReason(context, missionToken) is { } stop)
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
                DrainRunningTasks(mission, context, scheduler, running, stop.ReasonType);
                return stop.ReasonType;
            }

            if (lastSweep.Elapsed.TotalSeconds >= AnthillRuntime.TaskTimeoutSweepSeconds)
            {
                lastSweep.Restart();
                lock (_executionLock)
                    foreach (var runningTask in running.Values.ToList())
                        if (runningTask.Status == TaskStatus.Running && runningTask.StartedAt is { } startedAt &&
                            (AnthillTime.NowUtc() - startedAt).TotalSeconds > context.Budgets.MaxTaskSeconds)
                            MarkTaskTimeout(runningTask, mission, context, scheduler);
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
                var openSlots = Math.Max(0, context.Options.MaxParallelWorkers - running.Count);
                toSubmit = eligible.Take(openSlots).ToList();
            }

            foreach (var task in toSubmit)
            {
                var captured = task;
                var future = System.Threading.Tasks.Task.Run(() =>
                    RunSingleTask(captured, mission, context, taskIndex.GetValueOrDefault(captured.Id), mission.Tasks.Count, scheduler));
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
                            _memory.LogEvent(mission.Id, "task_failed", task.Result, task.Id, task.AssignedAnt,
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
                if (running.Count == 0 && ApplyAdaptiveDecision(mission, context, scheduler, waveFingerprint))
                    return AdaptiveStopReason;
                waveFingerprint = AdaptiveMissionController.Fingerprint(mission);
            }
        }
    }

    private void LogSchedulerIssues(Mission mission, List<TaskGraphIssue> issues)
    {
        foreach (var issue in issues)
            _memory.LogEvent(mission.Id, "task_graph_validation_issue", issue.Message, issue.TaskId, "scheduler",
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
                _memory.LogEvent(mission.Id, "task_ready", $"Task ready: {task.Title}", task.Id, "scheduler", metadata);
            else if (transition.ToStatus == TaskStatus.Blocked.Value())
                _memory.LogEvent(mission.Id, "task_blocked", transition.Reason ?? $"Task blocked: {task.Title}", task.Id, "scheduler", metadata);
            else if (transition.ToStatus == TaskStatus.Skipped.Value())
            {
                task.Result ??= transition.Reason ?? "Task skipped by scheduler.";
                task.SkippedReason ??= transition.Reason;
                FinalizeTaskResult(mission, task);
                var depSkip = transition.ReasonType is "failed_dependency" or "missing_dependency" or "dead_dependency";
                _memory.LogEvent(mission.Id, depSkip ? "task_skipped_dependency" : "task_skipped", task.Result, task.Id, task.AssignedAnt, metadata);
                Console.WriteLine(task.Result);
            }
        }
    }

    /// <summary>
    /// Reports why the mission must stop dispatching, or null to continue. The deadline is checked
    /// first so it is reported as <c>mission_timeout</c>; an external cancel (job cancelled) reached
    /// before the deadline is reported as <c>mission_cancelled</c>. Both leave the same cancelled
    /// token that already aborted any in-flight model call.
    ///
    /// v3.1.0 (ADR-002): the deadline is the context's ABSOLUTE instant, not a duration measured
    /// from a start time carried alongside it. Two loops comparing the same instant cannot disagree
    /// about when the mission expired, and a resumed run inherits the original boundary.
    /// </summary>
    private static (string Message, string ReasonType)? MissionStopReason(MissionContext context, CancellationToken missionToken)
    {
        if (context.IsPastDeadline(AnthillTime.NowUtc()))
            return ("Task skipped because mission timed out.", Outcomes.MissionStopReasons.Timeout);
        if (missionToken.IsCancellationRequested)
            return ("Task skipped because the mission was cancelled.", Outcomes.MissionStopReasons.Cancelled);
        return null;
    }

    private void RunSingleTask(Task task, Mission mission, MissionContext context, int index, int total, TaskScheduler? scheduler)
    {
        var taskStartedAt = AnthillTime.NowUtc();
        AntRuntimeSelection runtimeSelection;
        try
        {
            // v3.1.0 (ADR-002): the mission's constraints, not a fresh parse of its goal. This site
            // re-parsed once PER TASK — the most expensive and most drift-prone of the eight.
            runtimeSelection = AntRuntime.Resolve(task, context.Constraints);
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
                _memory.LogEvent(mission.Id, "worker_runtime_denied", task.Result, task.Id, task.AssignedWorker ?? task.AssignedAnt,
                    new() { ["assigned_ant"] = task.AssignedAnt, ["assigned_worker"] = task.AssignedWorker, ["error"] = error.Message });
                Console.WriteLine(task.Result);
            }
            return;
        }
        Task taskSnapshot;
        Mission missionSnapshot;
        lock (_executionLock)
        {
            // THE DURABLE CLAIM IS TAKEN FIRST, AND A REFUSAL MEANS THIS PROCESS DOES NOT RUN IT.
            // v0.3.8.91 — the ordering, not the symptom.
            //
            // The claim used to be taken AFTER `MarkRunning`, and a refusal was logged and then
            // ignored, with a comment giving the honest reason: the in-process scheduler had already
            // committed the task to Running, so refusing would strand it in Running with nothing
            // executing it. The reasoning was right about the consequence and wrong about the fix.
            // Committing first is what created the trap; claim first and there is nothing to strand.
            //
            // What the old order cost: `TryClaimTask` is genuinely atomic — its guard and insert are
            // one transaction, deliberately — so "another worker holds a live lease" was a
            // trustworthy signal that the caller then discarded. The lease was telemetry rather than
            // mutual exclusion. On a single process that is nearly unobservable. The moment two
            // processes share a colony database it is duplicate model calls, duplicate tool calls,
            // duplicate patch proposals and two writers racing the same workspace — and this is a
            // prerequisite for any distributed-worker work, not a follow-up to it.
            //
            // Also fixed by the reordering: `_liveAttempts[task.Id]` was never set on the refused
            // path, so lease renewal and the terminal attempt state were lost for exactly the runs
            // that most needed a record.
            var claim = _memory.TryClaimTask(task.Id, mission.Id, LocalWorker.Id, ClaimLease);
            if (claim is null)
            {
                _memory.LogEvent(mission.Id, "attempt_claim_refused",
                    "Task NOT run here: another worker holds a live lease on it. The durable claim is "
                  + "mutual exclusion, so this process yields rather than executing a second copy.",
                    task.Id, runtimeSelection.RuntimeNodeId,
                    new() { ["worker_id"] = LocalWorker.Id, ["executed"] = false });
                return;
            }

            if (scheduler is not null)
            {
                if (!scheduler.MarkRunning(task.Id))
                {
                    // The claim is real and this invocation is not going to use it. Release it, or
                    // the task carries a live lease no worker is honouring until it expires —
                    // which would turn a scheduler decision into a task nobody may claim.
                    _memory.FinishAttempt(claim.Id, AttemptState.Abandoned,
                        failureReason: "the in-process scheduler declined to start this task after "
                                     + "the durable claim was taken");
                    return;
                }
                taskStartedAt = task.StartedAt ?? taskStartedAt;
            }
            else
            {
                if (task.Status is not (TaskStatus.Pending or TaskStatus.Ready))
                {
                    _memory.FinishAttempt(claim.Id, AttemptState.Abandoned,
                        failureReason: $"task was {task.Status.Value()} when execution reached it, "
                                     + "after the durable claim was taken");
                    return;
                }
                task.Status = TaskStatus.Running;
                task.AttemptCount += 1;
                task.StartedAt = taskStartedAt;
                task.FinishedAt = null;
                task.ElapsedSeconds = null;
            }

            _liveAttempts[task.Id] = claim.Id;

            var runtimeMetadata = AntRuntime.Metadata(runtimeSelection);
            Console.WriteLine($"Task {index}/{total} -> {runtimeSelection.RuntimeNodeId} worker via {task.AssignedAnt} ant: {task.Title}");
            _memory.SaveTask(mission.Id, task); // live status: the canvas/graph sees "running" now
            _memory.LogEvent(mission.Id, "worker_permission_audited", $"Worker permission boundary audited: {runtimeSelection.RuntimeNodeId}", task.Id, runtimeSelection.RuntimeNodeId,
                runtimeMetadata);
            _memory.LogEvent(mission.Id, "task_started", $"Task started: {task.Title}", task.Id, runtimeSelection.RuntimeNodeId, MergeMetadata(runtimeMetadata, new()
            {
                ["task_type"] = task.TaskType, ["index"] = index, ["parallel"] = context.Options.ParallelExecution,
                ["assigned_worker"] = task.AssignedWorker,
                ["max_task_seconds"] = context.Budgets.MaxTaskSeconds, ["attempt_count"] = task.AttemptCount,
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
                ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds, ["parallel_execution"] = context.Options.ParallelExecution,
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
                _memory.LogEvent(mission.Id, "task_failed", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
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
                taskCts.CancelAfter(TimeSpan.FromSeconds(context.Budgets.MaxTaskSeconds));
                using var taskScope = ModelCallScope.Enter(taskCts.Token);
                // v2.19.0: the STRUCTURED contract. The ant declares its outcome; the orchestrator
                // no longer infers one from the absence of an exception. The narrative is kept for
                // the operator but carries no control meaning.
                // v3.2.0 (phase): the contract is checked at DISPATCH, for every ant.
                //
                // Five specialists checked their own contract's task type on entry; the six core
                // ants and the cartographer did not — so "no ant bypasses the contract" was true
                // of whoever had remembered to write the check. Enforcing it here covers every
                // role including any added later, and it fails BEFORE the model call rather than
                // after paying for one.
                //
                // The specialists' own checks stay, and are not a duplicate decision: this one
                // refuses to DISPATCH work outside a role's contract, theirs refuses to RUN it
                // however they were called — including directly, as their tests do. Both answer
                // from the same contract, so they cannot disagree about what it says.
                var contract = AntExecutionCatalog.ContractFor(ant.Name);
                // v0.3.8.57 (PLAN.md gate 7): a UI change cannot REACH the coder without a valid map.
                //
                // Here rather than in the planner, and that is the whole point. The planner has
                // injected a cartographer ahead of the coder since Stage E, but planner output is
                // model-influenced and the dependency it creates says "the cartographer's task
                // finished", which includes finishing by failing. This asks the store whether a
                // usable map EXISTS, which is the claim the coder actually needs to be true.
                var uiGate = UiChangeGate.Check(taskSnapshot, mission,
                    _memory as Anthill.SDK.Artifacts.IArtifactStore,
                    AntExecutorCatalog.RuntimeAvailable("ui_cartographer"));

                if (contract is not null && !contract.SupportsTaskType(taskSnapshot.TaskType))
                {
                    execution = AntExecutionResult.Blocked(
                        $"task type '{taskSnapshot.TaskType}' is outside the {ant.Name} execution contract " +
                        $"(v{contract.Version})");
                }
                else if (!uiGate.Allowed)
                {
                    // BLOCKED, not failed. The condition is curable — a cartographer run produces the
                    // map — and a failure would spend a repair budget on something no repair fixes.
                    _memory.LogEvent(mission.Id, "ui_change_blocked_unmapped", uiGate.Reason,
                        task.Id, task.AssignedAnt,
                        new() { ["role"] = task.AssignedAnt, ["reason"] = uiGate.Reason });
                    execution = AntExecutionResult.Blocked(uiGate.Reason);
                }
                else
                {
                    // v0.3.8.51 (field report): the operator's approval gate REACHES THE WORKER.
                    // The mission's owning conversation carries the effective policy the operator
                    // chose in chat, and the project's directory gates carry the paths they opened;
                    // both ride to the reasoning provider as ambient scope, where an agent CLI
                    // translates them into its own flags. A mission no conversation started runs
                    // with "ask" and no grants — absence is not consent.
                    // v0.3.8.93: the ROLE rides along too, so the CLI translation can clamp on its
                    // contract — a read-only role under Skip-all-approvals must not become a writer.
                    using var access = EnterAgentAccess(mission, ant.Name);

                    // Structural repair §3: a deterministic check role runs INSIDE the mission's
                    // current materialized revision when one exists — the patched tree, kept alive
                    // by MissionRevisionRegistry — and the task is stamped with the revision it
                    // actually judged. Without a revision (research missions, unpatched work) the
                    // ambient behaviour is exactly as before and RanRevisionId stays null, which
                    // MissionVerification reads as "evidence about the unpatched tree".
                    var revision = ant.Name is "tester" or "soldier" or "builder"
                        ? Workspaces.MissionRevisionRegistry.CurrentFor(mission.Id)
                        : null;
                    if (revision is not null && Directory.Exists(revision.Root))
                    {
                        using var revisionScope = Workspaces.MissionWorkspaceScope.Enter(new Workspaces.MissionWorkspace
                        {
                            Id = $"revision-{revision.RevisionId}",
                            MissionId = mission.Id,
                            Root = revision.Root,
                            Mode = revision.Mode,
                            SourceRoot = AnthillRuntime.AllowedWorkspaceRoot,
                            BaseRevision = revision.BaseRevision,
                            State = Workspaces.WorkspaceState.Active,
                            MaterializedPatchSetId = revision.PatchSetId,
                            RevisionId = revision.RevisionId,
                            TreeHash = revision.TreeHash,
                            PatchSetHash = revision.PatchSetHash,
                        });
                        execution = ant.Execute(taskSnapshot, missionSnapshot);
                        task.RanRevisionId = revision.RevisionId;
                        _memory.LogEvent(mission.Id, "task_ran_in_revision",
                            $"{ant.Name} executed inside revision {revision.RevisionId} (patch set {revision.PatchSetId})",
                            task.Id, ant.Name, new()
                            {
                                ["revision_id"] = revision.RevisionId, ["patch_set_id"] = revision.PatchSetId,
                                ["tree_hash"] = revision.TreeHash,
                            });
                    }
                    else
                    {
                        execution = ant.Execute(taskSnapshot, missionSnapshot);
                    }
                }
                result = execution.Narrative ?? execution.Summary;
            }

            // v0.3.8.81 (PLAN.md §2 R3) — THE OPERATOR'S STOP OUTRANKS WHATEVER THE ANT REPORTED.
            //
            // Every model-calling role reads a non-Ok call as "the routed model is unavailable" and
            // DEGRADES rather than failing, which is the right behaviour for the case it was written
            // for. Cancellation arrives through that same non-Ok door — a stopped call is
            // `ModelCallOutcome.Cancelled` and `Ok` is false for it — so the researcher and the
            // builder returned SucceededWithWarnings and the task COMPLETED. A completed task then
            // ingests handoffs, inserts a verification task after a deliverable, hands the archivist
            // something to remember, and processes the coder's patch proposals. The operator pressed
            // stop and the colony answered with a fabricated fallback deliverable and more work.
            //
            // Checked ONCE here rather than at the eight ant call sites. Which roles degrade on a bad
            // model call is a decision each ant owns and should keep owning; what a STOPPED mission
            // is allowed to record is a decision this class owns. Putting it in the ants would also
            // be eight copies of one rule, and the release that fixed seven of them would look done.
            //
            // `DrainRunningTasks` has recorded this state since v2.26.0 — for tasks still RUNNING at
            // the grace deadline. A task that finished INSIDE the grace period by degrading was never
            // its business, and that is exactly the hole: the faster the role gave up, the more likely
            // its cancelled work was recorded as a success. Both paths now go through one method.
            //
            // `MissionStopReason` rather than a fresh token check, so "why did the mission stop" keeps
            // one answer — it reports the deadline as timeout and an external cancel as cancelled, and
            // this site must not invent a second opinion about which happened.
            if (MissionStopReason(context, ModelCallScope.Current) is { } stop)
            {
                lock (_executionLock)
                {
                    if (task.Status != TaskStatus.Running)
                    {
                        _memory.LogEvent(mission.Id, "task_late_result_ignored",
                            "Late result ignored for a task already terminal when the mission stopped: "
                          + task.Status.Value(), task.Id, runtimeSelection.RuntimeNodeId,
                            MergeMetadata(AntRuntime.Metadata(runtimeSelection),
                                new() { ["reason_type"] = stop.ReasonType }));
                        return;
                    }
                    MarkStoppedMidFlight(mission, task, scheduler, taskStartedAt, stop.ReasonType,
                        "task_stopped_mid_flight", execution);
                    if (scheduler is not null) LogSchedulerTransitions(mission, scheduler);
                }
                return;
            }

            // v3.2.0 (phase): record what the ant REPORTED, before the scheduler decides what to do
            // with it. Written here rather than at finalization because the mapping below can
            // legitimately discard this result (a late one, for a task no longer running) or
            // replace its text (a timeout overwrites it with a one-line reason) — and those are
            // precisely the executions whose evidence is worth having afterwards.
            // v3.8.26: the timing moved ABOVE the save, so the persisted record carries the metrics
            // the runtime measured rather than the zeros the ant did not fill in. Both lines are pure
            // computations over taskStartedAt, so the reorder changes nothing but what is knowable at
            // the point of writing.
            var finishedAt = AnthillTime.NowUtc();
            var elapsed = Math.Round((finishedAt - taskStartedAt).TotalSeconds, 3);

            // Called EXACTLY ONCE per task: TakeDispatchCount removes the counter as it reads it, so
            // a second call would report zero tool calls for work that made several.
            execution = WithMeasuredMetrics(execution, task, elapsed, context.Options.EnvironmentFingerprint);

            _memory.SaveTaskResult(mission.Id, task.Id, ant.Name, execution);
            lock (_executionLock)
            {
                if (task.Status != TaskStatus.Running)
                {
                    _memory.LogEvent(mission.Id, "task_late_result_ignored",
                        $"Late result ignored for task already in terminal/non-running state: {task.Status.Value()}", task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["elapsed_seconds"] = elapsed, ["result_preview"] = TextUtil.Truncate(result ?? "", 500) }));
                    return;
                }
                task.Result = result;
                task.FinishedAt = finishedAt;
                task.ElapsedSeconds = elapsed;
                if (elapsed > context.Budgets.MaxTaskSeconds)
                {
                    task.Result = $"Task exceeded max runtime of {context.Budgets.MaxTaskSeconds} seconds. Elapsed: {elapsed} seconds.";
                    if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "timeout", false, finishedAt, elapsed);
                    else { task.Status = TaskStatus.Failed; task.FailedAt = finishedAt; task.FailureReason = task.Result; task.FailureType = "timeout"; }
                    FinalizeTaskResult(mission, task);
                    _memory.LogEvent(mission.Id, "task_failed_timeout", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["max_task_seconds"] = context.Budgets.MaxTaskSeconds }));
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
                    ApplyNonCompletingOutcome(mission, context, task, runtimeSelection, execution, decision, finishedAt, elapsed, scheduler);
                    return;
                }

                if (decision.Warnings.Count > 0)
                    _memory.LogEvent(mission.Id, "task_completed_with_warnings",
                        $"Task completed with {decision.Warnings.Count} warning(s): {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
                        MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["warnings"] = decision.Warnings }));

                if (scheduler is not null) scheduler.MarkComplete(task.Id, result, finishedAt, elapsed);
                else { task.Status = TaskStatus.Complete; task.CompletedAt = finishedAt; }
                FinalizeTaskResult(mission, task);
                _memory.LogEvent(mission.Id, "task_completed", $"Task completed: {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
                    MergeMetadata(AntRuntime.Metadata(runtimeSelection), new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = elapsed, ["status_code"] = execution.StatusCode, ["result_preview"] = TextUtil.Truncate(task.Result ?? "", 500) }));
                if (task.AssignedAnt == "coder") ProcessPatchProposals(mission, context, task, scheduler);
                // v0.3.8.41 — the informational branch of the lifecycle. A draft deliverable exists,
                // so verification is inserted after it rather than left to the plan.
                if (task.AssignedAnt == "builder") EnsureVerificationAfterDeliverable(mission, context, task, scheduler);
                if (task.AssignedAnt == "archivist") IngestMemoryCandidates(mission, task, execution);
                IngestHandoffs(mission, context, task, execution, runtimeSelection, scheduler);
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
                    _memory.LogEvent(mission.Id, "task_late_error_ignored",
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
                _memory.LogEvent(mission.Id, terminalFailure ? "task_failed" : "task_retry_scheduled", task.Result, task.Id, runtimeSelection.RuntimeNodeId,
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
    private void DrainRunningTasks(Mission mission, MissionContext context, TaskScheduler scheduler,
        Dictionary<System.Threading.Tasks.Task, Task> running, string reasonType)
    {
        if (running.Count == 0) return;
        try
        {
            System.Threading.Tasks.Task.WaitAll(
                running.Keys.ToArray(), TimeSpan.FromSeconds(context.Options.MissionDrainGraceSeconds));
        }
        catch { /* task-level failures were already handled inside RunSingleTask */ }

        lock (_executionLock)
        {
            foreach (var task in running.Values.Where(t => t.Status == TaskStatus.Running).ToList())
                MarkStoppedMidFlight(mission, task, scheduler, task.StartedAt ?? AnthillTime.NowUtc(),
                    reasonType, "task_drained", discarded: null,
                    extra: new() { ["grace_seconds"] = context.Options.MissionDrainGraceSeconds });
            LogSchedulerTransitions(mission, scheduler);
        }
    }

    /// <summary>
    /// Records a task the COLONY stopped — the one implementation of that rule. v0.3.8.81.
    ///
    /// Two paths reach it and they used to be one path and one hole. <see cref="DrainRunningTasks"/>
    /// has handled tasks still RUNNING when the grace period expires since v2.26.0. The other is a
    /// task that RETURNED after the stop — which every degrading role does promptly, because a
    /// cancelled model call comes back as an unavailable-provider result and the role answers with a
    /// fallback. That path had no handler at all, so the quicker a role gave up on a stopped mission
    /// the more likely its work was recorded as a completion.
    ///
    /// Non-retryable, always. A retryable failure returns the task to Ready, and while the dispatch
    /// loop would then skip it as "mission cancelled", the operator is left reading three records
    /// for one stop — failed, retry scheduled, skipped — none of which says they stopped it.
    ///
    /// <paramref name="discarded"/> is what the ant reported and is NOT persisted as an execution
    /// record. It goes into this event's metadata instead: the outcome is worth having for forensics
    /// — it is how this defect was found — and must not enter the evidence channel, where a
    /// `succeeded_with_warnings` from a stopped role is exactly the row that would let a cancelled
    /// mission be graded as having done something.
    /// </summary>
    private void MarkStoppedMidFlight(Mission mission, Task task, TaskScheduler? scheduler,
        DateTime startedAt, string reasonType, string eventName,
        AntExecutionResult? discarded = null, Dictionary<string, object?>? extra = null)
    {
        var now = AnthillTime.NowUtc();
        var cancelled = reasonType == Outcomes.MissionStopReasons.Cancelled;
        // The same two sentences DrainRunningTasks has written since v2.26.0, kept verbatim so an
        // operator reading a stopped mission sees one vocabulary rather than two that mean the same.
        var reason = cancelled
            ? "cancelled: mission was cancelled while this task was still running"
            : $"timed_out: mission stopped ({reasonType}) while this task was still running";
        var failureType = cancelled ? "cancelled" : "timeout";

        task.CancellationReason = reason;
        task.Result = reason;
        task.FinishedAt = now;
        task.ElapsedSeconds = Math.Round((now - (task.StartedAt ?? startedAt)).TotalSeconds, 3);
        if (scheduler is not null)
            scheduler.MarkFailed(task.Id, reason, failureType, retryable: false, now, task.ElapsedSeconds);
        else
        {
            task.Status = TaskStatus.Failed;
            task.FailedAt = now;
            task.FailureReason = reason;
            task.FailureType = failureType;
        }
        FinalizeTaskResult(mission, task);

        var metadata = new Dictionary<string, object?>
        {
            ["reason_type"] = reasonType,
            ["role"] = task.AssignedAnt,
            ["failure_type"] = failureType,
            ["elapsed_seconds"] = task.ElapsedSeconds,
            ["discarded_status_code"] = discarded?.StatusCode,
            ["discarded_summary"] = discarded is null ? null : TextUtil.Truncate(discarded.Summary, 300),
        };
        if (extra is not null) metadata = MergeMetadata(metadata, extra);

        _memory.LogEvent(mission.Id, eventName, reason, task.Id, task.AssignedAnt, metadata);
        Console.WriteLine(reason);
    }

    private void MarkTaskTimeout(Task task, Mission mission, MissionContext context, TaskScheduler? scheduler)
    {
        var now = AnthillTime.NowUtc();
        task.FinishedAt = now;
        if (task.StartedAt is { } st) task.ElapsedSeconds = Math.Round((now - st).TotalSeconds, 3);
        task.Result = $"Task exceeded max runtime of {context.Budgets.MaxTaskSeconds} seconds.";
        if (scheduler is not null) scheduler.MarkFailed(task.Id, task.Result, "timeout", false, now, task.ElapsedSeconds);
        else { task.Status = TaskStatus.Failed; task.FailedAt = now; task.FailureReason = task.Result; task.FailureType = "timeout"; }
        FinalizeTaskResult(mission, task);
        _memory.LogEvent(mission.Id, "task_failed_timeout", task.Result, task.Id, task.AssignedAnt,
            new() { ["task_type"] = task.TaskType, ["elapsed_seconds"] = task.ElapsedSeconds, ["max_task_seconds"] = context.Budgets.MaxTaskSeconds });
        Console.WriteLine(task.Result);
    }

    /// <summary>
    /// v3.8.0 — close the durable attempt this task opened.
    ///
    /// Hooked into finalization rather than each terminal branch because finalization IS the choke
    /// point: every path that ends a task passes through here with its final status already set.
    /// Attaching to the branches instead would mean eleven places to remember, and the one that got
    /// forgotten would leave a lease held against a task that finished.
    ///
    /// Skipped is deliberately Abandoned rather than Failed. A skipped task was never executed, so
    /// nothing failed — and Failed would tell a later reader that something was tried and did not
    /// work, which is a different and wrong story about the same row.
    /// </summary>
    private void CloseAttempt(Mission mission, Task task)
    {
        if (!_liveAttempts.TryRemove(task.Id, out var attemptId)) return;

        var state = task.Status switch
        {
            TaskStatus.Complete => AttemptState.Succeeded,
            TaskStatus.Failed   => AttemptState.Failed,

            // A RETRYABLE failure leaves the task Ready for another attempt, so its status describes
            // the task's future rather than this attempt's ending. This attempt failed, and was
            // observed failing — recording it as Abandoned would claim nobody saw how it ended and
            // would mark work that is about to be retried as possibly-completed, which is the exact
            // confusion the Abandoned/Failed split exists to prevent.
            TaskStatus.Ready or TaskStatus.Pending when !string.IsNullOrEmpty(task.FailureReason)
                => AttemptState.Failed,

            _ => AttemptState.Abandoned,
        };

        try
        {
            _memory.FinishAttempt(attemptId, state,
                failureClass: task.FailureType, failureReason: task.FailureReason ?? task.BlockedReason);
        }
        catch (Exception error)
        {
            // Never let bookkeeping fail a task that has already finished. An unclosed attempt is
            // recoverable — its lease lapses and the reclaim sweep marks it abandoned — whereas an
            // exception thrown here would propagate out of finalization and lose the result itself.
            _memory.LogEvent(mission.Id, "attempt_close_failed",
                $"Could not close attempt {attemptId}: {error.Message}", task.Id, task.AssignedAnt);
        }
    }

    private void FinalizeTaskResult(Mission mission, Task task)
    {
        CloseAttempt(mission, task);
        task.ResultChars = (task.Result ?? "").Length;
        task.EstimatedTokens = TextUtil.EstimateTokenCount(task.Result);
        task.ResultSummary = TextUtil.CreateResultSummary(task.Result, AnthillRuntime.MaxResultSummaryChars);
        _memory.SaveTask(mission.Id, task); // live status: terminal state visible to /graph immediately
        _memory.SaveTaskResultSummary(mission.Id, task);
        _memory.LogMessageMetric(mission.Id, task.Id, task.AssignedAnt, "task_result",
            (task.Description ?? "").Length, task.ResultChars,
            new() { ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["summary_chars"] = (task.ResultSummary ?? "").Length, ["context_packets_enabled"] = AnthillRuntime.EnableContextPackets });
        if (!string.IsNullOrWhiteSpace(task.AssignedWorker))
            _memory.LogMessageMetric(mission.Id, task.Id, task.AssignedWorker, "worker_task_result",
                (task.Description ?? "").Length, task.ResultChars,
                new() { ["assigned_ant"] = task.AssignedAnt, ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["summary_chars"] = (task.ResultSummary ?? "").Length });
        _memory.LogEvent(mission.Id, "task_result_summarized", $"Task result summarized for compact downstream context: {task.Title}", task.Id, task.AssignedAnt,
            new() { ["result_chars"] = task.ResultChars, ["summary_chars"] = (task.ResultSummary ?? "").Length, ["estimated_tokens"] = task.EstimatedTokens });
    }

    /// <summary>
    /// The patch set as a typed artifact. v3.8.21.
    ///
    /// This is the coder's real output, and it is genuinely structured — file paths, change types,
    /// risk. It just was not reachable as an artifact, because the STRUCTURE is produced here rather
    /// than in the ant: the coder emits prose and <c>PatchProposalParser</c> turns it into a
    /// <c>PatchSet</c> one layer up. So the artifact is emitted where the structure exists, not where
    /// the text was written.
    /// </summary>
    /// <returns>
    /// The artifact id, or null when the record could not be written. v0.3.8.57 — the id was
    /// previously discarded, which is why the reviews inserted immediately below had to go looking
    /// for "the mission's patch sets" instead of being handed the one they exist to review.
    /// </returns>
    /// <param name="environmentFingerprint">
    /// From the CONTEXT, never from AnthillRuntime. The first draft of this read the static
    /// directly and tripped TheMissionExecutionPath_ReadsNoMutableFeatureGate — correctly: a
    /// mission resolves its environment at intake, and a live read here would stamp an artifact
    /// with whatever the process happens to report now rather than what the mission ran under.
    /// </param>
    private string? RecordPatchArtifact(Mission mission, Task task, PatchSet patchSet,
        string environmentFingerprint)
    {
        try
        {
            return ((Anthill.SDK.Artifacts.IArtifactStore)_memory).Put(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.PatchSet,
                producerRole: task.AssignedAnt,
                missionId: mission.Id,
                payload: Json.Dumps(new
                {
                    patch_set_id = patchSet.Id,
                    summary = patchSet.Summary,
                    proposals = patchSet.Proposals.Select(pr => new
                    {
                        pr.FilePath, change_type = pr.ChangeType.Value(), pr.Risk, pr.RequiresApproval,
                        // v3.8.25: the CONTENT joins the artifact.
                        //
                        // Without it the soldier had nothing to review but prior tasks' prose about
                        // the patch, and a policy engine that scans a description of a change cannot
                        // find a secret in the change. The external review's phrasing is the right
                        // one: raw patch contents belong in the access-controlled artifact store,
                        // not in event-log metadata — so they go here, at Colony visibility, rather
                        // than into the log line beside it.
                        new_content = pr.NewContent,
                    }),
                }, indented: true),
                taskId: task.Id,
                // v0.3.8.57 — provenance, limited to what THIS site can truthfully state. The
                // execution result is not in scope here (the patch set is parsed from task.Result
                // one layer up), so provider and model are genuinely unknown and are therefore
                // absent rather than filled in from the configured route, which a reroute would
                // make a lie. ModelInvolved is TRUE regardless: a coder's patch text came from a
                // model call whichever one served it, and that much is not in doubt.
                provenance: new Anthill.SDK.Artifacts.ArtifactProvenance
                {
                    ColonyVersion = AnthillRuntime.Version,
                    EnvironmentFingerprint = environmentFingerprint,
                    RuntimeNode = task.AssignedAnt,
                    ModelInvolved = true,
                },
                // Explicit rather than defaulted. This artifact now carries proposed source, which
                // is exactly the material the visibility classes exist to distinguish: readable by
                // the colony's own roles, not published outward with the operator summary.
                visibility: Anthill.SDK.Artifacts.ArtifactVisibility.Colony));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not record the patch set artifact for task {task.Id}: {error.Message}");
            // Null, not an empty string. A review task whose declared input is "" would ask the
            // store for an artifact that cannot exist and report it missing; null means "nothing
            // was declared", which falls back to the mission-wide read this had before.
            return null;
        }
    }

    /// <summary>
    /// The tree the verdicts describe, as a first-class artifact. v3.8.23.
    ///
    /// A verification bundle without this is unfalsifiable: "build passed" is a claim about a
    /// specific set of bytes in a specific directory, and until v3.8.23 the only record of which
    /// directory that was is that it happened to be the primary workspace — which is precisely how
    /// v3.8.22 shipped build verdicts that were true and irrelevant.
    ///
    /// Three hashes rather than one, because they answer three different questions: the base
    /// revision says what the patch was applied to, the patch-set hash says what was asked for, and
    /// the applied-tree hash says what actually landed. A replay that reproduces all three has
    /// reproduced the verification; one that reproduces only the first two has reproduced the
    /// intent.
    /// </summary>
    /// <summary>
    /// The complete verification bundle, bound to the patch and the tree it ran in. v3.8.27.
    ///
    /// v3.8.22 recorded each verifier's verdict as its own evidence row and kept the bundle only in
    /// memory. That answers "did the build pass" and cannot answer "what was the full set of checks
    /// this patch was REQUIRED to pass, and did it pass all of them" — which is the only one of the
    /// two that constitutes a verification. A bundle whose required list is absent cannot be
    /// distinguished from one that required nothing.
    ///
    /// Bound to `patch_set_hash` and `applied_tree_hash` rather than to the patch-set ID, because an
    /// id can be reused by a later edit and a hash cannot. A replay that reproduces both hashes has
    /// reproduced the thing that was verified.
    /// </summary>
    private void RecordVerificationBundle(Mission mission, Task task, PatchSet patchSet,
        Verification.MaterializedPatchSet materialized,
        IReadOnlyList<Verification.VerificationBundle> bundles)
    {
        try
        {
            ((Anthill.SDK.Artifacts.IArtifactStore)_memory).Put(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.VerificationBundle,
                producerRole: "queen",   // the runner is orchestration; no ant owns this verdict
                missionId: mission.Id,
                payload: Json.Dumps(new
                {
                    patch_set_id = patchSet.Id,
                    patch_set_hash = materialized.PatchSetHash,
                    applied_tree_hash = materialized.AppliedTreeHash,
                    base_revision = materialized.BaseRevision,
                    resolved_task_type = Verification.VerificationPolicy.Canonical(task.TaskType),
                    // Promotable ONLY if every proposal is. A patch is applied as a unit.
                    promotable = bundles.All(b => b.Promotable),
                    proposals = bundles.Select((b, i) => new
                    {
                        file_path = i < patchSet.Proposals.Count ? patchSet.Proposals[i].FilePath : "",
                        required = b.Required,
                        promotable = b.Promotable,
                        has_deterministic_evidence = b.HasDeterministicEvidence,
                        blocked_reasons = b.BlockedReasons,
                        results = b.Results.Select(r => new
                        {
                            verifier = r.Verifier, passed = r.Passed,
                            deterministic = r.Deterministic, summary = r.Summary,
                        }),
                    }),
                }, indented: true),
                taskId: task.Id,
                visibility: Anthill.SDK.Artifacts.ArtifactVisibility.Colony));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not record the verification bundle for task {task.Id}: {error.Message}");
        }
    }

    private void RecordWorkspaceSnapshot(Mission mission, Task task, PatchSet patchSet,
        Verification.MaterializedPatchSet materialized)
    {
        try
        {
            ((Anthill.SDK.Artifacts.IArtifactStore)_memory).Put(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.WorkspaceSnapshot,
                producerRole: "queen",   // the Queen materialises; the coder only proposes
                missionId: mission.Id,
                payload: Json.Dumps(new
                {
                    patch_set_id = patchSet.Id,
                    base_revision = materialized.BaseRevision,
                    patch_set_hash = materialized.PatchSetHash,
                    applied_tree_hash = materialized.AppliedTreeHash,
                    workspace_mode = materialized.Mode,
                    applied_paths = materialized.AppliedPaths,
                }, indented: true),
                taskId: task.Id));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not record the workspace snapshot for task {task.Id}: {error.Message}");
        }
    }

    /// <summary>
    /// Actually verify the patch. v3.8.21 — and this is a behaviour change, stated plainly.
    ///
    /// <c>VerificationRunner</c>, <c>BuildVerifier</c>, <c>TestVerifier</c>, <c>DiffVerifier</c>,
    /// <c>SecurityPolicyVerifier</c> and <c>VerificationPolicy</c> have all existed and been tested
    /// since v2.12, and NOTHING IN PRODUCTION EVER CALLED THEM. The framework declared that a
    /// <c>code_patch</c> requires diff + build + test + security_policy, and no code patch was ever
    /// checked against it. This is that call site.
    ///
    /// What changes: a code-patch task now runs the real toolchain, so a patch that does not compile
    /// can no longer reach a verified outcome. Missions get slower, and missions that used to pass on
    /// a patch that never built will now fail — which is the point, and is why it is called out here
    /// rather than buried.
    ///
    /// The results become ADR-004 evidence, deterministic flag and all. That is what makes
    /// <c>HasDeterministicPass</c> mean something for code work, and it is the input worker
    /// reputation needs before it can be learned from anything but prose.
    ///
    /// A verification fault must never fail the task that produced the patch: the proposals are
    /// already saved and the approval pipeline still owns whether anything is applied. A colony that
    /// loses a patch because the verifier crashed is worse than one that records no evidence for it.
    /// </summary>
    private void VerifyPatchSet(Mission mission, Task task, PatchSet patchSet)
    {
        if (patchSet.Proposals.Count == 0) return;

        // Owned until the registry takes it. On any exception before registration this is disposed
        // here — the `using` that used to do that job had to go (§3: the tree must outlive this
        // method), and an unregistered orphan sandbox must not outlive it too.
        Verification.MaterializedPatchSet? unregistered = null;
        try
        {
            // v3.8.23: write the patch set into a disposable copy of the workspace and verify THAT.
            //
            // v3.8.22 pointed every request at AnthillRuntime.AllowedWorkspaceRoot — the primary
            // tree, which does not contain the patch. BuildVerifier therefore compiled the
            // repository as it already was and reported success about code the proposal never
            // touched. The gate ran; it just answered a question adjacent to the one asked, which is
            // the sixth instance of that shape in this codebase and the reason this release exists.
            var materialization = Verification.PatchSetMaterializer.Materialize(
                patchSet, AnthillRuntime.AllowedWorkspaceRoot);

            if (!materialization.Ok)
            {
                // Fail CLOSED. An unverifiable patch set is not a verified one, and the most likely
                // cause of a materialisation failure is a proposal whose path escapes the sandbox —
                // which is a security finding, not a skip.
                _memory.LogEvent(mission.Id, "patch_set_materialization_failed",
                    $"Patch set {patchSet.Id} could not be written to an isolated workspace: {materialization.Problem}",
                    task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["problem"] = materialization.Problem });
                task.DeterministicBlock =
                    $"patch set {patchSet.Id} could not be materialised for verification: {materialization.Problem}";
                return;
            }

            // Structural repair §3: the materialized tree is NOT disposed here anymore. Ownership
            // transfers to MissionRevisionRegistry below, which keeps it alive for the policy-
            // inserted tester/soldier (they run as their own tasks, later) and disposes it when a
            // newer revision replaces it or the mission finalizes. Before this, "Tester PASS" for a
            // coding mission was a statement about the UNPATCHED tree.
            var materialized = materialization.Materialized!;
            unregistered = materialized;

            // The scope is load-bearing, not decoration. RunAllowlistedCheckTool resolves its working
            // directory and its check catalog from WorkspaceCapabilityManifest.ForCurrentMission()
            // when a scope is active, and from its injected workdir otherwise — so without this,
            // an ambient mission workspace could silently redirect the build back to a different
            // tree. Entering the scope for the patched sandbox makes the manifest describe the tree
            // being verified, and gets check SELECTION from it too: a Node patch set gets Node
            // checks rather than dotnet_build.
            using var scope = Workspaces.MissionWorkspaceScope.Enter(new Workspaces.MissionWorkspace
            {
                Id = $"verify-{patchSet.Id}",
                MissionId = mission.Id,
                Root = materialized.Root,
                Mode = materialized.Mode,
                SourceRoot = AnthillRuntime.AllowedWorkspaceRoot,
                BaseRevision = materialized.BaseRevision,
                State = Workspaces.WorkspaceState.Active,
                // v0.3.8.41 — this tree CONTAINS the patch, and now says so. Anything running a
                // check here can distinguish it from the MISSION workspace, which is the same source
                // WITHOUT the proposal in it and is what a tester ant resolves to instead.
                MaterializedPatchSetId = patchSet.Id,
            });

            // v3.8.22: one request PER PROPOSAL, carrying the change. v3.8.21 sent a single request
            // with neither ChangedPath nor content, which DiffVerifier answers with "no changed path
            // supplied — nothing to verify" and a FAIL. It also passed task.TaskType unresolved, so
            // the planner's `patch_proposal` matched no policy key and only security_policy ran.
            // Both halves are fixed here and in VerificationPolicy.Canonical.
            // v0.3.8.75 — the policy is chosen from what the WHOLE SET touches, not from the task's
            // declared type alone. `patch_proposal` aliases to `code_patch`, which requires a build,
            // so a README-only change was compiled with `dotnet build -c Release` before it could be
            // called verified — while `docs_patch`, which requires no build, sat unreachable.
            //
            // The SET's paths, not each proposal's: a set applies as a unit, so one code file among
            // ten documents makes the whole set a code patch. Passing per-proposal paths here would
            // let the .md files in a mixed set be verified under the lighter policy.
            var setPaths = patchSet.Proposals.Select(p => p.FilePath).ToList();
            var policyType = Verification.VerificationPolicy.Canonical(task.TaskType, setPaths);

            var requests = patchSet.Proposals.Select(p => new Verification.VerificationRequest(
                TaskType: policyType,
                WorkspaceRoot: materialized.Root,
                ChangedPath: p.FilePath,
                NewContent: p.NewContent,
                OldContent: p.OldContent)).ToList();

            var bundles = _verification.RunForEach(requests);

            // The snapshot the verdicts are bound to. Recorded BEFORE the results, so evidence that
            // references it can never point at a snapshot row that does not exist.
            RecordWorkspaceSnapshot(mission, task, patchSet, materialized);

            // v3.8.27 (Stage C): the BUNDLE, as a durable artifact bound to the patch and the tree.
            //
            // v3.8.22 wrote individual evidence rows and left the bundle in memory — so the colony
            // could answer "did the build pass" but not "what was the complete set of checks this
            // patch was required to pass, and did it pass all of them". Those are different
            // questions, and only the second one is a verification.
            RecordVerificationBundle(mission, task, patchSet, materialized, bundles);

            var store = (Anthill.SDK.Artifacts.IEvidenceStore)_memory;
            for (var i = 0; i < bundles.Count; i++)
                foreach (var verdict in bundles[i].Results)
                    store.Put(Anthill.SDK.Artifacts.Evidence.Create(
                        kind: verdict.Verifier,
                        deterministic: verdict.Deterministic,
                        passed: verdict.Passed,
                        missionId: mission.Id,
                        // The proposal the verdict is ABOUT, and the SNAPSHOT it was computed
                        // against. Evidence that cannot be traced to the change it judged is why
                        // per-proposal verification was worth the work; evidence that cannot be
                        // traced to the tree it ran in is why v3.8.22's build verdicts were
                        // meaningless — they were true statements about the wrong workspace.
                        detail: $"[{patchSet.Proposals[i].FilePath} @ {materialized.AppliedTreeHash[..12]}] {verdict.Summary}",
                        taskId: task.Id,
                        // v0.3.8.57 — the identity as STRUCTURED FIELDS, not only inside the prose
                        // above. The twelve-character hash in `Detail` is readable by a person and
                        // useless to a query, so "does this build result belong to the revision the
                        // verifier is about to promote?" had no answer the runtime could compute.
                        // Both hashes are recorded because they answer different questions: the
                        // patch-set hash is what was asked for, the tree hash is what landed, and
                        // they differ exactly when evidence must not be reused.
                        revisionId: $"rev:{patchSet.Id}",
                        patchSetHash: materialized.PatchSetHash,
                        treeHash: materialized.AppliedTreeHash));

            // The set is promotable only if EVERY proposal is. One unverifiable change in a set is an
            // unverifiable set — a patch is applied as a unit, so it must be judged as one.
            var failed = bundles.Where(b => !b.Promotable).ToList();
            var promotable = failed.Count == 0;

            _memory.LogEvent(mission.Id, "patch_set_verified",
                $"Verification ran for {task.TaskType} (resolved: {Verification.VerificationPolicy.Canonical(task.TaskType)}) " +
                $"over {bundles.Count} proposal(s): {bundles.Count - failed.Count}/{bundles.Count} promotable.",
                task.Id, task.AssignedAnt,
                new()
                {
                    ["patch_set_id"] = patchSet.Id,
                    ["promotable"] = promotable,
                    ["proposals"] = bundles.Count,
                    ["resolved_task_type"] = Verification.VerificationPolicy.Canonical(task.TaskType),
                    ["required_verifiers"] = string.Join(",", Verification.VerificationPolicy.For(task.TaskType)),
                    ["deterministic_evidence"] = bundles.All(b => b.HasDeterministicEvidence),
                    // Which tree the verdicts describe. Without these an operator reading a passing
                    // build has no way to tell whether it compiled the patch or the repository.
                    ["base_revision"] = materialized.BaseRevision,
                    ["patch_set_hash"] = materialized.PatchSetHash,
                    ["applied_tree_hash"] = materialized.AppliedTreeHash,
                    ["workspace_mode"] = materialized.Mode,
                    ["blocked_reasons"] = string.Join("; ",
                        failed.SelectMany(b => b.BlockedReasons.Concat(
                            b.Results.Where(r => !r.Passed).Select(r => $"{r.Verifier}: {r.Summary}")))
                            .Distinct()),
                });

            // v3.8.22: the verdict is now CONSEQUENTIAL. Until this line a non-promotable bundle was
            // written to an event row and read by nothing, so a patch that failed the build verifier
            // reached completed_verified exactly as if it had passed.
            if (!promotable)
                task.DeterministicBlock =
                    $"patch set {patchSet.Id}: {failed.Count} of {bundles.Count} proposal(s) not promotable — " +
                    string.Join("; ", failed.Take(3).Select(b => b.Explain()));

            // Structural repair §3/§4: the patched tree becomes the mission's CURRENT REVISION and
            // stays alive for the downstream tasks. Registering a second patch set replaces (and
            // disposes) the first — from that instant, the old revision's evidence can no longer
            // satisfy verification, which is the fresh-retest invariant made structural. The
            // producing task carries the revision id so the evaluator can pair candidate and
            // evidence without parsing anything.
            var revision = Workspaces.MissionRevisionRegistry.Register(mission.Id, task.Id, materialized);
            unregistered = null;   // the registry owns it now
            task.ProducedRevisionId = revision.RevisionId;

            // v0.3.8.91: fingerprint the LIVE tree at the moment the sandbox that verification reads
            // was built from it. Everything else the colony binds evidence to describes the patch —
            // the base revision, the patch-set content hash, and `AppliedTreeHash`, which despite
            // its name covers only the files the patch touched. None of them notices an edit to a
            // file the patch did NOT touch, which is the one the build might actually depend on.
            //
            // Captured here rather than at apply time for the obvious reason: later would fingerprint
            // a tree that had already had time to move.
            var fingerprint = Workspaces.WorkspaceFingerprint.Capture(AnthillRuntime.AllowedWorkspaceRoot);
            _memory.SetPatchSetBaseFingerprint(patchSet.Id, fingerprint);
            _memory.LogEvent(mission.Id, "mission_revision_registered",
                $"Revision {revision.RevisionId} registered: patch set {patchSet.Id} materialized at {revision.Root}",
                task.Id, task.AssignedAnt, new()
                {
                    ["revision_id"] = revision.RevisionId, ["patch_set_id"] = patchSet.Id,
                    ["patch_set_hash"] = revision.PatchSetHash, ["tree_hash"] = revision.TreeHash,
                    ["base_revision"] = revision.BaseRevision, ["mode"] = revision.Mode,
                });
        }
        catch (Exception error)
        {
            try { unregistered?.Dispose(); } catch { }
            Console.Error.WriteLine($"Verification faulted for task {task.Id}: {error.Message}");

            // FAILED TO RUN IS NOT THE SAME AS RAN AND PASSED, and until v0.3.8.91 this path treated
            // them alike. The catch logged and returned; `DeterministicBlock` stayed null; and
            // `ProcessPatchProposals` continued straight into `InsertPolicyReviewTasks` and
            // `ApplyUnderBypass`, whose FIRST gate is `task.DeterministicBlock is not null`. So a
            // fault in materialisation, workspace scope, the evidence store or revision registration
            // produced no block, and under a Bypass conversation the patch was written to the
            // operator's tree with no verification behind it at all.
            //
            // This method's own doc says "the approval pipeline still owns whether anything is
            // applied". That was true when it was written and stopped being true when the bypass
            // lane was added — a guarantee stated in one file and revoked in another. The block is
            // the mechanism that makes the sentence true again.
            //
            // `??=` rather than `=`: an earlier in-band refusal already wrote a more specific
            // reason, and overwriting it would replace "the build failed" with "verification
            // crashed" for an operator trying to understand which.
            task.DeterministicBlock ??=
                $"verification could not run for patch set {patchSet.Id}: {error.Message}. A patch "
              + "is promotable only on evidence that verification PASSED; a verifier that failed to "
              + "execute produced no evidence, which is not the same as producing none needed.";

            _memory.LogEvent(mission.Id, "patch_set_verification_faulted",
                $"Verification could not run: {error.Message}", task.Id, task.AssignedAnt,
                new()
                {
                    ["patch_set_id"] = patchSet.Id,
                    // Named so the operator's failure view can distinguish this from a verifier that
                    // ran and said no — different diagnosis, different fix.
                    ["promotable"] = false,
                    ["deterministic_block"] = task.DeterministicBlock,
                });

            try { _memory.SaveTask(mission.Id, task); }
            catch (Exception save)
            {
                // The block must outlive this process. If it cannot be persisted, say so loudly
                // rather than proceeding with an in-memory-only refusal that a restart forgets.
                Console.Error.WriteLine(
                    $"Could not persist the verification-fault block for task {task.Id}: {save.Message}");
            }
        }
    }

    /// <summary>
    /// The current bytes of a proposed file, or null when it is absent or unreadable. v0.3.8.57.
    ///
    /// Feeds <see cref="PatchProposalParser"/> so a newly produced modify/delete/rename records what
    /// it was built against. Returns null on ANY failure rather than throwing: a proposal whose base
    /// cannot be read is one the applier will refuse with a reason, and losing the whole patch set to
    /// an exception here would be a worse answer than a proposal that has to be re-read.
    ///
    /// Path containment is the guard's, not ours — an absolute path aimed at the live checkout
    /// throws out of ResolveSafePath and lands in the same null.
    /// </summary>
    private static string? ReadForBaseHash(string filePath)
    {
        try
        {
            var guard = new Security.WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
            var resolved = guard.ResolveSafePath(filePath);
            return File.Exists(resolved) ? File.ReadAllText(resolved) : null;
        }
        catch { return null; }
    }

    private void ProcessPatchProposals(Mission mission, MissionContext context, Task task, TaskScheduler? scheduler)
    {
        if (string.IsNullOrEmpty(task.Result)) return;
        try
        {
            // v0.3.8.57 — the parser gets a reader, so newly produced destructive proposals carry a
            // base hash. Resolved through WorkspacePathGuard, which answers against the MISSION
            // workspace when a scope is active, so the hash records the tree the coder was actually
            // looking at rather than the live checkout.
            var patchSet = _patchParser.Parse(task.Result, mission.Id, task.Id, ReadForBaseHash);
            ProcessPatchSet(mission, context, task, patchSet, scheduler);
        }
        catch (Exception error)
        {
            _memory.LogEvent(mission.Id, "patch_proposal_parse_failed", $"Patch proposal parsing failed: {error.Message}", task.Id, task.AssignedAnt,
                new() { ["error"] = error.Message, ["raw_preview"] = TextUtil.Truncate(task.Result, 1000) });
            _memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", false, -0.03,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["error"] = error.Message });
        }
    }

    /// <summary>
    /// v0.3.8.93 — THE ONE PIPELINE EVERY PATCH SET GOES THROUGH, whoever produced it.
    ///
    /// Two producers exist: the coder's structured-JSON path (parsed from a model turn, above) and
    /// the acting-CLI path, whose filesystem diff <c>WorkspaceChangeSet.Create</c> turns into the
    /// same <c>PatchSet</c> type at mission finalization. Until this release only the FIRST reached
    /// verification, review insertion, approval cards and the bypass gate — a harvested change set
    /// was saved to the store and stopped there: no evidence, no approval request, no card, so work
    /// an acting agent produced in its isolated worktree was reviewable in principle and unreachable
    /// in practice. Same capability, two pipelines, one of them a stub — the divergence shape the
    /// promotion gate was built to end, one layer earlier.
    ///
    /// <paramref name="context"/> and <paramref name="scheduler"/> are null for the harvested lane,
    /// which runs at finalization — after the plan's last task, when nothing can dispatch an
    /// inserted review task. The policy-review insertion is therefore SKIPPED there, and skipped
    /// LOUDLY: the event records that tester/soldier review did not run for this set, so the
    /// promotion gate's evidence requirements (which still stand — nothing here waives them) read
    /// against an honest record rather than a silent gap.
    /// </summary>
    internal void ProcessPatchSet(Mission mission, MissionContext? context, Task task, PatchSet patchSet,
        TaskScheduler? scheduler)
    {
        // Indentation note: the body below kept its original depth when it moved out of
        // ProcessPatchProposals' try block, so the diff stays reviewable as a move rather than a
        // rewrite. The extra brace pair is that move's scar, not a scope with meaning.
        {
            _memory.SavePatchSet(patchSet);
            var patchArtifactId = RecordPatchArtifact(mission, task, patchSet,
                context?.EnvironmentFingerprint ?? "");
            VerifyPatchSet(mission, task, patchSet);

            // v3.8.26: the review roles are INSERTED here, not planned.
            //
            // This is the input the policy waits for. A patch set existing is the condition that
            // makes a test run and a security review meaningful, and it is knowable from the
            // colony's own state rather than from whether a model remembered to include the step.
            //
            // AFTER RecordPatchArtifact deliberately: the soldier reads the patch-set artifact
            // (v3.8.25), so inserting its task before the artifact exists would schedule a review of
            // something not yet written. The ordering here IS the contract between the two.
            if (context is not null && scheduler is not null)
                InsertPolicyReviewTasks(mission, context, task, patchSet, scheduler, patchArtifactId);
            else if (patchSet.Proposals.Count > 0)
                // The harvested lane, at finalization: the mission's task graph is closed, so an
                // inserted tester/soldier task would sit unscheduled forever. Saying so is the
                // record the operator reads when the promotion gate refuses on missing review.
                _memory.LogEvent(mission.Id, "policy_review_skipped",
                    $"Policy review tasks were not inserted for patch set {patchSet.Id}: the set was "
                  + "harvested at mission finalization, after the task graph closed. The promotion "
                  + "gate's evidence requirements still apply to every proposal.",
                    task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["reason"] = "harvested_at_finalization" });
            _memory.LogEvent(mission.Id, "patch_set_created", $"Patch set created with {patchSet.Proposals.Count} proposal(s).", task.Id, task.AssignedAnt,
                new() { ["patch_set_id"] = patchSet.Id, ["proposal_count"] = patchSet.Proposals.Count, ["summary"] = patchSet.Summary, ["saved"] = true });

            // v0.3.8.51 (field report): "Skip all approvals" means WHAT IT SAYS — the operator set
            // Bypass in words, so a verified patch applies WITHOUT a card. Prompts are skipped,
            // security is not: a DeterministicBlock (failed build verifier, policy finding, scope
            // escape) leaves the patch unapplied exactly as it would refuse anyone else, and the
            // apply below runs the same audited transitions the operator's own button does.
            // Automatically approve deliberately keeps the manual apply card — that policy means
            // "act freely, but ask me before changing real files."
            ApplyUnderBypass(mission, task, patchSet);
            if (patchSet.Proposals.Count == 0)
            {
                _memory.LogEvent(mission.Id, "patch_set_empty", "CoderAnt returned a valid patch set with no proposals.", task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["summary"] = patchSet.Summary });
                _memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", true, 0.005,
                    new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["proposal_count"] = 0, ["reason"] = "valid_empty_patch_set" });
                return;
            }
            foreach (var proposal in patchSet.Proposals)
            {
                _memory.LogEvent(mission.Id, "patch_proposal_created", $"Patch proposal created for {proposal.FilePath}", task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value(), ["requires_approval"] = proposal.RequiresApproval, ["status"] = proposal.Status.Value() });
                // Autonomous objectives re-propose the same change run after run while the first
                // request sits unreviewed — don't stack identical approval requests.
                if (_memory.HasDuplicatePendingApproval(proposal))
                {
                    _memory.LogEvent(mission.Id, "approval_request_deduped",
                        $"Identical change for {proposal.FilePath} is already awaiting approval — no duplicate request created.", task.Id, "queen",
                        new() { ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value() });
                    continue;
                }
                var approval = CreatePatchApprovalRequest(mission, task, patchSet, proposal);
                _memory.SaveApprovalRequest(approval);
                _memory.LogEvent(mission.Id, "approval_request_created", $"Approval request created for patch proposal: {proposal.FilePath}", task.Id, "queen",
                    new() { ["approval_request_id"] = approval.Id, ["target_id"] = approval.TargetId, ["action_type"] = approval.ActionType.Value(), ["approval_status"] = approval.Status.Value() });
            }
            _memory.UpdatePheromoneTrail("capability:structured_patch_proposals", "capability", true, 0.03,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["proposal_count"] = patchSet.Proposals.Count, ["approval_requests_created"] = patchSet.Proposals.Count });
            _memory.UpdatePheromoneTrail("capability:approval_gate", "capability", true, 0.02,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["approval_requests_created"] = patchSet.Proposals.Count });
        }
    }

    /// <summary>
    /// v0.3.8.93 — the harvested lane's entry into <see cref="ProcessPatchSet"/>: a change set built
    /// from a mission workspace's filesystem diff at finalization. Called by the Queen, which owns
    /// the harvest moment; anchored to the task whose work produced the changes so verification
    /// faults and approval cards attribute to real work rather than to a synthetic row.
    /// </summary>
    public void ProcessHarvestedPatchSet(Mission mission, Task anchorTask, PatchSet patchSet) =>
        ProcessPatchSet(mission, context: null, anchorTask, patchSet, scheduler: null);

    private static ApprovalRequest CreatePatchApprovalRequest(Mission mission, Task task, PatchSet patchSet, PatchProposal proposal) => new()
    {
        MissionId = mission.Id, TaskId = task.Id, ActionType = ApprovalActionType.PatchProposal, TargetId = proposal.Id,
        Title = $"Approve patch proposal for {proposal.FilePath}",
        Description = $"Patch proposal requires approval before application.\nFile: {proposal.FilePath}\nChange Type: {proposal.ChangeType.Value()}\n" +
                      $"Reason: {proposal.Reason}\nRisk: {proposal.Risk}\n\nApproval alone does not apply the patch. Use /apply <approval_id> after approval and after enabling write gates.",
        Metadata = new() { ["patch_set_id"] = patchSet.Id, ["patch_proposal_id"] = proposal.Id, ["file_path"] = proposal.FilePath, ["change_type"] = proposal.ChangeType.Value(), ["requires_approval"] = proposal.RequiresApproval, ["patch_application_enabled"] = AnthillRuntime.EnablePatchApplication, ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting },
    };

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
        // v3.0.1: carry the ant's structured degraded-generation disclosure onto the task so the
        // canonical evaluator can see it. A fallback ant returns succeeded_with_warnings with a
        // provider_failure warning — this reads that structure, never the result prose.
        task.GenerationDegraded = execution.StatusCode == "succeeded_with_warnings"
            && execution.Warnings.Any(w => w.Contains("provider_failure", StringComparison.Ordinal));

        // v3.8.22: the same treatment for a deterministic policy block. The soldier computes its
        // verdict from PolicyScan before any model text exists and marks a blocking result; this
        // carries that onto the task so the canonical evaluator sees it. Nothing read the soldier's
        // block before this line, which made "not overridable" in its own summary untrue.
        //
        // NOT overwritten if something already set it — a task can be blocked by more than one
        // deterministic check (a patch set here, its policy review there) and the first reason is as
        // valid as the second. Losing one to a later assignment would understate why.
        if (task.DeterministicBlock is null
            && execution.Warnings.Any(w => string.Equals(w, Agents.SoldierAnt.SoldierBlockMarker, StringComparison.Ordinal)))
            task.DeterministicBlock =
                $"policy review blocked: {string.Join(", ", execution.Warnings.Where(w => w != Agents.SoldierAnt.SoldierBlockMarker))}";

        _memory.LogEvent(mission.Id, "task_execution_recorded",
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
                // Structural repair §10 — fallback exposure, all derived from STRUCTURE. A role can
                // do useful deterministic work while its routed model is down; what it may never do
                // is look like model execution succeeded. These fields keep the two stories apart
                // in the durable record: role_invoked is always true here (this event exists),
                // model_executed only when calls were actually made AND generation was not the
                // degraded fallback, fallback_used mirrors the provider_failure disclosure.
                ["role_invoked"] = true,
                ["model_requested_for_role"] = runtimeSelection.ExecutorRoleId,
                ["model_calls_made"] = execution.Metrics.ModelCalls,
                ["model_executed"] = execution.Metrics.ModelCalls > 0 && !task.GenerationDegraded,
                ["fallback_used"] = task.GenerationDegraded,
                ["generation_degraded"] = task.GenerationDegraded,
                ["deterministic_work_completed"] = execution.Evidence.Any(e =>
                    Anthill.SDK.Artifacts.EvidenceKinds.Reproducible.Contains(e.Kind) || e.Kind == "check"),
                // v3.8.32: wire form, matching every other failure_class in the tree. An event
                // stream that spells a class differently from the tables is a query that silently
                // returns nothing.
                ["failure_class"] = execution.Failure is { } ef ? FailureClassNames.Wire(ef.Class) : null,
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
    /// <remarks>
    /// v3.8.32: INTERNAL rather than private, so the handoff gate below can be exercised directly.
    /// It was private, and the consequence was that the tester→medic route — the colony's entire
    /// repair path — had no test that ran this method at all. Anthill.Core already grants
    /// InternalsVisibleTo("Anthill.Tests"); this widens nothing for anyone else.
    /// </remarks>
    internal void ApplyNonCompletingOutcome(Mission mission, MissionContext context, Task task,
        AntRuntimeSelection runtimeSelection,
        AntExecutionResult execution, TaskOutcomeDecision decision, DateTime finishedAt, double elapsed,
        TaskScheduler? scheduler)
    {
        task.Result = decision.Reason;

        // Whether the task is REALLY finished failing, as opposed to eligible for another attempt.
        // Only the scheduler can answer this: it holds the attempt budget. See the handoff gate below.
        bool terminallyFailed;

        if (decision.Action == TaskOutcomeAction.Skip)
        {
            if (scheduler is not null) scheduler.MarkSkipped(task.Id, decision.Reason, decision.FailureType);
            else { task.Status = TaskStatus.Skipped; task.SkippedAt = finishedAt; task.SkippedReason = decision.Reason; }
            terminallyFailed = false;
        }
        else
        {
            // The scheduler owns the retry decision: it knows the attempt budget. Retryable here
            // means "eligible", not "guaranteed" — which is exactly why its RETURN VALUE, and not
            // `decision.Retryable`, decides whether this failure was the last one.
            if (scheduler is not null)
                terminallyFailed = scheduler.MarkFailed(
                    task.Id, decision.Reason, decision.FailureType, decision.Retryable, finishedAt, elapsed);
            else
            {
                task.Status = TaskStatus.Failed; task.FailedAt = finishedAt;
                task.FailureReason = decision.Reason; task.FailureType = decision.FailureType;
                // With no scheduler there is no retry machinery, so this failure is final by
                // construction. Treating it as non-terminal would drop handoffs on every path that
                // runs without one, which is most of the test suite and the single-task API route.
                terminallyFailed = true;
            }
        }

        FinalizeTaskResult(mission, task);

        // v3.8.25 — HANDOFFS ARE INGESTED ON THE FAILURE PATH.
        //
        // Until this line, `IngestHandoffs` was called only after `decision.Action == Complete`, and
        // this method returns before reaching it. So a FAILED task's handoffs were recorded as
        // proposals and acted on by nothing — which made the tester's failure→medic handoff
        // unreachable in principle. The medic is triggered by failure and the only route to it was
        // gated on success. The colony's repair path could not fire, ever.
        //
        // FAIL ONLY, not Skip and not a retry-bound failure:
        //   - Skip means the task did not run. It has no findings, and its declared handoffs are
        //     proposals about work that never happened.
        //   - The scheduler owns retries. Ingesting on a failure that is about to be retried would
        //     dispatch a medic to diagnose a task the colony has not finished attempting, and then
        //     again on the next attempt — a repair loop bounded by nothing.
        //
        // Terminal failure is the one state where a diagnosis is both warranted and final.
        //
        // v3.8.32 — the reasoning above was right and the CONDITION was wrong. It read
        // `!decision.Retryable`, which is derived from the ant's status code, not from anything the
        // scheduler decided. The tester emits `failed_retryable` on every failed check, so
        // `decision.Retryable` was true on every attempt including the one that exhausted the budget
        // — and the tester→medic handoff, declared `required: true`, was dropped every single time.
        // The repair loop the previous comment claimed to have fixed still could not fire.
        //
        // `MarkFailed` already returned the right answer ("true when terminally failed; false when a
        // bounded retry was scheduled") and this line threw it away. It is now the gate.
        if (decision.Action == TaskOutcomeAction.Fail && terminallyFailed)
        {
            // Structural repair §2: the typed failure record is produced HERE, at the boundary
            // where the failure became terminal — BEFORE the handoffs are ingested, so the medic
            // task that a failure handoff creates finds its failure_context already persisted.
            // Recovery consumes this artifact; it never reconstructs failure state from prose.
            RecordFailureContext(mission, task, execution, runtimeSelection);
            IngestHandoffs(mission, context, task, execution, runtimeSelection, scheduler);
        }

        _memory.LogEvent(mission.Id, "task_outcome_applied",
            $"Task did not complete ({execution.StatusCode}): {task.Title}", task.Id, runtimeSelection.RuntimeNodeId,
            MergeMetadata(AntRuntime.Metadata(runtimeSelection), new()
            {
                ["status_code"] = execution.StatusCode, ["action"] = decision.Action.ToString(),
                ["retryable"] = decision.Retryable, ["failure_type"] = decision.FailureType,
                ["reason"] = TextUtil.Truncate(decision.Reason, 500), ["elapsed_seconds"] = elapsed,
            }));
        Console.WriteLine($"Task {execution.StatusCode}: {task.Title} ({elapsed}s) — {TextUtil.Truncate(decision.Reason, 160)}");
    }

    /// <summary>v0.3.8.51 — the Bypass path's unprompted apply. Refuses without a Bypass
    /// conversation, with a deterministic block standing, with any proposal the promotion gate
    /// refuses, or without a transactional set applier; logs every outcome. v0.3.8.91 applies the
    /// set as ONE unit rather than one proposal at a time.</summary>
    private void ApplyUnderBypass(Mission mission, Task task, PatchSet patchSet)
    {
        if (patchSet.Proposals.Count == 0) return;
        try
        {
            if (task.DeterministicBlock is not null)
            {
                _memory.LogEvent(mission.Id, "patch_bypass_blocked",
                    $"Skip-all-approvals did NOT apply patch set {patchSet.Id}: {task.DeterministicBlock}",
                    task.Id, task.AssignedAnt, new() { ["patch_set_id"] = patchSet.Id });
                return;
            }
            var conversation = _memory.FindConversationForMission(mission.Id);
            if (conversation?.EffectivePolicy != Conversations.EscalationPolicy.Bypass) return;

            var who = $"bypass-policy({conversation.PolicySetBy ?? "operator"})";

            // THE GATE, AS BYPASS, FOR EVERY PROPOSAL — AND THEN THE SET AS ONE UNIT. v0.3.8.91.
            //
            // Two defects lived in the loop this replaces. It reached the apply path having checked
            // only the block and the policy, and that path then satisfied its own human gate with an
            // approval row this very call had just created and approved — a synthesized approval is
            // not a human, so the human gate was answering nobody. And it applied `foreach
            // (proposal)` and CONTINUED past a failure, so a three-file set whose second proposal hit
            // a stale base left files one and three written: a tree nothing verified, described by a
            // verification record that judged the set as a whole.
            //
            // Now every proposal faces the gate as `Bypass` — the human is skipped by policy and
            // nothing else is — and the set is refused entirely if any one of them is refused. Then
            // the whole set goes through one transaction that preflights every target, journals, and
            // rolls everything back on any failure.
            var refusals = new List<string>();
            foreach (var proposal in patchSet.Proposals)
            {
                var verdict = Verification.PatchPromotionGate.Evaluate(
                    _memory, (Anthill.SDK.Artifacts.IEvidenceStore)_memory, proposal.Id,
                    Verification.PromotionActor.Bypass);

                if (!verdict.Promotable)
                    refusals.Add($"{proposal.FilePath} [{verdict.Layer}]: {verdict.Reason}");
            }

            if (refusals.Count > 0)
            {
                _memory.LogEvent(mission.Id, "patch_bypass_apply_refused",
                    $"Skip-all-approvals did not apply patch set {patchSet.Id}: "
                  + $"{refusals.Count} of {patchSet.Proposals.Count} proposal(s) were refused, so none "
                  + "were applied. " + string.Join(" | ", refusals.Take(5)),
                    task.Id, task.AssignedAnt,
                    new()
                    {
                        ["patch_set_id"] = patchSet.Id, ["ok"] = false,
                        ["refused_count"] = refusals.Count, ["refusals"] = refusals,
                    });
                return;
            }

            if (_applyPatchSet is null)
            {
                // No set-level applier wired (CLI shapes, tests without a Queen). The old behaviour
                // here was a per-proposal loop; degrading to that would reintroduce the partial-set
                // write this release removed, so it degrades to the manual card instead.
                _memory.LogEvent(mission.Id, "patch_bypass_apply_refused",
                    $"Skip-all-approvals did not apply patch set {patchSet.Id}: no transactional "
                  + "set applier is wired in this host, and a set is not applied one file at a time.",
                    task.Id, task.AssignedAnt,
                    new() { ["patch_set_id"] = patchSet.Id, ["ok"] = false, ["reason"] = "no_set_applier" });
                return;
            }

            var outcome = _applyPatchSet(patchSet.Id, who);
            _memory.LogEvent(mission.Id,
                outcome.Applied ? "patch_bypass_applied" : "patch_bypass_apply_refused",
                $"Skip-all-approvals {(outcome.Applied ? "applied" : "did not apply")} patch set "
              + $"{patchSet.Id}: {outcome.Message}",
                task.Id, task.AssignedAnt,
                new()
                {
                    ["patch_set_id"] = patchSet.Id, ["ok"] = outcome.Applied,
                    ["applied_count"] = outcome.Count, ["refusals"] = outcome.Refusals,
                });
        }
        catch (Exception error)
        {
            // Refusing to apply is always a safe failure; the card remains for the operator.
            Console.Error.WriteLine($"[execution] bypass apply failed for {patchSet.Id}: {error.Message}");
        }
    }

    /// <summary>
    /// v0.3.8.51 — resolve what the operator ALLOWED for this mission's work: the owning
    /// conversation's effective policy (wire form) and the project's granted directories. Cached
    /// per call rather than per mission on purpose: a policy change mid-mission should govern the
    /// tasks dispatched after it, exactly as the escalation gate already behaves.
    /// </summary>
    private IDisposable EnterAgentAccess(Mission mission, string roleId)
    {
        var policy = "ask";
        IReadOnlyList<string> grants = Array.Empty<string>();
        string? workingDirectory = null;

        // v0.3.8.93 — the role's write capability, read from ITS OWN contract in the registry.
        // Fail closed: an unknown role gets no write translation, for the same reason
        // ToolAuthorization denies an unknown identity — a name the registry cannot vouch for
        // must never widen access. ProposePatches OR WriteWorkspace, because both are "this role's
        // contract contemplates changing files"; everything else is a reader however it is routed.
        var roleMayWrite = AntRegistry.ByRole.TryGetValue(roleId ?? "", out var roleDef)
            && (roleDef.Permissions.ProposePatches || roleDef.Permissions.WriteWorkspace);
        try
        {
            var conversation = _memory.FindConversationForMission(mission.Id);
            if (conversation is not null)
            {
                policy = conversation.EffectivePolicy.ToString().ToLowerInvariant();

                // v0.3.8.58 — the SAME resolution the conversation lane used, not a second one.
                //
                // This used to read grants inline and never resolve a working directory, which was
                // survivable only because the chat lane resolved both for the agent that actually
                // ran. With chat deleted, a mission is the only lane, so both answers have to be
                // right here: grants (which include the colony's own source tree as reach, and did
                // not in the inline copy) and the project's tree, or a project's mission would run
                // in whatever directory the provider defaults to.
                grants = Conversations.ConversationRunner.ProjectGrantPaths(_memory, conversation);
                workingDirectory = Conversations.ConversationRunner.ProjectDirectory(_memory, conversation);
            }
        }
        catch (Exception error)
        {
            // A failed lookup must degrade toward LESS access, never more — and must not fail the task.
            Console.Error.WriteLine($"[execution] agent access lookup failed for {mission.Id}: {error.Message}");
        }
        // confinedWorkspace: mission tasks run in disposable sandboxes/worktrees, never the live tree.
        return Anthill.SDK.Reasoning.AgentAccessScope.Enter(
            policy, grants, confinedWorkspace: true, workingDirectory: workingDirectory,
            roleMayWrite: roleMayWrite);
    }

    /// <summary>
    /// Structural repair §2 — the typed <c>failure_context</c> artifact, produced at the terminal
    /// failure boundary from STRUCTURED state only: the ant's typed <see cref="AntFailure"/>, its
    /// typed evidence rows, the ambient workspace scope's revision identity, and the runtime
    /// selection. No prose is parsed here, and an execution that carried no typed failure is
    /// recorded as <see cref="FailureClass.UnknownFailure"/> — unknown STAYS unknown; it is never
    /// promoted into InternalDefect by absence of information.
    ///
    /// Failure to record the context must never mask the failure itself, so this catches and logs.
    /// </summary>
    internal void RecordFailureContext(Mission mission, Task task, AntExecutionResult execution,
        AntRuntimeSelection runtimeSelection)
    {
        try
        {
            var cls = execution.Failure?.Class ?? FailureClass.UnknownFailure;
            if (cls == FailureClass.None) cls = FailureClass.UnknownFailure;
            var rawError = execution.Failure?.Reason ?? task.FailureReason ?? execution.Summary ?? "";

            var failingChecks = execution.Evidence
                .Where(e => e.Kind == "check" && (e.Detail?.Contains("success=False") ?? false))
                .Select(e => e.Value).ToList();
            var affectedPaths = execution.Evidence
                .Where(e => e.Kind == "file_path").Select(e => e.Value).ToList();

            var scope = Workspaces.MissionWorkspaceScope.Current;
            var context = new Anthill.SDK.Artifacts.FailureContext
            {
                MissionId = mission.Id,
                FailedTaskId = task.Id,
                FailedRole = task.AssignedAnt,
                TaskType = task.TaskType,
                Attempt = Math.Max(1, task.AttemptCount),
                FailureClass = FailureClassNames.Wire(cls),
                FailureCode = task.FailureType,
                Retryable = execution.Failure?.Retryable ?? FailureClassify.IsRetryable(cls),
                RawError = TextUtil.Truncate(rawError, 2000),
                NormalizedError = Anthill.SDK.Artifacts.FailureContext.NormalizeError(rawError),
                Provider = runtimeSelection.RuntimeNodeId,
                FailingChecks = failingChecks,
                Tool = execution.Evidence.FirstOrDefault(e => e.Kind == "tool")?.Value,
                ArtifactKinds = execution.Artifacts.Select(a => a.Kind).Distinct().ToList(),
                AffectedPaths = affectedPaths,
                PatchSetId = scope?.MaterializedPatchSetId,
                BaseRevision = scope?.BaseRevision,
                WorkspaceId = scope?.Id,
                EnvironmentFingerprint = execution.Metrics.EnvironmentFingerprint,
            };

            ((Anthill.SDK.Artifacts.IArtifactStore)_memory).Put(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.FailureContext,
                producerRole: task.AssignedAnt,
                missionId: mission.Id,
                payload: context.ToJson(),
                taskId: task.Id));

            _memory.LogEvent(mission.Id, "failure_context_recorded",
                $"failure_context recorded for '{task.Title}': {context.FailureClass}, signature {context.FailureSignature}",
                task.Id, task.AssignedAnt, new()
                {
                    ["failure_class"] = context.FailureClass,
                    ["failure_signature"] = context.FailureSignature,
                    ["retryable"] = context.Retryable,
                    ["failing_checks"] = failingChecks,
                });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[execution] could not record failure_context for {task.Id}: {error.Message}");
        }
    }

    /// <summary>
    /// v2.20.0: the archivist's memory candidates finally have a consumer. Each well-formed
    /// candidate becomes a durable memory_candidate event with provenance — queryable by reporting,
    /// never fed to planning, never certified here (auto_promote is recorded, not acted on).
    /// Runs only on the completion path: a blocked or failed archival produced no candidates.
    /// </summary>
    /// <summary>The interface surface for the post-finalization archivist; the task path keeps
    /// calling the private one, so there is exactly one implementation.</summary>
    public void IngestMemoryCandidatesFor(Mission mission, Task task, AntExecutionResult execution) =>
        IngestMemoryCandidates(mission, task, execution);

    private void IngestMemoryCandidates(Mission mission, Task task, AntExecutionResult execution)
    {
        var candidates = Outcomes.MemoryCandidateIngest.Extract(execution);
        foreach (var candidate in candidates)
            _memory.LogEvent(mission.Id, Outcomes.MemoryCandidateIngest.EventType,
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
    public void IngestHandoffs(Mission mission, MissionContext context, Task sourceTask, AntExecutionResult execution,
        AntRuntimeSelection runtimeSelection, TaskScheduler? scheduler)
    {
        // v3.1.0: the gate is read from the mission's OWN resolved capability set. Flipping the
        // static mid-mission can no longer change what an in-flight mission is permitted to do.
        if (!context.Options.HandoffIngestion || execution.Handoffs.Count == 0) return;

        var depth = HandoffGate.NextDepthFrom(sourceTask);
        var constraints = context.Constraints;

        foreach (var proposed in execution.Handoffs)
        {
            var handoff = proposed with { Depth = depth };
            var admission = HandoffGate.Evaluate(handoff, mission);
            if (!admission.Accepted || admission.CreatedTask is null)
            {
                LogHandoffRejected(mission, sourceTask, handoff, admission.Reason, runtimeSelection);
                RecordRequiredHandoffRefusal(mission, sourceTask, handoff, admission.Reason);
                continue;
            }

            var created = admission.CreatedTask;
            created.ParentTaskIds = new List<string> { sourceTask.Id };

            if (TryAdmitDynamicTask(mission, scheduler, created, constraints) is { Length: > 0 } refusal)
            {
                LogHandoffRejected(mission, sourceTask, handoff, refusal, runtimeSelection);
                RecordRequiredHandoffRefusal(mission, sourceTask, handoff, refusal);
                continue;
            }

            _memory.LogEvent(mission.Id, "handoff_admitted",
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

    /// <summary>
    /// A refused REQUIRED handoff is a deterministic block. v3.8.25.
    ///
    /// <c>AntHandoff.Required</c> has existed since v2.21.0 and meant nothing. A refusal was written
    /// to an event row and read by no gate, so a mission whose tester demanded a medic — and did not
    /// get one — completed exactly as if the repair had happened. "Required" that nothing enforces is
    /// a comment with a bool's type.
    ///
    /// It demotes rather than fails the mission, and the distinction is deliberate. The work that ran
    /// still ran and its results are still worth keeping; what cannot be claimed is that the mission
    /// is VERIFIED, because a step its own roles declared necessary did not happen. That is precisely
    /// what <c>Task.DeterministicBlock</c> means — a reproducible "no" the canonical evaluator honours
    /// — so this reuses it rather than inventing a second demotion path beside it.
    ///
    /// An OPTIONAL handoff refusal stays a log line. Optional means the colony proposed something it
    /// can do without, and treating a declined suggestion as a block would make every capped or
    /// deduplicated handoff a mission failure.
    /// </summary>
    private void RecordRequiredHandoffRefusal(Mission mission, Task sourceTask, AntHandoff handoff, string reason)
    {
        if (!handoff.Required) return;

        var block = $"required handoff refused: {handoff.SourceRole} -> {handoff.DestinationRole} " +
                    $"({handoff.RequiredTaskType}) — {TextUtil.Truncate(reason, 200)}";

        // First reason wins, as everywhere else DeterministicBlock is set: a task can be blocked by
        // more than one deterministic check and the earliest is as valid as the latest.
        sourceTask.DeterministicBlock ??= block;

        _memory.LogEvent(mission.Id, "required_handoff_refused",
            $"REQUIRED handoff refused, mission cannot be verified: {handoff.SourceRole} -> {handoff.DestinationRole} — {reason}",
            sourceTask.Id, handoff.SourceRole,
            new()
            {
                ["destination_role"] = handoff.DestinationRole,
                ["required_task_type"] = handoff.RequiredTaskType,
                ["dedupe_key"] = handoff.DedupeKey,
                ["rejection_reason"] = reason,
                ["blocks_verification"] = true,
            });
    }

    private void LogHandoffRejected(Mission mission, Task sourceTask, AntHandoff handoff, string reason,
        AntRuntimeSelection runtimeSelection) =>
        _memory.LogEvent(mission.Id, "handoff_rejected",
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
    /// <summary>
    /// Insert the review roles whenever their input exists. v3.8.26 — the last Stage B item.
    ///
    /// `SchedulingMode.PolicyInserted` was declared in v3.8.23 and left UNENFORCED in v3.8.25,
    /// deliberately: nothing inserted these roles, so blocking the planner from scheduling them
    /// would have removed their only path. This is the replacement, and enforcing the rule now
    /// removes nothing.
    ///
    /// What "policy" means here is deliberately small. It is not a model, not a heuristic, and not a
    /// plan — it is the observation that a patch set now exists, which is exactly the condition under
    /// which a test run and a security review have something to say. A plan that omits the tester is
    /// not a plan that skipped a step; it is a plan whose patches are unverified, produced by the
    /// component least able to be relied on for that.
    ///
    /// Each inserted task carries the coder task as its PARENT, which is what lets it past the
    /// scheduling rule in `AntRegistry.ValidateTask` — the same discriminator handoffs use, for the
    /// same reason: this task was caused by something that happened, not scheduled speculatively.
    /// </summary>
    private void InsertPolicyReviewTasks(Mission mission, MissionContext context, Task coderTask,
        PatchSet patchSet, TaskScheduler? scheduler, string? patchArtifactId = null)
    {
        if (patchSet.Proposals.Count == 0) return;

        // The evidence tasks this patch set now has, collected so the verifier can be made to wait
        // for them. v0.3.8.41 — see EnsureVerificationWaitsFor.
        var inserted = new List<string>();

        foreach (var role in new[] { "tester", "soldier" })
        {
            // A role whose gate is closed is SKIPPED and SAID SO. Silently not inserting it would
            // make "the review did not run" indistinguishable from "the review found nothing",
            // which is the confusion this whole program exists to remove.
            if (!AntExecutorCatalog.RuntimeAvailable(role))
            {
                _memory.LogEvent(mission.Id, "policy_review_skipped",
                    $"{role} review not inserted for patch set {patchSet.Id}: "
                    + AntExecutorCatalog.Snapshot.GetValueOrDefault(role)?.UnavailabilityReason,
                    coderTask.Id, "queen",
                    new() { ["role"] = role, ["patch_set_id"] = patchSet.Id, ["inserted"] = false });
                continue;
            }

            // One review per role per patch set. Without this an autonomous objective re-proposing
            // the same change stacks a review task on every run — the same failure the approval
            // pipeline already dedupes against.
            var marker = $"policy-review:{role}:{patchSet.Id}";
            if (mission.Tasks.Any(t => t.Description.Contains(marker, StringComparison.Ordinal))) continue;

            var created = new Task
            {
                Title = role == "tester" ? "Run checks on the proposed change" : "Security review of the proposed change",
                Description = role == "tester"
                    ? $"Run the workspace's declared checks against patch set {patchSet.Id}. [{marker}]"
                    : $"Review patch set {patchSet.Id} for secrets, forbidden paths, permission expansion and scope. [{marker}]",
                AssignedAnt = role,
                TaskType = role == "tester" ? "test_execution" : "security_review",
                ParentTaskIds = new List<string> { coderTask.Id },
                DependsOn = new List<string> { coderTask.Id },
                // CRITICAL. A failed safety review must be able to stop the mission reaching a
                // verified outcome — MissionEvaluator disqualifies on a failed critical task.
                Critical = true,
                // v0.3.8.57 — THE artifact this review exists to review, named rather than
                // searched for. This is the one insertion point in the colony where the producer
                // is unambiguous: the patch set was written one statement ago. Everywhere else
                // the mission-wide fallback still applies, because guessing a narrower input
                // would starve a worker of context it legitimately used.
                InputArtifactIds = patchArtifactId is { Length: > 0 }
                    ? new List<string> { patchArtifactId }
                    : new List<string>(),
            };

            if (TryAdmitDynamicTask(mission, scheduler, created, context.Constraints) is { Length: > 0 } refusal)
            {
                _memory.LogEvent(mission.Id, "policy_review_refused",
                    $"{role} review could not be inserted for patch set {patchSet.Id}: {refusal}",
                    coderTask.Id, "queen",
                    new() { ["role"] = role, ["patch_set_id"] = patchSet.Id, ["reason"] = refusal });
                continue;
            }

            _memory.LogEvent(mission.Id, "policy_review_inserted",
                $"{role} review inserted for patch set {patchSet.Id} — by policy, not by the plan.",
                created.Id, role,
                new() { ["role"] = role, ["patch_set_id"] = patchSet.Id, ["source_task_id"] = coderTask.Id });
            Console.WriteLine($"Policy inserted {role} review for patch set {patchSet.Id}.");
            inserted.Add(created.Id);
        }

        // v0.3.8.41 — and the VERIFIER waits for both of them.
        EnsureVerificationWaitsFor(mission, context, coderTask, scheduler, inserted,
            because: $"patch set {patchSet.Id} must be verified against its own test and security evidence",
            evidence: new() { ["patch_set_id"] = patchSet.Id });
    }

    /// <summary>
    /// The verifier runs AFTER the evidence it is supposed to read. v0.3.8.41.
    ///
    /// THE DEFECT. `SchedulingMode.PolicyInserted` covers tester and soldier; the verifier stayed
    /// `PlannerSelectable`, and `AutoWireDependencies` wires it to "everything before it" — which
    /// means everything the PLANNER produced. The tester and soldier tasks do not exist at planning
    /// time; they are inserted later, when a patch set appears. So the verifier's dependency set was
    /// computed before its two most important inputs existed, and it could be dispatched, ask a model
    /// whether the mission succeeded, and answer — while the checks it was meant to be reading had
    /// not run.
    ///
    /// Nothing failed when that happened. The verifier returns a verdict either way, and a verdict
    /// reached without evidence looks exactly like one reached with it. That is the shape of every
    /// defect in this repository's record: a check answering an adjacent question, and passing.
    ///
    /// TWO PATHS, ONE RULE. If a verification task already exists — planned, or added by the adaptive
    /// controller — its dependencies are WIDENED rather than a second one being created. Two
    /// verifiers on one mission is worse than none, because the colony then holds two verdicts about
    /// one deliverable with no rule for which wins. If none exists, one is inserted, parented to the
    /// task whose output made verification meaningful.
    ///
    /// Widening only applies to a task that has not started. A verifier that already ran cannot be
    /// made to have waited, and quietly adding a dependency to a completed task would produce a graph
    /// that claims an ordering the run did not have.
    /// </summary>
    private void EnsureVerificationWaitsFor(Mission mission, MissionContext context, Task sourceTask,
        TaskScheduler? scheduler, List<string> evidenceTaskIds, string because,
        Dictionary<string, object?> evidence)
    {
        // Nothing to wait for. Inserting a verifier with no evidence to read would be theatre.
        if (evidenceTaskIds.Count == 0) return;

        if (!AntExecutorCatalog.RuntimeAvailable("verifier"))
        {
            _memory.LogEvent(mission.Id, "verification_skipped",
                "Verification not inserted: "
                + (AntExecutorCatalog.Snapshot.GetValueOrDefault("verifier")?.UnavailabilityReason ?? "unavailable"),
                sourceTask.Id, "queen",
                MergeMetadata(new Dictionary<string, object?>(evidence), new() { ["role"] = "verifier" }));
            return;
        }

        // The VERIFIER role, not `MissionVerification.IsVerificationTask`.
        //
        // That helper answers "is this task a verification STEP", and its role set is
        // {verifier, tester, soldier} — correct for grading a mission, and wrong here by exactly the
        // margin that matters: the tester task inserted four lines ago satisfies it, so this lookup
        // would find the tester, decide a verifier already exists, and wire the tester to depend on
        // the soldier. A real verdict would never be scheduled and nothing would say so. Using a
        // near-enough predicate for a question it was not written for is the defect this file's
        // history is mostly made of.
        var existing = mission.Tasks.FirstOrDefault(t =>
            string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase)
            && t.Status is TaskStatus.Pending or TaskStatus.Ready or TaskStatus.Blocked);

        if (existing is not null)
        {
            var added = evidenceTaskIds
                .Where(id => id != existing.Id && !existing.DependsOn.Contains(id))
                .ToList();
            if (added.Count == 0) return;

            existing.DependsOn = existing.DependsOn.Concat(added).ToList();
            // Re-evaluated so the scheduler sees the new edges before it can pick the task up.
            scheduler?.Evaluate();
            _memory.SaveTask(mission.Id, existing);

            _memory.LogEvent(mission.Id, "verification_bound_to_evidence",
                $"Verification now waits for {added.Count} evidence task(s): {because}.",
                existing.Id, "verifier",
                MergeMetadata(new Dictionary<string, object?>(evidence), new()
                {
                    ["verification_task_id"] = existing.Id,
                    ["waits_for"] = added,
                    ["source_task_id"] = sourceTask.Id,
                }));
            Console.WriteLine($"Verification bound to {added.Count} evidence task(s).");
            return;
        }

        // Already verified, or verification already failed. Either way there is a verdict on record
        // and a second one would not be a stronger answer. Verifier role only, for the reason above.
        if (mission.Tasks.Any(t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase)))
            return;

        var verify = new Task
        {
            Title = "Verify the deliverable against its evidence",
            Description = $"Verify the mission's deliverable against the evidence produced for it: {because}. "
                        + $"Goal: {TextUtil.Truncate(mission.Goal, 400)}",
            AssignedAnt = "verifier",
            TaskType = "verification",
            // The parent is what makes this admissible under the scheduling rule, and it is honest:
            // this task was caused by evidence arriving, not scheduled speculatively.
            ParentTaskIds = new List<string> { sourceTask.Id },
            DependsOn = evidenceTaskIds.Append(sourceTask.Id).Distinct().ToList(),
            // A mission that could not verify its own deliverable is not a verified mission.
            Critical = true,
        };

        if (TryAdmitDynamicTask(mission, scheduler, verify, context.Constraints) is { Length: > 0 } refusal)
        {
            // Fail CLOSED and say so. An unverifiable deliverable must not read as a verified one,
            // and `DeterministicBlock` is the existing mechanism for exactly that demotion.
            sourceTask.DeterministicBlock ??= $"verification could not be inserted: {refusal}";
            _memory.LogEvent(mission.Id, "verification_refused",
                $"Verification could not be inserted, so this mission cannot be verified: {refusal}",
                sourceTask.Id, "queen",
                MergeMetadata(new Dictionary<string, object?>(evidence), new()
                {
                    ["reason"] = refusal, ["blocks_verification"] = true,
                }));
            return;
        }

        _memory.LogEvent(mission.Id, "verification_inserted",
            $"Verification inserted by policy — {because}.", verify.Id, "verifier",
            MergeMetadata(new Dictionary<string, object?>(evidence), new()
            {
                ["waits_for"] = verify.DependsOn, ["source_task_id"] = sourceTask.Id,
            }));
        Console.WriteLine($"Policy inserted verification after {sourceTask.AssignedAnt}.");
    }

    /// <summary>
    /// The non-change branch of the lifecycle: a draft deliverable exists, so it gets verified.
    /// v0.3.8.41.
    ///
    /// Stage 4A of the canonical flow is "Builder creates a typed draft deliverable, THEN Verifier is
    /// inserted automatically after the draft exists". For a repository-change mission the trigger is
    /// the patch set and its assurance evidence; for an informational one it is the deliverable
    /// itself, because there is no patch to test and the thing being verified is the answer.
    ///
    /// Guarded on there being no patch set: a code mission's builder writes the OPERATOR SUMMARY
    /// after verification, and treating that as a fresh deliverable to verify would insert a second
    /// verification of a mission that already has one.
    /// </summary>
    private void EnsureVerificationAfterDeliverable(Mission mission, MissionContext context, Task builderTask,
        TaskScheduler? scheduler)
    {
        if (_memory.CountPatchProposalsForMission(mission.Id) > 0) return;

        EnsureVerificationWaitsFor(mission, context, builderTask, scheduler,
            evidenceTaskIds: new List<string> { builderTask.Id },
            because: "a deliverable was produced and must be checked against the goal, its sources and "
                   + "the mission's constraints",
            evidence: new Dictionary<string, object?> { ["deliverable_task_id"] = builderTask.Id });
    }

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
        _memory.SaveTask(mission.Id, created);   // survives restart like any planned task
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
    /// <summary>
    /// Whether the adaptive stop that just happened was SATISFACTION rather than escalation.
    /// v0.3.8.74. Set by the one arm that stops because the work is already complete, and read by
    /// <see cref="AdaptiveStopReason"/> immediately afterwards.
    ///
    /// A field rather than a richer return type because <see cref="ApplyAdaptiveDecision"/> is
    /// called from three sites that all treat its bool as "stop now", and widening the contract
    /// would have meant changing three call sites to carry a value only one of them can produce.
    /// Reset on every call, so a satisfaction stop cannot be read by a later escalation.
    /// </summary>
    private bool _adaptiveStopWasSatisfaction;

    /// <summary>The stop reason for the decision just applied — see the note at the satisfaction arm.</summary>
    private string AdaptiveStopReason =>
        _adaptiveStopWasSatisfaction
            ? Outcomes.MissionStopReasons.AdaptiveStopSatisfied
            : Outcomes.MissionStopReasons.AdaptiveStop;

    private bool ApplyAdaptiveDecision(Mission mission, MissionContext context, TaskScheduler? scheduler, string? previousFingerprint)
    {
        // Every call answers afresh: a stop is satisfaction only if THIS decision says so.
        _adaptiveStopWasSatisfaction = false;

        if (!context.Options.AdaptiveMissionControl) return false;

        var budget = new AdaptiveBudget(
            ReplansUsed: _memory.GetRecentEvents(200, "adaptive_delta_plan", mission.Id).Count,
            RepairCyclesUsed: _memory.GetRecentEvents(200, "adaptive_repair", mission.Id).Count);

        var decision = _adaptive.Assess(mission, budget, previousFingerprint);
        if (decision.Action is AdaptiveAction.Continue or AdaptiveAction.Finish) return false;

        var constraints = context.Constraints;

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
                // v0.3.8.74 — THIS STOP IS A SUCCESS, and it used to be graded as an escalation.
                //
                // The controller wanted to add a verifier, looked, and found the mission already has
                // one. Nothing is wrong; there is simply nothing to add. But every stop returned the
                // single reason `adaptive_stop`, and `MissionEvaluation.Resolve` maps that
                // unconditionally to `escalated` — so a mission whose plan included a verifier, and
                // which passed every check and every review, was graded as escalated and could never
                // become `completed_verified`.
                //
                // The consequence is not cosmetic. Auto-apply consumes the canonical evaluation, so
                // this made a clean, fully verified patch mission structurally incapable of applying.
                // It was found by qualification scenario 3, which is the first test ever to drive a
                // mission from a goal to applied bytes and therefore the first to need this outcome.
                //
                // One reason code was answering two opposite questions: "we stopped because the
                // bound is spent and the problem persists" and "we stopped because the work is
                // already done". They are now separate reasons.
                _adaptiveStopWasSatisfaction = true;
                return true;
            }
            // Structural repair §7: the delta verifier VERIFIES the mission's completed work, so
            // that work is its lineage — parents and dependencies both. This was the one dynamic
            // creation path that produced an orphan (no ParentTaskIds, no DependsOn), which is the
            // historical reason the verifier stayed planner-selectable: the graph could not carry a
            // policy-inserted one. It can now.
            var verified = mission.Tasks
                .Where(t => t.Status == TaskStatus.Complete && !MissionVerification.IsVerificationTask(t))
                .Select(t => t.Id).ToList();
            var verify = new Task
            {
                Title = "Verify mission outcome",
                Description = $"Independently verify that the mission goal was met: {TextUtil.Truncate(mission.Goal, 400)} "
                            + $"[adaptive delta generation:{budget.ReplansUsed + 1}]",
                AssignedAnt = "verifier",
                TaskType = "verify",
                Critical = true,
                ParentTaskIds = verified,
                DependsOn = verified,
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

        _memory.LogEvent(mission.Id, eventType, $"{decision.Action}: {created.Title}", created.Id, created.AssignedAnt,
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
        _memory.LogEvent(mission.Id, "adaptive_escalated", $"Mission stopped by the adaptive controller: {reason}",
            metadata: new()
            {
                ["action"] = decision.Action.ToString(), ["reason"] = reason,
                ["unmet_criteria"] = decision.UnmetCriteria,
            });
        Console.WriteLine($"Adaptive stop: {reason}");
    }

    private void RecordAgentMessage(string missionId, string? taskId, string sender, string recipient, string messageType,
        string content, Dictionary<string, object?> metadata)
    {
        if (!AnthillRuntime.EnableAgentCommunicationLedger) return;
        _memory.LogAgentMessage(missionId, sender, recipient, messageType, content, taskId, metadata);
    }

    private static Dictionary<string, object?> MergeMetadata(Dictionary<string, object?> first, Dictionary<string, object?> second)
    {
        foreach (var (key, value) in second) first[key] = value;
        return first;
    }
}
