using System;
using System.Collections.Generic;
using System.Linq;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Planning;
using Anthill.SDK.Knowledge;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A SEEDED STUDY MISSION ACTUALLY READS THE KNOWLEDGE BASE. v0.3.9.8.
///
/// FOUND IN THE OPERATOR'S LIVE COLONY, after the first Study pass that ever completed. All fifty
/// seeded missions planned as `builder -> verifier`. No researcher, no `knowledge_retrieve`, nothing
/// in the graph ever opened the knowledge base — and every one then graded `completed_verified` at
/// 1.0 while its own answer said it had found nothing:
///
///   "The knowledge base contains no established facts about '1988.txt'. The prior task output only
///    describes the mission's parameters and mode but does not include any substantive findings."
///
/// A perfect score for answering a question about documents it never opened. Worse than a failure,
/// because a graded success reinforces the pheromone trails for the route that took it — seeding
/// was teaching the colony that this is how you answer a question about the organization's
/// knowledge.
///
/// THE CAUSE WAS A CLASSIFICATION, and every layer along the way was correct on its own terms.
/// `KnowledgeSeeder` phrases its goal as a QUESTION on purpose, because a question resolves to a
/// class whose authority ceiling is `Observe` — the property that makes it safe to start twenty-five
/// of these with one click. A question with no target and an `Explain` intent is a `simple_answer`.
/// And `simple_answer`'s promise, in its own words, is that "the answer rests on nothing retrieved
/// and nothing inspected", so `EnsureClassCoverage` excludes the knowledge step for it and the
/// planner's own reduction strips every gathering step anyway.
///
/// So the single lane built to read a bound knowledge base was the one lane that could never read
/// it. `.156` inserts the step, `.157` made the researcher dispatch the tool, `.9.6` made the
/// binding reach the resolver — and the mission needing all three took the path that skipped the
/// first.
///
/// THESE TESTS PIN THE PROPERTY, NOT THE REGEX. What matters is that the seeder's own goal — read
/// from `KnowledgeSeeder.GoalForName` rather than retyped here, so the two cannot drift — produces a
/// plan containing a step that reads the knowledge base. A test that asserted the class name would
/// pass while the plan stayed builder-and-verifier, which is precisely the mistake that let this
/// ship.
/// </summary>
public class SeededStudyMissionTests
{
    private static string SeededGoal() =>
        Anthill.Api.Knowledge.KnowledgeSeeder.GoalForName("1988.txt");

    private static KnowledgeScope Scope() => KnowledgeScope.ForProject("proj_kb", "anthill-proj");

    /// <summary>
    /// THE ASSERTION THAT WOULD HAVE CAUGHT IT: the seeded goal, planned inside a knowledge scope,
    /// yields a plan with a researcher step that names a knowledge tool.
    /// </summary>
    [Fact]
    public void TheSeededGoal_PlansAStepThatReadsTheKnowledgeBase()
    {
        using var _ = KnowledgeScopeContext.Enter(Scope());

        var goal = SeededGoal();
        var specification = MissionIntake.Resolve(goal);
        var tasks = Planner.EnsureClassCoverage(new List<Anthill.Core.Domain.Task>(), goal, specification);

        Assert.Contains(tasks, t =>
            string.Equals(t.AssignedAnt, "researcher", StringComparison.OrdinalIgnoreCase)
         && (t.Description ?? "").Contains(KnowledgeToolNames.Retrieve, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AND THE CLASSIFICATION UNDERNEATH IT. Stated separately because the plan above depends on it,
    /// and a failure in one tells you which of the two moved.
    /// </summary>
    [Fact]
    public void AQuestionAboutTheKnowledgeBase_IsNotASimpleAnswer_WhenAScopeIsInForce()
    {
        using var _ = KnowledgeScopeContext.Enter(Scope());

        Assert.NotEqual(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(SeededGoal()).MissionClass);
    }

    /// <summary>
    /// WITH NO BINDING, NOTHING CHANGES. There is nothing to retrieve on a colony that has bound no
    /// knowledge base, so `simple_answer` stays the honest reading — and this is what keeps the fix
    /// from being a behaviour change for every colony that does not use FORAGER at all.
    /// </summary>
    [Fact]
    public void TheSameQuestion_IsStillASimpleAnswer_WithNoKnowledgeScope()
    {
        Assert.False(KnowledgeScopeContext.HasScope);

        Assert.Equal(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(SeededGoal()).MissionClass);
    }

    /// <summary>
    /// AND THE NARROWNESS IS THE OTHER HALF OF THE FIX. v0.3.8.145 paid for `simple_answer` with a
    /// live failure: "how do you make tacos?" planned seven tasks, hit a 240s research cap, and died
    /// on the 600s budget with NOT ANSWERED — ten minutes for a child's question. Widening the class
    /// out of existence to fix the seeder would have bought that back.
    ///
    /// An ordinary question inside a bound project stays trivial.
    /// </summary>
    [Fact]
    public void AnOrdinaryQuestion_StaysTrivial_EvenInsideABoundProject()
    {
        using var _ = KnowledgeScopeContext.Enter(Scope());

        Assert.Equal(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve("how do you make tacos?").MissionClass);
    }

    /// <summary>
    /// THE GOAL AND THE CLASSIFIER ARE ONE RULE. The seeder could be reworded tomorrow into
    /// something the intake no longer recognises, and every test above would still pass while
    /// production went back to answering from nothing — the goal is only ever read through
    /// `GoalForName`, so this asserts the phrase they actually share.
    /// </summary>
    [Fact]
    public void TheSeederNamesTheKnowledgeBase_WhichIsWhatTheClassifierReads()
    {
        Assert.Contains("knowledge base", SeededGoal(), StringComparison.OrdinalIgnoreCase);
    }
}
