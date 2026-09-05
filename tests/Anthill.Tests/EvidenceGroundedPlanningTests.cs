using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Orchestration;
using Anthill.Core.Pheromones;
using Anthill.Core.Planning;
using Anthill.Core.Tools;
using Xunit;
// The domain entities collide by name with System.Threading.Tasks under implicit usings, exactly as
// they do in Anthill.Core — where a global alias settles it. Test assemblies get no such alias, so
// the two that appear here are named explicitly, matching PlanningServiceTests.
using DomainTask = Anthill.Core.Domain.Task;
using DomainTaskStatus = Anthill.Core.Domain.TaskStatus;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.123 — A REQUEST FOR EVIDENCE IS PLANNED AS A STEP THAT GOES AND READS IT.
///
/// THE DEFECT, and it is the length gate rather than a missing mission class.
/// `Planner.CreateSpecIngestionTasks` is reached on `goal.Length` and nothing else, and every task
/// it produces is a `section_analysis` assigned to `researcher.mission_researcher` — a worker whose
/// own contract is to read the colony's mission history. So "inspect the repository and report what
/// the code actually does", written carefully enough to be long, became N tasks that paraphrased the
/// operator's own sentences back to them and one synthesis that combined the paraphrases. Every task
/// completed. Nothing opened a file. The mission graded green.
///
/// That is mission `7afd85b2`'s shape again — tasks completed, nothing read, findings asserted —
/// arriving through a door no class gate can guard: the length branch is taken BEFORE any class
/// branch is reached, and a `general` mission has no class branch to reach at all.
///
/// WHAT THESE TESTS HOLD, and the second is the half a presence check would have missed:
///
///   1. the step EXISTS, on the short path and inside the chunked plan alike, and it carries the
///      CAPABILITY rather than a worker name — so which worker serves it stays the registry's
///      question;
///   2. the step comes FIRST. An inspection that runs after the synthesis is evidence nothing
///      consumed, which is a differently-shaped version of the same green run;
///   3. and the guarantee is RECORDED, because a plan an operator did not write must be one an
///      operator can see.
///
/// The fourth test is about the other direction — a task that already declares what it must be able
/// to do must arrive at dispatch still declaring it, and no accumulated reputation may take the
/// worker that satisfies it.
/// </summary>
public class EvidenceGroundedPlanningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_grounded_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteMemory _memory;
    private readonly PlanningService _planning;

    private readonly bool _specIngestionWas = AnthillRuntime.EnableSpecIngestion;
    private readonly int _thresholdWas = AnthillRuntime.LongInputThreshold;

    public EvidenceGroundedPlanningTests()
    {
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "grounded.db"));
        // useOllama:false — the deterministic path. A planning test that needed a live model would
        // be testing the model, and would not run in CI at all.
        _planning = new PlanningService(new Planner(useOllama: false, router: null), _memory,
            new ToolRegistry(_memory), () => _memory.LoadSkillRegistry());
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSpecIngestion = _specIngestionWas;
        AnthillRuntime.LongInputThreshold = _thresholdWas;
        _memory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The recorded ask, verbatim in shape: it names the repository AND what the code does,
    /// which is the request the length gate turned into prose analysis.</summary>
    private const string GroundedAsk =
        "Inspect the repository and report what the code actually does.";

    private static Planner Offline() => new(useOllama: false, router: null);

    private static DomainTask? WithCapability(IEnumerable<DomainTask> tasks, string capability) =>
        tasks.FirstOrDefault(t => string.Equals(t.RequiredCapability, capability, StringComparison.OrdinalIgnoreCase));

    // ---- 1. the step exists, and it exists inside the chunked plan too -------------------------

    /// <summary>
    /// THE STEP IS PLANNED, and it names the CAPABILITY. Naming `file.file_reader` here instead
    /// would be this method deciding a question the registry owns — the `.98` rule, applied to a
    /// step inserted for a demand rather than for a class.
    ///
    /// Asserted with NO SPECIFICATION, deliberately. An audit reaches the same insertion through its
    /// contract — it declares that it must SHOW an inspection — and testing only that path would
    /// prove the audit lane works and say nothing about the mission this release is actually about:
    /// a `general` mission whose operator asked, in plain words, to be answered from the source.
    /// </summary>
    [Fact]
    public void AGoalDemandingRepositoryInspection_PlansAStepThatCarriesTheCapability()
    {
        var reported = new List<string>();

        var tasks = Offline().CreateTasks(GroundedAsk, MissionConstraints.None,
            onSubstituted: (reason, _) => reported.Add(reason));

        var inspection = WithCapability(tasks, WorkerCapabilities.InspectRepository);
        Assert.NotNull(inspection);
        Assert.Equal("file", inspection!.AssignedAnt);

        // A capability nothing serves is this repository's house defect wearing a specification's
        // clothes, and it would be introduced HERE if anywhere — the step is inserted by a detector
        // rather than by a class that was designed alongside its workers.
        Assert.Contains(AntRegistry.ByWorker.Values,
            w => w.Enabled && w.Capabilities.Contains(WorkerCapabilities.InspectRepository, StringComparer.OrdinalIgnoreCase));

        // AND IT IS RECORDED. Without the row an operator reading this plan concludes the planner
        // happened to include an inspection step, and the guarantee is invisible until it is removed.
        Assert.Contains(PlanSubstitutions.GroundedInspectionRequired, reported);
    }

    /// <summary>
    /// THE RUNTIME HALF, when the operator asked for it. "What is implemented" and "what is running
    /// right now" are different questions against different sources, and a request that names the
    /// second must not be served by a step that reads the first — the `.98` distinction, reached
    /// here from the operator's wording rather than from a mission class.
    /// </summary>
    [Fact]
    public void AGoalAskingWhatIsActuallyRunning_AlsoPlansTheRuntimeInspection()
    {
        var tasks = Offline().CreateTasks(
            "Inspect the repository and report which workers are actually running.",
            MissionConstraints.None);

        Assert.NotNull(WithCapability(tasks, WorkerCapabilities.InspectRepository));
        Assert.NotNull(WithCapability(tasks, WorkerCapabilities.InspectRuntimeState));
    }

    /// <summary>
    /// THE CHUNKED PLAN IS GROUNDED, NOT REPLACED. The spec-ingestion architecture is not wrong — a
    /// long request still has to be read in bounded pieces, and abandoning that would trade a
    /// mission that inspects nothing for a mission that overflows context. So the section analyses
    /// stay and an inspection is guaranteed inside them.
    ///
    /// AND THE SYNTHESIS WAITS FOR IT, which is the half that presence alone does not buy. The
    /// spec-ingestion plan declares its own edges and `PlanningService` therefore skips auto-wiring
    /// it entirely (`IsLongInput`), so an inspection inserted without an edge would be free to run
    /// after the synthesis that was supposed to consume it: the same green run with the tasks in a
    /// different order.
    /// </summary>
    [Fact]
    public void ALongGroundedGoal_KeepsSpecIngestion_AndTheSynthesisDependsOnTheInspection()
    {
        AnthillRuntime.EnableSpecIngestion = true;
        AnthillRuntime.LongInputThreshold = 1200;

        var goal = GroundedAsk + "\n\n"
                 + string.Concat(Enumerable.Repeat(
                       "The workflow has several ordered stages and each one has its own rules. ", 40));
        Assert.True(Planner.IsLongInput(goal), "the fixture must actually reach the length gate");

        var reported = new List<string>();
        var tasks = Offline().CreateTasks(goal, MissionConstraints.None,
            onSubstituted: (reason, _) => reported.Add(reason));

        // Both facts are reported: the plan WAS chunked, and it was ALSO grounded. Either alone
        // would tell an operator half of what happened to their request.
        Assert.Contains(PlanSubstitutions.LongInputSpecIngestion, reported);
        Assert.Contains(PlanSubstitutions.GroundedInspectionRequired, reported);

        // The chunking survived.
        Assert.Contains(tasks, t => t.TaskType == "section_analysis");

        var inspection = WithCapability(tasks, WorkerCapabilities.InspectRepository);
        Assert.NotNull(inspection);

        var synthesis = tasks.Single(t => t.TaskType == "synthesis");
        Assert.Contains(inspection!.Id, synthesis.DependsOn);

        // And the inspection itself waits for nothing — an evidence step that depended on the
        // analyses would be reading the source AFTER the plan had already been written from prose.
        Assert.Empty(inspection.DependsOn);
    }

    /// <summary>
    /// THE DETECTOR DOES NOT FIRE ON ORDINARY WORK, and this is the assertion that keeps the one
    /// above from being bought too cheaply. A detector that answered true for everything would
    /// satisfy every test in this file and spend a real inspection task on every mission that
    /// mentions a file in passing.
    ///
    /// The negatives are chosen for how close they come. Two of them contain the very nouns the
    /// detector keys on — "Research…" holds the substring `search`, and "lay out a repository" names
    /// the repository outright — and neither names an ACT of reading the colony's own source, which
    /// is the whole distinction. A substring matcher would fire on both.
    /// </summary>
    [Theory]
    [InlineData("Research what the papers and vendors say about local model quantization.")]
    [InlineData("What is the queen's role in the colony?")]
    [InlineData("How do other open-source projects lay out a repository?")]
    [InlineData("Write a summary of the release for the changelog.")]
    public void AnOrdinaryGoal_IsNotTreatedAsADemandForEvidence(string goal)
    {
        Assert.False(Planner.DemandsGroundedInspection(goal, null));
        Assert.Null(WithCapability(
            Offline().CreateTasks(goal, MissionConstraints.None), WorkerCapabilities.InspectRepository));
    }

    // ---- 2. an explicit capability survives the pipeline, and no trail takes its worker ---------

    /// <summary>
    /// A DECLARED REQUIREMENT SURVIVES EVERY LAYER THAT COULD QUIETLY DROP IT, and reputation never
    /// outranks compatibility.
    ///
    /// The task under test is the audit class's runtime-inspection step, which is the sharpest case
    /// available: `researcher` holds three workers, exactly one declares
    /// `inspect_runtime_state`, and one of the other two — `mission_researcher` — is what the
    /// keyword resolver picks from the word "missions" in the operator's own sentence. So every
    /// mechanism that could go wrong has something to go wrong toward.
    ///
    /// FOUR LAYERS, one plan: `EnforceConstraints` filters, `EnsureClassCoverage` inserts,
    /// `AssignDefaultWorkers` resolves and admits, and `PlanningService.CreatePlan` repairs and then
    /// consults the pheromone trail. The capability is a field on a mutable task travelling through
    /// all four, which is precisely the kind of thing a rebuild-into-a-new-object step loses without
    /// anybody noticing.
    ///
    /// THE TRAIL IS REAL, and the second block proves it rather than assuming it. A verified trail
    /// on `researcher.mission_researcher` is strong enough to decide among the researcher's workers
    /// — asserted directly — and it still does not touch this task, because
    /// `WorkerResolution` recorded the assignment's BASIS as `Specification` and
    /// `PlanningService` consults a trail only for `Default`. That is the existing mechanism, read
    /// off the task rather than re-derived, and it is what makes "compatibility outranks strength"
    /// a property of the wiring instead of a comment.
    /// </summary>
    [Fact]
    public void AnExplicitRequiredCapability_SurvivesPlanning_AndNoTrailTakesTheWorkerThatServesIt()
    {
        // The adversarial phrasing from the audit suite: it says "missions" in passing, the way an
        // operator naturally would, and that single word is what routes a researcher task to the
        // mission-history worker when nothing stronger has been said.
        const string goal = "Assess what this colony can do today and whether its missions reach the right workers.";

        var mission = new Mission { Goal = goal };
        _memory.SaveMission(mission);
        var context = MissionContext.ForMission(mission);
        Assert.Equal(MissionSpecification.SystemAuditClass, context.Specification.MissionClass);

        // A verified trail: strength above the 0.5 baseline with successes outnumbering failures,
        // which by construction of the single writer only completed_verified missions can produce.
        _memory.UpdatePheromoneTrail("worker:researcher.mission_researcher", "worker", success: true, 0.4,
            new() { ["seed"] = "test" });

        // It really would decide, given the chance — over the whole role, this trail wins.
        Assert.Equal("researcher.mission_researcher", TrailGuidedSelection.Prefer(
            WorkerResolution.CompatibleCandidates("researcher", requiredCapabilities: null),
            key => _memory.GetPheromoneTrail(key))!.WorkerId);

        var tasks = _planning.CreatePlan(context);

        var runtime = WithCapability(tasks, WorkerCapabilities.InspectRuntimeState);
        Assert.NotNull(runtime);
        Assert.Equal("researcher.runtime_researcher", runtime!.AssignedWorker);
        Assert.Equal(WorkerDecisionBasis.Specification, runtime.WorkerBasis);
        Assert.NotEqual(DomainTaskStatus.Failed, runtime.Status);   // and admission kept it

        // The repository half is the AMBIGUOUS case — two file workers declare the capability, so
        // the capability decides nothing and a trail is allowed to break the tie. What it may never
        // do is reach outside the compatible set, which is what the narrowing in
        // `CompatibleCandidates` is for, so the requirement itself must still be on the task.
        var repository = WithCapability(tasks, WorkerCapabilities.InspectRepository);
        Assert.NotNull(repository);
        Assert.All(WorkerResolution.CompatibleCandidates("file", new[] { WorkerCapabilities.InspectRepository }),
            w => Assert.Contains(WorkerCapabilities.InspectRepository, w.Capabilities, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(AntRegistry.ByWorker[repository!.AssignedWorker!].Capabilities,
            c => string.Equals(c, WorkerCapabilities.InspectRepository, StringComparison.OrdinalIgnoreCase));
    }
}
