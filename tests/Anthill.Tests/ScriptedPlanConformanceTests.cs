using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every scripted plan in this suite is one the PLANNER would accept. v0.3.8.83.
///
/// WHY THIS EXISTS. v0.3.8.82 found that the cancellation harness had never once run the plan it
/// wrote: `Planner.TasksFromJson` rejects a plan with fewer than `AnthillRuntime.MinDynamicTasks`
/// usable tasks, `Planner.Plan` then substitutes the static `FallbackTasks` graph, and a fixture
/// that asserts on a role the fallback happens to contain passes anyway. The substitution is loud on
/// stderr and invisible to the test.
///
/// THE SWEEP THAT FOUND THE SECOND ONE. `EarnedRepairLifecycleTests` scripted a two-task plan —
/// researcher and coder — for the goal "Add a colony note to the documentation.", which contains
/// both `document` and `add` and therefore selects the CODE branch of the fallback: researcher,
/// file, coder, builder, verifier. Every one of that fixture's assertions (the tester runs twice, a
/// medic appears, two revisions, two patch sets) is satisfied by the fallback's coder, so scenario
/// 15's last edge was being proved about a plan nobody wrote. Fixed in the same release by giving
/// that plan its third task.
///
/// WHAT THIS GUARDS, and the two ways a fixture may satisfy it. A scripted plan is conformant if it
/// is STATICALLY acceptable — enough tasks, planner-eligible roles — or if its file verifies the
/// plan at RUNTIME, which is what `RoleCancellationTests` does now with
/// `AssertTheMissionRanTheScriptedPlan`. Runtime verification is the stronger of the two and the
/// only one that survives a rule changing under it; the static check exists because most fixtures
/// build their plan as a constant and a constant can be read without running anything.
///
/// WHAT IT DELIBERATELY DOES NOT DO. It does not require every fixture to assert its plan at
/// runtime. That would be the right end state and it is not a mechanical change — the richer
/// lifecycle scenarios acquire policy-inserted tester, soldier and verification tasks as they run,
/// so "the plan the mission ran" is a larger set than "the plan the fixture wrote" and each fixture
/// has to say which it means. Recorded in PLAN.md rather than half-done here.
/// </summary>
public class ScriptedPlanConformanceTests
{
    private static string TestsDir() => Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests");

    /// <summary>A planner script named by identifier: `.Role("planner", TwelveRolePlan)`.</summary>
    private static readonly Regex PlannerScript =
        new(@"\.Role\(""planner"",\s*(?<name>[A-Za-z_]\w*)\s*\)");

    /// <summary>The tasks a scripted plan declares, by their `assigned_ant` entries.</summary>
    private static readonly Regex AssignedAnt = new(@"""assigned_ant""\s*:\s*""(?<role>[a-z_]+)""");

    private sealed record ScriptedPlan(string File, string Name, string Json);

    /// <summary>
    /// Resolves every `.Role("planner", NAME)` to the raw-string constant NAME names, in the same
    /// file. A NAME that resolves to nothing is not an error here — `RoleCancellationTests` builds
    /// its plan from a method — but such a file must verify its plan at runtime instead, which
    /// <see cref="EveryScriptedPlan_IsOneThePlannerWouldAccept"/> requires.
    /// </summary>
    private static (List<ScriptedPlan> Resolved, List<string> Unresolved) Collect()
    {
        var resolved = new List<ScriptedPlan>();
        var unresolved = new List<string>();

        foreach (var file in Directory.GetFiles(TestsDir(), "*.cs"))
        {
            // This file declares no plans and QUOTES the pattern it looks for, twice, in its own
            // documentation. Skipped explicitly rather than relying on the comment stripper alone,
            // because "the checker matched its own explanatory prose" is a failure this repository
            // has hit before and the first draft of this guard hit it again.
            if (Path.GetFileName(file) == "ScriptedPlanConformanceTests.cs") continue;

            var source = File.ReadAllText(file);

            // Matched against CODE, not text. `SourceText.CodeOnly` exists for exactly this: several
            // fixtures discuss `.Role("planner", …)` in comments explaining why their plan is shaped
            // the way it is, and a raw-text scan reports the explanation as an instance.
            foreach (Match use in PlannerScript.Matches(SourceText.CodeOnly(source)))
            {
                var name = use.Groups["name"].Value;
                var constant = Regex.Match(source,
                    @"const string " + Regex.Escape(name) + @"\s*=\s*""""""(?<json>.*?)"""""";",
                    RegexOptions.Singleline);

                if (constant.Success) resolved.Add(new(Path.GetFileName(file), name, constant.Groups["json"].Value));
                else unresolved.Add($"{Path.GetFileName(file)}:{name}");
            }
        }
        return (resolved, unresolved);
    }

    /// <summary>
    /// THE ASSERTION. Enough tasks to clear `MinDynamicTasks`, and every role planner-eligible.
    ///
    /// Both halves matter and they fail differently: too few tasks is rejected as a plan, an
    /// ineligible role is rejected per task — and `TasksFromJson` reports the role first, because
    /// "a rejected task is not a usable one, so a plan that already failed on a bad role would ALSO
    /// report below-the-minimum". Either way the mission runs the fallback.
    /// </summary>
    [Fact]
    public void EveryScriptedPlan_IsOneThePlannerWouldAccept()
    {
        var (resolved, unresolved) = Collect();
        var problems = new List<string>();

        foreach (var plan in resolved)
        {
            var roles = AssignedAnt.Matches(plan.Json).Select(m => m.Groups["role"].Value).ToList();

            if (roles.Count < AnthillRuntime.MinDynamicTasks)
                problems.Add($"{plan.File}:{plan.Name} declares {roles.Count} task(s), below the "
                           + $"minimum of {AnthillRuntime.MinDynamicTasks} — Planner rejects it and the "
                           + "mission runs FallbackTasks instead");

            foreach (var role in roles.Where(r => !AntRegistry.ExecutableRoleIds.Contains(r)).Distinct())
                problems.Add($"{plan.File}:{plan.Name} assigns '{role}', which is not a "
                           + "planner-eligible role — TasksFromJson rejects the whole plan");
        }

        // A file whose plan is built rather than declared must verify it at runtime instead.
        foreach (var use in unresolved)
        {
            var file = use.Split(':')[0];
            var source = File.ReadAllText(Path.Combine(TestsDir(), file));
            if (!source.Contains("AssertTheMissionRanTheScriptedPlan", StringComparison.Ordinal))
                problems.Add($"{use} is not a constant this guard can read, and {file} does not "
                           + "assert what the mission actually planned. One of the two is required: "
                           + "a plan nothing checks is a plan the Planner may have replaced.");
        }

        Assert.True(problems.Count == 0,
            "scripted plans the Planner would discard:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// And the guard is not vacuous. A rename of `ScriptBook.Role` or of the `"planner"` key would
    /// leave the assertion above passing over an empty set — which is exactly how a sweep stops
    /// sweeping without anyone noticing.
    /// </summary>
    [Fact]
    public void TheSweep_ActuallyFindsTheScriptedPlans()
    {
        var (resolved, unresolved) = Collect();

        Assert.True(resolved.Count >= 5,
            $"this guard resolved only {resolved.Count} scripted plan(s). The suite has several "
          + "lifecycle scenarios that script a planner; finding almost none means the pattern it "
          + "matches has moved, not that the plans have gone.");

        // The cancellation harness is the runtime-verified case and must stay in the unresolved set
        // for the right reason — it builds its plan per role rather than declaring one.
        Assert.Contains(unresolved, u => u.StartsWith("RoleCancellationTests.cs:", StringComparison.Ordinal));
    }
}
