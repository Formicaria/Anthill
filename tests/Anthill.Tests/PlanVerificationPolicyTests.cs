using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Structural repair §6 — mandatory verification is runtime policy, not a planner option. The
/// planner may request verification; a plan that omits it and still produces work gets the
/// verifier appended, bound by lineage and dependency to every deliverable-producing task.
///
/// v0.3.8.93 — the rule was SPLIT, and these tests now pin both halves. Permanent: a plan with
/// CONSEQUENTIAL work (any patch-producing task) always gets verification — that half is
/// unchanged and stays strict. Expired: forcing a verifier onto purely informational plans,
/// where the appended task graded the wording of an answer at the price of a model call. The
/// fixtures below use a coder task where the appended verifier is asserted, and assert its
/// ABSENCE for the informational shapes that used to get one.
/// </summary>
public class PlanVerificationPolicyTests : IDisposable
{
    /// <summary>
    /// EnsurePlanVerification fails CLOSED when the executor catalog says the verifier is not
    /// runtime-available — and the catalog is only populated when a Queen is composed. Found by
    /// test ordering: two green runs had a Queen-building test execute first; the third did not,
    /// the catalog was empty, and the policy honestly refused to append a task nothing could run.
    /// The fixture composes a real Queen so the tests stand in production's shape instead of
    /// depending on their neighbours to set the stage.
    /// </summary>
    private readonly Anthill.Core.Orchestration.Queen _queen =
        new(new Anthill.Core.Memory.SqliteMemory(":memory:"));

    public void Dispose() => _queen.Dispose();

    private static DomainTask Work(string ant, string type = "research") => new()
    {
        Title = $"{ant} work", AssignedAnt = ant, TaskType = type,
    };

    [Fact]
    public void APlanThatOmitsTheVerifier_GetsOneAppended_WithFullLineage()
    {
        var tasks = new List<DomainTask> { Work("researcher"), Work("coder", "patch_proposal") };
        PlanningService.EnsurePlanVerification(tasks);

        var verifier = Assert.Single(tasks, t => t.AssignedAnt == "verifier");
        Assert.Equal("verification", verifier.TaskType);
        Assert.True(verifier.Critical);
        // §7: no orphans. The verifier's parents and dependencies are the work it verifies.
        Assert.Equal(2, verifier.ParentTaskIds.Count);
        Assert.Equal(verifier.ParentTaskIds.OrderBy(x => x), verifier.DependsOn.OrderBy(x => x));
        Assert.Contains(tasks[0].Id, verifier.DependsOn);
        Assert.Contains(tasks[1].Id, verifier.DependsOn);
    }

    [Fact]
    public void APlanThatAlreadyHasAVerifier_IsNotDoubled()
    {
        var tasks = new List<DomainTask>
            { Work("coder", "patch_proposal"), Work("verifier", "verification") };
        PlanningService.EnsurePlanVerification(tasks);
        Assert.Single(tasks, t => t.AssignedAnt == "verifier");
    }

    /// <summary>
    /// v0.3.8.93 — THE EXPIRED HALF, PINNED IN THE OTHER DIRECTION. An informational plan — no
    /// patch-producing task anywhere in it — keeps the shape its planner chose. The verifier is
    /// protection for changes; an answer's wording is not a change, and forcing a grading task
    /// onto every question was verification of nothing at a model call each.
    /// </summary>
    [Fact]
    public void AnInformationalPlan_IsNotForcedAVerifier()
    {
        var tasks = new List<DomainTask> { Work("researcher"), Work("builder", "build_answer") };
        PlanningService.EnsurePlanVerification(tasks);
        Assert.DoesNotContain(tasks, t => t.AssignedAnt == "verifier");

        // And a planner that WANTED one keeps it — the rule stopped forcing, not permitting.
        var chosen = new List<DomainTask> { Work("builder", "build_answer"), Work("verifier", "verification") };
        PlanningService.EnsurePlanVerification(chosen);
        Assert.Single(chosen, t => t.AssignedAnt == "verifier");
    }

    [Fact]
    public void AnEmptyOrFullyRefusedPlan_GetsNoVerifier()
    {
        var empty = new List<DomainTask>();
        PlanningService.EnsurePlanVerification(empty);
        Assert.Empty(empty);

        var refused = new List<DomainTask> { Work("researcher") };
        refused[0].Status = Anthill.Core.Domain.TaskStatus.Failed;
        PlanningService.EnsurePlanVerification(refused);
        Assert.DoesNotContain(refused, t => t.AssignedAnt == "verifier");
    }

    /// <summary>The inserted task says WHY it exists — an operator reading the plan must see the
    /// omission was closed by policy, not silently rewritten.</summary>
    [Fact]
    public void TheInsertedVerifier_DeclaresItsOrigin()
    {
        var tasks = new List<DomainTask> { Work("coder", "patch_proposal") };
        PlanningService.EnsurePlanVerification(tasks);
        Assert.Contains("inserted by runtime policy", tasks.Single(t => t.AssignedAnt == "verifier").Description);
    }
}
