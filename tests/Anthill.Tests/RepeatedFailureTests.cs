using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.SDK.Actions;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A DEFECT THAT CAME BACK STOPS THE MISSION FOR THAT REASON. v0.3.8.105, PLAN.md §2b `.105`.
///
/// THE EXIT GATE'S THIRD CLAUSE: "repeated identical failure stops truthfully."
///
/// WHAT WAS ALREADY TRUE. The medic has bounded repair looping on the SEMANTIC failure signature
/// since `.57`, and correctly: the same signature under a new task id, after a repair was actually
/// attempted, means the defect returned with nothing material changed — so it escalates rather than
/// looping. That bound is untouched here and stays exactly where it is.
///
/// WHAT THE CONTROLLER COULD NOT DO WAS SAY SO. An exhausted repair loop stopped with
/// `adaptive_stop`, whose reason reads "the bound is spent, not the problem" — true, and silent
/// about the fact that the problem was reproducible and the store already knew it.
/// `repeated_failure` is that sentence.
///
/// IT DOES NOT CHANGE WHEN THE MISSION STOPS, and an earlier draft of this release did. That draft
/// read the recurrence ABOVE the repair budget, on the reasoning that a reproducible defect makes
/// the next cycle futile — which deleted the repair loop's second generation and with it the
/// medic's only route into the mission. `CodePatchLifecycleTests` refused it. A repair GENERATION
/// changes the artifact, so one signature across two generations is the loop working. A recurrence
/// explains a stop; it never causes one.
///
/// AND THE TAXONOMY, CONSULTED. `RecoveryOrchestrator` decided recovery from four booleans and knew
/// nothing about `FailureClass` — twenty-three members and three predicates the rest of the colony
/// classifies failures with. A policy denial and a rate limit reached the same `Retryable` bool and
/// whichever the caller put in it was the answer. `.105` EXTENDS that taxonomy into recovery rather
/// than replacing anything: every existing caller passes no class and keeps its behaviour to the
/// letter, and a supplied class can only narrow a retry into an escalation, never the reverse.
/// </summary>
public class RepeatedFailureTests : IDisposable
{
    private readonly string _dir;

    public RepeatedFailureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-repeat-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"r-{Guid.NewGuid():N}.db"));

    /// <summary>Record one failure_context for a task, the way the failure boundary does.</summary>
    private static void RecordFailure(SqliteMemory memory, string missionId, string taskId, string error)
    {
        var context = new FailureContext
        {
            MissionId = missionId,
            FailedTaskId = taskId,
            FailedRole = "coder",
            TaskType = "code_change",
            FailureClass = "build_failure",
            RawError = error,
            NormalizedError = FailureContext.NormalizeError(error),
        };

        ((IArtifactStore)memory).Put(Artifact.Create(
            schema: ArtifactSchemas.FailureContext, producerRole: "coder",
            missionId: missionId, payload: context.ToJson(), taskId: taskId));
    }

    private static Mission BrokenMission()
    {
        var mission = new Mission { Goal = "Fix the failing build." };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "Repair the build", Description = "Repair the build",
            AssignedAnt = "coder", TaskType = "code_change",
            Critical = true, Status = TaskStatus.Failed,
            FailureReason = "the build failed",
        });
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "Verify", Description = "Verify",
            AssignedAnt = "verifier", TaskType = "verify", Status = TaskStatus.Pending,
        });
        return mission;
    }

    // ---- the detector --------------------------------------------------------------------------

    /// <summary>
    /// TWO DISTINCT TASKS, ONE SIGNATURE — that is a recurrence. ONE task recording the same
    /// context twice is NOT, and the difference is the rule `MedicAnt` learned and this type
    /// inherited: a failing task records a context on every attempt, so counting artifacts would
    /// escalate a single failure on its own retry and turn a bounded repair into no repair at all.
    /// </summary>
    [Fact]
    public void ARecurrenceIsTwoTasks_NotOneTaskTwice()
    {
        using var memory = Memory();
        var mission = BrokenMission();
        memory.SaveMission(mission);

        RecordFailure(memory, mission.Id, "task-a", "error CS1002: ; expected in Foo.cs(12,3)");
        RecordFailure(memory, mission.Id, "task-a", "error CS1002: ; expected in Foo.cs(12,3)");

        Assert.Empty(FailureRecurrence.InMission(memory, mission.Id));

        RecordFailure(memory, mission.Id, "task-b", "error CS1002: ; expected in Foo.cs(12,3)");

        var found = FailureRecurrence.InMission(memory, mission.Id);
        Assert.Single(found);
        Assert.Equal(2, found[0].DistinctTasks);
        Assert.Equal("build_failure", found[0].FailureClass);
        Assert.Contains("build_failure", found[0].Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A DIFFERENT DEFECT IS NOT A RECURRENCE. The signature is semantic, so this is what stops
    /// the detector from reading "two failures happened" as "the same failure happened twice" —
    /// which would stop every mission that fails at two different steps.
    /// </summary>
    [Fact]
    public void TwoDifferentFailures_AreNotARecurrence()
    {
        using var memory = Memory();
        var mission = BrokenMission();
        memory.SaveMission(mission);

        RecordFailure(memory, mission.Id, "task-a", "error CS1002: ; expected in Foo.cs(12,3)");
        RecordFailure(memory, mission.Id, "task-b", "error CS0246: type or namespace 'Bar' not found");

        Assert.Empty(FailureRecurrence.InMission(memory, mission.Id));
    }

    /// <summary>No store means no recurrence, and never an invented one — a bound that fires when
    /// it should not is still a bound that fired wrongly.</summary>
    [Fact]
    public void NoStore_ReportsNoRecurrence()
    {
        Assert.Empty(FailureRecurrence.InMission(null, "m1"));
        // Null, not false: the caller decides what "could not look" is worth, and the two
        // consumers of this record genuinely decide it differently.
        Assert.Null(FailureRecurrence.Recurred(null, "m1", "fsig:whatever"));
    }

    // ---- the controller ------------------------------------------------------------------------

    /// <summary>
    /// A RECURRENCE EXPLAINS A STOP AND NEVER CAUSES ONE, and this test is the corrected form of
    /// one that asserted the opposite.
    ///
    /// The first draft of `.105` checked the recurrence ABOVE the repair budget, reasoning that a
    /// reproducible defect makes the next cycle futile. That reasoning is wrong. A repair
    /// GENERATION changes the artifact — the coder re-proposes, a fresh patch set is materialised,
    /// a fresh tester judges it — so the same signature across two generations is the loop working
    /// rather than spinning. Checking first deleted the second generation and, with it, the medic's
    /// only route into the mission; `CodePatchLifecycleTests` caught both.
    ///
    /// So the recurrence is read only where the mission was stopping anyway. The trajectory is
    /// identical to every release before this one, to the task; the REASON is the whole change.
    /// </summary>
    [Fact]
    public void ARecurrence_NeverPreemptsARepairGeneration()
    {
        var controller = new AdaptiveMissionController();
        var mission = BrokenMission();
        var recurrence = new FailureRecurrence.Recurrence("fsig:abc123", 2, "build_failure");

        var withBudget = new AdaptiveBudget();
        Assert.True(withBudget.CanRepair);

        // WITH budget, a recurrence changes nothing at all: the repair still runs.
        Assert.Equal(AdaptiveAction.Repair, controller.Assess(mission, withBudget).Action);
        Assert.Equal(AdaptiveAction.Repair,
            controller.Assess(mission, withBudget, recurrence: recurrence).Action);
        Assert.Null(controller.Assess(mission, withBudget, recurrence: recurrence).Recurrence);
    }

    /// <summary>
    /// AND AT THE BOUND IT IS THE REASON. Same stop, same task, same grade — a truthful sentence
    /// instead of one that says "the bound is spent, not the problem" when the problem is precisely
    /// that the failure is reproducible.
    /// </summary>
    [Fact]
    public void ARepeatedFailure_ExplainsTheStopAtTheBound()
    {
        var controller = new AdaptiveMissionController();
        var mission = BrokenMission();
        var spent = new AdaptiveBudget(RepairCyclesUsed: AdaptiveBudget.MaxRepairCycles);
        Assert.False(spent.CanRepair);

        // Without a recurrence the mission stops exactly as it always did.
        var plain = controller.Assess(mission, spent);
        Assert.Equal(AdaptiveAction.Escalate, plain.Action);
        Assert.Null(plain.Recurrence);

        var recurrence = new FailureRecurrence.Recurrence("fsig:abc123", 2, "build_failure");
        var explained = controller.Assess(mission, spent, recurrence: recurrence);

        Assert.Equal(AdaptiveAction.Escalate, explained.Action);
        Assert.Same(recurrence, explained.Recurrence);
        Assert.Contains("fsig:abc123", explained.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE STOP IS REPORTED AS ITSELF. Both reasons escalate and both mean the bound is spent;
    /// only one of them also says the failure was reproducible, which is the half an operator needs
    /// to decide whether a retry could ever help.
    /// </summary>
    [Fact]
    public void ARepeatedFailure_StopsWithItsOwnReason()
    {
        Assert.NotEqual(MissionStopReasons.AdaptiveStop, MissionStopReasons.RepeatedFailure);

        // Both need a person, so both escalate — the mission is not graded as a success either way.
        Assert.True(MissionStopReasons.IsEscalation(MissionStopReasons.RepeatedFailure));
        Assert.False(MissionStopReasons.IsPause(MissionStopReasons.RepeatedFailure));

        var evaluation = MissionEvaluator.Evaluate(BrokenMission(),
            MissionStopReasons.RepeatedFailure, 0,
            Anthill.Core.Common.MissionConstraints.None, objectiveVerificationEnabled: false);

        Assert.Equal(MissionOutcome.Escalated, evaluation.OutcomeCode);
        Assert.Equal(MissionStopReasons.RepeatedFailure, evaluation.StopReason);
    }

    /// <summary>
    /// A DECISION ONLY REPORTS A RECURRENCE WHEN IT USED ONE. Typed rather than inferred, because
    /// a mission can escalate for no progress while an unrelated recurrence sits in its store, and
    /// a stop reason derived from that coincidence would be a near-miss of exactly the kind this
    /// repository keeps paying for.
    /// </summary>
    [Fact]
    public void OnlyTheArmThatUsedTheRecurrence_ReportsIt()
    {
        var controller = new AdaptiveMissionController();
        var recurrence = new FailureRecurrence.Recurrence("fsig:abc123", 2, "build_failure");

        // No broken critical task; the mission is simply not moving. Escalates for a different
        // reason, and the recurrence must not be attached to it.
        var stalled = new Mission { Goal = "stalled" };
        stalled.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Id = "t1", Title = "Work", Description = "Work",
            AssignedAnt = "researcher", TaskType = "research", Status = TaskStatus.Pending,
        });

        var fingerprint = AdaptiveMissionController.Fingerprint(stalled);
        var decision = controller.Assess(stalled, new AdaptiveBudget(), fingerprint, null, recurrence);

        Assert.Equal(AdaptiveAction.Escalate, decision.Action);
        Assert.Null(decision.Recurrence);
    }

    // ---- recovery, extended by the taxonomy ----------------------------------------------------

    /// <summary>
    /// A POLICY, SECURITY OR AUTHORIZATION DENIAL IS NEVER RETRIED. The medic has refused to route
    /// around these since the structural-repair release; recovery orchestration did not, so one
    /// denial reached two components and got two answers.
    /// </summary>
    [Theory]
    [InlineData(FailureClass.PolicyDenial)]
    [InlineData(FailureClass.SecurityFailure)]
    [InlineData(FailureClass.AuthorizationFailure)]
    public void RecoveryNeverRetriesADenial(FailureClass denial)
    {
        var decision = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false, Retryable: true, Class: denial));

        Assert.Equal(RecoveryAction.Escalate, decision.Action);
        Assert.True(decision.SuspendsAutonomy);
        Assert.Contains(FailureClassNames.Wire(denial), decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>A defect that already came back is not transient, whatever its class says.</summary>
    [Fact]
    public void RecoveryNeverRetriesARecurrence()
    {
        var fresh = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false, Retryable: true, Class: FailureClass.Timeout));
        Assert.Equal(RecoveryAction.RetryAfterCooldown, fresh.Action);

        var again = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false, Retryable: true, Class: FailureClass.Timeout,
            SignatureSeenBefore: true));
        Assert.Equal(RecoveryAction.Escalate, again.Action);
    }

    /// <summary>An unclassified failure is insufficient evidence, not permission to try again.</summary>
    [Fact]
    public void RecoveryNeverRetriesAnUnclassifiedFailure()
    {
        var decision = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false, Retryable: true, Class: FailureClass.UnknownFailure));

        Assert.Equal(RecoveryAction.Escalate, decision.Action);
        Assert.Contains("UNCLASSIFIED", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND EVERY CALLER THAT SUPPLIES NO CLASS BEHAVES EXACTLY AS BEFORE. This is what makes the
    /// change safe to ship: `ShadowOperator` and the homelab lifecycle bridge pass no class, and
    /// `FailureClass.None` means "this caller has no typed class" — not "unclassified", which is a
    /// different claim with a different answer.
    /// </summary>
    [Fact]
    public void RecoveryWithoutATypedClass_IsUnchanged()
    {
        Assert.Equal(RecoveryAction.Escalate, RecoveryOrchestrator.Decide(
            new RecoveryContext(RollbackAvailable: true, RollbackAttemptedAndFailed: true)).Action);
        Assert.Equal(RecoveryAction.Quarantine, RecoveryOrchestrator.Decide(
            new RecoveryContext(RollbackAvailable: true, SecurityImplication: true)).Action);
        Assert.Equal(RecoveryAction.ImmediateRollback, RecoveryOrchestrator.Decide(
            new RecoveryContext(RollbackAvailable: true)).Action);
        Assert.Equal(RecoveryAction.RetryAfterCooldown, RecoveryOrchestrator.Decide(
            new RecoveryContext(false, Retryable: true)).Action);
        Assert.Equal(RecoveryAction.Failover, RecoveryOrchestrator.Decide(
            new RecoveryContext(false, Retryable: true, PriorAttempts: 2, FailoverAvailable: true)).Action);
        Assert.Equal(RecoveryAction.RestoreFromBackup, RecoveryOrchestrator.Decide(
            new RecoveryContext(false, BackupAvailable: true)).Action);
        Assert.Equal(RecoveryAction.Escalate, RecoveryOrchestrator.Decide(
            new RecoveryContext(false)).Action);
    }

    /// <summary>
    /// A TYPED CLASS NARROWS AND NEVER WIDENS. A caller that said "not retryable" is not overruled
    /// INTO a retry by a class that happens to be transient — widen where a check comes from, never
    /// what a refusal means.
    /// </summary>
    [Fact]
    public void ATypedClass_NeverWidensACallersRefusal()
    {
        var decision = RecoveryOrchestrator.Decide(new RecoveryContext(
            RollbackAvailable: false, Retryable: false, Class: FailureClass.Timeout));

        Assert.NotEqual(RecoveryAction.RetryAfterCooldown, decision.Action);
    }

    /// <summary>
    /// THE MEDIC AND THE CONTROLLER READ ONE RECORD.
    ///
    /// Source-shape, and it is the assertion that stops this release from creating the defect it
    /// closes. The query moved out of `MedicAnt` so the controller could ask the same question one
    /// step earlier; a copy reintroduced into either file would give two layers two answers about
    /// the same rows, which is the shape `MissionContract` exists to end for the operator's goal.
    /// </summary>
    [Fact]
    public void TheMedicAndTheController_ReadOneRecurrenceRecord()
    {
        var medic = File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs"));
        var execution = File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs"));

        Assert.Contains("FailureRecurrence.Recurred(", medic, StringComparison.Ordinal);
        Assert.Contains("FailureRecurrence", execution, StringComparison.Ordinal);

        // And neither re-implements the grouping. `ArtifactSchemas.FailureContext` may be READ in
        // both (the boundary writes it in ExecutionService), but the medic must no longer group
        // signatures itself — that is the query that moved.
        Assert.DoesNotContain("FailureSignature, signature, StringComparison.Ordinal",
            SourceText.CodeOnly(medic), StringComparison.Ordinal);
    }
}
