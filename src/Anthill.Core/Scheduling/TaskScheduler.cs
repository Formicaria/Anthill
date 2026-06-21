using System.Text.RegularExpressions;
using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Native;

namespace Anthill.Core.Scheduling;

public sealed record TaskGraphIssue(string Code, string TaskId, string Message, string? DependencyId = null);

public sealed record TaskTransition(string TaskId, string FromStatus, string ToStatus, string? Reason = null, string? ReasonType = null);

/// <summary>Retained for v1.6.x import-compatibility checks.</summary>
public sealed class SchedulerNotImplementedException : Exception { }

/// <summary>
/// Pure task-graph state machine. It mutates the supplied <see cref="Task"/> objects but
/// knows nothing about executing ants, saving memory, or printing — the Queen remains the
/// orchestrator. This is a faithful port of <c>anthill/core/scheduler.py</c>: dependency
/// validation, cycle detection (delegated to the native kernel), ready/blocked/skipped
/// transitions, bounded retries, lifecycle metadata, and metadata-first graph export.
/// </summary>
public sealed partial class TaskScheduler
{
    public static readonly HashSet<TaskStatus> TerminalStatuses = new()
        { TaskStatus.Complete, TaskStatus.Failed, TaskStatus.Skipped, TaskStatus.Cancelled };

    private static readonly HashSet<TaskStatus> PermanentlyUnmetDependencyStatuses = new()
        { TaskStatus.Failed, TaskStatus.Skipped, TaskStatus.Cancelled };

    private static readonly HashSet<TaskStatus> WaitingDependencyStatuses = new()
        { TaskStatus.Pending, TaskStatus.Ready, TaskStatus.Blocked, TaskStatus.Running };

    public List<Task> Tasks { get; }
    public HashSet<string> DuplicateTaskIds { get; }
    public Dictionary<string, Task> TaskById { get; }
    public string? MissionId { get; }
    public List<TaskGraphIssue> ValidationIssues { get; private set; } = new();

    private readonly Func<DateTime> _nowFn;
    private readonly List<TaskTransition> _transitions = new();

    public TaskScheduler(IEnumerable<Task> tasks, string? missionId = null, Func<DateTime>? nowFn = null)
    {
        Tasks = tasks.ToList();
        _nowFn = nowFn ?? AnthillTime.NowUtc;
        MissionId = missionId;

        var idCounts = new Dictionary<string, int>();
        foreach (var task in Tasks) idCounts[task.Id] = idCounts.GetValueOrDefault(task.Id) + 1;
        DuplicateTaskIds = idCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();
        // Do not expose duplicate ids through TaskById; a naive dictionary would silently
        // overwrite earlier tasks and make execution ambiguous.
        TaskById = Tasks.Where(t => !DuplicateTaskIds.Contains(t.Id))
                        .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public List<TaskGraphIssue> ValidateGraph()
    {
        var issues = new List<TaskGraphIssue>();
        var taskIds = TaskById.Keys.ToHashSet();

        foreach (var duplicateId in DuplicateTaskIds.OrderBy(x => x, StringComparer.Ordinal))
            issues.Add(new TaskGraphIssue("duplicate_task_id", duplicateId,
                $"Duplicate task id {duplicateId} appears more than once."));

        foreach (var task in Tasks)
        {
            foreach (var depId in DependencyIds(task))
            {
                if (depId == task.Id)
                    issues.Add(new TaskGraphIssue("self_dependency", task.Id, $"Task {task.Id} depends on itself.", depId));
                else if (!taskIds.Contains(depId))
                    issues.Add(new TaskGraphIssue("missing_dependency", task.Id, $"Task {task.Id} depends on missing task id {depId}.", depId));
            }
            foreach (var parentId in ParentIds(task))
                if (parentId == task.Id)
                    issues.Add(new TaskGraphIssue("self_parent", task.Id, $"Task {task.Id} lists itself as a parent.", parentId));
        }

        foreach (var taskId in CycleTaskIds().OrderBy(x => x, StringComparer.Ordinal))
            issues.Add(new TaskGraphIssue("cycle", taskId, $"Task {taskId} participates in a dependency cycle."));

        ValidationIssues = issues;
        return issues;
    }

    /// <summary>Validate the graph and move invalid tasks to skipped, then evaluate readiness.</summary>
    public List<TaskGraphIssue> Prepare()
    {
        var issues = ValidateGraph();
        foreach (var issue in issues)
        {
            if (issue.Code == "duplicate_task_id")
            {
                foreach (var task in TasksForId(issue.TaskId))
                    if (!TerminalStatuses.Contains(task.Status))
                        SetStatus(task, TaskStatus.Skipped, issue.Message, issue.Code);
            }
            else if (issue.Code is "self_dependency" or "missing_dependency" or "cycle")
            {
                if (TaskById.TryGetValue(issue.TaskId, out var task) && !TerminalStatuses.Contains(task.Status))
                    MarkSkipped(task.Id, issue.Message, issue.Code);
            }
        }
        Evaluate();
        return issues;
    }

    /// <summary>Refresh pending/blocked/ready/skipped states from dependency state (to a fixed point).</summary>
    public void Evaluate()
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var task in Tasks)
            {
                if (TerminalStatuses.Contains(task.Status) || task.Status == TaskStatus.Running) continue;
                if (DuplicateTaskIds.Contains(task.Id))
                {
                    changed = SetStatus(task, TaskStatus.Skipped,
                        $"Task skipped because duplicate task id is ambiguous: {task.Id}", "duplicate_task_id") || changed;
                    continue;
                }

                var depIds = DependencyIds(task);
                if (depIds.Count == 0)
                {
                    changed = SetStatus(task, TaskStatus.Ready, null, null) || changed;
                    continue;
                }

                var missing = depIds.Where(d => !TaskById.ContainsKey(d)).ToList();
                if (missing.Count > 0)
                {
                    changed = SetStatus(task, TaskStatus.Skipped,
                        $"Task skipped because dependencies are missing: [{string.Join(", ", missing)}]", "missing_dependency") || changed;
                    continue;
                }

                var depStatuses = depIds.ToDictionary(d => d, d => TaskById[d].Status);
                // Only CRITICAL dependencies that end Failed/Skipped/Cancelled propagate a skip.
                // A non-critical dependency (e.g. a spec-ingestion section) that fails is tolerated:
                // the dependent still waits for it to finish, then proceeds with partial input.
                var terminallyUnmet = depStatuses
                    .Where(kv => PermanentlyUnmetDependencyStatuses.Contains(kv.Value) && IsCriticalDependency(kv.Key))
                    .Select(kv => kv.Key).ToList();
                if (terminallyUnmet.Count > 0)
                {
                    changed = SetStatus(task, TaskStatus.Skipped,
                        $"Task skipped because dependencies cannot complete: [{string.Join(", ", terminallyUnmet)}]", "failed_dependency") || changed;
                    continue;
                }

                var waiting = depStatuses.Where(kv => WaitingDependencyStatuses.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                if (waiting.Count > 0)
                {
                    changed = SetStatus(task, TaskStatus.Blocked,
                        $"Waiting on dependencies: [{string.Join(", ", waiting)}]", "waiting_dependency") || changed;
                    continue;
                }

                changed = SetStatus(task, TaskStatus.Ready, null, null) || changed;
            }
        }
    }

    public List<Task> ReadyTasks()
    {
        Evaluate();
        return Tasks.Where(t => t.Status == TaskStatus.Ready).ToList();
    }

    public Task? NextReadyTask() => ReadyTasks().FirstOrDefault();

    public bool MarkRunning(string taskId)
    {
        if (DuplicateTaskIds.Contains(taskId)) return false;
        var task = TaskById[taskId];
        if (task.Status is not (TaskStatus.Ready or TaskStatus.Pending)) return false;
        task.AttemptCount += 1;
        task.StartedAt = _nowFn();
        task.FinishedAt = null;
        task.ElapsedSeconds = null;
        task.BlockedReason = null;
        return SetStatus(task, TaskStatus.Running, null, null);
    }

    public void MarkComplete(string taskId, string? result = null, DateTime? completedAt = null, double? elapsedSeconds = null)
    {
        var task = TaskById[taskId];
        if (result is not null) task.Result = result;
        var completed = completedAt ?? _nowFn();
        task.CompletedAt = completed;
        task.FinishedAt = completed;
        task.ElapsedSeconds = elapsedSeconds;
        task.FailureReason = null;
        task.FailureType = null;
        task.BlockedReason = null;
        SetStatus(task, TaskStatus.Complete, null, null);
        Evaluate();
    }

    /// <returns>True when terminally failed; false when a bounded retry was scheduled.</returns>
    public bool MarkFailed(string taskId, string reason, string failureType = "execution_error",
        bool retryable = true, DateTime? failedAt = null, double? elapsedSeconds = null)
    {
        var task = TaskById[taskId];
        task.FailureReason = reason;
        task.FailureType = failureType;
        task.Result = reason;
        task.FinishedAt = failedAt ?? _nowFn();
        task.ElapsedSeconds = elapsedSeconds;
        var maxAttempts = Math.Max(1, task.MaxAttempts);
        var attempts = Math.Max(0, task.AttemptCount);
        if (retryable && attempts < maxAttempts)
        {
            SetStatus(task, TaskStatus.Ready,
                $"Retry scheduled after failed attempt {attempts}/{maxAttempts}: {reason}", "retry_scheduled");
            return false;
        }
        task.FailedAt = failedAt ?? _nowFn();
        SetStatus(task, TaskStatus.Failed, reason, failureType);
        Evaluate();
        return true;
    }

    public void MarkSkipped(string taskId, string reason, string reasonType = "skipped")
    {
        if (DuplicateTaskIds.Contains(taskId))
        {
            foreach (var task in TasksForId(taskId)) SetStatus(task, TaskStatus.Skipped, reason, reasonType);
            Evaluate();
            return;
        }
        SetStatus(TaskById[taskId], TaskStatus.Skipped, reason, reasonType);
        Evaluate();
    }

    public void SkipRemaining(string reason, string reasonType = "scheduler_stop")
    {
        foreach (var task in Tasks)
            if (task.Status is TaskStatus.Pending or TaskStatus.Ready or TaskStatus.Blocked)
                MarkSkipped(task.Id, reason, reasonType);
    }

    public bool IsFinished() => Tasks.All(t => TerminalStatuses.Contains(t.Status));

    public List<TaskTransition> ConsumeTransitions()
    {
        var transitions = new List<TaskTransition>(_transitions);
        _transitions.Clear();
        return transitions;
    }

    public Dictionary<string, object?> ExportGraph(bool includeResults = false, bool includeResultPreview = true, int previewChars = 240)
    {
        Evaluate();
        var childIds = Tasks.ToDictionary(t => t.Id, _ => new List<string>());
        var edges = new List<Dictionary<string, string>>();
        foreach (var task in Tasks)
        {
            foreach (var depId in DependencyIds(task))
            {
                edges.Add(new() { ["from"] = depId, ["to"] = task.Id, ["type"] = "depends_on" });
                if (childIds.TryGetValue(depId, out var kids)) kids.Add(task.Id);
            }
            foreach (var parentId in ParentIds(task))
            {
                edges.Add(new() { ["from"] = parentId, ["to"] = task.Id, ["type"] = "parent_task" });
                if (childIds.TryGetValue(parentId, out var kids)) kids.Add(task.Id);
            }
        }

        var nodes = new List<Dictionary<string, object?>>();
        foreach (var task in Tasks)
        {
            var node = new Dictionary<string, object?>
            {
                ["mission_id"] = MissionId, ["task_id"] = task.Id, ["title"] = task.Title, ["name"] = task.Title,
                ["assigned_ant"] = task.AssignedAnt, ["assigned_agent"] = task.AssignedAnt, ["role"] = task.AssignedAnt,
                ["task_type"] = task.TaskType, ["status"] = task.Status.Value(), ["critical"] = task.Critical,
                ["dependency_ids"] = DependencyIds(task), ["depends_on"] = DependencyIds(task),
                ["parent_task_id"] = task.ParentTaskId, ["parent_task_ids"] = ParentIds(task),
                ["child_task_ids"] = childIds.GetValueOrDefault(task.Id, new()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["attempt_count"] = task.AttemptCount, ["max_attempts"] = task.MaxAttempts,
                ["created_at"] = task.CreatedAt.ToIso(), ["started_at"] = task.StartedAt.ToIsoOrNull(),
                ["completed_at"] = task.CompletedAt.ToIsoOrNull(), ["failed_at"] = task.FailedAt.ToIsoOrNull(),
                ["skipped_at"] = task.SkippedAt.ToIsoOrNull(), ["failure_type"] = task.FailureType,
                ["status_message"] = StatusMessage(task), ["elapsed_seconds"] = task.ElapsedSeconds,
            };
            if (includeResultPreview && !string.IsNullOrEmpty(task.ResultSummary))
                node["result_summary_preview"] = SafePreview(task.ResultSummary, previewChars);
            if (includeResults) node["result_summary"] = task.ResultSummary;
            nodes.Add(node);
        }

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "task-graph-v2", ["mission_id"] = MissionId, ["nodes"] = nodes, ["edges"] = edges,
            ["validation_issues"] = ValidationIssues.Select(i => new Dictionary<string, object?>
            {
                ["code"] = i.Code, ["task_id"] = i.TaskId, ["message"] = i.Message, ["dependency_id"] = i.DependencyId,
            }).ToList(),
        };
    }

    public string? StatusMessage(Task task) => task.Status switch
    {
        TaskStatus.Failed => task.FailureReason,
        TaskStatus.Skipped => task.SkippedReason,
        TaskStatus.Blocked => task.BlockedReason,
        TaskStatus.Ready => "Dependencies satisfied; task is ready to run.",
        _ => null,
    };

    /// <summary>A dependency counts as critical unless the dependency task is explicitly marked non-critical.</summary>
    private bool IsCriticalDependency(string depId) => !TaskById.TryGetValue(depId, out var dep) || dep.Critical;

    public List<string> DependencyIds(Task task) =>
        (task.DependsOn ?? new List<string>()).Select(d => d?.ToString() ?? "").Where(d => d.Length > 0).ToList();

    public List<string> ParentIds(Task task)
    {
        var ids = (task.ParentTaskIds ?? new List<string>()).Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList();
        if (!string.IsNullOrEmpty(task.ParentTaskId) && !ids.Contains(task.ParentTaskId)) ids.Add(task.ParentTaskId);
        return ids;
    }

    private List<Task> TasksForId(string taskId) => Tasks.Where(t => t.Id == taskId).ToList();

    private bool SetStatus(Task task, TaskStatus status, string? reason, string? reasonType)
    {
        var previous = task.Status;
        if (previous == status)
        {
            if (status == TaskStatus.Blocked && reason is not null) task.BlockedReason = reason;
            return false;
        }

        var now = _nowFn();
        task.Status = status;
        switch (status)
        {
            case TaskStatus.Ready: task.BlockedReason = null; break;
            case TaskStatus.Blocked: task.BlockedReason = reason; break;
            case TaskStatus.Skipped:
                task.SkippedReason = reason; task.SkippedAt = now; task.FinishedAt = now;
                task.ElapsedSeconds ??= 0.0; break;
            case TaskStatus.Failed:
                task.FailureReason = reason; task.FailureType = reasonType; task.FailedAt = now; task.FinishedAt = now; break;
            case TaskStatus.Cancelled:
                task.SkippedReason = reason; task.SkippedAt = now; task.FinishedAt = now;
                task.ElapsedSeconds ??= 0.0; break;
        }
        _transitions.Add(new TaskTransition(task.Id, previous.Value(), status.Value(), reason, reasonType));
        return true;
    }

    /// <summary>
    /// Detects every task on a dependency cycle. The graph is handed to the native kernel
    /// as a dense edge list; the managed fallback inside <see cref="NativeKernel"/> produces
    /// identical results when the native library is unavailable.
    /// </summary>
    private HashSet<string> CycleTaskIds()
    {
        var indexed = TaskById.Keys.ToList();
        if (indexed.Count == 0) return new HashSet<string>();
        var indexOf = indexed.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var from = new List<int>();
        var to = new List<int>();
        foreach (var task in Tasks)
        {
            if (!indexOf.TryGetValue(task.Id, out var childIdx)) continue;
            foreach (var depId in DependencyIds(task))
                if (indexOf.TryGetValue(depId, out var parentIdx))
                {
                    from.Add(parentIdx); // dependency
                    to.Add(childIdx);    // dependent task
                }
        }

        var flags = NativeKernel.DetectCycles(indexed.Count, from.ToArray(), to.ToArray());
        var result = new HashSet<string>();
        for (var i = 0; i < indexed.Count; i++) if (flags[i]) result.Add(indexed[i]);
        return result;
    }

    private static string SafePreview(string value, int maxChars)
    {
        var redacted = SensitiveAssignment().Replace(value ?? "", "$1=[redacted]");
        return TextUtil.Truncate(redacted, maxChars, "...[preview truncated]");
    }

    [GeneratedRegex(@"(?i)\b(token|api[_-]?key|password|passwd|secret|authorization)\b\s*[:=]\s*[^,\s;]+")]
    private static partial Regex SensitiveAssignment();
}
