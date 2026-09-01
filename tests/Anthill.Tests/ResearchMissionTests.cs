using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.Core.Planning;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE RESEARCH CLASS, END TO END. v0.3.8.109, PLAN.md §2b `.109`.
///
/// WHAT WAS ACTUALLY WRONG, and it is not that research missions failed — it is that they could not
/// fail. `CitationIntegrity` was built at `.99` for a class that did not exist, and it can only
/// catch a citation resolving to nothing. A mission asked to find something out, which retrieved
/// nothing and cited nothing, left an empty store; an empty store contradicts no citation; the gate
/// correctly read it as "nothing to check". So a fluent answer written from a model's own weights
/// and an answer built from real sources were indistinguishable at every layer of this runtime.
///
/// `.104` recorded precisely what was missing and why it could not be faked: "no `research` mission
/// class, no evidence kind meaning 'a source was retrieved', and no worker capability a research
/// mission could require." Any one of the three invented alone would have been a branch nothing
/// reaches. They arrive together here.
/// </summary>
public class ResearchMissionTests
{
    /// <summary>
    /// DELIBERATELY FREE OF `current`, and that is not fussiness about a fixture. `current` is a
    /// RuntimeTargets word — it has been since `.98`, where "what is enabled currently" is the
    /// runtime half of an audit — so "the current best practice" would set the Runtime flag and this
    /// request would name both worlds, which the class correctly refuses. The words that reach a
    /// class are the words the class is about.
    /// </summary>
    private const string Request =
        "Research what the papers and vendors say about local model quantization. "
      + "What do the papers recommend? Which vendors ship it?";

    // ---- fixtures ------------------------------------------------------------------------------

    private static Artifact SourceSet(string missionId, params string[] urls) => Artifact.Create(
        schema: ArtifactSchemas.SourceSet,
        producerRole: "web",
        missionId: missionId,
        payload: Json.Dumps(new
        {
            query = "q",
            sources = urls.Select(u => new { Title = "t", Url = u, Domain = "d", confidence = 0.8 }),
        }));

    private static Artifact Answer(string missionId, params (string Text, string? Url)[] claims) =>
        Artifact.Create(
            schema: ArtifactSchemas.SourcedAnswer,
            producerRole: "builder",
            missionId: missionId,
            payload: new SourcedAnswer
            {
                Claims = claims.Select(c => new SourcedClaim(c.Text, c.Url)).ToList(),
            }.ToJson());

    private static Evidence Retrieval(string missionId, string? taskId = null) => Evidence.Create(
        kind: EvidenceKinds.SourceRetrieval,
        deterministic: false,
        passed: true,
        missionId: missionId,
        detail: "web_search: q",
        taskId: taskId);

    /// <summary>A mission whose builder compiled everything — the INFERRED-claim shape, which is
    /// what a planner that does not itemise its steps produces and therefore the common case.</summary>
    private static Mission Compiled(MissionSpecification specification, string builderTaskId = "t_build") => new()
    {
        Id = "m_research",
        Goal = specification.OriginalRequest,
        Status = MissionStatus.Complete,
        UserResult = "an answer",
        Tasks = new List<Task>
        {
            new() { Id = "t_web", AssignedAnt = "web", TaskType = "research", Status = TaskStatus.Complete, Result = "sources" },
            new()
            {
                Id = builderTaskId, AssignedAnt = "builder", TaskType = "synthesis",
                Status = TaskStatus.Complete, Result = "the answer text",
            },
        },
    };

    private static MissionEvaluation Evaluate(Mission mission, MissionSpecification specification,
        IReadOnlyList<Artifact>? artifacts, IReadOnlyList<Evidence>? evidence) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: false,
            evidence: evidence, specification: specification,
            consumptions: Array.Empty<ArtifactConsumption>(), artifacts: artifacts);

    // ---- classification ------------------------------------------------------------------------

    /// <summary>
    /// THE CLASS EXISTS, and it carries `Observe` — the same ceiling as the audit class, which is
    /// worth asserting because this class touches the network and that one does not. Observe is a
    /// ceiling on what a mission may CHANGE, and reading pages changes nothing.
    /// </summary>
    [Fact]
    public void AResearchRequest_ClassifiesAsResearch_UnderObserveAuthority()
    {
        var specification = MissionIntake.Resolve(Request);

        Assert.Equal(MissionSpecification.ResearchClass, specification.MissionClass);
        Assert.Equal(MissionIntent.Research, specification.Intent);
        Assert.Equal(MissionTargets.World, specification.Targets);
        Assert.Equal(MissionAuthority.Observe, specification.Authority);
        Assert.Contains(EvidenceKinds.SourceRetrieval, specification.RequiredEvidence);
        Assert.Contains(WorkerCapabilities.RetrieveSources, specification.RequiredCapabilities);
        Assert.True(specification.Deliverables.Count >= 2);
    }

    /// <summary>
    /// AND THE RUNTIME ENFORCES IT WITHOUT ANYONE TURNING VERIFICATION ON — `.104`'s rule, which a
    /// new class has to join rather than be exempt from.
    /// </summary>
    [Fact]
    public void TheResearchClass_IsRecognized()
    {
        Assert.Contains(MissionSpecification.ResearchClass, MissionContracts.RecognizedClasses);
        Assert.True(MissionContracts.ForPreview(Request).VerificationRequired);
    }

    // ---- the boundaries ------------------------------------------------------------------------

    /// <summary>
    /// THE ONE THAT WOULD HAVE BEEN MISSED. The troubleshooting branch read `targets != None`,
    /// which was exactly right while every target was something the colony could execute a check
    /// against. A purely outward "why" would now enter the class whose entire premise is a
    /// reproduction — and the colony cannot re-run the world.
    /// </summary>
    [Fact]
    public void AnOutwardWhyQuestion_IsNotTroubleshooting() =>
        Assert.NotEqual(MissionSpecification.TroubleshootingClass,
            MissionIntake.Resolve("Why is the market moving away from that vendor?").MissionClass);

    /// <summary>
    /// AND NO REQUEST THAT CLASSIFIED AS TROUBLESHOOTING BEFORE THIS RELEASE CLASSIFIES OTHERWISE
    /// AFTER IT. That is what the narrowing had to preserve, and it is asserted rather than argued:
    /// `InspectableTargets` is what "any target" meant on the day the branch was written.
    /// </summary>
    [Fact]
    public void TheTroubleshootingClass_IsUnchangedForEveryColonySideSymptom()
    {
        foreach (var symptom in new[]
        {
            "Why is the test suite failing in this repository right now?",
            "Why is the media-server container on pve1 unhealthy?",
            "Why is the incident webhook endpoint failing?",
        })
            Assert.Equal(MissionSpecification.TroubleshootingClass,
                MissionIntake.Resolve(symptom).MissionClass);
    }

    /// <summary>
    /// A REQUEST NAMING BOTH WORLDS IS NOT ADMITTED. Its answer rests half on an inspection and half
    /// on a retrieval, and this class's gate can speak only for the retrieval half. Admitting it
    /// would let the repository half go unexamined behind a passing research grade — which is the
    /// direction that produces a confident answer about code nobody read.
    /// </summary>
    [Fact]
    public void ARequestNamingBothWorlds_IsNotAdmitted() =>
        Assert.NotEqual(MissionSpecification.ResearchClass,
            MissionIntake.Resolve(
                "Research how our repository's retry policy compares to what the upstream project does.")
                .MissionClass);

    /// <summary>
    /// AND A DESTINATION STILL WINS. `World` and `External` share a noun and differ by direction;
    /// a send is not research however many outward words surround it.
    /// </summary>
    [Fact]
    public void ASendToAnEndpoint_IsStillAnExternalAction() =>
        Assert.Equal(MissionSpecification.ExternalActionClass,
            MissionIntake.Resolve("Post the industry news summary to the team's incident webhook.")
                .MissionClass);

    /// <summary>The audit lane is untouched by the new verbs — the `.98` boundary, kept.</summary>
    [Fact]
    public void AnAuditRequest_IsUnaffectedByTheResearchVerbs()
    {
        Assert.Equal(MissionSpecification.SystemAuditClass,
            MissionIntake.Resolve("Assess the current health of the colony and report what is enabled.")
                .MissionClass);

        // "look up" reaching the repository is a file read, not a mission class.
        Assert.NotEqual(MissionSpecification.ResearchClass,
            MissionIntake.Resolve("Look up the retry constant in this codebase.").MissionClass);
    }

    // ---- the evidence kind ---------------------------------------------------------------------

    /// <summary>
    /// WHY `source_retrieval` IS ITS OWN KIND. `AssessmentObjective` requires `inspection` rows
    /// before an audit's conclusions can be believed. Had a web search written one, an audit of what
    /// is implemented in the operator's own repository could be satisfied by searching the internet
    /// — a claim about their code established from somebody else's.
    /// </summary>
    [Fact]
    public void TheAuditsInspectionRequirement_IsNotSatisfiedByAWebSearch()
    {
        var audit = MissionIntake.Resolve(
            "Audit this repository and the running colony: what is implemented, and what is enabled now?");
        Assert.Equal(MissionSpecification.SystemAuditClass, audit.MissionClass);
        Assert.Contains(EvidenceKinds.Inspection, audit.RequiredEvidence);
        Assert.DoesNotContain(EvidenceKinds.SourceRetrieval, audit.RequiredEvidence);

        var retrieval = Anthill.Core.Tools.ToolEvidence.For(
            "web_search", success: true, "m_audit", taskId: "t1", detail: "q");

        Assert.NotNull(retrieval);
        Assert.Equal(EvidenceKinds.SourceRetrieval, retrieval!.Kind);
        Assert.NotEqual(EvidenceKinds.Inspection, retrieval.Kind);
        Assert.False(retrieval.Deterministic, "the internet is not a tree; a retrieval promotes nothing");
    }

    // ---- the gate ------------------------------------------------------------------------------

    /// <summary>
    /// THE CASE THE `.99` TRIGGER COULD NOT SEE. Nothing retrieved, nothing cited, every task
    /// complete — an empty store, which contradicts no citation. Before the contract trigger this
    /// mission was gradeable as a verified success.
    /// </summary>
    [Fact]
    public void AResearchMissionThatRetrievedNothing_IsRefused()
    {
        var specification = MissionIntake.Resolve(Request);

        var evaluation = Evaluate(Compiled(specification), specification,
            artifacts: Array.Empty<Artifact>(), evidence: Array.Empty<Evidence>());

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
        Assert.False(evaluation.IsPositive);
        Assert.Contains("recorded none", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RESEARCH MISSION THAT DID THE WORK PASSES. Without this the class would be a way to fail
    /// missions rather than a way to run them, and every negative assertion above would prove only
    /// that the gate refuses everything.
    /// </summary>
    [Fact]
    public void AResearchMissionThatRetrievedAndAttributed_IsSatisfied()
    {
        var specification = MissionIntake.Resolve(Request);
        const string url = "https://example.org/quantization";

        var research = ResearchIntegrity.Evaluate(
            specification,
            new[] { SourceSet("m_research", url), Answer("m_research", ("Quantization is common.", url)) },
            new[] { Retrieval("m_research", "t_web") },
            AssembledAnswer.Build(specification, Compiled(specification).Tasks, "an answer",
                new[] { Retrieval("m_research", "t_web") }));

        Assert.True(research.Satisfied, research.Explanation);
        Assert.Equal(1, research.Retrieved);
    }

    /// <summary>The `.99` failure still fails — the new trigger widens what is checked, never what
    /// is accepted.</summary>
    [Fact]
    public void AnAnswerCitingWhatWasNeverRetrieved_IsStillRefused()
    {
        var specification = MissionIntake.Resolve(Request);

        var research = ResearchIntegrity.Evaluate(
            specification,
            new[]
            {
                SourceSet("m_research", "https://example.org/real"),
                Answer("m_research", ("Invented.", "https://example.org/never-fetched")),
            },
            new[] { Retrieval("m_research", "t_web") },
            AssembledAnswer.Build(specification, Compiled(specification).Tasks, "an answer",
                new[] { Retrieval("m_research", "t_web") }));

        Assert.False(research.Satisfied);
        Assert.Contains("never-fetched", research.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TWO RECORDS MUST AGREE. The artifact store says what was cited; the evidence store says a
    /// retrieval tool ran. A mission holding one and not the other is a mission whose two accounts
    /// of itself disagree, which is the shape ADR-004 exists to refuse.
    /// </summary>
    [Fact]
    public void ARetrievalTheEvidenceStoreDoesNotKnowAbout_IsRefused()
    {
        var specification = MissionIntake.Resolve(Request);
        const string url = "https://example.org/quantization";

        var research = ResearchIntegrity.Evaluate(
            specification,
            new[] { SourceSet("m_research", url), Answer("m_research", ("Claim.", url)) },
            Array.Empty<Evidence>(),
            AssembledAnswer.Build(specification, Compiled(specification).Tasks, "an answer", null));

        Assert.False(research.Satisfied);
        Assert.Contains("no record of having fetched", research.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// PER-SECTION EVIDENCE, READ BY SOMETHING. `.106` built the join and left it unread; §2c
    /// recorded that rendering a join is not the same as making it a checkable property.
    ///
    /// A DECLARED section — the plan named a step for this question — must carry its own retrieval.
    /// Here the builder declared `d1` and consulted nothing, while the web task retrieved for
    /// nobody: mission-level grounding exists and the SECTION is still hollow.
    /// </summary>
    [Fact]
    public void ADeclaredSectionWithNoRetrieval_IsRefused()
    {
        var specification = MissionIntake.Resolve(Request);
        const string url = "https://example.org/quantization";

        var declared = new Mission
        {
            Id = "m_research",
            Goal = specification.OriginalRequest,
            Status = MissionStatus.Complete,
            UserResult = "an answer",
            Tasks = new List<Task>
            {
                new()
                {
                    Id = "t_web", AssignedAnt = "web", TaskType = "research",
                    Status = TaskStatus.Complete, Result = "sources",
                },
                new()
                {
                    Id = "t_build", AssignedAnt = "builder", TaskType = "synthesis",
                    Status = TaskStatus.Complete, Result = "the answer text",
                    DeliverableIds = specification.Deliverables.Select(d => d.Id).ToList(),
                },
            },
        };

        var evidence = new[] { Retrieval("m_research", "t_web") };

        var assembled = AssembledAnswer.Build(specification, declared.Tasks, "an answer", evidence);
        Assert.All(assembled.Sections, s => Assert.Equal(DeliverableClaim.Declared, s.Claim));
        Assert.All(assembled.Sections, s => Assert.False(s.Grounded));

        var research = ResearchIntegrity.Evaluate(
            specification,
            new[] { SourceSet("m_research", url), Answer("m_research", ("Claim.", url)) },
            evidence,
            assembled);

        Assert.False(research.Satisfied);
        Assert.Contains("without consulting anything", research.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND AN INFERRED SECTION DEGRADES TO MISSION-LEVEL GROUNDING RATHER THAN FAILING. Under an
    /// inferred claim the compiling builder is credited with every deliverable and leaves no
    /// evidence of its own — requiring per-section grounding there would fail every research mission
    /// whose planner did not itemise its steps, which grades the planner's verbosity, not the work.
    /// </summary>
    [Fact]
    public void AnInferredSection_FallsBackToTheMissionsOwnRetrieval()
    {
        var specification = MissionIntake.Resolve(Request);
        const string url = "https://example.org/quantization";
        var evidence = new[] { Retrieval("m_research", "t_web") };

        var assembled = AssembledAnswer.Build(specification, Compiled(specification).Tasks, "an answer", evidence);
        Assert.All(assembled.Sections, s => Assert.Equal(DeliverableClaim.Inferred, s.Claim));

        var research = ResearchIntegrity.Evaluate(
            specification,
            new[] { SourceSet("m_research", url), Answer("m_research", ("Claim.", url)) },
            evidence,
            assembled);

        Assert.True(research.Satisfied, research.Explanation);
        Assert.True(research.GroundedSections > 0);
    }

    /// <summary>
    /// FAIL CLOSED. An unreadable store cannot show that a retrieval happened, and "we could not
    /// tell" must not read as "yes" for a class whose whole promise is that something specific
    /// happened. The asymmetry against `.99`'s permissive null is deliberate and is the substance of
    /// the second trigger.
    /// </summary>
    [Fact]
    public void AResearchMissionWithAnUnreadableStore_FailsClosed()
    {
        var specification = MissionIntake.Resolve(Request);

        Assert.True(CitationIntegrity.Evaluate(artifacts: null).Satisfied,
            "the retrieval trigger's permissive null is unchanged");

        Assert.False(CitationIntegrity.Evaluate(specification, null).Satisfied,
            "the contract trigger's null must fail: absence is the whole question here");

        Assert.True(CitationIntegrity.ContractTriggerAvailable);
    }

    // ---- the plan ------------------------------------------------------------------------------

    /// <summary>
    /// THE RETRIEVAL STEP IS ENSURED DETERMINISTICALLY, the standing doctrine for every class in
    /// this program. It matters more here than elsewhere: the planner's own rule says to use the web
    /// ant only when the mission needs external information, and a model reading it conservatively
    /// plans a builder that answers from its own weights — an answer that is fluent, sourceless, and
    /// indistinguishable from research to anyone reading it.
    /// </summary>
    [Fact]
    public void TheResearchPlan_AlwaysCarriesARetrievalStep()
    {
        var specification = MissionIntake.Resolve(Request);

        var planned = Planner.EnsureClassCoverage(
            new List<Task> { new() { Title = "Answer it", AssignedAnt = "builder", TaskType = "synthesis" } },
            specification.OriginalRequest, specification);

        Assert.Contains(planned, t => string.Equals(t.RequiredCapability,
            WorkerCapabilities.RetrieveSources, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(planned, t => t.AssignedAnt == "verifier");

        // And the capability is one a worker actually declares — a requirement nothing serves is
        // this repository's house defect wearing a specification's clothes.
        Assert.Contains(Anthill.Core.Agents.AntRegistry.Roles.SelectMany(r => r.Workers),
            w => w.Capabilities.Contains(WorkerCapabilities.RetrieveSources, StringComparer.OrdinalIgnoreCase));
    }
}
