using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.Core.Planning;
using Anthill.SDK.Artifacts;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// THE ANSWER THE OPERATOR READS, AND THE GRADE IT GETS. v0.3.8.147.
///
/// ALL THREE OF THESE CAME FROM ONE SESSION OF REAL CHAT TRAFFIC on the operator's colony, after
/// `.146` fixed the planning that was sending questions to the coder. The class worked — three chat
/// questions, three `class_needs_no_plan` plans, two tasks each, no coder anywhere. What was left
/// was everything AROUND the answer.
/// </summary>
public class AnswerPresentationTests : IDisposable
{
    private readonly bool _objVerify;

    public AnswerPresentationTests()
    {
        AnthillRuntime.Initialize();
        _objVerify = AnthillRuntime.EnableObjectiveVerification;
    }

    public void Dispose() => AnthillRuntime.EnableObjectiveVerification = _objVerify;

    private static DomainTask Builder() => new()
    {
        Id = "t_build", Title = "Answer", AssignedAnt = "builder", TaskType = "build_answer",
        Status = TaskStatus.Complete, Result = "The 1980s were a transformative decade.",
    };

    private static DomainTask Verifier(string prose) => new()
    {
        Id = "t_verify", Title = "Verify", AssignedAnt = "verifier", TaskType = "verification",
        Status = TaskStatus.Complete, Result = prose,
    };

    private static Mission MissionWith(params DomainTask[] tasks)
    {
        var m = new Mission { Goal = "give me a summary on history of the 1980s", Status = MissionStatus.Complete };
        m.Tasks.AddRange(tasks);
        return m;
    }

    private static MissionEvaluation Evaluate(Mission mission) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: false,
            evidence: Array.Empty<Evidence>(), specification: null,
            consumptions: Array.Empty<ArtifactConsumption>(), artifacts: Array.Empty<Artifact>());

    // ---- 1. "needs improvement" is a note, not a refusal ------------------------------------------

    /// <summary>
    /// THE ONE THE OPERATOR FELT. Three good chat answers in one session — the 1980s, what science
    /// is, how people become millionaires — and all three graded `partial`, because all three
    /// verifiers returned `Needs Improvement` and `.140` counted that as something saying no.
    ///
    /// IT IS A CONSTANT, NOT A SIGNAL. The verifier prompt offers three options and a model asked to
    /// critique a short answer reaches for the middle one essentially always. The verdict on the
    /// 1980s briefing read "provides a partial summary of the 1980s, covering key politi[cal]…" —
    /// an editorial note on an answer it accepted.
    ///
    /// STILL NOT A PASS. `IsSatisfied` requires an unambiguous `Passed`, so this is `inconclusive`:
    /// unverified, and not accused of having failed. What it no longer does is refuse closure.
    /// </summary>
    [Fact]
    public void NeedsImprovement_DoesNotRefuseClosure()
    {
        var evaluation = Evaluate(MissionWith(Builder(),
            Verifier("- Verdict: Needs Improvement\n- Reasoning: provides a partial summary.")));

        Assert.Equal(MissionEvaluation.Verification.Inconclusive, evaluation.VerificationStatus);
        Assert.Equal(MissionStatus.Complete.Value(), evaluation.StructuralStatus);
        Assert.Equal(MissionOutcome.CompletedUnverified, evaluation.OutcomeCode);
        Assert.DoesNotContain("Closure refused", evaluation.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A REAL "NO" STILL REFUSES IT. `.140`'s subject is intact: the distinction being drawn is
    /// between a verifier that judged the work UNACCEPTABLE and one that judged it acceptable and
    /// improvable, not between refusing and never refusing.
    /// </summary>
    [Fact]
    public void VerificationFailed_StillRefusesClosure()
    {
        var evaluation = Evaluate(MissionWith(Builder(),
            Verifier("- Verdict: Verification Failed\n- Reasoning: the answer was not provided.")));

        Assert.Equal(MissionEvaluation.Verification.Failed, evaluation.VerificationStatus);
        Assert.Equal(MissionStatus.Partial.Value(), evaluation.StructuralStatus);
        Assert.Contains("Closure refused", evaluation.Explanation, StringComparison.Ordinal);
    }

    // ---- 2. the answer stops talking to itself ----------------------------------------------------

    /// <summary>
    /// `[d1]`, WHICH THE OPERATOR ASKED ABOUT — AND THAT WAS THE ARGUMENT. Every section was
    /// prefixed with its internal deliverable id and the operator's own question quoted back, on an
    /// answer that has exactly one part. They asked the question a moment earlier, there is nothing
    /// for it to be distinguished from, and `d1` is a handle for the ledger.
    /// </summary>
    [Fact]
    public void ASingleSectionAnswer_HasNoHeadingAndNoDeliverableId()
    {
        var specification = MissionIntake.Resolve("give me a summary on history of the 1980s");
        var assembled = AssembledAnswer.Build(specification, new List<DomainTask> { Builder() }, "fallback");

        var rendered = assembled.Render();

        Assert.DoesNotContain("[d1]", rendered, StringComparison.Ordinal);
        Assert.Equal("The 1980s were a transformative decade.", rendered.Trim());
    }

    /// <summary>
    /// AND WITH MORE THAN ONE IT STILL SAYS WHICH IS WHICH — the heading earns its place exactly
    /// where it does work. An answer to "what is good about it? what is bad?" must name its halves,
    /// and `AnswerCoverage` grades those two requests separately. The REQUEST does that work; the
    /// internal id never did, so it is gone in both cases.
    /// </summary>
    [Fact]
    public void AMultiSectionAnswer_KeepsItsHeadings_ButNotTheIds()
    {
        var specification = MissionIntake.Resolve(
            "What is good about the retry policy? What is bad about it?");
        Assert.True(specification.Deliverables.Count >= 2, "fixture needs two deliverables");

        var rendered = AssembledAnswer
            .Build(specification, new List<DomainTask> { Builder() }, "fallback")
            .Render();

        Assert.DoesNotContain("[d1]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("[d2]", rendered, StringComparison.Ordinal);
        Assert.Contains("good about", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 3. something has to answer ---------------------------------------------------------------

    /// <summary>
    /// A PLAN WITH NOTHING TO ANSWER WITH IS A MISSION BUILT TO FAIL, and the operator's colony
    /// produced one: "do a self check of the anthill colony, what are the registered…" planned
    /// `researcher + verifier`. The researcher investigated and wrote a brief; nobody compiled an
    /// answer; the verifier read what was there and returned "Verification Failed: the builder did
    /// not provide the specific answer requested (a list of registered ant roles)".
    ///
    /// The verifier was right, and the END of a mission is the most expensive possible place to
    /// discover that nothing was going to answer.
    ///
    /// THE FIRST CUT PUT THIS BESIDE THE GUARANTEED VERIFIER IN `EnforceConstraints` AND WAS WRONG.
    /// That method returns at its first line unless `BlocksPatches` — its whole body is scoped to
    /// no-patch missions — so the guarantee would have covered the one lane that already plans
    /// carefully and missed every ordinary mission. This test failed on the first run and is the
    /// reason it lives in `EnsureClassCoverage`, the funnel every planning path goes through.
    /// </summary>
    [Fact]
    public void APlanWithNoBuilder_GetsAStepThatAnswers()
    {
        var planned = new List<DomainTask>
        {
            new() { Title = "Investigate", AssignedAnt = "researcher", TaskType = "research", Description = "r" },
            new() { Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", Description = "v" },
        };

        var tasks = Planner.EnsureClassCoverage(planned, "do a self check of the colony", specification: null);

        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AND ONLY WHAT IS MISSING IS ADDED — the standing rule of every guarantee in that method. A
    /// plan that already answers gets exactly one builder, not two.
    /// </summary>
    [Fact]
    public void APlanThatAlreadyAnswers_GainsNoSecondBuilder()
    {
        var planned = new List<DomainTask>
        {
            new() { Title = "Answer", AssignedAnt = "builder", TaskType = "build_answer", Description = "b" },
            new() { Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", Description = "v" },
        };

        var tasks = Planner.EnsureClassCoverage(planned, "answer the question", specification: null);

        Assert.Single(tasks, t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase));
    }
}
