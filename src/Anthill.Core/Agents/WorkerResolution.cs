using Anthill.Core.Domain;
using Anthill.Core.Missions;

namespace Anthill.Core.Agents;

/// <summary>
/// WHICH WORKER SERVES A TASK, DECIDED ONCE. v0.3.8.98.
///
/// WHY THIS TYPE EXISTS. Worker selection was answered in three places that could not see each
/// other: <c>Planner.AssignDefaultWorkers</c> filled a blank from the task text, then
/// <see cref="AntRegistry.ValidateTask"/> filled it again if it was still blank, and
/// <c>AntRuntime.Resolve</c> filled it a third time at dispatch. Because the first one ran first
/// and always succeeded, everything downstream saw an assignment that already existed and left it
/// alone. v0.3.8.98's capability-first branch was written in <c>PlanningService</c>, one layer too
/// late, and never executed once — the audit acceptance test resolved
/// <c>researcher.mission_researcher</c> with the capability code present, compiled and unreachable.
/// That is this repository's recurring defect ("declared and reaching nobody"), caught here by a
/// test rather than in a live mission, and the fix is one resolver that every caller shares.
///
/// THE ORDER OF EVIDENCE, strongest first, and it is not arbitrary:
///
/// 1. SPECIFICATION. The mission declared what it must be able to do; a worker's contract declares
///    what it can do. Matching those answers the question actually being asked — can this worker
///    serve this mission — and it is the only one of the three that is a fact about the WORKER.
/// 2. KEYWORD. The registry's keyword branches are its capability map in another spelling: "ui"
///    means the ui_coder because that is what the ui_coder is FOR. Weaker than a declared
///    capability, because it is a fact about the text; stronger than nothing.
/// 3. DEFAULT. Declaration order — a tie-break taken when neither of the above said anything. It
///    is a guess, it is labelled a guess, and it is the ONLY basis a pheromone trail may replace
///    (see <see cref="Pheromones.TrailGuidedSelection"/>). Reputation never outranks compatibility.
///
/// The basis travels with the task on <see cref="Domain.Task.WorkerBasis"/> precisely so a later
/// layer can tell a decision from a guess without re-deriving it and reaching a different answer
/// than the layer that actually assigned the worker.
/// </summary>
public static class WorkerResolution
{
    /// <summary>
    /// Resolve a worker for <paramref name="task"/> and RECORD what decided it. Never throws;
    /// leaves the worker null when the role has none, which <see cref="AntRegistry.ValidateTask"/>
    /// then refuses in the operator's sight rather than this silently inventing one.
    ///
    /// <paramref name="specification"/> may be null — every mission before v0.3.8.98, and every
    /// class intake cannot yet serve, resolve to a permissive specification that requires nothing.
    /// In that case this behaves exactly as the keyword resolver did, which is what makes this
    /// change safe to ship: it can only differ where the mission SAID what it needs.
    /// </summary>
    public static void Assign(Domain.Task task, string goal, MissionSpecification? specification)
    {
        var (worker, basis) = Resolve(
            task.AssignedAnt ?? "", task.TaskType ?? "",
            $"{goal} {task.Title} {task.Description}",
            specification?.RequiredCapabilities);

        task.AssignedWorker = worker?.WorkerId;
        task.WorkerBasis = basis;
    }

    /// <summary>The decision itself, free of the task object, so tests and previews can ask it.</summary>
    public static (AntWorkerDefinition? Worker, WorkerDecisionBasis Basis) Resolve(
        string roleId, string taskType, string text, IReadOnlyList<string>? requiredCapabilities)
    {
        var (byCapability, capabilityDecided) = AntRegistry.ResolveByCapability(roleId, requiredCapabilities);
        if (capabilityDecided && byCapability is not null)
            return (byCapability, WorkerDecisionBasis.Specification);

        var (byKeyword, keywordDecided) = AntRegistry.ResolveWorker(roleId, taskType, text);
        if (keywordDecided && byKeyword is not null)
            return (byKeyword, WorkerDecisionBasis.Keyword);

        // An AMBIGUOUS capability match — more than one worker in the role declares the capability —
        // is a starting point rather than an answer. It narrows the field to compatible candidates
        // and leaves the choice among them to the trail, which is exactly the rule v0.3.8.93 set
        // for the keyword tie-break and is applied here for the same reason.
        return (byCapability ?? byKeyword, WorkerDecisionBasis.Default);
    }

    /// <summary>
    /// A worker the PLAN named that cannot serve what the mission declared it needs. v0.3.8.98.
    ///
    /// ADR-008's division: a model may propose structure, and a deterministic gate decides what is
    /// kept. A planner naming <c>researcher.mission_researcher</c> for a repository audit has
    /// proposed a worker whose own contract says it reads mission history — and until this release
    /// that proposal was final, because an explicitly named worker skipped resolution entirely. The
    /// same audit therefore got a different specialist depending on what the planner happened to
    /// write, which is the defect the capability system removes, arriving through a second door.
    ///
    /// DELIBERATELY NARROW. It repairs only when all four hold: the mission DECLARED capabilities;
    /// the named worker declares NONE of them; exactly one worker in that role does; and the role
    /// is the one the plan chose (a wrong ROLE is a planning error, not a resolution one, and the
    /// admission gate answers for it). An ambiguous alternative decides nothing, and a mission that
    /// declared nothing repairs nothing — so every mission before v0.3.8.98 is untouched.
    /// </summary>
    /// <returns>The worker that was replaced, or null when nothing was repaired.</returns>
    public static string? RepairIncompatible(Domain.Task task, IReadOnlyList<string>? requiredCapabilities)
    {
        if (requiredCapabilities is null || requiredCapabilities.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(task.AssignedWorker)) return null;
        if (!AntRegistry.ByWorker.TryGetValue(task.AssignedWorker!, out var named)) return null;
        if (!string.Equals(named.ParentRoleId, task.AssignedAnt, StringComparison.OrdinalIgnoreCase)) return null;

        // Already compatible: nothing to answer for. A worker declaring one of the required
        // capabilities is serving the mission, whoever chose it.
        if (named.Capabilities.Any(c => requiredCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase)))
            return null;

        var (byCapability, decided) = AntRegistry.ResolveByCapability(task.AssignedAnt, requiredCapabilities);
        if (!decided || byCapability is null || byCapability.WorkerId == task.AssignedWorker) return null;

        var previous = task.AssignedWorker!;
        task.AssignedWorker = byCapability.WorkerId;
        task.WorkerBasis = Domain.WorkerDecisionBasis.Specification;
        return previous;
    }

    /// <summary>
    /// The workers a trail is allowed to choose between: the role's own, narrowed to those whose
    /// declared capabilities the mission actually requires when it required any.
    ///
    /// Narrowing is not decoration. A trail ranking the WHOLE role could promote a worker the
    /// mission's specification excludes — reputation quietly outranking compatibility, which is
    /// the one thing pheromone selection must never do.
    /// </summary>
    public static IReadOnlyList<AntWorkerDefinition> CompatibleCandidates(
        string roleId, IReadOnlyList<string>? requiredCapabilities)
    {
        if (!AntRegistry.ByRole.TryGetValue(roleId ?? "", out var role))
            return Array.Empty<AntWorkerDefinition>();

        var enabled = role.Workers.Where(w => w.Enabled).ToList();
        if (requiredCapabilities is null || requiredCapabilities.Count == 0) return enabled;

        var compatible = enabled
            .Where(w => w.Capabilities.Any(c => requiredCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        // No worker in this role declares anything the mission needs: the role is serving the plan
        // for a reason the specification does not describe, so the specification narrows nothing
        // rather than emptying the field and leaving the task with no worker at all.
        return compatible.Count > 0 ? compatible : enabled;
    }
}
