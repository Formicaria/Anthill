using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every handoff this colony can declare asks its destination for a task type that destination
/// supports. v0.3.8.90.
///
/// WHY THIS EXISTS. Four routes to the builder — the soldier's block on a security finding, the
/// medic's escalation, and both of `SelectSpecialist`'s escalation returns — asked for task type
/// `"build"`. The builder's contract declares `SupportedTaskTypes: S("build_answer", "synthesis")`.
/// `HandoffGate.Evaluate` refuses a handoff whose `RequiredTaskType` the destination does not
/// support, by exact set membership, so all four were refused every single time they fired.
///
/// AND THE REFUSAL WAS NOT A NO-OP. Three of the four are `Required: true`, so the refusal set
/// `DeterministicBlock` on the source task and logged `required_handoff_refused` — "mission cannot
/// be verified". The two paths whose entire purpose is to REACH A HUMAN instead marked the mission
/// unverifiable and reached nobody. It looked like a strict colony rather than a broken route.
///
/// WHY NOTHING CAUGHT IT. `RosterContractTests.TheTaskTypesThePlannerEmits_AreSupportedByTheAssignedRole`
/// reads the types the PLANNER emits; `RoleCancellationTests.TheTaskTypeMap_AgreesWithTheContracts`
/// reads the harness's own map. A handoff's `RequiredTaskType` is a third population and nothing was
/// reading it — the "adjacent question" defect, in the shape of two guards that between them cover
/// everything except the thing that broke.
///
/// HOW IT ASKS. From SOURCE, because the population is every handoff that CAN be declared, and a
/// runtime sweep would only see the ones some test happens to trigger. The types themselves are
/// resolved against the live catalog, so this cannot pass by agreeing with a stale copy of the
/// contracts.
/// </summary>
public class HandoffTaskTypeTests
{
    private static string SrcDir() => Path.Combine(SourceText.RepoRoot(), "src");

    /// <summary>
    /// `new AntHandoff("soldier", "builder", "reason", "build_answer", …)`.
    ///
    /// v0.3.8.112 — KEPT, AND NO LONGER THE ONLY READER. This pattern is the most positionally
    /// fragile in the sweep: all four arguments must be literals, in order, so
    /// `new AntHandoff("soldier", "builder", reason, TaskTypes.BuildAnswer)` matches NOTHING and
    /// the route it declares is never checked against its destination's contract. It is kept
    /// alongside <see cref="HandoffRoutes"/> because it also pins the ARGUMENT ORDER, which a
    /// positional resolver reading one index at a time cannot see.
    /// </summary>
    private static readonly Regex HandoffLiteral = new(
        @"new AntHandoff\(\s*""(?<from>[a-z_]+)""\s*,\s*""(?<to>[a-z_]+)""\s*,\s*""[^""]*""\s*,\s*""(?<type>[a-z_]+)""");

    /// <summary>
    /// The same routes, read through the shared resolver so a constant-named argument is seen.
    /// Destination is argument 1 and task type is argument 3, matching the record's own order.
    /// </summary>
    private static IEnumerable<(string Destination, string TaskType)> HandoffRoutes(string code)
    {
        var constants = SourceText.ConstantsAcrossSource(SourceText.RepoRoot());
        foreach (var call in SourceText.CallSites(code, "AntHandoff"))
        {
            var destination = call.Resolve(1, constants);
            var taskType = call.Resolve(3, constants);
            if (destination is not null && taskType is not null) yield return (destination, taskType);
        }
    }

    /// <summary>`return ("builder", "build_answer", "…");` — the specialist routing table.</summary>
    private static readonly Regex RoutedSpecialist = new(
        @"return \(\s*""(?<to>[a-z_]+)""\s*,\s*""(?<type>[a-z_]+)""\s*,\s*""");

    private sealed record Route(string File, string Destination, string TaskType, string Shape);

    private static List<Route> Declared()
    {
        var routes = new List<Route>();

        foreach (var file in Directory.GetFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
        {
            // Comments blanked first: this file's own reasoning quotes both shapes, and several
            // production files explain their handoffs in prose above them. A guard that matched its
            // own explanation is the trap this repository keeps re-finding.
            var code = SourceText.CodeOnly(File.ReadAllText(file));
            var name = Path.GetFileName(file);

            foreach (Match m in HandoffLiteral.Matches(code))
                routes.Add(new(name, m.Groups["to"].Value, m.Groups["type"].Value, "AntHandoff"));

            // v0.3.8.112 — the same construction read positionally, so a constant-named destination
            // or task type is checked too. Deduplicated against the literal pass below rather than
            // replacing it: the literal regex additionally pins the ARGUMENT ORDER, which a
            // positional reader cannot see, and losing that would be a different guard.
            foreach (var (destination, taskType) in HandoffRoutes(code))
                if (!routes.Any(r => r.File == name && r.Destination == destination
                                  && r.TaskType == taskType && r.Shape == "AntHandoff"))
                    routes.Add(new(name, destination, taskType, "AntHandoff"));

            foreach (Match m in RoutedSpecialist.Matches(code))
                routes.Add(new(name, m.Groups["to"].Value, m.Groups["type"].Value, "SelectSpecialist"));
        }

        return routes;
    }

    /// <summary>
    /// THE ASSERTION. A destination with a contract must support the type the route asks it for.
    ///
    /// A destination with NO contract is not a finding: `HandoffGate` only applies this check when a
    /// contract exists, so a contractless role is a different question (and one
    /// `CapabilityDeclarationTests` already asks). Saying so here rather than silently skipping,
    /// because a skip that looks like a pass is how a sweep stops sweeping.
    /// </summary>
    [Fact]
    public void EveryDeclaredHandoff_AsksForATaskTypeItsDestinationSupports()
    {
        var problems = new List<string>();

        foreach (var route in Declared())
        {
            var contract = AntExecutionCatalog.ContractFor(route.Destination);
            if (contract is null) continue;

            if (!contract.SupportsTaskType(route.TaskType))
                problems.Add(
                    $"{route.File}: {route.Shape} routes to '{route.Destination}' with task type "
                  + $"'{route.TaskType}', which that role's contract does not support "
                  + $"(it supports: {string.Join(", ", contract.SupportedTaskTypes)}). "
                  + "HandoffGate.Evaluate refuses this by exact set membership, so the route can "
                  + "never be taken — and if it is Required, the refusal marks the source task "
                  + "DeterministicBlock instead of doing nothing.");
        }

        Assert.True(problems.Count == 0,
            "handoff routes that can never be admitted:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// And the sweep is not vacuous. It found ten routes when it was written; a rename of
    /// `AntHandoff` or a reshaped `SelectSpecialist` return would leave the assertion above passing
    /// over an empty set, which is exactly how a guard stops guarding without anyone noticing.
    /// </summary>
    [Fact]
    public void TheSweep_ActuallyFindsTheHandoffRoutes()
    {
        var routes = Declared();

        Assert.True(routes.Count >= 6,
            $"this guard found only {routes.Count} declared handoff route(s). The colony declares "
          + "handoffs from the soldier, the medic and the failure router; finding almost none means "
          + "the shape it matches has moved, not that the routes have gone.");

        Assert.Contains(routes, r => r.Destination == "builder");
    }

    /// <summary>
    /// The specific route that was broken, pinned by name so a regression is legible.
    ///
    /// `build_answer` and not `build`: "build" is also a VERIFIER name
    /// (`VerificationResult.Verifier == "build"`), so admitting it as a task type would put one
    /// string in two vocabularies — which is why the contract was left alone and the four call sites
    /// were fixed instead.
    /// </summary>
    [Fact]
    public void TheBuildersTaskType_IsBuildAnswer_AndNotBuild()
    {
        var builder = AntExecutionCatalog.ContractFor("builder");
        Assert.NotNull(builder);

        Assert.True(builder!.SupportsTaskType("build_answer"));
        Assert.False(builder.SupportsTaskType("build"),
            "the builder now accepts task type 'build'. If that was deliberate, note that 'build' is "
          + "also a verifier name, so one string now means two things — and the four call sites this "
          + "guard exists for were fixed on the opposite assumption.");
    }
}
