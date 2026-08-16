using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md acceptance gate 6 — one bounded repair, and the bound cannot be argued away. v0.3.8.57.
///
/// THE DEFECT. The bound on repair looping was this:
///
///     mission.Tasks.Any(t => t.AssignedAnt == "medic" &amp;&amp; t.Result.Contains(signature))
///
/// A substring search of a previous medic's NARRATIVE. Task results are summarised and truncated —
/// `ResultChars`, `MaxResultSummaryChars` — so a diagnosis long enough to push the signature past the
/// cut silently stopped matching, and the loop control vanished in precisely the missions that had
/// generated the most output. That is the worst possible correlation: the bound was weakest exactly
/// where the loop was longest.
///
/// It also reproduces, one layer down, the failure ADR-004 exists to end. The `failure_context`
/// artifact has carried `FailureSignature` as a FIELD since the structural repair release; the medic
/// was reading it to diagnose and then grepping prose to decide whether to stop.
/// </summary>
public class BoundedRepairTests
{
    private static string MedicSource() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

    // -------------------------------------------------------------------------------------------
    // One budget, in one place
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Repairs and replans have SEPARATE counters, and exhausting one does not borrow from the other.
    /// ADR §3.1 is explicit about it, and the reason is that a shared budget makes "how many times
    /// may this mission retry" unanswerable — the number depends on what else already happened.
    /// </summary>
    [Fact]
    public void RepairAndReplanBudgets_AreSeparateCounters()
    {
        var budget = new AdaptiveBudget();

        Assert.True(budget.CanRepair);
        Assert.True(budget.CanReplan);

        var afterRepairs = budget.AfterRepair().AfterRepair();

        Assert.False(afterRepairs.CanRepair);
        // Spending every repair must not touch the replan budget.
        Assert.True(afterRepairs.CanReplan);
    }

    [Fact]
    public void TheRepairBudget_IsSpentAndThenClosed()
    {
        var budget = new AdaptiveBudget();

        for (var i = 0; i < AdaptiveBudget.MaxRepairCycles; i++)
        {
            Assert.True(budget.CanRepair);
            budget = budget.AfterRepair();
        }

        Assert.False(budget.CanRepair);
    }

    /// <summary>
    /// The mission carries the budget it was created with, rather than each site consulting the
    /// constant. One number, resolved at intake — the same rule the rest of the runtime follows for
    /// anything a mission's behaviour depends on.
    /// </summary>
    [Fact]
    public void TheBudget_ReachesTheMissionThroughItsContext()
    {
        var context = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "MissionContext.cs")));

        Assert.Contains("MaxRepairCycles", context);
    }

    /// <summary>
    /// And a spent budget produces a NAMED terminal reason. "The mission stopped" and "the mission
    /// stopped because it had run out of repair attempts" are different facts, and an operator can
    /// only act on the second.
    /// </summary>
    [Fact]
    public void AnExhaustedBudget_EndsTheMissionWithAnExplicitReason()
    {
        var execution = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        // v0.3.8.74 — the reason is now a NAMED constant rather than a literal, because
        // `adaptive_stop` turned out to be returned for two opposite situations and the evaluator
        // graded both as an escalation. This test's property is unchanged and is the reason the
        // split matters: an exhausted budget must end the mission with a reason an operator can act
        // on. It now checks that the executor derives that reason instead of hard-coding one.
        Assert.Contains("AdaptiveStopReason", execution);
        Assert.Contains("MissionStopReasons.AdaptiveStop", execution);
    }

    // -------------------------------------------------------------------------------------------
    // Repeated failure detection
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The bound reads the TYPED record. This is the fix, and the assertion that would have failed
    /// before it.
    /// </summary>
    [Fact]
    public void RepeatedFailureDetection_ReadsFailureContextArtifacts()
    {
        var medic = MedicSource();

        Assert.Contains("HasSeenSignatureBefore", medic);
        Assert.Contains("ArtifactSchemas.FailureContext", medic);
        Assert.Contains("x.Context!.FailureSignature", medic);
    }

    /// <summary>
    /// Counted by DISTINCT TASK, not by artifact. One failing task can record several contexts across
    /// its attempts, and counting those would make a single failure look like a repeat on its own
    /// retry — escalating immediately and turning a bounded repair into no repair at all.
    ///
    /// This is the direction that would have been easy to get wrong and hard to notice: it fails
    /// safe-looking. The mission escalates, an operator sees a diagnosis, and nobody asks why the
    /// repair never ran.
    /// </summary>
    [Fact]
    public void RepeatsAreCountedByTask_NotByArtifact()
    {
        var medic = MedicSource();

        Assert.Contains("Distinct(StringComparer.Ordinal)", medic);
        Assert.Contains("tasksWithThisSignature.Count > 1", medic);
    }

    /// <summary>
    /// The narrative scan survives only as the no-store fallback, and an unreadable store falls back
    /// rather than silently dropping the bound.
    ///
    /// Removing the fallback would have been cleaner to read and worse to run: dozens of tests and
    /// the CLI construct a medic with no artifact store, and in that configuration a weaker bound
    /// beats no bound. What must not happen is the strong path failing quietly into no path.
    /// </summary>
    [Fact]
    public void WithNoStore_TheWeakerBoundStillApplies()
    {
        var medic = MedicSource();

        Assert.Contains("if (_artifacts is null)", medic);
        Assert.Contains("falling back to the narrative scan for loop control", medic);
    }

    // -------------------------------------------------------------------------------------------
    // The plan
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void PlanAcceptanceGateSix_IsRecordedAsClosed()
    {
        var gate = SourceText.PlanAcceptanceGate(6);

        Assert.Contains("✅", gate);
        Assert.Contains("v0.3.8.57", gate);
    }
}
