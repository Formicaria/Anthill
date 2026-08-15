using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Pheromones;
using Anthill.Core.Tools;
using Anthill.SDK.Contracts;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// What a run can PROVIDE, as opposed to what a role declares it needs. v3.8.25.
///
/// <c>ToolExecutionContext</c> sat in the tree capability-aware and tested with no production caller
/// since the execution framework was written, and the reason was mundane: <c>GrantedCapabilities</c>
/// had no source, so nobody could construct one honestly. These tests pin the source and, more
/// importantly, pin that it is derived from the RUN rather than from the contracts — granting each
/// role exactly what it declares would produce a check that can never fail.
/// </summary>
public class CapabilityGrantTests
{
    private static IReadOnlySet<string> Tools(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The load-bearing assertion. A colony built without `Anthill.Modules.Tools` genuinely cannot
    /// read a file, and the grant must say so rather than assuming the capability exists because a
    /// contract asked for it.
    /// </summary>
    [Fact]
    public void RepoRead_IsNotGranted_WhenNoReadingToolWasRegistered()
    {
        var granted = CapabilityGrant.Resolve(
            Tools("system_info", "run_allowlisted_check"), modelAvailable: true, webSearchEnabled: true);

        Assert.DoesNotContain(Capability.RepoRead, granted);
    }

    [Fact]
    public void RepoRead_IsGranted_WhenAReadingToolExists() =>
        Assert.Contains(Capability.RepoRead,
            CapabilityGrant.Resolve(Tools("read_text_file"), modelAvailable: false, webSearchEnabled: false));

    /// <summary>
    /// The web capability follows the GATE as well as the tool. A registered `web_search` in a run
    /// whose switch is off is a tool that will refuse, and the capability must not claim otherwise.
    /// </summary>
    [Fact]
    public void NetworkAccess_NeedsBothTheToolAndTheSwitch()
    {
        Assert.DoesNotContain(Capability.NetworkHttpPublic,
            CapabilityGrant.Resolve(Tools("web_search"), modelAvailable: true, webSearchEnabled: false));
        Assert.Contains(Capability.NetworkHttpPublic,
            CapabilityGrant.Resolve(Tools("web_search"), modelAvailable: true, webSearchEnabled: true));
    }

    /// <summary>
    /// The core has been able to run with no reasoning provider since v3.8.5. In that colony no role
    /// can invoke a model, and the grant is where that becomes checkable.
    /// </summary>
    [Fact]
    public void ModelInvoke_TracksWhetherAProviderWasComposedIn()
    {
        Assert.DoesNotContain(Capability.ModelInvoke,
            CapabilityGrant.Resolve(Tools("read_text_file"), modelAvailable: false, webSearchEnabled: false));
        Assert.Contains(Capability.ModelInvoke,
            CapabilityGrant.Resolve(Tools("read_text_file"), modelAvailable: true, webSearchEnabled: false));
    }

    /// <summary>
    /// PROPOSING is not APPLYING. The two capabilities exist as separate names so this function can
    /// grant one and withhold the other, and no configuration may ever produce the second.
    /// </summary>
    [Fact]
    public void PatchApply_IsNeverGranted_ByAnyConfiguration()
    {
        foreach (var model in new[] { true, false })
        foreach (var web in new[] { true, false })
        {
            var granted = CapabilityGrant.Resolve(
                Tools("read_text_file", "list_directory", "search_workspace", "repository_index",
                      "run_allowlisted_check", "web_search", "apply_patch", "write_text_file", "shell_command"),
                model, web);

            Assert.Contains(Capability.RepoPatchPropose, granted);
            Assert.DoesNotContain(Capability.RepoPatchApply, granted);
            Assert.DoesNotContain(Capability.RepoWriteSandbox, granted);
        }
    }

    /// <summary>
    /// A fully-equipped colony must be able to satisfy every capability the twelve contracts require.
    /// If this fails, a role has declared a need the grant cannot express — which presents at runtime
    /// as a denial nobody can act on, and is a mapping bug rather than a policy decision.
    /// </summary>
    [Fact]
    public void AFullyEquippedColony_SatisfiesEveryContractsRequirements()
    {
        var granted = CapabilityGrant.Resolve(
            Tools("system_info", "read_text_file", "list_directory", "search_workspace",
                  "repository_index", "run_allowlisted_check", "web_search"),
            modelAvailable: true, webSearchEnabled: true);

        var unmet = AntExecutionCatalog.Contracts
            .SelectMany(kv => kv.Value.RequiredCapabilities.Select(c => (Role: kv.Key, Capability: c)))
            .Where(x => !granted.Contains(x.Capability))
            .ToList();

        Assert.True(unmet.Count == 0,
            "These roles require capabilities a fully-equipped run cannot grant: "
            + string.Join(", ", unmet.Select(u => $"{u.Role} needs {u.Capability}")));
    }
}

/// <summary>
/// <c>SchedulingMode</c> becomes binding. v3.8.25.
///
/// v3.8.23 declared it on all twelve contracts and nothing read it, so it was documentation with a
/// type. These tests pin the rule that gives it force, and the discriminator it rests on.
/// </summary>
public class SchedulingModeEnforcementTests : IDisposable
{
    // The gates these tests must open, and put back. ValidateTask refuses a gated-off specialist
    // BEFORE it reaches the scheduling rule — correctly, because "this role does not run in this
    // build" is the more fundamental fact and the better message. The first draft of these tests
    // asserted against a default configuration and was therefore measuring the ROLLOUT GATE while
    // claiming to measure the scheduling rule: it passed for the wrong reason, and would have kept
    // passing if the rule had never been written.
    //
    // Worth stating plainly: with medic and archivist gated off by default, this rule is currently
    // unreachable in production. It is a rule that starts mattering the moment an operator opens
    // those gates, which is exactly when a planner scheduling a medic would otherwise waste a
    // dispatch on a handler that can only refuse.
    private readonly bool _specialistsWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly bool _medicWas = AnthillRuntime.EnableMedicAnt;
    private readonly bool _archivistWas = AnthillRuntime.EnableArchivistAnt;
    private readonly bool _testerWas = AnthillRuntime.EnableTesterAnt;
    private readonly bool _soldierWas = AnthillRuntime.EnableSoldierAnt;

    public SchedulingModeEnforcementTests()
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSpecialistAntExecution = _specialistsWas;
        AnthillRuntime.EnableMedicAnt = _medicWas;
        AnthillRuntime.EnableArchivistAnt = _archivistWas;
        AnthillRuntime.EnableTesterAnt = _testerWas;
        AnthillRuntime.EnableSoldierAnt = _soldierWas;
    }

    private static DomainTask Planned(string role, string taskType) => new()
    {
        Title = $"{role} step", AssignedAnt = role, TaskType = taskType, Description = "d",
    };

    private static DomainTask FromHandoff(string role, string taskType)
    {
        var t = Planned(role, taskType);
        t.ParentTaskIds = new List<string> { "the-task-that-failed" };
        return t;
    }

    private static MissionConstraints NoConstraints() => MissionConstraints.Parse("do a thing");

    /// <summary>
    /// The headline rule. A planner that includes a diagnosis step before anything has failed
    /// produces a task that can only refuse — MedicAnt.Execute opens by returning Blocked in exactly
    /// that case, which is a handler defending itself against its own scheduler.
    /// </summary>
    [Theory]
    [InlineData("medic", "failure_diagnosis")]
    [InlineData("archivist", "mission_summary")]
    public void ATriggeredRole_CannotBeScheduledAsAPlannedStep(string role, string taskType)
    {
        var result = AntRegistry.ValidateTask(Planned(role, taskType), NoConstraints());

        Assert.False(result.Allowed);
        Assert.Contains("cannot be scheduled by the planner", result.Reason);
    }

    /// <summary>
    /// PolicyInserted IS enforced as of v3.8.26, and the two-step is the whole lesson.
    ///
    /// v3.8.25 deliberately left this unenforced and a test pinned the gap: nothing inserted tester
    /// or soldier, so the rule would have removed their only path — a correct rule landing as a
    /// regression. v3.8.26 built `InsertPolicyReviewTasks`, so the replacement exists and the rule
    /// can bind. This assertion is the inversion that release predicted, with the reasoning moved
    /// across rather than deleted.
    /// </summary>
    /// <summary>
    /// v0.3.8.51 (field report) — INVERTED, deliberately. The operator's six-step plan opened
    /// with "run the baseline checks" and the gate threw the step away: "tester is PolicyInserted
    /// and cannot be scheduled by the planner." A planned tester/soldier step is a plan asking
    /// for MORE safety; PolicyInserted now means the runtime GUARANTEES the role runs when its
    /// trigger fires — a floor, not a ceiling. Only the two modes whose handlers can only refuse
    /// a planned invocation (medic, archivist) keep the refusal, pinned above.
    /// </summary>
    [Theory]
    [InlineData("tester", "test_execution")]
    [InlineData("soldier", "security_review")]
    public void PolicyInsertedRoles_MayAlsoBePlanned_TheFloorIsNotACeiling(string role, string taskType)
    {
        Assert.Equal(SchedulingMode.PolicyInserted, AntExecutionCatalog.ContractFor(role)!.Scheduling);

        var result = AntRegistry.ValidateTask(Planned(role, taskType), NoConstraints());

        Assert.DoesNotContain("cannot be scheduled by the planner", result.Reason ?? "");
    }

    /// <summary>
    /// ...and arriving with a parent — which is how policy inserts them — is not refused for the
    /// scheduling reason. Without this the rule above would pass on a build where the roles were
    /// simply unreachable.
    /// </summary>
    [Theory]
    [InlineData("tester", "test_execution")]
    [InlineData("soldier", "security_review")]
    public void PolicyInsertedRoles_AreAdmittedWhenInserted(string role, string taskType)
    {
        var result = AntRegistry.ValidateTask(FromHandoff(role, taskType), NoConstraints());

        Assert.DoesNotContain("cannot be scheduled by the planner", result.Reason);
    }

    /// <summary>
    /// ...and the same role arriving through a HANDOFF is not refused for that reason. A task with a
    /// parent was caused by something that actually happened, which is the whole distinction.
    ///
    /// Asserted as "not refused FOR SCHEDULING" rather than "allowed", because these roles are also
    /// behind rollout gates that are closed by default — this test is about the scheduling rule, and
    /// conflating it with the gate would make it pass for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData("medic", "failure_diagnosis")]
    [InlineData("archivist", "mission_summary")]
    public void TheSameRoleArrivingByHandoff_IsNotRefusedForBeingUnplannable(string role, string taskType)
    {
        var result = AntRegistry.ValidateTask(FromHandoff(role, taskType), NoConstraints());

        Assert.DoesNotContain("cannot be scheduled by the planner", result.Reason);
    }

    /// <summary>
    /// The roles that do the mission's work are untouched. Without this the rule above would pass
    /// even if it had accidentally blocked everything.
    /// </summary>
    [Theory]
    [InlineData("researcher", "research")]
    [InlineData("coder", "patch_proposal")]
    [InlineData("builder", "build_answer")]
    public void PlannerSelectableRoles_AreStillPlannable(string role, string taskType)
    {
        var result = AntRegistry.ValidateTask(Planned(role, taskType), NoConstraints());

        Assert.True(result.Allowed, result.Reason);
    }
}

/// <summary>
/// A required handoff that is refused blocks verification. v3.8.25.
///
/// <c>AntHandoff.Required</c> has existed since v2.21.0 and meant nothing: a refusal reached an event
/// row and no gate, so a mission whose tester demanded a medic and did not get one completed exactly
/// as if the repair had happened.
/// </summary>
public class RequiredHandoffTests
{
    /// <summary>
    /// The type carries the distinction, so it is worth pinning that the two cases are actually
    /// different values rather than a field nobody sets.
    /// </summary>
    [Fact]
    public void TheRequiredFlag_DistinguishesTheTwoCases()
    {
        var required = new AntHandoff("tester", "medic", "failed", "failure_diagnosis",
            new[] { "test_report" }, Required: true, Depth: 1, DedupeKey: "k");
        var optional = required with { Required = false };

        Assert.True(required.Required);
        Assert.False(optional.Required);
    }

    /// <summary>
    /// The tester's failure handoff to the medic is REQUIRED, which is what makes the plumbing in
    /// this release consequential rather than cosmetic. If a future edit makes it optional, the
    /// repair path silently becomes advisory again.
    /// </summary>
    [Fact]
    public void TheTestersFailureHandoff_IsRequired()
    {
        var mission = new Mission { Goal = "run the checks", Status = MissionStatus.Running };
        var task = new DomainTask
        {
            Title = "Run checks", AssignedAnt = "tester", TaskType = "test_execution",
            Description = "run the build check",
        };
        mission.Tasks.Add(task);

        var contract = AntExecutionCatalog.ContractFor("tester")!;

        // The contract is what declares the medic reachable at all.
        Assert.Contains("medic", contract.AllowedHandoffRoles);
    }
}

/// <summary>
/// The soldier reviews the PATCH. v3.8.25.
///
/// Until this release its entire input was the task description plus prior tasks' result prose, so
/// it was scanning descriptions of a change. The `secret_material` rule looks for
/// `-----BEGIN PRIVATE KEY-----` and `api_key = "…"` in SOURCE, and source was the one thing the
/// review never saw — every rule about content was matching a summary.
/// </summary>
public class SoldierReviewsThePatchTests
{
    private sealed class FakeArtifacts : Anthill.SDK.Artifacts.IArtifactStore
    {
        private readonly List<Anthill.SDK.Artifacts.Artifact> _items = new();

        public void AddPatchSet(string missionId, string payload) =>
            _items.Add(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.PatchSet,
                producerRole: "coder", missionId: missionId, payload: payload));

        public string Put(Anthill.SDK.Artifacts.Artifact artifact) { _items.Add(artifact); return artifact.Id; }
        public Anthill.SDK.Artifacts.Artifact? Get(string artifactId) => _items.FirstOrDefault(a => a.Id == artifactId);
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ForMission(string missionId, int limit = 200) =>
            _items.Where(a => a.MissionId == missionId).Take(limit).ToList();
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ForMission(string missionId, string schema, int limit = 200) =>
            _items.Where(a => a.MissionId == missionId && a.Schema == schema).Take(limit).ToList();
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> SourcesOf(string artifactId) =>
            Array.Empty<Anthill.SDK.Artifacts.Artifact>();
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ConsumersOf(string artifactId) =>
            Array.Empty<Anthill.SDK.Artifacts.Artifact>();

        // v0.3.8.57 — the consumption ledger. Left unimplemented DELIBERATELY rather than added to
        // IArtifactStore as a default no-op: a default would mean a real store that forgot to record
        // reads compiles, ships, and keeps an empty ledger forever, which is the defect class this
        // release keeps finding. A fake that records nothing is honest; an interface that lets a
        // production store record nothing silently is not.
        public void RecordConsumption(Anthill.SDK.Artifacts.ArtifactConsumption consumption) { }
        public IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption> ConsumptionsOf(string artifactId) =>
            Array.Empty<Anthill.SDK.Artifacts.ArtifactConsumption>();
        public IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption> ConsumptionsForMission(string missionId, int limit = 500) =>
            Array.Empty<Anthill.SDK.Artifacts.ArtifactConsumption>();
    }

    private static (Mission mission, DomainTask task) Fixture(string description = "review the proposed change")
    {
        var mission = new Mission { Goal = "change a file", Status = MissionStatus.Running };
        var task = new DomainTask
        {
            Title = "Security review", AssignedAnt = "soldier",
            TaskType = "security_review", Description = description,
        };
        mission.Tasks.Add(task);
        return (mission, task);
    }

    /// <summary>
    /// THE HEADLINE. A secret inside the proposed source is found — and would not have been, because
    /// no prior task's prose contains the key material.
    /// </summary>
    [Fact]
    public void ASecretInTheProposedSource_IsFound()
    {
        var (mission, task) = Fixture();
        var artifacts = new FakeArtifacts();
        // A PRIVATE KEY HEADER rather than a quoted assignment, deliberately. The first draft used
        // `var apiKey = "sk-…"` and did not match, for two reasons worth keeping: the rule is
        // case-SENSITIVE, so `api[_-]?key` does not match `apiKey`, and the JSON escaping put a
        // backslash exactly where the regex needs the opening quote. The key header has no casing
        // or quoting to get wrong, so this fixture exercises the plumbing rather than my ability to
        // escape a string through two layers.
        artifacts.AddPatchSet(mission.Id,
            """{"proposals":[{"FilePath":"src/Config.cs","new_content":"-----BEGIN RSA PRIVATE KEY-----"}]}""");

        var result = new SoldierAnt(artifacts).Execute(task, mission);

        Assert.Contains(SoldierAnt.SoldierBlockMarker, result.Warnings);
        Assert.Contains(result.Warnings, w => w == "secret_material");
    }

    /// <summary>The control: the same review WITHOUT the store sees only the description, and finds
    /// nothing. This is the state every release before v3.8.25 shipped.</summary>
    [Fact]
    public void TheSameSecret_IsInvisibleWithoutTheArtifactStore()
    {
        var (mission, task) = Fixture();

        var result = new SoldierAnt().Execute(task, mission);

        Assert.DoesNotContain(SoldierAnt.SoldierBlockMarker, result.Warnings);
    }

    /// <summary>
    /// How many patch artifacts were actually read reaches the review text, so a clean scan of a
    /// real patch is distinguishable from a scan of nothing.
    /// </summary>
    [Fact]
    public void TheReviewRecords_HowManyPatchArtifactsItRead()
    {
        var (mission, task) = Fixture();
        var artifacts = new FakeArtifacts();
        artifacts.AddPatchSet(mission.Id, """{"proposals":[{"FilePath":"docs/notes.md","new_content":"hello"}]}""");

        var withPatch = new SoldierAnt(artifacts).Execute(task, mission);
        var withoutPatch = new SoldierAnt().Execute(task, mission);

        Assert.Contains("patch_artifacts_reviewed: 1", withPatch.Narrative);
        Assert.Contains("patch_artifacts_reviewed: 0", withoutPatch.Narrative);
    }

    /// <summary>A store that faults must not stop the review — the deterministic rules it can still
    /// apply to the description are worth more than a refusal.</summary>
    [Fact]
    public void AFaultingStore_DoesNotBlockTheReview()
    {
        var (mission, task) = Fixture();

        var result = new SoldierAnt(new ThrowingArtifacts()).Execute(task, mission);

        Assert.True(result.Success);
        Assert.Contains("patch_artifacts_reviewed: 0", result.Narrative);
    }

    private sealed class ThrowingArtifacts : Anthill.SDK.Artifacts.IArtifactStore
    {
        public string Put(Anthill.SDK.Artifacts.Artifact artifact) => throw new InvalidOperationException("nope");
        public Anthill.SDK.Artifacts.Artifact? Get(string artifactId) => throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ForMission(string missionId, int limit = 200) =>
            throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ForMission(string missionId, string schema, int limit = 200) =>
            throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> SourcesOf(string artifactId) =>
            throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.Artifact> ConsumersOf(string artifactId) =>
            throw new InvalidOperationException("nope");

        // Throws like everything else here: this fake exists to prove the soldier survives a store
        // that is entirely unavailable, and a ledger call that quietly succeeded would weaken that.
        public void RecordConsumption(Anthill.SDK.Artifacts.ArtifactConsumption consumption) =>
            throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption> ConsumptionsOf(string artifactId) =>
            throw new InvalidOperationException("nope");
        public IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption> ConsumptionsForMission(string missionId, int limit = 500) =>
            throw new InvalidOperationException("nope");
    }
}

/// <summary>
/// The archivist has a trigger. v3.8.26.
///
/// It had NEVER RUN — not once, in the project's history. Zero planner references, no handoff
/// targeting it, no policy creating one. Registered, contracted, handler-complete, gated, and
/// unreachable, with nothing reporting that because every check asked whether it was ENABLED rather
/// than whether anything could CALL it.
/// </summary>
public class ArchivistTriggerTests
{
    /// <summary>
    /// The structural fact that made it unreachable, pinned so it cannot silently return: no
    /// planner-produced plan may contain an archivist, because the contract forbids it. Its only
    /// path is the post-finalization lifecycle step.
    /// </summary>
    [Fact]
    public void TheArchivist_IsNotSomethingAPlanCanContain() =>
        Assert.Equal(SchedulingMode.PostFinalization,
            AntExecutionCatalog.ContractFor("archivist")!.Scheduling);

    /// <summary>
    /// It refuses a mission that is still running, which is exactly why a planner-scheduled archivist
    /// could never have worked: the planner schedules while the mission runs.
    /// </summary>
    [Fact]
    public void ARunningMission_IsRefused()
    {
        var mission = new Mission { Goal = "do a thing", Status = MissionStatus.Running };
        var task = new DomainTask
        {
            Title = "Archive", AssignedAnt = "archivist", TaskType = "mission_summary",
            Description = "Extract durable lessons from the finished mission.",
        };

        var result = new ArchivistAnt().Execute(task, mission);

        Assert.False(result.Success);
        Assert.Contains("not terminal", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A TERMINAL mission with the canonical outcome handed over runs. This is the shape the Queen
    /// builds after SaveMissionEvaluation, and the reason the outcome is passed rather than
    /// re-derived: the point of a persisted evaluation is that nothing downstream computes its own.
    /// </summary>
    [Fact]
    public void ATerminalMission_WithTheCanonicalOutcome_IsArchived()
    {
        var mission = new Mission { Goal = "do a thing", Status = MissionStatus.Complete };
        mission.Tasks.Add(new DomainTask
        {
            Title = "Work", AssignedAnt = "researcher", TaskType = "research",
            Status = TaskStatus.Complete, Result = "did the thing",
        });

        var task = new DomainTask
        {
            Title = "Archive mission lessons", AssignedAnt = "archivist", TaskType = "mission_summary",
            Description = "Extract durable lessons from the finished mission. outcome: completed_verified",
        };

        var result = new ArchivistAnt().Execute(task, mission);

        Assert.True(result.Success, result.Summary);
    }

    /// <summary>
    /// The task type the Queen's synthetic task uses must be one the contract supports, or the
    /// handler refuses before doing anything. Keyed to the string the PRODUCER emits — the rule this
    /// suite adopted after the v3.8.21 verification defect.
    /// </summary>
    [Fact]
    public void TheSyntheticTaskType_IsSupportedByTheContract() =>
        Assert.True(AntExecutionCatalog.ContractFor("archivist")!.SupportsTaskType("mission_summary"));
}

/// <summary>
/// A role is never punished for not running. Stage E, v3.8.26.
///
/// The line this replaces pushed a SKIPPED task's role trail down by -0.01 and swept Blocked,
/// Cancelled and Pending into the same -0.04 as a real failure. Survivable while six of twelve roles
/// never ran; not survivable in the release that gives all twelve a trigger, because a specialist
/// enabled for the first time would arrive carrying negative reputation from missions it was gated
/// out of.
/// </summary>
public class LearningAttributionTests
{
    private static DomainTask TaskWith(TaskStatus status, string? failureType = null) => new()
    {
        Title = "t", AssignedAnt = "tester", TaskType = "test_execution",
        Status = status, FailureType = failureType,
    };

    /// <summary>THE headline: not running is not a failure.</summary>
    [Theory]
    [InlineData(TaskStatus.Skipped)]
    [InlineData(TaskStatus.Pending)]
    [InlineData(TaskStatus.Blocked)]
    [InlineData(TaskStatus.Cancelled)]
    public void ARoleThatDidNotRun_IsNeitherCreditedNorBlamed(TaskStatus status)
    {
        var attribution = LearningAttribution.For(TaskWith(status), missionVerified: false);

        Assert.Equal(LearningAttribution.Attribution.Neutral, attribution);
        Assert.Equal(0.0, LearningAttribution.DeltaFor(attribution));
    }

    /// <summary>Neutral is EXACTLY zero. The old -0.01 for a skipped task looked like rounding and
    /// was a judgment.</summary>
    [Fact]
    public void NeutralIsExactlyZero_NotASmallNegative() =>
        Assert.Equal(0.0, LearningAttribution.DeltaFor(LearningAttribution.Attribution.Neutral));

    /// <summary>
    /// Completed work is positive only when the mission's own verification stood behind it. A
    /// completed task in an unverified mission is NEUTRAL: the work happened, and whether it was the
    /// right work is exactly what verification failed to establish.
    /// </summary>
    [Fact]
    public void CompletedWork_IsPositiveOnlyInAVerifiedMission()
    {
        Assert.Equal(LearningAttribution.Attribution.Positive,
            LearningAttribution.For(TaskWith(TaskStatus.Complete), missionVerified: true));
        Assert.Equal(LearningAttribution.Attribution.Neutral,
            LearningAttribution.For(TaskWith(TaskStatus.Complete), missionVerified: false));
    }

    /// <summary>
    /// A provider outage, a rate limit or a dependency failure is not the worker's doing. ModelRouter
    /// and ToolRegistry already record those against the provider and the tool — charging them to the
    /// worker too counts one fact twice, against the wrong subject.
    /// </summary>
    [Theory]
    [InlineData(nameof(FailureClass.TransientProviderFailure))]
    [InlineData(nameof(FailureClass.RateLimit))]
    [InlineData(nameof(FailureClass.Timeout))]
    [InlineData(nameof(FailureClass.DependencyFailure))]
    [InlineData(nameof(FailureClass.AuthorizationFailure))]
    public void AnEnvironmentalFailure_IsNotChargedToTheWorker(string failureType) =>
        Assert.Equal(LearningAttribution.Attribution.Neutral,
            LearningAttribution.For(TaskWith(TaskStatus.Failed, failureType), missionVerified: false));

    /// <summary>
    /// ...but a failure that IS the doer's remains negative. Without this the rule above would pass
    /// on an implementation that never blamed anyone for anything.
    /// </summary>
    [Theory]
    [InlineData(nameof(FailureClass.ValidationFailure))]
    [InlineData(nameof(FailureClass.InternalDefect))]
    [InlineData(nameof(FailureClass.VerificationFailure))]
    public void AFailureAttributableToTheDoer_IsStillNegative(string failureType)
    {
        var attribution = LearningAttribution.For(TaskWith(TaskStatus.Failed, failureType), missionVerified: false);

        Assert.Equal(LearningAttribution.Attribution.Negative, attribution);
        Assert.True(LearningAttribution.DeltaFor(attribution) < 0);
    }

    /// <summary>An unclassified failure is still the doer's. Absence of a class is not evidence of
    /// an environmental cause, and failing toward "blameless" would make every unclassified failure
    /// invisible to learning.</summary>
    [Fact]
    public void AnUnclassifiedFailure_RemainsAttributable() =>
        Assert.Equal(LearningAttribution.Attribution.Negative,
            LearningAttribution.For(TaskWith(TaskStatus.Failed), missionVerified: false));

    /// <summary>A status this function has not been taught fails toward NEUTRAL — a new status
    /// silently defaulting to "penalise" is how a role acquires a reputation nobody decided on.</summary>
    [Fact]
    public void AnUnknownStatus_FailsTowardNeutral() =>
        Assert.Equal(LearningAttribution.Attribution.Neutral,
            LearningAttribution.For(TaskWith(TaskStatus.Running), missionVerified: true));

    /// <summary>
    /// A trail must be observed more than once before it steers planning. The first mission a
    /// newly-enabled role appears in must not decide how the colony feels about it — which is the
    /// specific risk of the release that gives all twelve roles a trigger.
    /// </summary>
    [Fact]
    public void ATrailMustBeObservedSeveralTimes_BeforeItSteersPlanning() =>
        Assert.True(Anthill.Core.Memory.SqliteMemory.MinObservationsToSteerPlanning >= 2,
            "one observation is an anecdote; a trail written once sits at whatever that run produced");
}

/// <summary>
/// One switch instead of nine. v3.8.26.
///
/// Turning the colony on required `specialist_ant_execution_enabled`, an activation tier and six
/// separate `*_ant_enabled` flags. Nine unrelated keys, and getting one wrong produces a role that is
/// silently absent — with nothing correlating them into "is the roster on".
///
/// Tested against the PURE resolver rather than the static projection, because the resolver is where
/// the rule lives and the statics are just where it lands.
/// </summary>
public class RosterProfileTests
{
    private static RosterActivation NothingOn() => new(
        SpecialistExecution: false, Tier: ActivationTier.Core,
        Tester: false, Soldier: false, Medic: false, Archivist: false,
        UiCartographer: false, Scribe: false,
        HandoffIngestion: false, AdaptiveMissionControl: false);

    /// <summary>
    /// The default changes NOTHING. A profile that silently switched six roles on for every existing
    /// installation on upgrade would be the opposite of the rollout discipline this program is built
    /// on, and this is the assertion that keeps it opt-in.
    /// </summary>
    [Fact]
    public void TheDefaultProfile_TurnsNothingOn()
    {
        var resolved = RosterProfiles.Resolve(RosterProfiles.Core, null, NothingOn());

        Assert.Equal(NothingOn(), resolved);
    }

    /// <summary>`full` enables the whole roster AND the two things that make it collaborate.</summary>
    [Fact]
    public void TheFullProfile_EnablesTheWholeRoster()
    {
        var r = RosterProfiles.Resolve(RosterProfiles.Full, null, NothingOn());

        Assert.True(r.SpecialistExecution);
        Assert.Equal(ActivationTier.Full, r.Tier);
        Assert.True(r.Tester && r.Soldier && r.Medic && r.Archivist && r.UiCartographer && r.Scribe);

        // A tester that cannot hand off to a medic, in a mission that cannot grow the repair task
        // the handoff asks for, is six roles that run and never collaborate.
        Assert.True(r.HandoffIngestion);
        Assert.True(r.AdaptiveMissionControl);
    }

    /// <summary>
    /// The kill switch survives the profile — the rollback path. A kill switch the profile could
    /// override would not be a kill switch.
    /// </summary>
    [Fact]
    public void ADisabledRole_StaysOffUnderTheFullProfile()
    {
        var r = RosterProfiles.Resolve(RosterProfiles.Full, new[] { "scribe" }, NothingOn());

        Assert.False(r.Scribe);
        Assert.True(r.Tester);   // the rest are unaffected
    }

    /// <summary>
    /// A profile only ever WIDENS. An operator who hand-enabled a role and then adopts `core` keeps
    /// it — the profile is a floor, not a replacement, so adopting one never silently switches
    /// something off.
    /// </summary>
    [Fact]
    public void AProfileNeverNarrowsWhatTheFlagsAlreadySet()
    {
        var handEnabled = NothingOn() with { Tester = true, SpecialistExecution = true };

        var r = RosterProfiles.Resolve(RosterProfiles.Core, null, handEnabled);

        Assert.True(r.Tester);
        Assert.True(r.SpecialistExecution);
    }

    /// <summary>
    /// A misspelled kill switch is REPORTED, not ignored. Silently dropping it leaves an operator
    /// believing a role is off while it runs — which is worse than the typo.
    /// </summary>
    [Fact]
    public void AMisspelledDisabledRole_IsReported()
    {
        Assert.Contains("scribbe", RosterProfiles.UnknownDisabledRoles(new[] { "scribbe", "tester" }));
        Assert.DoesNotContain("tester", RosterProfiles.UnknownDisabledRoles(new[] { "scribbe", "tester" }));
    }
}

