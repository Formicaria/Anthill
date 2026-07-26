using Anthill.Core.Domain;

namespace Anthill.Core.Agents;

/// <summary>
/// Execution framework Stage E — bounded handoff admission (spec §10). A specialist's handoff may
/// create a dynamic task ONLY when every gate passes: the destination is runtime-eligible, its
/// contract supports the required task type, handoff depth is under the limit, the mission task
/// budget holds, and no near-duplicate exists (dedupe key). Rejections carry the reason — nothing
/// is dropped silently, and recursive unlimited task creation is structurally impossible.
/// </summary>
public static class HandoffGate
{
    public const int MaxHandoffDepth = 2;
    public const int MaxMissionTasks = 12;

    public sealed record Admission(bool Accepted, string Reason, Task? CreatedTask);

    public static Admission Evaluate(AntHandoff handoff, Mission mission)
    {
        if (handoff.Depth > MaxHandoffDepth)
            return new(false, $"handoff depth {handoff.Depth} exceeds limit {MaxHandoffDepth}", null);

        if (mission.Tasks.Count >= MaxMissionTasks)
            return new(false, $"mission task budget exhausted ({mission.Tasks.Count}/{MaxMissionTasks})", null);

        if (!AntRegistry.ExecutableRoleIds.Contains(handoff.DestinationRole))
            return new(false, $"destination role '{handoff.DestinationRole}' is not runtime-eligible (gate closed or not executable)", null);

        var contract = AntExecutionCatalog.ContractFor(handoff.DestinationRole);
        if (contract is not null && !contract.SupportsTaskType(handoff.RequiredTaskType))
            return new(false, $"destination '{handoff.DestinationRole}' does not support task type '{handoff.RequiredTaskType}'", null);

        if (mission.Tasks.Any(t => t.Description.Contains(handoff.DedupeKey, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"near-duplicate handoff suppressed (dedupe '{handoff.DedupeKey}')", null);

        var created = new Task
        {
            Title = $"Handoff: {handoff.SourceRole} -> {handoff.DestinationRole}",
            Description = $"{handoff.Reason} [handoff dedupe:{handoff.DedupeKey} depth:{handoff.Depth}]",
            AssignedAnt = handoff.DestinationRole,
            TaskType = handoff.RequiredTaskType,
            Critical = handoff.Required,
        };
        return new(true, "", created);
    }
}
