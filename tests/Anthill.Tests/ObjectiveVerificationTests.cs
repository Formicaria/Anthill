using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.24.0 Phase C5: "was the goal met" on top of "did a verifier pass".
///
/// The gap: a mission whose goal is "add a CHANGELOG entry" can plan a researcher and a builder,
/// produce a description of the change, have the verifier honestly pass — every task did what it
/// said — and deliver no file change at all. `completed_verified` then flows to pheromones,
/// objective EMA, skill credit, and the auto-apply precondition.
///
/// The design constraint these tests hold: this is ADDITIVE. It can only narrow. Nothing that
/// fails the interim gate can newly pass, and nothing whose goal cannot be read is failed by it.
/// </summary>
public class ObjectiveVerificationTests
{

    /// <summary>
    /// v3.1.0 (ADR-002): the deliverable check takes the mission's constraints instead of
    /// re-parsing its goal. Resolved here exactly as intake resolves them, so these tests still
    /// pin the production rule.
    /// </summary>
    private static bool Satisfied(Mission? mission, int patches) =>
        ObjectiveVerification.IsSatisfied(mission, MissionConstraints.Parse(mission?.Goal), patches);

    private static string Explain(Mission? mission, int patches) =>
        ObjectiveVerification.Explain(mission, MissionConstraints.Parse(mission?.Goal), patches);
    private const string PassText = "Verification Passed\nReasoning: checked.";

    private static Mission Verified(string goal) =>
        new()
        {
            Goal = goal,
            Tasks =
            {
                new DomainTask { Title = "work", AssignedAnt = "coder", Status = TaskStatus.Complete },
                new DomainTask { Title = "verify", AssignedAnt = "verifier", TaskType = "verify", Status = TaskStatus.Complete, Result = PassText },
            },
        };

    // ---- reading the goal ------------------------------------------------------------------------

    [Theory]
    [InlineData("add a changelog entry for the release")]
    [InlineData("update the readme with the new flag")]
    [InlineData("fix the bug in the scheduler")]
    [InlineData("refactor the planner prompt")]
    public void GoalsThatPlainlyAskForAFileChange_AreRead(string goal) =>
        Assert.Equal(ObjectiveVerification.Deliverable.FileChange, ObjectiveVerification.Required(goal));

    /// <summary>
    /// Kept narrow deliberately. A verb that only MIGHT imply a change would make this fire on
    /// missions that legitimately deliver an answer — and a deliverable check that misfires is
    /// worse than none, because it marks genuinely complete work unverified and suppresses the
    /// learning that work earned.
    /// </summary>
    [Theory]
    [InlineData("summarise the recent mission failures")]
    [InlineData("what is the current disk usage on the proxmox node")]
    [InlineData("improve reliability")]
    [InlineData("investigate the timeout")]
    [InlineData("")]
    [InlineData(null)]
    public void GoalsWithoutAnExplicitFileChange_AreUnknown(string? goal) =>
        Assert.Equal(ObjectiveVerification.Deliverable.Unknown, ObjectiveVerification.Required(goal));

    /// <summary>
    /// A no-patch mission is FORBIDDEN from changing files. Requiring a change would make the two
    /// rules contradict, and this one would win by failing every such mission.
    /// </summary>
    [Fact]
    public void AReadOnlyGoal_NeverRequiresAFileChange()
    {
        const string goal = "review only: refactor the planner prompt and report what you would change";
        var constraints = MissionConstraints.Parse(goal);
        Assert.True(constraints.BlocksPatches);
        Assert.Equal(ObjectiveVerification.Deliverable.Unknown, ObjectiveVerification.Required(goal, constraints));
    }

    // ---- the deliverable check --------------------------------------------------------------------

    [Fact]
    public void AFileChangeGoalWithNoProposal_IsNotSatisfied()
    {
        var mission = Verified("add a changelog entry for v2.24.0");
        Assert.False(Satisfied(mission, 0));
        Assert.Contains("proposed none", Explain(mission, 0));
    }

    [Fact]
    public void AFileChangeGoalWithAProposal_IsSatisfied()
    {
        var mission = Verified("add a changelog entry for v2.24.0");
        Assert.True(Satisfied(mission, 1));
        Assert.Equal("objective satisfied", Explain(mission, 1));
    }

    /// <summary>
    /// PROPOSED, not applied. Ants propose and a human (or gated auto-apply) applies, so requiring
    /// an applied change would fail every correctly operating mission awaiting approval.
    /// </summary>
    [Fact]
    public void AProposalIsEnough_ApplicationIsNotRequired()
    {
        // The count is of proposals; nothing here consults application state at all.
        Assert.True(ObjectiveVerification.DeliverablePresent(ObjectiveVerification.Deliverable.FileChange, 1));
    }

    [Fact]
    public void AGoalAskingForNothingSpecific_RequiresNothingSpecific()
    {
        var mission = Verified("summarise the recent mission failures");
        Assert.True(Satisfied(mission, 0));
    }

    // ---- additive: it can only narrow --------------------------------------------------------------

    /// <summary>
    /// The floor is never relaxed. A mission that fails the interim gate stays failed, whatever
    /// it delivered — a pile of patches does not substitute for verification.
    /// </summary>
    [Fact]
    public void FailingTheInterimGate_IsStillAFailure_HoweverMuchWasDelivered()
    {
        var unverified = new Mission
        {
            Goal = "add a changelog entry",
            Tasks = { new DomainTask { Title = "work", AssignedAnt = "coder", Status = TaskStatus.Complete } },
        };
        Assert.False(MissionVerification.IsSatisfied(unverified.Tasks));
        Assert.False(Satisfied(unverified, 99));
    }

    /// <summary>
    /// The safety property of the whole design: for every mission, the objective gate implies the
    /// interim gate. Nothing can pass here that the floor rejects.
    /// </summary>
    [Fact]
    public void TheObjectiveGateNeverAdmitsWhatTheFloorRejects()
    {
        var goals = new[] { "add a changelog entry", "summarise failures", "read-only: refactor", "" };
        foreach (var goal in goals)
        foreach (var verified in new[] { true, false })
        foreach (var patches in new[] { 0, 1, 5 })
        {
            var mission = verified ? Verified(goal) : new Mission { Goal = goal };
            if (Satisfied(mission, patches))
                Assert.True(MissionVerification.IsSatisfied(mission.Tasks),
                    $"objective gate admitted a mission the floor rejects: goal='{goal}', patches={patches}");
        }
    }

    [Fact]
    public void ANullMission_IsNotSatisfied() =>
        Assert.False(Satisfied(null, 1));

    // ---- gated, and wired -------------------------------------------------------------------------

    [Fact]
    public void ObjectiveVerification_IsOffByDefault()
    {
        Assert.False(AnthillRuntime.EnableObjectiveVerification);
        Assert.False(new AnthillConfig().ObjectiveVerificationEnabled);
    }

    /// <summary>
    /// One place decides whether a mission is verified, and the skill-credit path reads it — so a
    /// mission that did not deliver cannot quietly promote the procedure it followed.
    /// </summary>
    [Fact]
    public void TheQueenHasOneVerificationDecision_AndCreditReadsIt()
    {
        string CodeOnly(string relative) => string.Join("\n",
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)))
                .Split('\n')
                .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        var code = CodeOnly("src/Anthill.Core/Orchestration/Queen.cs");

        // v2.26.0: the one decision moved from Queen.MissionIsVerified into the canonical
        // evaluator — computed once, persisted, consumed. Same intent, structurally stronger.
        // v3.1.0: the evaluator is INJECTED (IMissionEvaluator), so the Queen grades through the
        // one grader rather than reaching for a static. The rule is unchanged; the authority is
        // now visible to the composition root.
        Assert.Contains("_evaluator.Evaluate(", code);
        Assert.Contains("SaveMissionEvaluation(evaluation)", code);

        // And that grader is a pass-through to the canonical rules — not a second set of them.
        var grader = CodeOnly("src/Anthill.Core/Orchestration/MissionServices.cs");
        Assert.Contains("MissionEvaluator.Evaluate(", grader);
        Assert.DoesNotContain("MissionIsVerified", code);         // the second authority is GONE
        Assert.Contains("objective_verification_failed", code);   // never a silent downgrade

        // v3.1.0: credit moved behind ILearningRecorder, so "the credit path reads the ONE
        // decision" is now two checkable facts — the Queen hands that decision to learning, and
        // the recorder consumes IsPositive rather than deriving verified-ness for itself. The
        // property this test defends is unchanged: there is exactly one authority, and credit
        // asks it.
        Assert.Contains("_learning.Record(mission, context, evaluation)", code);

        var learning = CodeOnly("src/Anthill.Core/Orchestration/LearningRecorder.cs");
        Assert.Contains("evaluation.IsPositive", learning);
        Assert.DoesNotContain("MissionEvaluator.Evaluate(", learning);   // it consumes; it never re-evaluates
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
