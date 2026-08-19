using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A call the COLONY stopped is evidence about the colony, never about the route. v0.3.8.81
/// (PLAN.md §2 R3).
///
/// THE DEFECT THIS PINS, found by driving R3's `during_generation` cells live rather than citing
/// them. `ModelRouter.SendCore` held two implementations of one rule, four lines apart:
///
///   _breaker?.Record(routeKey, result.Status.ToCircuitSignal());   // Cancelled → Neutral
///   ...
///   var success = result.Ok;                                       // Cancelled → false
///   var pheromoneDelta = success ? 0.01 : ... : -0.01;             // Cancelled → -0.01
///
/// The breaker's own comment said it: "we stopped the call ourselves — no signal about provider
/// health". The trail below it disagreed by omission, because it derived everything from `Ok` and a
/// cancelled call is not Ok. So every operator stop wrote a FAILURE against
/// `model:{provider}:{model}:{role}`.
///
/// WHY IT MATTERED MORE THAN THE NUMBER SUGGESTS. The breaker's copy is transient state that decays
/// in minutes; the trail is the colony's DURABLE memory of which model suits which role, and R8's
/// reputation-aware routing is scheduled to read it. Nothing looked wrong at the time — the mission
/// was cancelled, which is what the operator asked for — and the damage only surfaces later, as a
/// route the colony has quietly learned to avoid because people kept stopping missions that used it.
/// A wrong memory that can be traced back to the mission that produced it is R8's exit gate; this is
/// one that could not have been, because the mission that wrote it looked fine.
///
/// These are cheap guards on purpose. The behavioural proof is
/// `RoleCancellationTests.ACancelledMission_StopsARoleMidGeneration`, which asserts no failure is
/// written to the role's trail. What is here is the RULE — one predicate, both readers — because the
/// behavioural test would still pass if someone re-derived the rule at a second site.
/// </summary>
public class CancellationIsNotReputationTests
{
    /// <summary>
    /// The two readers agree for every outcome, by construction rather than by coincidence.
    /// </summary>
    [Fact]
    public void EveryColonyStoppedOutcome_IsNeutralToTheBreaker()
    {
        foreach (var outcome in Enum.GetValues<ModelCallOutcome>())
        {
            if (!outcome.IsColonyStopped()) continue;

            Assert.Equal(CircuitSignal.Neutral, outcome.ToCircuitSignal());
            Assert.False(new ModelCallResult(outcome, "").Ok,
                $"'{outcome.Name()}' is a colony-stopped outcome and also reports Ok. Then the trail "
              + "would REINFORCE the route for a call nobody completed, which is the same defect "
              + "pointed the other way.");
        }
    }

    /// <summary>Cancellation is the one, and it is stated rather than assumed.</summary>
    [Fact]
    public void CancelledIsColonyStopped_AndAnErrorIsNot()
    {
        Assert.True(ModelCallOutcome.Cancelled.IsColonyStopped());

        // Deliberately NOT folded in: an Error is a call we could not read, not one we stopped, and
        // only the second is guaranteed to say nothing about the route. The breaker treats both as
        // Neutral for its own reasons; the trail must not inherit that by association.
        Assert.False(ModelCallOutcome.Error.IsColonyStopped());
        Assert.False(ModelCallOutcome.Timeout.IsColonyStopped());
        Assert.False(ModelCallOutcome.ConnectError.IsColonyStopped());
        Assert.False(ModelCallOutcome.Ok.IsColonyStopped());
    }

    /// <summary>
    /// The router's trail write is GATED on the shared predicate, read from source.
    ///
    /// A source guard rather than only a behavioural one because the failure being prevented is a
    /// future edit re-deriving the rule locally — `outcome is ModelCallOutcome.Cancelled` spelled out
    /// again next to the write — which every behavioural test in the suite would still pass.
    /// </summary>
    [Fact]
    public void TheRoutersTrailWrite_ReadsTheSharedPredicate()
    {
        var router = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Models", "ModelRouter.cs")));

        Assert.Matches(@"IsColonyStopped\s*\(\s*\)", router);

        // The write is inside a conditional, and the conditional is the one derived above. Matched as
        // "an `if` mentioning the flag, then the write, with no intervening `}`" so a later edit that
        // moves the write out of the guard fails here.
        Assert.Matches(
            new Regex(@"if\s*\(\s*isRouteEvidence\s*\)[^}]*UpdatePheromoneTrail\s*\(", RegexOptions.Singleline),
            router);
    }

    /// <summary>
    /// And nowhere else re-derives it. One authority means one place that names the enum member in
    /// this context; every other reader asks the predicate.
    /// </summary>
    [Fact]
    public void NoOtherSiteDecidesReputationFromTheCancelledMemberItself()
    {
        var offenders = new List<string>();
        var src = Path.Combine(SourceText.RepoRoot(), "src");

        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = SourceText.CodeOnly(File.ReadAllText(file));
            if (!code.Contains("UpdatePheromoneTrail", StringComparison.Ordinal)) continue;
            if (code.Contains("ModelCallOutcome.Cancelled", StringComparison.Ordinal)
                && !code.Contains("IsColonyStopped", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "these files decide a pheromone write near ModelCallOutcome.Cancelled without asking "
          + "IsColonyStopped: " + string.Join(", ", offenders)
          + ". Two implementations of one rule is how this defect existed in the first place.");
    }
}
