using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.Core.Planning;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE ANSWER CLASS, END TO END. v0.3.8.134.
///
/// WHAT WAS ACTUALLY WRONG, and it is worth stating in the terms the operator saw it in: "how do you
/// make tacos" came back as a proposed source patch. Not a bad answer — a PATCH, offered for
/// approval, against a repository the question never mentioned.
///
/// AND NOTHING IN THE RUNTIME WAS ABLE TO SAY THAT WAS WRONG. The cause was not the planner, though
/// the planner is where it became visible: its standing rule says a goal that creates, adds, writes
/// or edits any file must include a `patch_proposal` coder task, and a model reading that rule
/// against a recipe found a verb it liked. What let the result through is that the mission had no
/// class. `general` declares no deliverable, no evidence and no authority ceiling, so
/// `MissionAuthorityGate` had nothing to enforce and `MissionEvaluator` had no promise to contradict.
/// The mission was not ungrated by accident; it was ungoverned by construction.
///
/// SO THE FIX IS A CLASS, NOT A BETTER PROMPT. A prompt asks a model to behave; a class gives every
/// layer underneath something to refuse. These tests assert the refusals, in the order they now
/// stand: the class exists and carries `Observe`; the ceiling refuses the change tools; the planner
/// stops building the change lane; and the gate refuses the record if one appears anyway.
/// </summary>
public class SimpleAnswerMissionTests
{
    private const string Request = "How do you make tacos?";

    private static Mission Answered(MissionSpecification specification, params Task[] tasks) => new()
    {
        Id = "m_answer",
        Goal = specification.OriginalRequest,
        Status = MissionStatus.Complete,
        UserResult = "You start with the tortillas.",
        Tasks = tasks.Length > 0
            ? tasks.ToList()
            : new List<Task>
            {
                new()
                {
                    Id = "t_build", AssignedAnt = "builder", TaskType = "build_answer",
                    Status = TaskStatus.Complete, Result = "You start with the tortillas.",
                },
            },
    };

    private static MissionEvaluation Evaluate(Mission mission, MissionSpecification specification,
        IReadOnlyList<Artifact>? artifacts) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: false,
            evidence: Array.Empty<Evidence>(), specification: specification,
            consumptions: Array.Empty<ArtifactConsumption>(), artifacts: artifacts);

    // ---- classification ------------------------------------------------------------------------

    /// <summary>
    /// THE CLASS EXISTS, and it carries `Observe` — the same ceiling as the audit class, for the
    /// opposite reason. There, Observe says the assessment must not repair what it finds. Here it
    /// says there is nothing to repair: the answer changes nothing because the question asked for
    /// nothing to be changed.
    /// </summary>
    [Fact]
    public void ASimpleQuestion_ClassifiesAsSimpleAnswer_UnderObserveAuthority()
    {
        var specification = MissionIntake.Resolve(Request);

        Assert.Equal(MissionSpecification.SimpleAnswerClass, specification.MissionClass);
        Assert.Equal(MissionIntent.Explain, specification.Intent);
        Assert.Equal(MissionTargets.None, specification.Targets);
        Assert.Equal(MissionAuthority.Observe, specification.Authority);
        Assert.NotEmpty(specification.Deliverables);
        Assert.Contains(WorkerCapabilities.CompileResult, specification.RequiredCapabilities);
    }

    /// <summary>
    /// AND IT REQUIRES NO EVIDENCE — the only recognized class that requires none, which is a
    /// property worth pinning rather than leaving as an empty array somebody later "fixes". The
    /// class's promise is that the answer rests on nothing retrieved and nothing inspected;
    /// requiring an evidence kind would demand a receipt for a thing that did not happen.
    /// </summary>
    [Fact]
    public void TheAnswerClass_RequiresNoEvidence() =>
        Assert.Empty(MissionIntake.Resolve(Request).RequiredEvidence);

    /// <summary>`.104`'s rule, which a new class joins rather than is exempt from.</summary>
    [Fact]
    public void TheAnswerClass_IsRecognized()
    {
        Assert.Contains(MissionSpecification.SimpleAnswerClass, MissionContracts.RecognizedClasses);
        Assert.True(MissionContracts.ForPreview(Request).VerificationRequired);
    }

    // ---- the boundaries ------------------------------------------------------------------------

    /// <summary>
    /// THE BOUNDARY IS TARGETS, NOT DIFFICULTY — and this is the assertion that keeps the class
    /// honest. A question naming something the colony can reach has something to inspect, and
    /// answering it from memory would be the assertion `.98` exists to refuse. Those requests keep
    /// whatever class they had; none of them arrives here.
    /// </summary>
    [Theory]
    [InlineData("What does this repository do?")]
    [InlineData("Refactor the retry helper in this codebase.")]
    [InlineData("Restart the media-server container on pve1.")]
    public void ARequestNamingATarget_IsNotASimpleAnswer(string request) =>
        Assert.NotEqual(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// AND AN IMPERATIVE IS NOT A QUESTION — the condition the first cut of this release left out,
    /// and the suite is what found it.
    ///
    /// `Explain` is the FALL-THROUGH intent: it says only that no change, diagnostic, research or
    /// assessment verb claimed the request. That is a statement about what the request is NOT, and
    /// plenty of imperatives land there. Both of these entered the class on the first cut — a
    /// creation request and a colony instruction, admitted to a class whose gate forbids changing
    /// anything, graded against a promise neither of them made.
    /// </summary>
    [Theory]
    [InlineData("Document the deployment procedure in a runbook.")]
    [InlineData("Exercise the coder and stop it while it works.")]
    [InlineData("Draft a note for the team.")]
    // v0.3.8.146 — THE BOUNDARY THE WIDENED SHAPE HAD TO KEEP. This names no target either, so a
    // bare "give me" opener would have pulled a genuine creation request into the class that
    // refuses to create. The object has to be an EXPLANATION, not an artifact.
    [InlineData("give me a python script that parses CSV")]
    public void AnImperativeWithNoTarget_IsNotASimpleAnswer(string request) =>
        Assert.NotEqual(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// A QUESTION REACHES IT BY EITHER ROUTE — a question mark anywhere, or an interrogative opener.
    /// The opener must be FIRST: "the report on what can be done" contains `what` and asks nothing,
    /// which is the same word-position discipline `RoutingWords` exists for, applied to a sentence.
    /// </summary>
    [Theory]
    [InlineData("How do you make tacos?")]
    [InlineData("Explain the difference between a mutex and a semaphore")]
    [InlineData("What is a good ratio of yeast to flour")]
    public void AQuestionWithNoTarget_ReachesTheClass(string request) =>
        Assert.Equal(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// AND AN ASK THAT IS NOT SHAPED LIKE A QUESTION IS STILL A QUESTION. v0.3.8.146, from the
    /// operator's live colony.
    ///
    /// "give me a briefing of 1990s historical events in the US" resolved `general`: Explain intent,
    /// no target, no interrogative opener. `general` is UNGOVERNED, so it reached the planner model
    /// — and whether it worked came down to whether that model's JSON happened to parse. The SAME
    /// message, sent twice a minute apart, produced a clean two-task answer once and a fourteen-task
    /// plan with a coder, patch proposals, testers and soldiers the other time. Same text, same
    /// intake, opposite outcomes: the coin flip the operator reported as "sometimes it works".
    ///
    /// A class removes the coin flip, because `.145` answers a `simple_answer` without calling the
    /// planner at all.
    /// </summary>
    [Theory]
    [InlineData("give me a briefing of 1990s historical events in the US")]
    [InlineData("summarize the french revolution")]
    [InlineData("walk me through how a jet engine works")]
    [InlineData("brief me on the cuban missile crisis")]
    [InlineData("help me understand compound interest")]
    public void AnAnswerRequestWithNoTarget_ReachesTheClass(string request) =>
        Assert.Equal(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// v0.3.8.145 — A GREETING REACHES IT TOO. Found by driving the console as a first-time user:
    /// "hello" is not question-shaped, resolved `general`, and stopped the colony to ask permission
    /// to start a mission. A salutation or an acknowledgement is answered from what is already
    /// known and changes nothing — this class's exact promise.
    /// </summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("Hi there!")]
    [InlineData("hey")]
    [InlineData("Good morning")]
    [InlineData("thanks!")]
    [InlineData("Thank you very much.")]
    [InlineData("ok")]
    public void AGreeting_ReachesTheClass(string request) =>
        Assert.Equal(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// AND ONLY A WHOLE-MESSAGE GREETING. A salutation is a weak signal, so a message that merely
    /// OPENS with one is not admitted on its strength — "hello, document the deployment procedure in
    /// a runbook" is a creation request wearing a greeting, and admitting it would put it in a class
    /// whose ceiling forbids writing the runbook: the `.134` regression in a new coat.
    /// </summary>
    [Theory]
    [InlineData("hello, document the deployment procedure in a runbook")]
    [InlineData("hi — draft a note for the team")]
    [InlineData("thanks, now exercise the coder and stop it while it works")]
    [InlineData("ok go")]
    public void AGreetingThatCarriesWork_IsNotASimpleAnswer(string request) =>
        Assert.NotEqual(MissionSpecification.SimpleAnswerClass,
            MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// AND EVERY OTHER CLASS IS UNTOUCHED. The branch sits last, below every class that claims a
    /// request by something the colony can do about it, so it can only take what nothing else
    /// wanted. Asserted rather than argued from the source order, because an ordering that happens
    /// to be safe today is one a later verb can quietly break.
    /// </summary>
    [Theory]
    [InlineData("Assess the current health of the colony and report what is enabled.", MissionSpecification.SystemAuditClass)]
    [InlineData("Why is the test suite failing in this repository right now?", MissionSpecification.TroubleshootingClass)]
    [InlineData("Restart the media-server container on pve1.", MissionSpecification.SystemActionClass)]
    [InlineData("Post the release summary to the team's incident webhook.", MissionSpecification.ExternalActionClass)]
    public void EveryOtherClass_StillClaimsItsOwnRequests(string request, string expected) =>
        Assert.Equal(expected, MissionIntake.Resolve(request).MissionClass);

    /// <summary>
    /// AND `general` DOES NOT GO AWAY. A request with an intent no class serves and a target it
    /// could have inspected still lands there, ungraded, exactly as before — which is what makes
    /// this release a narrowing of `general` rather than its removal.
    /// </summary>
    [Fact]
    public void ATargetedRequestNoClassServes_IsStillGeneral() =>
        Assert.Equal(MissionSpecification.GeneralClass,
            MissionIntake.Resolve("Rewrite the retry helper in this repository.").MissionClass);

    // ---- the ceiling ---------------------------------------------------------------------------

    /// <summary>
    /// THE REFUSAL THAT ACTUALLY STOPS THE TACO PATCH, and it is a ceiling rather than a gate: the
    /// gate below grades a finished mission, this refuses the call. `Observe` cannot reach the three
    /// tools that change the operator's tree, at dispatch, before a model's opinion about what the
    /// question needs can matter.
    /// </summary>
    [Theory]
    [InlineData("apply_patch")]
    [InlineData("write_text_file")]
    [InlineData("shell_command")]
    public void TheAnswerCeiling_RefusesEveryChangeTool(string action) =>
        Assert.False(MissionAuthorityGate.Evaluate(
            MissionIntake.Resolve(Request).Authority, action).Allowed);

    // ---- the plan ------------------------------------------------------------------------------

    /// <summary>
    /// AND THE PLAN STOPS BUILDING THE LANE AT ALL. Nothing here is load-bearing for safety — the
    /// ceiling above is — but a mission that proposes a patch card the operator must decline and
    /// then grades itself not satisfied is a correct outcome nobody wanted to see. The coverage
    /// pass drops the change-typed steps and leaves an answer to compile.
    /// </summary>
    [Fact]
    public void TheCoveragePass_DropsTheChangeLane()
    {
        var specification = MissionIntake.Resolve(Request);
        var planned = new List<Task>
        {
            new() { Title = "Create structured patch proposal", AssignedAnt = "coder", TaskType = "patch_proposal" },
        };

        var tasks = Planner.EnsureClassCoverage(planned, Request, specification);

        Assert.DoesNotContain(tasks, t => AnswerIntegrity.ChangeTaskTypes.Contains(t.TaskType));
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AND IT DROPS THE GATHERING LANE TOO. v0.3.8.145, found live on a local model: this exact
    /// question was classified `simple_answer` — correctly — and then planned as seven tasks,
    /// including a `research` step and a workspace `file_inspection`. The research step hit its
    /// 240-second cap, the builder and verifier were skipped because their dependency could not
    /// complete, and the mission died on the 600-second budget with NOT ANSWERED. Ten minutes, for
    /// a recipe question, ending in a failure.
    ///
    /// Dropping only the CHANGE steps was never enough: this class's specification requires no
    /// evidence at all, because its promise is that the answer rests on nothing retrieved and
    /// nothing inspected. A plan that gathers is not serving the class, it is contradicting it —
    /// so the plan is reduced to what `SimpleAnswerCapabilities` actually names: compile the
    /// answer, and check it answered what was asked.
    /// </summary>
    [Fact]
    public void TheCoveragePass_DropsTheGatheringLane_AndLeavesAnAnswerThatCanRun()
    {
        var specification = MissionIntake.Resolve(Request);
        var research = new Task { Id = "t_research", Title = "Research taco recipes", AssignedAnt = "researcher", TaskType = "research" };
        var inspect = new Task { Id = "t_inspect", Title = "Inspect the workspace", AssignedAnt = "file", TaskType = "file_inspection" };
        var answer = new Task
        {
            Id = "t_answer", Title = "Write the answer", AssignedAnt = "builder", TaskType = "build_answer",
            // The edges the live plan had: the answer waited on the step that timed out.
            DependsOn = new List<string> { "t_research", "t_inspect" },
            ParentTaskIds = new List<string> { "t_research" },
        };

        var tasks = Planner.EnsureClassCoverage(new List<Task> { research, inspect, answer }, Request, specification);

        Assert.DoesNotContain(tasks, t => string.Equals(t.TaskType, "research", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tasks, t => string.Equals(t.TaskType, "file_inspection", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase));

        // AND EVERY SURVIVOR CAN ACTUALLY RUN. A kept step still pointing at a dropped one is
        // "skipped because dependencies cannot complete" — the very failure this reduction exists
        // to prevent, reproduced by the fix for it.
        var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            Assert.All(task.DependsOn, d => Assert.Contains(d, ids));
            Assert.All(task.ParentTaskIds, p => Assert.Contains(p, ids));
        }
    }

    /// <summary>
    /// AND THE PLANNER MODEL IS NEVER CALLED FOR ONE. v0.3.8.145, measured on the operator's own
    /// colony: the taco question sat for two and a half minutes with an empty graph while a local
    /// 35B composed a plan whose every step the reduction above was about to drop. The model cannot
    /// contribute to this class by construction, so the call is not made — and the substitution is
    /// RECORDED, because "the plan has a shape nobody proposed" is what that vocabulary is for.
    ///
    /// Asserted through a planner with NO router, which is the only honest way to prove a model was
    /// not consulted: if this branch were removed, the no-router branch would answer instead and the
    /// recorded reason would be `no_model_router` rather than `class_needs_no_plan`.
    /// </summary>
    [Fact]
    public void AQuestion_IsNotPlanned_ItIsAnswered()
    {
        var specification = MissionIntake.Resolve(Request);
        var planner = new Planner(useOllama: true, router: null);
        var substitutions = new List<string>();

        var tasks = planner.CreateTasks(Request, MissionConstraints.None, specification: specification,
            onSubstituted: (reason, _) => substitutions.Add(reason));

        Assert.Contains(PlanSubstitutions.ClassNeedsNoPlan, substitutions);
        Assert.DoesNotContain(PlanSubstitutions.NoModelRouter, substitutions);
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tasks, t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase));
        Assert.All(tasks, t => Assert.True(Planner.ConsumesEvidence(t), $"'{t.TaskType}' is not an answer step"));
    }

    /// <summary>
    /// A plan that was ALREADY only an answer is left alone — the reduction removes what does not
    /// belong, it does not rebuild what does.
    /// </summary>
    [Fact]
    public void AnAnswerOnlyPlan_SurvivesTheReductionUnchanged()
    {
        var specification = MissionIntake.Resolve(Request);
        var answer = new Task { Id = "t_a", Title = "Answer", AssignedAnt = "builder", TaskType = "build_answer" };
        var verify = new Task { Id = "t_v", Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", DependsOn = new List<string> { "t_a" } };

        var tasks = Planner.EnsureClassCoverage(new List<Task> { answer, verify }, Request, specification);

        Assert.Equal(2, tasks.Count);
        Assert.Equal(new[] { "t_a" }, tasks.Single(t => t.Id == "t_v").DependsOn);
    }

    // ---- the gate ------------------------------------------------------------------------------

    /// <summary>An answered question that changed nothing is what the class promises. It passes.</summary>
    [Fact]
    public void AnAnsweredQuestionThatChangedNothing_IsSatisfied()
    {
        var specification = MissionIntake.Resolve(Request);

        var evaluation = Evaluate(Answered(specification), specification, Array.Empty<Artifact>());

        Assert.Equal(MissionEvaluation.Deliverable.Satisfied, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// THE PLAN'S OWN ACCOUNT. A change-typed step in a mission admitted as needing no change is
    /// the taco defect at its source, and the gate names it whether or not the step produced
    /// anything — the decision to plan it is the thing being contradicted.
    /// </summary>
    [Fact]
    public void AChangeTypedStep_IsRefused()
    {
        var specification = MissionIntake.Resolve(Request);
        var mission = Answered(specification,
            new Task
            {
                Id = "t_patch", AssignedAnt = "coder", TaskType = "patch_proposal",
                Status = TaskStatus.Complete, Result = "{}",
            },
            new Task
            {
                Id = "t_build", AssignedAnt = "builder", TaskType = "build_answer",
                Status = TaskStatus.Complete, Result = "You start with the tortillas.",
            });

        var evaluation = Evaluate(mission, specification, Array.Empty<Artifact>());

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
        Assert.Contains("patch_proposal", evaluation.Explanation);
    }

    /// <summary>
    /// AND THE STORE'S. The second account, and the reason it is not redundant: a plan can be clean
    /// and an artifact still record that a change was proposed. Two accounts of one mission that do
    /// not agree is the finding, not a discrepancy to reconcile quietly.
    /// </summary>
    [Fact]
    public void AChangeArtifact_IsRefused_EvenWhenThePlanIsClean()
    {
        var specification = MissionIntake.Resolve(Request);
        var patch = Artifact.Create(
            schema: ArtifactSchemas.PatchSet, producerRole: "coder",
            missionId: "m_answer", payload: Json.Dumps(new { patches = Array.Empty<object>() }));

        var evaluation = Evaluate(Answered(specification), specification, new[] { patch });

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// AND A MISSION THAT ANSWERED NOTHING IS NOT RESCUED BY HAVING CHANGED NOTHING. The class's
    /// content IS the answer; producing none leaves nothing for it to stand behind.
    /// </summary>
    [Fact]
    public void AnUnansweredQuestion_IsRefused()
    {
        var specification = MissionIntake.Resolve(Request);
        var mission = Answered(specification,
            new Task
            {
                Id = "t_build", AssignedAnt = "builder", TaskType = "build_answer",
                Status = TaskStatus.Failed, Result = "",
            });
        mission.UserResult = "";

        var evaluation = Evaluate(mission, specification, Array.Empty<Artifact>());

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// AND AN UNREADABLE STORE FAILS CLOSED, which reads oddly for a gate that mostly checks for
    /// absence — surely an unreadable store is the same as an empty one — and is precisely why it
    /// must not. "We could not see whether anything was changed" is not "nothing was changed", and
    /// the second is this class's whole promise.
    /// </summary>
    [Fact]
    public void AnUnreadableStore_IsNotAPass()
    {
        var specification = MissionIntake.Resolve(Request);

        var evaluation = Evaluate(Answered(specification), specification, artifacts: null);

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }
}
