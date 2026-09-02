using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Models;
using Anthill.SDK.Contracts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A declaration belongs to something that can act on it. v0.3.8.76 (PLAN.md §2 R1).
///
/// THE DEFECT. Five contracts — soldier, medic, archivist, ui_cartographer, scribe — declared
/// `AllowsModelCalls: true` and a `ModelRequirement`, and their ants take no `ModelRouter`. They
/// have never been able to make a model call. Nothing failed, because a requirement is only
/// falsified where the thing it constrains happens: model fitness graded them, reported them unfit
/// on a model that suited every role that does call one, and sent operators to change models for
/// roles that ask a model nothing. That is where the colony's "seven roles need a capable model"
/// warning came from, on a colony with five such roles.
///
/// THE MIRROR IMAGE, which is the half that actually cost something. `planner` and `strategist`
/// call a model on every mission and every autonomy cycle and have no contract, so fitness — which
/// iterated contracts — never graded them. The planner's shortfall is silent by construction: a
/// model that cannot emit JSON does not error, it falls back to a static task plan. An operator sees
/// a colony that ignores their goal, and the one report that could have named the cause was
/// enumerating a different set.
///
/// So the guards below run in BOTH directions, because this defect has always had two ends and
/// fixing one of them is how it comes back.
///
/// WHAT THESE READ. Source text, deliberately. The property is "the constructor can be handed a
/// router", and a reflection test would assert against the same declarations it is supposed to be
/// checking. Reading the constructor is reading the thing itself.
/// </summary>
public class ContractDeclarationTests
{
    private static string AntSource() =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")))
      + "\n"
      + SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

    /// <summary>
    /// Every ant's constructor, by the role it passes to <c>BaseAnt</c>.
    ///
    /// `: base("coder")` is how a class binds itself to a contract, so it is the honest join between
    /// the two — better than a name convention, which would let `CoderAnt` be renamed without the
    /// mapping noticing, and better than a table in this file, which would be a third declaration
    /// that can disagree with the other two.
    /// </summary>
    private static readonly Regex AntConstructor = new(
        @"public\s+(?<type>\w+Ant)\s*\((?<parameters>[^)]*)\)\s*:\s*base\(""(?<role>[a-z_]+)""\)",
        RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, (string Type, string Parameters)> AntsByRole()
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AntConstructor.Matches(AntSource()))
            found[m.Groups["role"].Value] = (m.Groups["type"].Value, m.Groups["parameters"].Value);
        return found;
    }

    /// <summary>
    /// The mapping itself resolves. If this fails, every assertion below would pass vacuously —
    /// which is the failure mode a source-reading test has and a reflection test does not, so it is
    /// checked first rather than assumed.
    /// </summary>
    [Fact]
    public void EveryContractedRole_HasAnAntThatBindsToIt()
    {
        var ants = AntsByRole();
        var unbound = AntExecutionCatalog.Contracts.Keys
            .Where(r => !ants.ContainsKey(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.True(unbound.Count == 0,
            "these roles have a contract and no ant constructor calling `: base(\"<role>\")`: "
          + string.Join(", ", unbound)
          + ". Either the ant was renamed out of the pattern this test reads, or the contract "
          + "describes a role nothing implements — and the assertions in this file would all have "
          + "passed by finding nothing to check.");
    }

    // -----------------------------------------------------------------------------------------------
    // Direction one: a declaration must belong to a role that can act on it
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ASSERTION THIS RELEASE EXISTS FOR. A contract that says the role calls a model belongs to
    /// an ant that can be handed a <c>ModelRouter</c>.
    ///
    /// Constructor parameters rather than field use, because being HANDED a router is the capability;
    /// what the handler then does with it varies per release and is not what the contract claims.
    /// </summary>
    [Fact]
    public void ARoleDeclaringModelCalls_HasAnAntThatCanBeGivenARouter()
    {
        var ants = AntsByRole();

        var cannot = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.AllowsModelCalls)
            .Where(kv => !ants[kv.Key].Parameters.Contains("ModelRouter", StringComparison.Ordinal))
            .Select(kv => $"{kv.Key} ({ants[kv.Key].Type})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(cannot.Count == 0,
            "these contracts declare `AllowsModelCalls: true` and their ants take no ModelRouter, so "
          + "they cannot make the call they are declaring: " + string.Join(", ", cannot)
          + ". Either wire the router in, or say `false` — a declaration nothing can act on is not a "
          + "conservative default. Model fitness grades it, and reports the role unfit on a model it "
          + "will never speak to.");
    }

    /// <summary>
    /// And the reverse: an ant that CAN be given a router, whose contract says it makes no calls.
    ///
    /// Weaker than its sibling by design — it is legitimate to hold a router and not use it on every
    /// path, and `VerifierAnt` does exactly that. So this is a `false` that must be deliberate:
    /// the roles below are the ones whose ant signature and contract disagree, and each has to be
    /// either corrected or listed here with the reason.
    /// </summary>
    [Fact]
    public void AnAntHoldingARouter_DeclaresTheCallsItMakes()
    {
        var ants = AntsByRole();

        var silent = AntExecutionCatalog.Contracts
            .Where(kv => !kv.Value.AllowsModelCalls)
            .Where(kv => ants[kv.Key].Parameters.Contains("ModelRouter", StringComparison.Ordinal))
            .Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(silent.Count == 0,
            "these ants can be handed a ModelRouter and their contracts say they make no model "
          + "calls: " + string.Join(", ", silent)
          + ". An undeclared call is worse than an overdeclared one — it is routed, it consumes "
          + "budget, and no fitness row grades the model it lands on.");
    }

    /// <summary>
    /// A role that makes no model calls declares no model needs.
    ///
    /// This existed as `ADeterministicRole_DeclaresNoModelNeeds` and named `tester` alone. Tester was
    /// the only role it was ever true of, so it passed for six releases beside five roles in exactly
    /// the state it describes — a test pinned to one example rather than to the property, which is
    /// how a suite comes to have a guard for a defect and the defect at the same time.
    /// </summary>
    [Fact]
    public void ARoleThatCallsNoModel_DeclaresNoModelNeeds()
    {
        var claiming = AntExecutionCatalog.Contracts
            .Where(kv => !kv.Value.AllowsModelCalls && !kv.Value.ModelNeeds.IsEmpty)
            .Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(claiming.Count == 0,
            "these roles make no model calls and still declare requirements of a model: "
          + string.Join(", ", claiming)
          + ". The requirement will be graded and can never be met or unmet by anything that happens.");
    }

    /// <summary>
    /// A contract agrees with ITSELF about model calls: `AllowsModelCalls` and the `model.invoke`
    /// capability say the same thing.
    ///
    /// Found while writing this file, and it had been true in both directions at once: `soldier` and
    /// `ui_cartographer` declared `AllowsModelCalls: true` without requiring `model.invoke`, while
    /// `medic`, `archivist` and `scribe` required `model.invoke` for a call they could not make. Two
    /// fields of one record disagreeing is the cheapest possible version of this whole defect class,
    /// and nothing was comparing them.
    /// </summary>
    [Fact]
    public void AContractAgreesWithItself_AboutWhetherItInvokesAModel()
    {
        var disagreeing = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.AllowsModelCalls
                      != kv.Value.RequiredCapabilities.Contains(Capability.ModelInvoke))
            .Select(kv => $"{kv.Key} (AllowsModelCalls: {kv.Value.AllowsModelCalls.ToString().ToLowerInvariant()}, "
                        + $"model.invoke: {kv.Value.RequiredCapabilities.Contains(Capability.ModelInvoke).ToString().ToLowerInvariant()})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(disagreeing.Count == 0,
            "these contracts disagree with themselves about whether the role invokes a model: "
          + string.Join("; ", disagreeing)
          + ". One record, two fields, one fact.");
    }

    // -----------------------------------------------------------------------------------------------
    // Direction two: a call must have a declaration
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every route name handed to the router, read from the call sites.
    ///
    /// v0.3.8.112 — RENAMED FROM `RouteLiteralsInSource` AND NO LONGER LITERAL-ONLY. The old regex
    /// required a quoted first argument, and BOTH assertions built on it failed in opposite
    /// directions the moment a call site named a constant: the first stopped seeing an undeclared
    /// route (a silent false negative), and the second reported a declared route as unreached (a
    /// noisy false positive whose obvious fix is deleting a route that is fine). The name changed
    /// too, because "literals" was the thing that was wrong.
    /// </summary>
    private static IReadOnlyList<string> RoutesInSource()
    {
        var constants = SourceText.ConstantsAcrossSource(SourceText.RepoRoot());
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var code = SourceText.CodeOnly(File.ReadAllText(file));
            foreach (var method in new[] { "GenerateTyped", "SendTyped", "Generate" })
                foreach (var route in SourceText.CallArgument(code, method, 0, constants))
                    if (Regex.IsMatch(route, "^[a-z_]+$")) found.Add(route);
        }
        return found.ToList();
    }

    /// <summary>
    /// EVERY ROUTE THAT REACHES THE ROUTER IS DECLARED. This is the assertion that would have caught
    /// `planner` and `strategist` — two routes that have called a model since before the fitness
    /// report existed and were never in the set it graded.
    /// </summary>
    [Fact]
    public void EveryRouteReachingTheRouter_IsDeclared()
    {
        var undeclared = RoutesInSource()
            .Where(r => ModelRouteRequirements.For(r) is null).ToList();

        Assert.True(undeclared.Count == 0,
            "these route names are passed to the model router and are not in "
          + "ModelRouteRequirements.Routes: " + string.Join(", ", undeclared)
          + ". An undeclared route still resolves — unknown names fall back to the default route — "
          + "so it will work, cost tokens, and never appear in the fitness report. Declare what it "
          + "needs and what silently happens when it does not get it.");
    }

    /// <summary>
    /// And every declared route is reached by something. A requirement for a call site that was
    /// deleted grades a model against a need nobody has, which is the same rot as the contracts
    /// this release corrected — pointed at the new table instead of the old one.
    /// </summary>
    [Fact]
    public void EveryDeclaredRoute_IsReachedBySomething()
    {
        var literals = RoutesInSource().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreached = ModelRouteRequirements.Routes.Keys
            .Where(r => !literals.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.True(unreached.Count == 0,
            "these routes declare model requirements and no source file passes their name to the "
          + "router: " + string.Join(", ", unreached)
          + ". Either the call site moved and this is now grading nothing, or the route is gone.");
    }

    /// <summary>
    /// Each declared route names a caller that exists, and says what silently happens on shortfall.
    ///
    /// The shortfall text is not decoration. Every entry in that table degrades into a plausible
    /// answer rather than an error — that is the entire reason the fitness report is a startup check
    /// — and a route added without thinking about its silent failure is a route whose row an
    /// operator cannot act on.
    /// </summary>
    [Fact]
    public void EveryDeclaredRoute_NamesARealCallerAndItsSilentFailure()
    {
        foreach (var (routeId, route) in ModelRouteRequirements.Routes)
        {
            Assert.False(string.IsNullOrWhiteSpace(route.OnShortfall),
                $"route '{routeId}' does not say what happens when its requirement is unmet.");

            var type = route.Caller.Split('.')[0];
            var exists = Directory.EnumerateFiles(
                    Path.Combine(SourceText.RepoRoot(), "src"), $"{type}.cs", SearchOption.AllDirectories)
                .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                || Directory.EnumerateFiles(
                    Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Any(p => File.ReadAllText(p).Contains($"class {type}", StringComparison.Ordinal));

            Assert.True(exists,
                $"route '{routeId}' names caller '{route.Caller}' and no type '{type}' exists. "
              + "A citation that does not resolve reads as a checked claim and is not one.");
        }
    }

    // -----------------------------------------------------------------------------------------------
    // The report the operator actually sees
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The fitness report grades ROUTES, not contracts — asserted on the source because the two
    /// enumerations are the same shape and swapping one for the other compiles, passes every
    /// capability test, and silently restores the defect.
    /// </summary>
    [Fact]
    public void TheFitnessReport_EnumeratesRoutes_NotContracts()
    {
        var fitness = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "AntModelFitness.cs")));

        Assert.Contains("ModelRouteRequirements.Routes", fitness);
        Assert.DoesNotContain("contracts.OrderBy", fitness);
    }
}
