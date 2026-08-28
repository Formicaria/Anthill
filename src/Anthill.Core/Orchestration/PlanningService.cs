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

        // v0.3.8.98 — the specification travels INTO planning, because worker assignment happens
        // inside `CreateTasks`. See the block below for what this release learned about the cost of
        // resolving one layer too late.
        var tasks = _planner.CreateTasks(goal, context.Constraints, memoryContext, _tools.DescribeTools(),
            _memory.FormatPheromoneContext(8), SkillPlanningContext.Format(_skills()),
            context.Specification);

        foreach (var task in tasks)
        {
            if (task.TaskType == "general")
                task.TaskType = TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description);
            // A task that reached here with no worker came from a path that does not assign one
            // (the planner's model-rejection fallbacks, and tasks inserted by constraint
            // enforcement). It gets the SAME resolver the planner uses, so there is exactly one
            // answer to "which worker serves this task" in the codebase.
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                Agents.WorkerResolution.Assign(task, goal, context.Specification);

            // v0.3.8.93 — a verified trail may replace a TIE-BREAK, and only a tie-break.
            //
            // A Specification or Keyword basis is a COMPATIBILITY decision and is final: no trail
            // is consulted, however strong, because reputation must never outrank compatibility. A
            // Default basis is declaration order — a guess — and the colony's own verified track
            // record (worker trails, reinforced only by completed_verified missions) is better
            // evidence than a guess. The event makes each use visible, because a learning signal
            // that silently steers dispatch is the kind of influence an operator must be able to
            // audit.
            //
            // v0.3.8.98 — AND THIS RULE NOW ACTUALLY RUNS. It was written inside
            // `if (string.IsNullOrWhiteSpace(task.AssignedWorker))`, and `Planner.CreateTasks` has
            // always filled that field before returning; the condition was therefore false on every
            // planner path and the trail was never consulted in a real mission. The basis is read
            // off the task rather than re-derived here, so this layer cannot reach a different
            // conclusion than the layer that made the assignment.
            if (task.WorkerBasis == Domain.WorkerDecisionBasis.Default
                && !string.IsNullOrWhiteSpace(task.AssignedWorker))
            {
                var candidates = Agents.WorkerResolution.CompatibleCandidates(
                    task.AssignedAnt, context.Specification.RequiredCapabilities);
                var preferred = Pheromones.TrailGuidedSelection.Prefer(
                    candidates, key => _memory.GetPheromoneTrail(key));
                if (preferred is not null && preferred.WorkerId != task.AssignedWorker)
                {
                    var previous = task.AssignedWorker;
                    task.AssignedWorker = preferred.WorkerId;
                    // Guarded like PatchApplyReconciler.Announce, for the same reason: the events
                    // table has an FK to missions, and PlanPreview runs this same code over a
                    // transient mission that is never persisted (deliberately — one plan
                    // construction, so preview equals dispatch). The SELECTION must be identical on
                    // both paths; the EVENT can only exist where the mission does, and a diagnostic
                    // must never break the decision it describes.
                    try
                    {
                        _memory.LogEvent(context.MissionId, "worker_selected_by_trail",
                            $"{preferred.WorkerId} takes '{task.Title}' over default {previous}: "
                          + "strongest verified worker trail among compatible candidates.",
                            task.Id, task.AssignedAnt, new()
                            {
                                ["worker"] = preferred.WorkerId,
                                ["default_worker"] = previous,
                                ["trail_key"] = Pheromones.TrailGuidedSelection.TrailKeyFor(preferred),
                            });
                    }
                    catch (Exception logError)
                    {
                        Console.Error.WriteLine(
                            $"[planning] worker_selected_by_trail not recorded for {context.MissionId}: {logError.Message}");
                    }
                }
            }

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

    /// <summary>
    /// Append the verifier the plan omitted, when the plan contains admissible CONSEQUENTIAL work.
    ///
    /// v0.3.8.93 — the §6 rule was split, not weakened. The permanent half stays permanent: a plan
    /// that CHANGES anything (any patch-producing task, per <see cref="Planner.IsConsequential"/>)
    /// gets verification whatever the planner said, because an unverified change that merely looks
    /// complete is the defect §6 exists to prevent. The half that expired: appending a verifier to
    /// purely informational plans, where the "deliverable" is prose and the appended task graded
    /// the wording of an answer at the price of a model call — verification of nothing, bought on
    /// every question. A planner that WANTS a verifier on an informational plan may still include
    /// one; this stops forcing it.
    /// </summary>
    internal static void EnsurePlanVerification(List<Task> tasks)
    {
        var admissible = tasks.Where(t => t.Status != TaskStatus.Failed).ToList();
        if (admissible.Count == 0) return;
        if (!admissible.Any(Planner.IsConsequential)) return;
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
