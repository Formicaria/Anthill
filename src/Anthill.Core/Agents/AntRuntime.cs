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

    public static Task PrepareWorkerTaskSnapshot(Task task, AntRuntimeSelection selection)
    {
        var copy = task.DeepCopy();
        var allowed = selection.Worker.AllowedTools.Count == 0 ? "none" : string.Join(", ", selection.Worker.AllowedTools);
        var forbidden = selection.Worker.ForbiddenTools.Count == 0 ? "none" : string.Join(", ", selection.Worker.ForbiddenTools);
        var context = $"""
Worker Runtime Context:
Selected worker: {selection.Worker.WorkerId} ({selection.Worker.DisplayName})
Parent role executor: {selection.ExecutorRoleId}
Worker purpose: {selection.Worker.Purpose}
Allowed worker tools: {allowed}
Forbidden worker tools: {forbidden}
Permission boundary: worker permissions cannot exceed parent role permissions; apply_patch is forbidden.

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
