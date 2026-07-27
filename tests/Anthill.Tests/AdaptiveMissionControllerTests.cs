using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.22.0 Phase B: the adaptive loop's decision layer. The ADR rejected letting the planner
/// re-plan freely on each wave — that is unbounded recursive task creation wearing a different
/// word — so replans are capped by generation, repairs by cycle, and a wave that changed nothing
/// escalates instead of spinning.
///
/// The controller is pure: no database, no model, no scheduler mutation. Same mission state, same
/// decision, every time.
/// </summary>
public class AdaptiveMissionControllerTests
{
    private static readonly AdaptiveMissionController Controller = new();

    private const string PassText = "Verification Passed\nReasoning: checked.";
    private const string FailText = "Verification Failed\nReasoning: broken.";

    private static DomainTask T(string ant, TaskStatus status, bool critical = true, string? result = null, string type = "general") =>
        new() { Title = $"{ant}:{type}", AssignedAnt = ant, TaskType = type, Status = status, Critical = critical, Result = result };

    private static Mission M(params DomainTask[] tasks)
    {
        var m = new Mission { Goal = "do the thing" };
        foreach (var t in tasks) m.Tasks.Add(t);
        return m;
    }

    /// <summary>A mission that genuinely finished and verified.</summary>
    private static Mission Verified() =>
        M(T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Complete, result: PassText, type: "verify"));

    // ---- budgets are separate, and do not lend to each other -------------------------------------

    /// <summary>
    /// ADR §3.1: "Each has its own counter, and exhausting one does not borrow budget from
    /// another." A mission out of replans must still be able to repair, and vice versa.
    /// </summary>
    [Fact]
    public void ExhaustingOneBudget_DoesNotConsumeTheOther()
    {
        var spentReplans = new AdaptiveBudget(ReplansUsed: AdaptiveBudget.MaxReplans);
        Assert.False(spentReplans.CanReplan);
        Assert.True(spentReplans.CanRepair);

        var spentRepairs = new AdaptiveBudget(RepairCyclesUsed: AdaptiveBudget.MaxRepairCycles);
        Assert.True(spentRepairs.CanReplan);
        Assert.False(spentRepairs.CanRepair);
    }

    [Fact]
    public void SpendingABudget_AdvancesOnlyThatCounter()
    {
        var b = new AdaptiveBudget();
        Assert.Equal(new AdaptiveBudget(ReplansUsed: 1, RepairCyclesUsed: 0), b.AfterReplan());
        Assert.Equal(new AdaptiveBudget(ReplansUsed: 0, RepairCyclesUsed: 1), b.AfterRepair());
    }

    // ---- the fingerprint is what distinguishes "working" from "stuck" ----------------------------

    [Fact]
    public void TheFingerprintChanges_WhenAnyTaskChangesState()
    {
        var task = T("coder", TaskStatus.Running);
        var mission = M(task);
        var before = AdaptiveMissionController.Fingerprint(mission);

        task.Status = TaskStatus.Complete;
        Assert.NotEqual(before, AdaptiveMissionController.Fingerprint(mission));
    }

    /// <summary>Task ordering must not make a stalled mission look like it moved.</summary>
    [Fact]
    public void TheFingerprintIsOrderIndependent()
    {
        var a = T("coder", TaskStatus.Complete);
        var b = T("verifier", TaskStatus.Pending);
        Assert.Equal(
            AdaptiveMissionController.Fingerprint(M(a, b)),
            AdaptiveMissionController.Fingerprint(M(b, a)));
    }

    [Fact]
    public void TheFingerprintIsStableForAnUnchangedMission()
    {
        var mission = Verified();
        Assert.Equal(AdaptiveMissionController.Fingerprint(mission), AdaptiveMissionController.Fingerprint(mission));
    }

    // ---- decisions ---------------------------------------------------------------------------------

    [Fact]
    public void AVerifiedTerminalMission_Finishes()
    {
        var decision = Controller.Assess(Verified(), new AdaptiveBudget());
        Assert.Equal(AdaptiveAction.Finish, decision.Action);
        Assert.Empty(decision.UnmetCriteria);
    }

    /// <summary>
    /// The finished-vs-stalled distinction. A complete mission assessed twice produces an identical
    /// fingerprint; if the stall check ran first, it would be escalated instead of finished.
    /// </summary>
    [Fact]
    public void ACompleteMission_IsNotMistakenForAStalledOne()
    {
        var mission = Verified();
        var fingerprint = AdaptiveMissionController.Fingerprint(mission);
        var decision = Controller.Assess(mission, new AdaptiveBudget(), previousFingerprint: fingerprint);
        Assert.Equal(AdaptiveAction.Finish, decision.Action);
    }

    [Fact]
    public void WorkOutstandingAndProgressing_Continues()
    {
        var mission = M(T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Pending, type: "verify"));
        var decision = Controller.Assess(mission, new AdaptiveBudget(), previousFingerprint: "something-else");
        Assert.Equal(AdaptiveAction.Continue, decision.Action);
    }

    /// <summary>Nothing moved during the wave: continuing would spin forever.</summary>
    [Fact]
    public void AWaveThatChangedNothing_Escalates_RatherThanLooping()
    {
        var mission = M(T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Pending, type: "verify"));
        var decision = Controller.Assess(mission, new AdaptiveBudget(),
            previousFingerprint: AdaptiveMissionController.Fingerprint(mission));

        Assert.Equal(AdaptiveAction.Escalate, decision.Action);
        Assert.Contains("not progressing", decision.Reason);
    }

    /// <summary>
    /// A broken step is a repair candidate before it is a replan candidate: the plan was not wrong,
    /// one of its steps failed. Repair is focused; delta planning is not.
    /// </summary>
    [Fact]
    public void AFailedCriticalTask_Repairs_BeforeItReplans()
    {
        var mission = M(T("coder", TaskStatus.Failed), T("verifier", TaskStatus.Pending, type: "verify"));
        var decision = Controller.Assess(mission, new AdaptiveBudget());
        Assert.Equal(AdaptiveAction.Repair, decision.Action);
        Assert.Contains(decision.UnmetCriteria, c => c.Contains("critical task failed"));
    }

    [Fact]
    public void AFailedCriticalTask_WithRepairsSpent_Escalates()
    {
        var mission = M(T("coder", TaskStatus.Failed), T("verifier", TaskStatus.Pending, type: "verify"));
        var decision = Controller.Assess(mission, new AdaptiveBudget(RepairCyclesUsed: AdaptiveBudget.MaxRepairCycles));
        Assert.Equal(AdaptiveAction.Escalate, decision.Action);
        Assert.Contains("bound is spent", decision.Reason);
    }

    /// <summary>A non-critical failure is not a mission emergency — it must not trigger repair.</summary>
    [Fact]
    public void ANonCriticalFailure_DoesNotTriggerRepair()
    {
        var mission = M(
            T("coder", TaskStatus.Complete),
            T("web", TaskStatus.Failed, critical: false),
            T("verifier", TaskStatus.Complete, result: PassText, type: "verify"));

        Assert.Equal(AdaptiveAction.Finish, Controller.Assess(mission, new AdaptiveBudget()).Action);
    }

    /// <summary>Everything ran, but the mission verified nothing: the PLAN was incomplete.</summary>
    [Fact]
    public void TerminalButUnverified_PlansOnlyTheDelta()
    {
        var mission = M(T("coder", TaskStatus.Complete), T("researcher", TaskStatus.Complete));
        var decision = Controller.Assess(mission, new AdaptiveBudget());

        Assert.Equal(AdaptiveAction.DeltaPlan, decision.Action);
        Assert.Contains(decision.UnmetCriteria, c => c.Contains("verification"));
    }

    [Fact]
    public void TerminalAndUnverified_WithReplansSpent_Escalates()
    {
        var mission = M(T("coder", TaskStatus.Complete), T("researcher", TaskStatus.Complete));
        var decision = Controller.Assess(mission, new AdaptiveBudget(ReplansUsed: AdaptiveBudget.MaxReplans));

        Assert.Equal(AdaptiveAction.Escalate, decision.Action);
        Assert.Contains("would not be bounded", decision.Reason);
    }

    /// <summary>
    /// A verifier that ran to completion but reported FAILURE leaves the mission unverified — the
    /// v2.19.0 rule. The controller must agree with the gate, or it would propose work the gate
    /// would never accept.
    /// </summary>
    [Fact]
    public void ACompletedButFailingVerifier_LeavesTheMissionUnmet()
    {
        var mission = M(T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Complete, result: FailText, type: "verify"));
        var decision = Controller.Assess(mission, new AdaptiveBudget());

        Assert.NotEqual(AdaptiveAction.Finish, decision.Action);
        Assert.Contains(decision.UnmetCriteria, c => c.Contains("verification"));
    }

    [Fact]
    public void ASkippedCriticalTask_IsAnUnmetCriterion()
    {
        var mission = M(
            T("coder", TaskStatus.Skipped),
            T("verifier", TaskStatus.Complete, result: PassText, type: "verify"));

        Assert.Contains(AdaptiveMissionController.UnmetCriteria(mission), c => c.Contains("did not run"));
    }

    [Fact]
    public void AnEmptyMission_Escalates_RatherThanClaimingSuccess()
    {
        var decision = Controller.Assess(new Mission { Goal = "g" }, new AdaptiveBudget());
        Assert.Equal(AdaptiveAction.Escalate, decision.Action);
    }

    // ---- purity: the same state always yields the same decision ----------------------------------

    /// <summary>
    /// Decisions compare by VALUE, criteria list included. A record's generated equality would
    /// compare that list by reference, so two assessments of the same unchanged mission would be
    /// unequal merely because each built its own list — quietly breaking any future "has the
    /// decision changed since the last wave?" check, which would always answer yes.
    /// </summary>
    [Fact]
    public void DecisionsCompareByValue_IncludingTheCriteriaList()
    {
        var a = AdaptiveDecision.Of(AdaptiveAction.Repair, "same", new List<string> { "x", "y" });
        var b = AdaptiveDecision.Of(AdaptiveAction.Repair, "same", new List<string> { "x", "y" });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, AdaptiveDecision.Of(AdaptiveAction.Repair, "same", new List<string> { "x" }));
        Assert.NotEqual(a, AdaptiveDecision.Of(AdaptiveAction.Escalate, "same", new List<string> { "x", "y" }));
    }

    [Fact]
    public void AssessmentIsDeterministic()
    {
        var mission = M(T("coder", TaskStatus.Failed), T("verifier", TaskStatus.Pending, type: "verify"));
        var budget = new AdaptiveBudget();
        var first = Controller.Assess(mission, budget);

        for (var i = 0; i < 5; i++)
            Assert.Equal(first, Controller.Assess(mission, budget));
    }

    /// <summary>Every decision that stops the mission must be able to say what it was waiting for.</summary>
    [Fact]
    public void EveryDecisionCarriesAnOperatorFacingReason()
    {
        foreach (var mission in new[]
                 {
                     Verified(),
                     M(T("coder", TaskStatus.Failed), T("verifier", TaskStatus.Pending, type: "verify")),
                     M(T("coder", TaskStatus.Complete), T("researcher", TaskStatus.Complete)),
                     new Mission { Goal = "g" },
                 })
        {
            var decision = Controller.Assess(mission, new AdaptiveBudget());
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        }
    }
}
