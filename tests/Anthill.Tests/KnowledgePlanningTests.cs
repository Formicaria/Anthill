using System;
using System.Collections.Generic;
using System.Linq;
using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Planning;
using Anthill.SDK.Knowledge;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A BOUND KNOWLEDGE BASE IS READ BY A STEP, OR IT IS NOT READ AT ALL. v0.3.8.156.
///
/// The defect these hold closed is the one v0.3.8.136 left behind. That release entered the mission's
/// knowledge scope so that a knowledge tool an ant dispatched would resolve rather than refuse —
/// and nothing in the colony ever planned a step that dispatched one. A project mapped to a FORAGER
/// project still planned `builder -> verifier`, the builder answered from the model's weights, and
/// the knowledge base was read by nobody while every layer reported success.
///
/// Asserted through a planner with NO router, because the guarantee must not depend on what a model
/// proposed: the point of putting it in `EnsureClassCoverage` is that every one of the five return
/// paths funnels through it.
/// </summary>
public class KnowledgePlanningTests
{
    private const string Goal = "summarize our deployment procedure and what changed in it";
    private static readonly KnowledgeScope Bound = KnowledgeScope.ForMission("forager-proj-1", "mission-1", "proj-1");

    private static Planner Offline() => new(useOllama: false, router: null);

    private static MissionSpecification ClassOf(string missionClass, string request = Goal) => new()
    {
        OriginalRequest = request,
        MissionClass = missionClass,
        Targets = MissionTargets.None,
        Authority = MissionAuthority.Observe,
        RequiredEvidence = Array.Empty<string>(),
    };

    [Fact]
    public void ABoundKnowledgeBase_GuaranteesAStepThatReadsIt()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);

        var tasks = Offline().CreateTasks(Goal, MissionConstraints.None, specification: ClassOf(MissionSpecification.GeneralClass));

        var step = Assert.Single(tasks, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal("researcher", step.AssignedAnt);
        // NAMED IN THE DESCRIPTION, because that is what reaches the model that decides which tool
        // to call — and it is also what the "already present" check reads.
        Assert.Contains(KnowledgeToolNames.Retrieve, step.Description, StringComparison.Ordinal);
        Assert.False(step.Critical);
    }

    /// <summary>
    /// THE TYPE IS RECONCILED AGAINST THE RESEARCHER'S OWN CONTRACT. v0.3.8.135 paid for the
    /// alternative: a step whose type no contract declared was refused at dispatch every time it
    /// fired, so the guarantee produced a mission built to fail rather than a mission that reads.
    /// </summary>
    [Fact]
    public void TheGuaranteedStep_IsATypeTheResearcherDeclares()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);

        var step = Assert.Single(
            Offline().CreateTasks(Goal, MissionConstraints.None, specification: ClassOf(MissionSpecification.GeneralClass)),
            t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle, StringComparison.OrdinalIgnoreCase));

        var contract = Anthill.Core.Agents.AntExecutionCatalog.Contracts["researcher"];
        Assert.Contains(step.TaskType, contract.SupportedTaskTypes);
    }

    /// <summary>Recorded, not merely done — a step nobody proposed is a step an operator must be
    /// able to see the reason for. The same channel `.123`'s inspection guarantee reports through.</summary>
    [Fact]
    public void TheGuarantee_IsRecordedAsASubstitution()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);
        var reasons = new List<string>();

        Offline().CreateTasks(Goal, MissionConstraints.None, specification: ClassOf(MissionSpecification.GeneralClass),
            onSubstituted: (reason, _) => reasons.Add(reason));

        Assert.Contains(PlanSubstitutions.KnowledgeBaseBound, reasons);
    }

    /// <summary>
    /// NO SCOPE, NO STEP — and this is the tenant boundary, not a preference. An unmapped project, a
    /// projectless run and a colony with knowledge disabled all resolve to the same refusal in
    /// `Queen.ResolveKnowledgeScope`, and a planner that read the project map for itself would be a
    /// second answer to which knowledge a mission may read.
    /// </summary>
    [Fact]
    public void NoBoundKnowledgeBase_PlansNoKnowledgeStep()
    {
        var tasks = Offline().CreateTasks(Goal, MissionConstraints.None, specification: ClassOf(MissionSpecification.GeneralClass));

        Assert.DoesNotContain(tasks, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnresolvedScope_PlansNoKnowledgeStep()
    {
        using var _ = KnowledgeScopeContext.Enter(KnowledgeScope.Unresolved);

        var tasks = Offline().CreateTasks(Goal, MissionConstraints.None, specification: ClassOf(MissionSpecification.GeneralClass));

        Assert.DoesNotContain(tasks, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AND THE ONE CLASS THAT IS EXCLUDED STAYS EXCLUDED. `simple_answer` is admitted on the promise
    /// that the answer rests on nothing retrieved; v0.3.8.145 reduces its plan to the two steps its
    /// specification names. A bound knowledge base does not reopen that — a question that needs the
    /// organization's documents is a classification the intake owes, not a step this layer may add
    /// on top of a class promising the opposite.
    /// </summary>
    [Fact]
    public void ASimpleAnswerMission_KeepsItsPromise_EvenWithAKnowledgeBaseBound()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);

        var tasks = Offline().CreateTasks("how do you make tacos?", MissionConstraints.None,
            specification: ClassOf(MissionSpecification.SimpleAnswerClass, "how do you make tacos?"));

        Assert.DoesNotContain(tasks, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
        Assert.All(tasks, t => Assert.True(Planner.ConsumesEvidence(t), $"'{t.TaskType}' is not an answer step"));
    }

    /// <summary>
    /// ONLY WHAT IS MISSING. A plan that already reads the knowledge base reads it for the same
    /// reason, whoever wrote the step — the standing rule every guarantee in `EnsureClassCoverage`
    /// follows, and the one that keeps a re-covered plan from carrying two spellings of one step.
    /// </summary>
    [Fact]
    public void APlanThatAlreadyReadsKnowledge_GetsNoSecondStep()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);

        var existing = new List<Anthill.Core.Domain.Task>
        {
            new()
            {
                Title = "Look it up",
                Description = $"Call {KnowledgeToolNames.Retrieve} for the deployment procedure",
                AssignedAnt = "researcher",
                TaskType = "research",
            },
        };

        var covered = Planner.EnsureClassCoverage(existing, Goal, ClassOf(MissionSpecification.GeneralClass));

        Assert.DoesNotContain(covered, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ORDER, NOT MERELY PRESENCE — a knowledge step that runs after the synthesis is evidence
    /// nothing consumed. A step that declares no dependencies is deliberately left to
    /// `PlanningService.AutoWireDependencies`; one that declares some gets ours added rather than
    /// being opted out of the wiring that carries the plan's other edges.
    /// </summary>
    [Fact]
    public void ASynthesisWithEdges_DependsOnTheKnowledgeStep()
    {
        using var _ = KnowledgeScopeContext.Enter(Bound);

        var upstream = new Anthill.Core.Domain.Task
        {
            Title = "Inspect", Description = "read", AssignedAnt = "file", TaskType = "file_inspection",
        };
        var builder = new Anthill.Core.Domain.Task
        {
            Title = "Answer", Description = "compile", AssignedAnt = "builder", TaskType = "build_answer",
            DependsOn = new List<string> { upstream.Id },
        };

        var covered = Planner.EnsureClassCoverage(new List<Anthill.Core.Domain.Task> { upstream, builder },
            Goal, ClassOf(MissionSpecification.GeneralClass));

        var step = Assert.Single(covered, t => string.Equals(t.Title, Planner.KnowledgeConsultationTitle,
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(step.Id, builder.DependsOn);
    }
}
