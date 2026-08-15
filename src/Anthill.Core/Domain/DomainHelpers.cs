using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Domain;

/// <summary>
/// Model-aware helpers that need to see <see cref="Task"/>/<see cref="Mission"/>:
/// the public structural dependency contract and the compact context-packet builder.
/// Kept aligned with the scheduler's structural validation so self-test never lags runtime.
/// </summary>
public static class DomainHelpers
{
    /// <summary>
    /// Returns structural task-dependency errors without mutating tasks. The scheduler
    /// still owns state transitions, retries, and failure propagation; this is the
    /// public/self-test contract that must agree with <c>TaskScheduler.ValidateGraph</c>.
    /// </summary>
    public static List<string> ValidateTaskDependencyContract(IReadOnlyList<Task> tasks)
    {
        var errors = new List<string>();
        var idCounts = new Dictionary<string, int>();
        foreach (var task in tasks)
            idCounts[task.Id] = idCounts.GetValueOrDefault(task.Id) + 1;
        var duplicateIds = idCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();
        foreach (var id in duplicateIds.OrderBy(x => x, StringComparer.Ordinal))
            errors.Add($"Duplicate task id {id} appears more than once.");

        var taskIds = tasks.Where(t => !duplicateIds.Contains(t.Id)).Select(t => t.Id).ToHashSet();
        var taskById = tasks.Where(t => !duplicateIds.Contains(t.Id)).ToDictionary(t => t.Id);

        foreach (var task in tasks)
        {
            var depIds = task.DependsOn ?? new List<string>();
            var parentIds = new List<string>(task.ParentTaskIds ?? new List<string>());
            if (!string.IsNullOrEmpty(task.ParentTaskId)) parentIds.Add(task.ParentTaskId);
            if (depIds.Contains(task.Id)) errors.Add($"Task {task.Id} depends on itself.");
            if (parentIds.Contains(task.Id)) errors.Add($"Task {task.Id} lists itself as a parent.");
            if (duplicateIds.Contains(task.Id)) continue;
            foreach (var depId in depIds)
                if (!taskIds.Contains(depId))
                    errors.Add($"Task {task.Id} depends on missing task id {depId}.");
        }

        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        var cycleNodes = new HashSet<string>();

        void Visit(string taskId, List<string> path)
        {
            if (visiting.Contains(taskId))
            {
                var start = path.IndexOf(taskId);
                if (start >= 0) cycleNodes.UnionWith(path.Skip(start));
                else cycleNodes.Add(taskId);
                return;
            }
            if (visited.Contains(taskId)) return;
            visiting.Add(taskId);
            if (taskById.TryGetValue(taskId, out var task))
                foreach (var depId in task.DependsOn ?? new List<string>())
                    if (taskById.ContainsKey(depId))
                        Visit(depId, path.Append(depId).ToList());
            visiting.Remove(taskId);
            visited.Add(taskId);
        }

        foreach (var task in tasks) Visit(task.Id, new List<string> { task.Id });
        foreach (var id in cycleNodes.OrderBy(x => x, StringComparer.Ordinal))
            errors.Add($"Task {id} participates in a dependency cycle.");
        return errors;
    }

    /// <summary>
    /// Builds the compact context packet a downstream ant consumes: mission framing plus
    /// summary-first task blocks, with selective raw extracts only for whitelisted roles.
    /// </summary>
    /// <param name="artifacts">v3.8.29 (Stage C): the mission's artifact store, so the packet can
    /// carry the TYPED record alongside the prose. Optional — a null store produces exactly the
    /// packet this method has always produced, which is what lets the interchange land without
    /// touching the CLI, the older ants, or a hundred tests.</param>
    /// <param name="declaredInputIds">v0.3.8.57 — the artifacts the CONSUMING TASK was
    /// authoritatively given. Non-empty narrows the typed block to exactly these; empty keeps the
    /// mission-wide block every task received before. See <see cref="ArtifactContext.Compile"/>.</param>
    /// <param name="consumerTaskId">v0.3.8.57 — the task this packet is being built for, so the
    /// consumption ledger can say WHICH task read an artifact and not merely which role. A role
    /// reads many times across a mission; a task reads once, which is the unit a replay needs.</param>
    public static string BuildContextPacketText(Mission mission, string consumerRole, int maxTotalChars,
        int maxItemChars = -1, Anthill.SDK.Artifacts.IArtifactStore? artifacts = null,
        IReadOnlyList<string>? declaredInputIds = null, string? consumerTaskId = null)
    {
        if (maxItemChars < 0) maxItemChars = AnthillRuntime.MaxContextItemChars;

        if (!AnthillRuntime.EnableContextPackets)
        {
            var rawBlocks = mission.Tasks.Where(t => !string.IsNullOrEmpty(t.Result)).Select(t =>
                $"Task: {t.Title}\nAnt: {t.AssignedAnt}\nTask Type: {t.TaskType}\nStatus: {t.Status.Value()}\nResult:\n{t.Result}");
            return TextUtil.Truncate(string.Join("\n\n---\n\n", rawBlocks), maxTotalChars, "...[context truncated]")
                 + ArtifactBlock(artifacts, mission.Id, maxTotalChars, declaredInputIds, consumerRole, consumerTaskId);
        }

        var allowedRawRoles = AnthillRuntime.RawContextRoles.GetValueOrDefault(consumerRole, new HashSet<string>());
        var blocks = new List<string>
        {
            "CONTEXT PACKET",
            $"Mission ID: {mission.Id}",
            $"Consumer Role: {consumerRole}",
            $"Mission Goal: {TextUtil.Truncate(mission.Goal, 500, "...[goal truncated]")}",
            "Mode: compact summaries with selective raw extracts",
        };

        var terminal = new HashSet<TaskStatus> { TaskStatus.Complete, TaskStatus.Failed, TaskStatus.Skipped };
        var included = 0;
        foreach (var item in mission.Tasks)
        {
            if (string.IsNullOrEmpty(item.Result) || !terminal.Contains(item.Status)) continue;
            if (included >= AnthillRuntime.MaxContextItemsPerPacket)
            {
                blocks.Add($"...[context item limit reached: {AnthillRuntime.MaxContextItemsPerPacket}]");
                break;
            }
            var summary = item.ResultSummary ?? TextUtil.CreateResultSummary(item.Result, AnthillRuntime.MaxContextSummaryChars);
            var block = $"Task ID: {item.Id}\nTitle: {item.Title}\nAnt: {item.AssignedAnt}\nTask Type: {item.TaskType}\n" +
                        $"Status: {item.Status.Value()}\nResult Summary:\n{summary}";
            if (allowedRawRoles.Contains(item.AssignedAnt))
            {
                var raw = TextUtil.Truncate(item.Result ?? "", maxItemChars, "...[raw extract truncated]");
                block += $"\nRaw Extract:\n{raw}";
            }
            blocks.Add(block);
            included++;
        }

        return TextUtil.Truncate(string.Join("\n\n---\n\n", blocks), maxTotalChars, "...[context packet truncated]")
             + ArtifactBlock(artifacts, mission.Id, maxTotalChars, declaredInputIds, consumerRole, consumerTaskId);
    }

    /// <summary>
    /// The typed-artifact half of a context packet. v3.8.29 (Stage C).
    ///
    /// Budgeted SEPARATELY from the prose rather than sharing its cap, and that is a deliberate
    /// trade. Sharing one budget would mean a long mission's narrative crowds out the structured
    /// record — which is the arrangement this stage exists to end, reproduced one level down. A
    /// quarter of the prose budget, floored so a small packet still gets a usable block.
    /// </summary>
    /// <remarks>
    /// PUBLIC since v0.3.8.57. Not every model-calling ant builds its prompt through
    /// <see cref="BuildContextPacketText"/> — the researcher assembles memory, pheromones and tool
    /// output itself — and those ants were therefore reaching a model having never seen a typed
    /// artifact. The alternative was a second copy of the quarter-budget rule at each such site,
    /// which is how two components come to disagree about how much context an artifact block may
    /// take. One function, every caller.
    /// </remarks>
    public static string ArtifactBlock(Anthill.SDK.Artifacts.IArtifactStore? artifacts,
        string missionId, int proseBudget, IReadOnlyList<string>? declaredInputIds = null,
        string? consumerRole = null, string? consumerTaskId = null)
    {
        if (artifacts is null) return "";
        var budget = Math.Max(1_500, proseBudget / 4);
        var block = ArtifactContext.Compile(artifacts, missionId, budget,
            declaredInputIds: declaredInputIds,
            consumerRole: consumerRole, consumerTaskId: consumerTaskId);
        return block.Length == 0 ? "" : "\n\n---\n\n" + block;
    }
}
