using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// An adaptive stop is not one thing. v0.3.8.74.
///
/// THE DEFECT, and it was found by the first test that ever needed the outcome it broke.
/// `ExecutionService` returned the single stop reason `adaptive_stop` from three call sites, for two
/// STRUCTURALLY OPPOSITE situations:
///
///   * the repair bound is spent and the critical failure persists — a genuine escalation;
///   * the controller wanted to add a verification step and found the mission already has one, so
///     there is nothing to add — a SUCCESS.
///
/// `MissionEvaluation.Resolve` mapped every `adaptive_stop` to `MissionOutcome.Escalated`, before it
/// looked at a single task, verdict or piece of evidence. So a mission whose plan included a
/// verifier — which is the ordinary shape — could pass every check, pass its security review, record
/// deterministic evidence bound to its revision, and still be graded `escalated`.
///
/// WHY IT MATTERED RATHER THAN JUST READING BADLY. Auto-apply consumes the canonical evaluation and
/// refuses anything that is not `completed_verified`. The second stop therefore made a clean, fully
/// verified patch mission **structurally incapable of applying its own patch** — in production, not
/// only in tests.
///
/// WHY IT SURVIVED THIS LONG. Every lifecycle test before v0.3.8.74 stopped at "materialized and
/// reviewed", where the difference between the two stops has no consequence. Qualification scenario
/// 3 is the first test in the project's history to drive a mission from a goal to applied bytes, and
/// the first that needed `completed_verified` to be reachable at all. It failed on assertion 2 with
/// `outcome: escalated` while its own log showed every task complete — including the tester — and
/// the stop line reading "verification already present". A green mission graded as an escalation.
/// </summary>
public class AdaptiveStopMeaningTests
{
    /// <summary>
    /// The two stops are different reasons. This is the fix stated at its narrowest: one code was
    /// answering two questions, and a distinction that exists only in a log line is not a
    /// distinction the runtime can act on.
    /// </summary>
    [Fact]
    public void SatisfactionAndEscalation_AreNotTheSameStopReason() =>
        Assert.NotEqual(MissionStopReasons.AdaptiveStop, MissionStopReasons.AdaptiveStopSatisfied);

    [Theory]
    [InlineData(MissionStopReasons.AdaptiveStop, true)]
    [InlineData(MissionStopReasons.AdaptiveStopSatisfied, false)]
    [InlineData(MissionStopReasons.Cancelled, false)]
    [InlineData(MissionStopReasons.Timeout, false)]
    [InlineData(null, false)]
    public void OnlyTheSpentBound_CountsAsAnEscalation(string? reason, bool expected) =>
        Assert.Equal(expected, MissionStopReasons.IsEscalation(reason));

    // -----------------------------------------------------------------------------------------------
    // What the evaluator does with each
    // -----------------------------------------------------------------------------------------------

    private static (Mission, MissionConstraints) VerifiedMission()
    {
        var verify = new Task
        {
            Title = "Verify the outcome", AssignedAnt = "verifier", TaskType = "verification",
            Status = TaskStatus.Complete, Result = "Verdict: Verification Passed",
        };
        var mission = new Mission { Goal = "add a note", Status = MissionStatus.Complete };
        mission.Tasks.Add(new Task
        {
            Title = "Propose", AssignedAnt = "coder", TaskType = "patch_proposal",
            Status = TaskStatus.Complete, Result = "proposed",
        });
        mission.Tasks.Add(verify);
        return (mission, MissionConstraints.None);
    }

    /// <summary>
    /// A SATISFACTION stop leaves the grade to the mission's own record — the controller looked,
    /// found nothing to do, and said so, which must not change the outcome.
    /// </summary>
    [Fact]
    public void ASatisfactionStop_DoesNotEscalate()
    {
        var (mission, constraints) = VerifiedMission();

        var evaluation = MissionEvaluator.Evaluate(
            mission, MissionStopReasons.AdaptiveStopSatisfied, patchProposalCount: 1,
            constraints, objectiveVerificationEnabled: false);

        Assert.NotEqual(MissionOutcome.Escalated, evaluation.OutcomeCode);
    }

    /// <summary>
    /// And an ESCALATING stop still escalates, over the identical mission. Proved from both sides so
    /// the fix cannot be "stop escalating" — the bound being spent is exactly when a person is
    /// needed, and that behaviour is unchanged.
    /// </summary>
    [Fact]
    public void AnEscalatingStop_StillEscalates_OverTheSameMission()
    {
        var (mission, constraints) = VerifiedMission();

        var evaluation = MissionEvaluator.Evaluate(
            mission, MissionStopReasons.AdaptiveStop, patchProposalCount: 1,
            constraints, objectiveVerificationEnabled: false);

        Assert.Equal(MissionOutcome.Escalated, evaluation.OutcomeCode);
    }

    // -----------------------------------------------------------------------------------------------
    // The runtime emits the distinction, not just the vocabulary
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A vocabulary nothing produces is a vocabulary that changes nothing — the "declared and
    /// reaching nobody" defect this repository keeps naming. So the executor is checked for the two
    /// halves that make the distinction real: the satisfaction arm sets the flag, and every stop
    /// returns the derived reason rather than a literal.
    /// </summary>
    [Fact]
    public void TheExecutor_EmitsTheSatisfactionReason_RatherThanALiteral()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        Assert.Contains("_adaptiveStopWasSatisfaction = true;", source);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            source, @"return AdaptiveStopReason;").Count);

        // No stop returns the bare literal any more; if one did, it would be graded as an
        // escalation whatever the arm meant, which is the defect itself.
        Assert.DoesNotContain("return \"adaptive_stop\";", source);
    }

    /// <summary>
    /// The flag is RESET on entry, so a satisfaction stop cannot colour a later escalation. Both
    /// arms run inside one mission in the composed tests, and a sticky flag would make the second
    /// stop inherit the first's meaning — a defect with exactly the shape of the one being fixed.
    /// </summary>
    [Fact]
    public void TheSatisfactionFlag_IsResetOnEveryDecision()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var reset = source.IndexOf("_adaptiveStopWasSatisfaction = false;", StringComparison.Ordinal);
        var set = source.IndexOf("_adaptiveStopWasSatisfaction = true;", StringComparison.Ordinal);

        Assert.True(reset >= 0, "the satisfaction flag is never reset, so one stop's meaning leaks into the next");
        Assert.True(reset < set, "the reset must precede the set — otherwise it clears the answer it just produced");
    }
}
