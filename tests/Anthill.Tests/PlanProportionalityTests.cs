using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.93 — mission size is proportional to the request, acceptance gate A's planning half.
///
/// The three-task minimum was written for consequential plans and applied to every plan: an
/// informational request either shipped three tasks or was silently swapped for the static
/// fallback (the v0.3.8.82 defect shape — the operator's question answered by a plan nobody
/// wrote, at three model calls instead of one). The guard was SPLIT, and these tests hold both
/// halves: small informational plans are a declared, accepted outcome; small consequential plans
/// are still refused; and the static fallback itself now answers a short question with one task.
/// </summary>
public class PlanProportionalityTests
{
    // No router: CreateTasks goes straight to the static fallback, which is the surface under test.
    private static Planner Offline() => new(useOllama: false, router: null);

    /// <summary>A goal that trips none of the fallback's code keywords and no web need.</summary>
    private const string SimpleGoal = "What is the queen's role in the colony?";

    [Fact]
    public void AShortInformationalGoal_GetsASingleTaskFallback()
    {
        var before = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableWebSearch = false;
            var tasks = Offline().CreateTasks(SimpleGoal, MissionConstraints.None);

            var task = Assert.Single(tasks);
            Assert.Equal("builder", task.AssignedAnt);
            Assert.Equal("build_answer", task.TaskType);
            Assert.False(string.IsNullOrWhiteSpace(task.AssignedWorker));
        }
        finally { AnthillRuntime.EnableWebSearch = before; }
    }

    /// <summary>The boundary is conservative: anything brief-sized keeps the reviewed
    /// research→build→verify shape exactly as before the split.</summary>
    [Fact]
    public void ALongInformationalGoal_KeepsTheFullFallbackShape()
    {
        var before = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableWebSearch = false;
            var goal = SimpleGoal + " " + new string('q', Planner.SimpleAnswerGoalChars);
            var tasks = Offline().CreateTasks(goal, MissionConstraints.None);

            Assert.Equal(3, tasks.Count);
            Assert.Contains(tasks, t => t.AssignedAnt == "researcher");
            Assert.Contains(tasks, t => t.AssignedAnt == "verifier");
        }
        finally { AnthillRuntime.EnableWebSearch = before; }
    }

    /// <summary>A code goal is consequential and keeps the full five-task fallback — the split
    /// never shrinks a plan that changes files.</summary>
    [Fact]
    public void ACodeGoal_KeepsTheConsequentialFallback_HoweverShort()
    {
        var tasks = Offline().CreateTasks("fix the bug", MissionConstraints.None);

        Assert.Contains(tasks, t => t.AssignedAnt == "coder");
        Assert.Contains(tasks, t => t.AssignedAnt == "verifier");
        Assert.True(tasks.Count >= AnthillRuntime.MinDynamicTasks);
    }

    /// <summary>One definition of consequential, shared by the size minimum, the constraint
    /// stripper and the verification policy — pinned so a fourth reader cannot invent a fifth.</summary>
    [Fact]
    public void Consequential_MeansPatchProducing()
    {
        Assert.True(Planner.IsConsequential(new Task { AssignedAnt = "coder", TaskType = "research" }));
        Assert.True(Planner.IsConsequential(new Task { AssignedAnt = "builder", TaskType = "patch_proposal" }));
        Assert.False(Planner.IsConsequential(new Task { AssignedAnt = "builder", TaskType = "build_answer" }));
        Assert.False(Planner.IsConsequential(new Task { AssignedAnt = "researcher", TaskType = "research" }));
    }
}
