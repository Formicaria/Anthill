using Anthill.Core.Common;
using Anthill.Core.Domain;

namespace Anthill.Core.Agents;

public sealed record AntRuntimeSelection(
    AntRoleDefinition Role,
    AntWorkerDefinition Worker,
    string ExecutorRoleId,
    string RuntimeNodeId,
    IReadOnlyList<string> AuditWarnings);

public static class AntRuntime
{
    public static AntRuntimeSelection Resolve(Task task, MissionConstraints constraints)
    {
        var selection = AntRegistry.ValidateTask(task, constraints);
        if (!selection.Allowed)
            throw new InvalidOperationException(selection.Reason);
        var role = AntRegistry.ByRole[task.AssignedAnt];
        var worker = !string.IsNullOrWhiteSpace(task.AssignedWorker) && AntRegistry.ByWorker.TryGetValue(task.AssignedWorker, out var found)
            ? found
            : AntRegistry.DefaultWorkerFor(task.AssignedAnt, task.TaskType, task.Description)
              ?? throw new InvalidOperationException($"No worker is registered for executable role: {task.AssignedAnt}");
        task.AssignedWorker = worker.WorkerId;
        return new(role, worker, role.RoleId, worker.WorkerId, BuildAuditWarnings(role, worker));
    }

    /// <summary>
    /// v0.3.8.93 — THE SNAPSHOT NAMES REAL TOOLS OR NONE. It used to present the registry's duty
    /// descriptors as "Allowed worker tools": names like `read_workspace_docs` and
    /// `read_task_outputs` that are worded as tools and implemented by nothing — the phantom-tool
    /// defect (ADR-006) reproduced inside every dispatched task's own prompt. A worker that asked
    /// for one was denied at dispatch and read as a weak model. The line now carries the role's
    /// actual dispatch allowlist from <see cref="Tools.ToolAuthorization.DispatchAllowlistFor"/> —
    /// the same table the denial would come from, so the prompt and the gate cannot disagree —
    /// and a role with no dispatchable tools is told so in words rather than handed fictions.
    /// The registry descriptors survive as what they always were, a duty description.
    /// </summary>
    public static Task PrepareWorkerTaskSnapshot(Task task, AntRuntimeSelection selection)
    {
        var copy = task.DeepCopy();
        var dispatchable = Tools.ToolAuthorization.DispatchAllowlistFor(selection.ExecutorRoleId);
        var tools = dispatchable.Count == 0
            ? "none — this worker reasons over the context it is given and dispatches no tools"
            : string.Join(", ", dispatchable.OrderBy(t => t, StringComparer.Ordinal));
        var context = $"""
Worker Runtime Context:
Selected worker: {selection.Worker.WorkerId} ({selection.Worker.DisplayName})
Parent role executor: {selection.ExecutorRoleId}
Worker purpose: {selection.Worker.Purpose}
Dispatchable tools: {tools}
Permission boundary: worker permissions cannot exceed parent role permissions; apply_patch is forbidden to every mission agent, as are shell_command and write_text_file.

Original task:
""";
        copy.Description = TextUtil.Truncate($"{context}\n{task.Description}", 6000, "...[worker task context truncated]");
        return copy;
    }

    public static Dictionary<string, object?> Metadata(AntRuntimeSelection selection) => new()
    {
        ["assigned_worker"] = selection.Worker.WorkerId,
        ["runtime_node"] = selection.RuntimeNodeId,
        ["executor_role"] = selection.ExecutorRoleId,
        ["worker_display_name"] = selection.Worker.DisplayName,
        ["worker_purpose"] = selection.Worker.Purpose,
        ["worker_allowed_tools"] = selection.Worker.AllowedTools,
        ["worker_forbidden_tools"] = selection.Worker.ForbiddenTools,
        ["permission_audit_warnings"] = selection.AuditWarnings,
    };

    private static IReadOnlyList<string> BuildAuditWarnings(AntRoleDefinition role, AntWorkerDefinition worker)
    {
        var warnings = new List<string>();
        if (worker.Permissions.ApplyPatches || role.Permissions.ApplyPatches)
            warnings.Add("apply_patch permission must remain false");
        if (!string.Equals(worker.ParentRoleId, role.RoleId, StringComparison.OrdinalIgnoreCase))
            warnings.Add("worker parent mismatch");
        if (worker.ForbiddenTools.Count == 0 || !worker.ForbiddenTools.Contains("apply_patch", StringComparer.OrdinalIgnoreCase))
            warnings.Add("apply_patch should be explicitly forbidden");
        return warnings;
    }
}
