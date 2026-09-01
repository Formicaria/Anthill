using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A WORKER THAT CANNOT SERVE THE TASK NEVER GETS TO RUN IT. v0.3.8.105, PLAN.md §2b `.105`.
///
/// WHAT THIS CLOSES, and it is a gap `.104` created rather than one it inherited. `MissionPreflight`
/// refuses a plan that could never deliver — and it has exactly ONE call site, in `Queen.RunMission`,
/// over the COMPILED plan, before execution begins. Every task admitted after that moment reached
/// dispatch unexamined: handoff tasks, delta-plan tasks, the medic's repair tasks, inserted policy
/// reviews, added verification steps. Those are the tasks created because something already went
/// wrong, which makes them both the most likely to be mis-assigned and the ones nothing checked.
///
/// THE COST OF BEING WRONG IS NOT A REFUSAL, IT IS A CONFIDENT ANSWER. `.98` recorded the shape: a
/// runtime-inspection step served by the researcher that reads mission history. The worker does not
/// decline work outside its declaration — it runs, spends model calls and tool calls, and produces
/// something plausible about a question it could not answer. Every gate downstream of that grades
/// the output, not the fitness, so the wrongness has to be caught before dispatch or not at all.
/// </summary>
public class TaskRerouteTests
{
    /// <summary>A capability exactly one researcher worker declares — the reroute needs a unique
    /// server or it is a tie-break, which is a different layer's decision.</summary>
    private static string UniquelyServed()
    {
        var candidates = AntRegistry.ByRole["researcher"].Workers
            .Where(w => w.Enabled)
            .SelectMany(w => w.Capabilities)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(candidates.Count > 0,
            "no capability in the researcher role has exactly one server, so this file cannot test "
          + "a decided reroute. The roster changed shape; the fixture needs rewriting, not deleting.");
        return candidates[0];
    }

    private static string ServerOf(string capability) =>
        AntRegistry.ByRole["researcher"].Workers
            .First(w => w.Enabled && w.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
            .WorkerId;

    private static string SomeOtherResearcher(string notThis) =>
        AntRegistry.ByRole["researcher"].Workers
            .First(w => w.Enabled && !string.Equals(w.WorkerId, notThis, StringComparison.Ordinal))
            .WorkerId;

    private static Anthill.Core.Domain.Task Task(string role, string? worker, string? capability) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = "Inspect the running colony",
        Description = "Inspect the running colony",
        AssignedAnt = role,
        AssignedWorker = worker,
        RequiredCapability = capability,
        TaskType = "research",
    };

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE EXIT GATE'S FIRST CLAUSE. A task whose assigned worker does not declare the capability
    /// the task requires is moved to the one that does — and moved BEFORE dispatch, which is the
    /// only place the move is worth anything.
    /// </summary>
    [Fact]
    public void WrongWorker_IsReroutedBeforeDispatch()
    {
        var capability = UniquelyServed();
        var right = ServerOf(capability);
        var wrong = SomeOtherResearcher(right);

        var task = Task("researcher", wrong, capability);
        var decision = TaskReroute.Evaluate(task);

        Assert.Equal(TaskReroute.RerouteKind.Rerouted, decision.Kind);
        Assert.Equal(wrong, decision.FromWorker);
        Assert.Equal(right, decision.ToWorker);
        Assert.Contains(capability, decision.Reason, StringComparison.Ordinal);

        Assert.True(TaskReroute.Apply(task, decision));
        Assert.Equal(right, task.AssignedWorker);
        // The basis records that a DECLARED capability decided it, not a keyword and not a guess —
        // the distinction `WorkerResolution` exists to keep, applied by the layer that moved it.
        Assert.Equal(WorkerDecisionBasis.Specification, task.WorkerBasis);
    }

    /// <summary>
    /// AND IT NEVER CROSSES A ROLE. A wrong ROLE is a planning error the admission gate and the
    /// authority ceiling answer for; a dispatch-time mechanism that could move work between roles
    /// would move it across boundaries three gates upstream had just finished checking.
    /// </summary>
    [Fact]
    public void ARerouteNeverCrossesRoles()
    {
        var capability = UniquelyServed();

        // The capability lives in the researcher role. A CODER task requiring it must not be
        // rerouted into the researcher: the plan named the wrong role, and this is not that layer.
        var task = Task("coder", null, capability);
        var decision = TaskReroute.Evaluate(task);

        Assert.NotEqual(TaskReroute.RerouteKind.Rerouted, decision.Kind);
        Assert.All(AntRegistry.ByRole["researcher"].Workers,
            w => Assert.NotEqual(w.WorkerId, decision.ToWorker));
    }

    /// <summary>
    /// A CAPABILITY NOTHING IN THE ROLE SERVES REFUSES THE DISPATCH. Not a reroute and not a
    /// failure of the plan: the colony cannot do this, and it will not be able to on a retry.
    /// </summary>
    [Fact]
    public void ACapabilityNoWorkerServes_RefusesDispatch()
    {
        var task = Task("researcher", null, "translate_to_latin");   // nothing declares it
        var decision = TaskReroute.Evaluate(task);

        Assert.Equal(TaskReroute.RerouteKind.Unserved, decision.Kind);
        Assert.True(decision.Blocks);
        Assert.Contains("translate_to_latin", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("researcher", decision.Reason, StringComparison.Ordinal);
        Assert.False(TaskReroute.Apply(task, decision));
    }

    /// <summary>A worker that already declares the capability is left exactly where it is. Without
    /// this the positive above proves nothing — a gate that reroutes everything would pass it.</summary>
    [Fact]
    public void ACompatibleWorker_IsLeftAlone()
    {
        var capability = UniquelyServed();
        var right = ServerOf(capability);

        var task = Task("researcher", right, capability);
        var decision = TaskReroute.Evaluate(task);

        Assert.Equal(TaskReroute.RerouteKind.Proceed, decision.Kind);
        Assert.False(TaskReroute.Apply(task, decision));
        Assert.Equal(right, task.AssignedWorker);
    }

    /// <summary>
    /// AN AMBIGUOUS CAPABILITY IS NOT A BLOCK. More than one worker declares it, so the colony CAN
    /// do this — and the choice among compatible candidates belongs to the trail, which is the rule
    /// `.93` set and `.98` kept. Blocking here would let a dispatch gate overrule a selection layer.
    /// </summary>
    [Fact]
    public void AnAmbiguousCapability_IsNotABlock()
    {
        var shared = AntRegistry.ByRole["researcher"].Workers
            .Where(w => w.Enabled)
            .SelectMany(w => w.Capabilities)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;

        if (shared is null) return;   // no ambiguous capability in this roster; nothing to assert

        var task = Task("researcher", SomeOtherResearcher(ServerOf(shared)), shared);
        var decision = TaskReroute.Evaluate(task);

        Assert.False(decision.Blocks);
    }

    /// <summary>
    /// A TASK THAT DECLARES NO CAPABILITY IS UNTOUCHED, and this is the assertion that keeps the
    /// release shippable. The overwhelming majority of tasks this colony runs — every coder, every
    /// builder, every task planned before capabilities existed — carry no `RequiredCapability`, and
    /// a gate that judged them against the mission-wide list would refuse legitimate plans at the
    /// most expensive possible moment. The same narrowing `WorkerResolution` and `MissionPreflight`
    /// both make, for the same reason.
    /// </summary>
    [Fact]
    public void ATaskWithNoDeclaredCapability_IsUntouched()
    {
        foreach (var role in new[] { "coder", "builder", "verifier", "researcher", "tester" })
        {
            var task = Task(role, null, capability: null);
            var decision = TaskReroute.Evaluate(task);

            Assert.Equal(TaskReroute.RerouteKind.Proceed, decision.Kind);
            Assert.False(decision.Blocks);
            Assert.Null(task.AssignedWorker);
        }
    }

    /// <summary>
    /// THE DISPATCH CHOKEPOINT ACTUALLY CALLS IT, AND CALLS IT FIRST.
    ///
    /// Read as source shape, for the reason `MissionContractTests` reads one: a gate wired into the
    /// right method in the wrong ORDER is invisible to every behavioural assertion above and
    /// useless in production. `.104` shipped `MissionAuthorityGate` proved directly and consulted
    /// nowhere; `.98` shipped a capability branch that compiled and never executed. The property
    /// that matters here is positional — before the durable claim, before the runtime resolution,
    /// before the model call — and position is what this reads.
    /// </summary>
    [Fact]
    public void TheRerouteRunsBeforeTheClaimAndTheModelCall()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs"));

        var method = source.IndexOf("private void RunSingleTask(", StringComparison.Ordinal);
        Assert.True(method > 0, "RunSingleTask has been renamed; this guard is reading nothing.");

        var reroute = source.IndexOf("TaskReroute.Evaluate(", method, StringComparison.Ordinal);
        var claim = source.IndexOf("_memory.TryClaimTask(", method, StringComparison.Ordinal);
        var resolve = source.IndexOf("AntRuntime.Resolve(", method, StringComparison.Ordinal);

        Assert.True(reroute > 0, "RunSingleTask does not consult TaskReroute at all.");
        Assert.True(reroute < resolve,
            "the reroute runs after AntRuntime.Resolve, so the runtime resolved the worker this "
          + "gate was about to replace.");
        Assert.True(reroute < claim,
            "the reroute runs after the durable claim. A refused dispatch would then hold a lease "
          + "on a task no worker is going to honour.");
    }
}
