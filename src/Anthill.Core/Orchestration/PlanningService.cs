using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Planning;
using Anthill.Core.Skills;
using Anthill.Core.Tools;

namespace Anthill.Core.Orchestration;

/// <summary>
/// v3.1.0 (ADR-001) — turning a goal into an admitted task graph.
///
/// This was inline in <c>Queen.RunMission</c>, and inline again — in a near-copy that had quietly
/// drifted — in <c>Queen.PlanPreview</c>. Planning is five distinct steps (assemble context, plan,
/// infer task types, resolve workers, admit through the authorization gate) followed by dependency
/// wiring, and having them written twice is precisely how a preview comes to describe a plan the
/// dispatch would not actually run.
///
/// The Queen remains the mission authority: this service constructs a plan and returns it. It does
/// not persist, log lifecycle events, execute, or decide anything about the mission's outcome.
/// </summary>
public interface IPlanningService
{
    /// <summary>
    /// The plan a dispatch will run: planned, task types inferred, workers resolved, and each task
    /// put through <see cref="AntRegistry.ValidateTask"/> — a task the registry refuses comes back
    /// already Failed, because a plan that contains work the runtime will not authorize should say
    /// so before anything tries to run it.
    /// </summary>
    List<Task> CreatePlan(MissionContext context);

    /// <summary>
    /// The same plan for operator review, WITHOUT the admission verdict applied.
    ///
    /// The difference from <see cref="CreatePlan"/> is deliberate and is preserved from v1.8.18
    /// behaviour rather than endorsed: the preview endpoint returns tasks with their planned status,
    /// so a task that dispatch would immediately refuse still renders as an ordinary planned step.
    /// In practice the planner's own constraint enforcement means this rarely diverges — but
    /// "rarely" is not "never", and a preview that disagrees with dispatch is worth naming.
    /// Recorded here as a known discrepancy for the v3.8.0 workflow templates to resolve; changing
    /// it now would change an API response, which v3.1.0 is not permitted to do.
    /// </summary>
    List<Task> PreviewPlan(MissionContext context);
}

public sealed class PlanningService : IPlanningService
{
    private readonly Planner _planner;
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _tools;
    private readonly Func<SkillRegistry> _skills;

    /// <param name="skills">A factory rather than an instance: the registry is hydrated lazily from
    /// the database and shared with the learning recorder, so the Queen keeps ownership of when it
    /// is loaded and this service simply asks for the current one.</param>
    public PlanningService(Planner planner, SqliteMemory memory, ToolRegistry tools, Func<SkillRegistry> skills)
    {
        _planner = planner;
        _memory = memory;
        _tools = tools;
        _skills = skills;
    }

    public List<Task> CreatePlan(MissionContext context) => Build(context, applyAdmission: true);

    public List<Task> PreviewPlan(MissionContext context) => Build(context, applyAdmission: false);

    private List<Task> Build(MissionContext context, bool applyAdmission)
    {
        var goal = context.Goal;

        // Memory limits are compile-time constants on AnthillRuntime, not operator-mutable gates —
        // reading them here is not the coupling ADR-001 removes. A const cannot drift between two
        // readers, which is the entire property that made the gates a problem.
        var memoryContext =
            $"Recent Memory:\n{_memory.FormatRecentMemory(AnthillRuntime.RecentMemoryLimit, AnthillRuntime.MemoryResultChars)}\n\n" +
            $"Relevant Memory:\n{_memory.FormatRelevantMemory(goal, AnthillRuntime.RelevantMemoryLimit, AnthillRuntime.MemoryResultChars)}";

        var tasks = _planner.CreateTasks(goal, context.Constraints, memoryContext, _tools.DescribeTools(),
            _memory.FormatPheromoneContext(8), SkillPlanningContext.Format(_skills()));

        foreach (var task in tasks)
        {
            if (task.TaskType == "general")
                task.TaskType = TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description);
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                task.AssignedWorker = AntRegistry.DefaultWorkerFor(
                    task.AssignedAnt, task.TaskType, $"{goal} {task.Title} {task.Description}")?.WorkerId;

            if (!applyAdmission) continue;

            var selection = AntRegistry.ValidateTask(task, context.Constraints);
            if (selection.Allowed) continue;
            task.Status = TaskStatus.Failed;
            task.FailureType = "ant_permission_denied";
            task.FailureReason = selection.Reason;
            task.Result = $"Task rejected by ant registry: {selection.Reason}";
        }

        // Spec-ingestion plans already carry explicit section→synthesis→verify wiring and
        // non-critical section flags; auto-wiring would only re-derive the same edges.
        if (context.Options.AutoDependencyWiring && !Planner.IsLongInput(goal))
        {
            var graph = new Mission { Goal = goal, Tasks = tasks };
            AutoWireDependencies(graph);
            tasks = graph.Tasks;
        }
        return tasks;
    }

    /// <summary>
    /// Default dependency edges for a plan that declared none: sources feed the coder, sources and
    /// the coder feed the builder, everything before it feeds the verifier. Explicit dependencies
    /// from the planner are always respected — this only fills in silence.
    /// </summary>
    internal static void AutoWireDependencies(Mission mission)
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
}
