using System.Text.Json.Nodes;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.2.0 (phase) — the planner accepts a proposed plan as a UNIT or not at all.
///
/// Until now the parser repaired plans in place. An unknown ant dropped that task; a dependency it
/// could not resolve dropped that edge, with the stated reason "so the scheduler doesn't deadlock".
/// Both are silent structural edits: a five-task plan became a two-task plan, ordering the model
/// had expressed disappeared, and the mission then reported success against a graph nobody
/// proposed and nobody reviewed — after spending the time and model calls to run it.
///
/// Trading a deadlock for a wrong answer is a bad trade, and the wrong answer is the harder one to
/// notice. These tests pin the replacement: normalisation that loses no structure is still allowed,
/// anything that changes WHICH WORK RUNS rejects the plan, and the mission falls back to the static
/// plan — which is a plan someone reviewed.
/// </summary>
public class PlanSchemaStrictnessTests
{
    // The parse is pure — no router, no memory, no state on the Planner — which is what lets these
    // run without a provider and why two interleaved parses cannot contaminate each other.
    private static Planner NewPlanner() => new(useOllama: false, router: null);

    private static readonly IReadOnlySet<string> NoSkills = new HashSet<string>();

    private static JsonObject Plan(params string[] taskJson) =>
        JsonNode.Parse("{\"tasks\":[" + string.Join(",", taskJson) + "]}")!.AsObject();

    private static string TaskJson(string ant, string title = "t", string? dependsOn = null) =>
        "{\"title\":\"" + title + "\",\"description\":\"d\",\"assigned_ant\":\"" + ant + "\""
        + (dependsOn is null ? "" : ",\"depends_on\":[" + dependsOn + "]") + "}";

    /// <summary>A well-formed plan is still accepted, or strictness would just mean "never plan".</summary>
    [Fact]
    public void AValidPlan_IsAccepted()
    {
        var plan = NewPlanner().TasksFromJson(
            Plan(TaskJson("researcher"), TaskJson("coder"), TaskJson("builder")), "goal", NoSkills);

        Assert.True(plan.Accepted);
        Assert.Empty(plan.Rejections);
        Assert.True(plan.Tasks.Count >= 3);
    }

    /// <summary>
    /// The case that motivated this: one bad role used to cost one task and the plan ran on.
    /// </summary>
    [Fact]
    public void AnUnknownAnt_RejectsTheWholePlan_RatherThanDroppingThatTask()
    {
        var plan = NewPlanner().TasksFromJson(
            Plan(TaskJson("researcher"), TaskJson("wizard"), TaskJson("builder")), "goal", NoSkills);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.Tasks);                     // not a smaller plan — no plan
        var r = Assert.Single(plan.Rejections);
        Assert.Equal("assigned_ant", r.Field);
        Assert.Equal(1, r.TaskIndex);                 // and it says WHICH task
        Assert.Contains("wizard", r.Reason);
    }

    /// <summary>
    /// An edge that cannot be resolved was previously dropped, which let the task run out of order
    /// against inputs its author said it needed. Ordering is part of the plan.
    /// </summary>
    [Fact]
    public void AnUnresolvableDependency_RejectsThePlan_RatherThanDroppingTheEdge()
    {
        var plan = NewPlanner().TasksFromJson(
            Plan(TaskJson("researcher"),
                 TaskJson("coder", "second", dependsOn: "\"a-task-that-does-not-exist\""),
                 TaskJson("builder")), "goal", NoSkills);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.Tasks);
        Assert.Contains(plan.Rejections, r => r.Field == "depends_on");
    }

    /// <summary>Dependencies the parser CAN resolve are still resolved — index and title forms.</summary>
    [Theory]
    [InlineData("0")]          // integer index
    [InlineData("\"first\"")]  // exact title
    public void ResolvableDependencyForms_AreStillAccepted(string dep)
    {
        var plan = NewPlanner().TasksFromJson(
            Plan(TaskJson("researcher", "first"),
                 TaskJson("coder", "second", dependsOn: dep),
                 TaskJson("builder")), "goal", NoSkills);

        Assert.True(plan.Accepted, string.Join("; ", plan.Rejections.Select(r => r.Describe())));
        Assert.Single(plan.Tasks[1].DependsOn);
        Assert.Equal(plan.Tasks[0].Id, plan.Tasks[1].DependsOn[0]);
    }

    /// <summary>
    /// Missing prose is NORMALISED, not rejected. The distinction the strictness rests on: a
    /// default title changes nothing about which work runs or in what order, so rejecting on it
    /// would fail plans that are structurally perfect and send good missions to the fallback.
    /// </summary>
    [Fact]
    public void MissingTitleOrDescription_IsNormalised_NotRejected()
    {
        var plan = NewPlanner().TasksFromJson(
            Plan("{\"assigned_ant\":\"researcher\"}",
                 "{\"assigned_ant\":\"coder\",\"title\":\"has a title\"}",
                 TaskJson("builder")), "goal", NoSkills);

        Assert.True(plan.Accepted, string.Join("; ", plan.Rejections.Select(r => r.Describe())));
        Assert.All(plan.Tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Title)));
        Assert.All(plan.Tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    /// <summary>A plan below the minimum is a rejection with a reason, not a silent empty list.</summary>
    [Fact]
    public void TooFewTasks_IsRejectedWithAStatedReason()
    {
        var plan = NewPlanner().TasksFromJson(Plan(TaskJson("researcher")), "goal", NoSkills);

        Assert.False(plan.Accepted);
        Assert.Contains(plan.Rejections, r => r.TaskIndex < 0 && r.Field == "tasks");
    }

    /// <summary>Malformed input is a rejection, never an exception the caller has to guess about.</summary>
    [Fact]
    public void AMissingTasksArray_IsRejected_NotThrown()
    {
        var plan = NewPlanner().TasksFromJson(JsonNode.Parse("{\"plan\":\"none\"}")!.AsObject(), "goal", NoSkills);

        Assert.False(plan.Accepted);
        Assert.Contains(plan.Rejections, r => r.Field == "tasks");
    }

    /// <summary>Every rejection must name the field and, where it applies, the task.</summary>
    [Fact]
    public void RejectionsAreActionable_NamingFieldAndTask()
    {
        var plan = NewPlanner().TasksFromJson(
            Plan(TaskJson("researcher"), TaskJson("wizard"), TaskJson("builder")), "goal", NoSkills);

        var described = Assert.Single(plan.Rejections).Describe();
        Assert.Contains("task[1]", described);
        Assert.Contains("assigned_ant", described);
    }
}
