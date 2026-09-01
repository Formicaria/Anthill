using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE ANSWER IS BUILT FROM WHAT WAS ASKED. v0.3.8.106, PLAN.md §2b `.106`.
///
/// THE EXIT GATE'S FIRST AND THIRD CLAUSES: "the general last-worker-output fallback is removed:
/// every answer is assembled specification → deliverable → evidence → answer section", and "an
/// omitted requested section fails coverage".
///
/// WHAT WAS WRONG, in `.98`'s own words: "`ResultAssembler` never read it at all and returned the
/// last builder task's output as the answer." The layer producing what the operator READS took no
/// account of what the operator ASKED — it picked a task by role (last completed builder, else
/// coder, else anything) and handed over its text. A mission asked three questions and answering
/// one produced an answer shaped exactly like a mission that answered all three.
///
/// COVERAGE IS CLAIM-AND-SERVED, NEVER A WORD SEARCH, and that is not a preference — `.98` rejected
/// the word search in writing and this release honours it. `MissionDeliverable.Subject` exists,
/// intake populates it, its own doc offers it as "the topic keywords a coverage check can look for
/// in an answer", and it stays unread. `SubjectIsStillUnread` is the guard that keeps it that way.
/// </summary>
public class AnswerCoverageTests
{
    private static Anthill.Core.Domain.Task Task(
        string role, string result, TaskStatus status = TaskStatus.Complete, params string[] deliverables)
    {
        var task = new Anthill.Core.Domain.Task
        {
            Id = Guid.NewGuid().ToString(),
            Title = $"{role} work", Description = $"{role} work",
            AssignedAnt = role, TaskType = role == "builder" ? "build_answer" : "research",
            Status = status, Result = result,
        };
        foreach (var d in deliverables) task.DeliverableIds.Add(d);
        return task;
    }

    /// <summary>An audit request with three questions, so the specification carries three ids.</summary>
    private static MissionSpecification ThreeQuestions() => MissionIntake.Resolve(
        "Audit this repository: what is implemented? What is enabled right now? Which workers actually ran?");

    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ASpecifiedMission_AssemblesOneSectionPerRequest()
    {
        var spec = ThreeQuestions();
        Assert.True(spec.Deliverables.Count >= 3);

        var tasks = spec.Deliverables
            .Select(d => Task("researcher", $"answer for {d.Id}", TaskStatus.Complete, d.Id))
            .ToList();

        var assembled = AssembledAnswer.Build(spec, tasks, "the raw best-task output");

        Assert.True(assembled.Specified);
        Assert.Equal(spec.Deliverables.Count, assembled.Sections.Count);
        Assert.All(assembled.Sections, s => Assert.True(s.Answered));
        Assert.True(assembled.Covered);

        // Each request is named in the answer, and its content is the SERVING task's — not the raw
        // fallback, which a specified mission must never reach.
        var rendered = assembled.Render();
        foreach (var d in spec.Deliverables)
        {
            Assert.Contains(d.Id, rendered, StringComparison.Ordinal);
            Assert.Contains($"answer for {d.Id}", rendered, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("the raw best-task output", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN OMITTED REQUEST FAILS COVERAGE — the exit gate's third clause. The mission answered two
    /// of three questions, which is the exact shape `.98` recorded as ungradeable.
    /// </summary>
    [Fact]
    public void AnOmittedRequest_FailsCoverage()
    {
        var spec = ThreeQuestions();
        var tasks = spec.Deliverables.Take(spec.Deliverables.Count - 1)
            .Select(d => Task("researcher", $"answer for {d.Id}", TaskStatus.Complete, d.Id))
            .ToList();

        var assembled = AssembledAnswer.Build(spec, tasks, "raw");
        var coverage = AnswerCoverage.Evaluate(assembled);

        Assert.False(coverage.Satisfied);
        Assert.False(assembled.Covered);

        var omitted = spec.Deliverables[^1];
        Assert.Contains(omitted.Id, coverage.Explanation, StringComparison.Ordinal);
        // And the ANSWER says so too — a gap visible only in a gate's explanation is one the
        // operator reading the answer never sees.
        Assert.Contains("NOT ANSWERED", assembled.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A TASK THAT OWNED A REQUEST AND FAILED IS NOT AN ANSWER. Its result is a failure message,
    /// and pasting that under the request's heading would read as a reply to the question.
    /// </summary>
    [Fact]
    public void AFailedOwner_DoesNotAnswerItsRequest()
    {
        var spec = ThreeQuestions();
        var tasks = spec.Deliverables
            .Select((d, i) => Task("researcher", i == 0 ? "the tool exploded" : $"answer for {d.Id}",
                i == 0 ? TaskStatus.Failed : TaskStatus.Complete, d.Id))
            .ToList();

        var assembled = AssembledAnswer.Build(spec, tasks, "raw");

        Assert.False(assembled.Covered);
        Assert.DoesNotContain("the tool exploded", assembled.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A COMPLETED OWNER THAT PRODUCED NOTHING IS ITS OWN STATE. "The step ran and said nothing"
    /// and "no step owned this" send an operator to different places.
    /// </summary>
    [Fact]
    public void AnEmptyOwner_IsDistinctFromAnUnownedRequest()
    {
        var spec = ThreeQuestions();
        var tasks = spec.Deliverables
            .Select(d => Task("researcher", "", TaskStatus.Complete, d.Id))
            .ToList();

        var assembled = AssembledAnswer.Build(spec, tasks, "raw");

        Assert.All(assembled.Sections, s => Assert.Equal(AnswerSectionState.Empty, s.State));
        Assert.All(AssembledAnswer.Build(spec, new List<Anthill.Core.Domain.Task>(), "raw").Sections,
            s => Assert.Equal(AnswerSectionState.Unanswered, s.State));
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE CODING LANE IS UNTOUCHED, and this is the assertion that keeps the release shippable.
    ///
    /// A general specification declares no deliverables, so it has one section, no headings, and
    /// content byte-for-byte identical to what the assembler produced before this type existed.
    /// That is what lets the assembler have ONE path rather than a path and an escape hatch —
    /// and the coding lane is the only lane qualified live, so a change that reshaped its answer
    /// would be the most expensive possible place to be clever.
    /// </summary>
    [Fact]
    public void AnUnspecifiedMission_RendersItsRawAnswerUnchanged()
    {
        var general = MissionSpecification.General("fix the failing build");
        const string raw = "Patched Foo.cs and the build is green.";

        var assembled = AssembledAnswer.Build(general, new List<Anthill.Core.Domain.Task>(), raw);

        Assert.False(assembled.Specified);
        Assert.Equal(raw, assembled.Render());
        // And the gate stays silent: there was nothing it could have omitted.
        Assert.False(AnswerCoverage.Applies(assembled));
    }

    /// <summary>A null specification behaves as an unspecified one — every mission before `.98`.</summary>
    [Fact]
    public void ANullSpecification_IsTreatedAsUnspecified()
    {
        var assembled = AssembledAnswer.Build(null, new List<Anthill.Core.Domain.Task>(), "raw");
        Assert.False(assembled.Specified);
        Assert.Equal("raw", assembled.Render());
    }

    /// <summary>
    /// COVERAGE FAILS CLOSED ON A NULL ANSWER, and the asymmetry with `CitationIntegrity` is the
    /// point. That layer catches a claim the record CONTRADICTS, so an unreadable store contradicts
    /// nothing. This layer asks whether something is ABSENT — and absence is the whole question.
    /// </summary>
    [Fact]
    public void CoverageFailsClosed_WhenTheAnswerCouldNotBeAssembled()
    {
        var result = AnswerCoverage.Evaluate(null);
        Assert.False(result.Satisfied);
        Assert.NotEmpty(result.Missing);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE GRADE DEMOTES ON AN UNANSWERED REQUEST, through the canonical evaluator and under
    /// ordinary defaults.
    /// </summary>
    [Fact]
    public void AnUncoveredMission_IsNotVerified()
    {
        var spec = ThreeQuestions();
        var mission = new Mission { Goal = spec.OriginalRequest, Status = MissionStatus.Complete };
        foreach (var d in spec.Deliverables.Take(1))
            mission.Tasks.Add(Task("researcher", $"answer for {d.Id}", TaskStatus.Complete, d.Id));
        mission.Tasks.Add(Task("verifier", "verified", TaskStatus.Complete));

        var evaluation = MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: true, specification: spec);

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
        Assert.False(evaluation.IsPositive);
        Assert.Contains("answer coverage", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND A MISSION THAT DECLARED NOTHING GRADES EXACTLY AS BEFORE. Without this the demotion
    /// above proves nothing — a gate that demoted everything would satisfy it, and would take the
    /// coding lane with it.
    /// </summary>
    [Fact]
    public void AnUnspecifiedMission_IsGradedAsBefore()
    {
        var mission = new Mission { Goal = "fix the failing build", Status = MissionStatus.Complete };
        mission.Tasks.Add(Task("coder", "patched Foo.cs"));
        mission.Tasks.Add(Task("verifier", "verified"));

        var withSpec = MissionEvaluator.Evaluate(mission, null, 0, MissionConstraints.None, true,
            specification: MissionSpecification.General("fix the failing build"));
        var withNone = MissionEvaluator.Evaluate(mission, null, 0, MissionConstraints.None, true);

        Assert.Equal(withNone.DeliverableStatus, withSpec.DeliverableStatus);
        Assert.DoesNotContain("answer coverage", withSpec.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// `MissionDeliverable.Subject` STAYS UNREAD, and this guard is the release's memory of why.
    ///
    /// `.98` considered keying coverage on it and rejected that in writing: "does the answer
    /// contain this question's words — grades on vocabulary: an answer reading 'Strengths: …
    /// Weaknesses: …' addresses 'what is good and bad about it' completely and contains neither
    /// word, and a gate that demoted it would make every real gate less trustworthy."
    ///
    /// The field is still populated at intake and its own doc comment offers it for exactly the
    /// check `.98` forbade — which makes wiring it the most natural-looking mistake available to
    /// the next person who reads this area. Source-shape, because the mistake is invisible to every
    /// behavioural assertion above: a coverage gate that ALSO consulted subject words would pass
    /// all of them.
    /// </summary>
    [Fact]
    public void SubjectIsStillUnread_ByEveryCoverageLayer()
    {
        foreach (var file in new[] { "AssembledAnswer.cs", "AnswerCoverage.cs" })
        {
            var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
                SourceText.RepoRoot(), "src", "Anthill.Core", "Outcomes", file)));

            Assert.DoesNotContain(".Subject", source, StringComparison.Ordinal);
        }
    }
}
