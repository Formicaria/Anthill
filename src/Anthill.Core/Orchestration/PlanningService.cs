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
/// A plan and the facts needed to explain it, resolved together.
///
/// v3.1.0: the plan-preview endpoint used to receive only the task list and then re-derive its own
/// answers — parsing the goal for constraints and re-running <c>AntRegistry.ValidateTask</c> over
/// every task to rebuild warnings the planning path had already computed. That is the same
/// re-derivation defect one layer out: two readings of one plan, free to disagree. The plan now
/// carries its own explanation.
/// </summary>
/// <param name="Tasks">The admitted task graph.</param>
/// <param name="Constraints">The constraints this plan was built under, resolved at intake.</param>
/// <param name="SpecIngestion">Whether the goal was ingested section-by-section as a long
/// specification rather than planned as a single analysis.</param>
public sealed record MissionPlan(List<Task> Tasks, MissionConstraints Constraints, bool SpecIngestion)
{
    /// <summary>Tasks the ant registry refused, with the reason it gave. Read off the plan rather
    /// than recomputed, so an operator's warning list cannot disagree with what dispatch did.</summary>
    public IReadOnlyList<Task> Refused =>
        Tasks.Where(t => t.FailureType == PlanningService.AdmissionRefusedFailureType).ToList();

    public IReadOnlyList<string> RefusalReasons =>
        Refused.Select(t => t.FailureReason ?? "refused").Distinct(StringComparer.Ordinal).ToList();
}

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
    /// The plan: planned, task types inferred, workers resolved, and each task put through
    /// <see cref="AntRegistry.ValidateTask"/> — a task the registry refuses comes back already
    /// Failed, because a plan containing work the runtime will not authorize should say so before
    /// anything tries to run it.
    ///
    /// There is exactly ONE plan construction, used by dispatch and by the preview endpoint alike.
    /// The preview briefly had its own copy that skipped admission; that is the defect this
    /// interface exists to make impossible, so no "preview mode" parameter is offered here.
    /// </summary>
    List<Task> CreatePlan(MissionContext context);
}

public sealed class PlanningService : IPlanningService
{
    /// <summary>The failure type a task carries when the ant registry refused it at admission.
    /// Named once so the plan, the API, and the runtime cannot spell it differently.</summary>
    public const string AdmissionRefusedFailureType = "ant_permission_denied";

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

    public List<Task> CreatePlan(MissionContext context)
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

            // The authorization verdict is part of the plan, not something a caller opts into. An
            // operator reviewing a preview is approving the plan that will actually run, so a task
            // the registry refuses must be visibly refused there too.
            var selection = AntRegistry.ValidateTask(task, context.Constraints);
            if (selection.Allowed) continue;
            task.Status = TaskStatus.Failed;
            task.FailureType = AdmissionRefusedFailureType;
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

        // Structural repair §6: MANDATORY VERIFICATION IS RUNTIME POLICY, NOT A PLANNER OPTION.
        // The planner may request verification; it cannot omit it. A model-generated plan that
        // produces a consequential deliverable (any admitted work task) and names no verifier gets
        // one appended here — bound by lineage and dependency to every deliverable-producing task,
        // so a planner omission can never yield an unverified mission that merely looks complete.
        EnsurePlanVerification(tasks);
        return tasks;
    }

    /// <summary>Append the verifier the plan omitted, when the plan contains admissible work.</summary>
    internal static void EnsurePlanVerification(List<Task> tasks)
    {
        var admissible = tasks.Where(t => t.Status != TaskStatus.Failed).ToList();
        if (admissible.Count == 0) return;
        if (admissible.Any(t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase)))
            return;
        if (!AntExecutorCatalog.RuntimeAvailable("verifier")) return;   // said elsewhere, honestly, at insert time

        var work = admissible.Select(t => t.Id).ToList();
        tasks.Add(new Task
        {
            Title = "Verify result",
            Description = "Independently verify the mission's deliverable against the work that produced it. "
                        + "[inserted by runtime policy: the plan omitted verification]",
            AssignedAnt = "verifier",
            TaskType = "verification",
            ParentTaskIds = work,
            DependsOn = work,
            Critical = true,
        });
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
