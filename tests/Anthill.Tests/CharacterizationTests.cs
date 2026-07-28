using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Homelab.Actions;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.0.0 baseline lock — CHARACTERIZATION tests.
///
/// These are a different kind of test from the rest of the suite. They do not assert that behaviour
/// is *correct*; they assert that it is *what it is today*, at v3.0.0, so that v3.1.0's runtime
/// decomposition can be proven behaviour-preserving instead of asserted to be. A refactor that
/// changes an answer here has changed the system, whether or not that was intended.
///
/// The five surfaces the roadmap names: mission outcome, patch/approval, autonomy, learning, and
/// action lifecycle. Each is pinned at its decision boundary rather than through the full runtime,
/// so a failure names the rule that moved rather than reporting that something, somewhere, differs.
///
/// If a V3 phase deliberately changes one of these, the correct action is to update the test IN THE
/// SAME COMMIT with the reason in its comment — never to delete it.
/// </summary>
[Collection("specialist-gates")]
public class CharacterizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_char_" + Guid.NewGuid().ToString("N"));
    private readonly bool _objVerify;
    private readonly bool _handoffs;

    public CharacterizationTests()
    {
        AnthillRuntime.Initialize();
        _objVerify = AnthillRuntime.EnableObjectiveVerification;
        _handoffs = AnthillRuntime.EnableHandoffIngestion;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnableObjectiveVerification = _objVerify;
        AnthillRuntime.EnableHandoffIngestion = _handoffs;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteMemory Memory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    private static DomainTask Verifier(bool pass) => new()
    {
        Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", Status = TaskStatus.Complete,
        Result = pass ? "Verification Passed\nReasoning: checked." : "Verification Failed\nReasoning: missing.",
    };

    private static DomainTask Work(TaskStatus status = TaskStatus.Complete, bool critical = true) => new()
    {
        Title = "Work", AssignedAnt = "researcher", TaskType = "research",
        Status = status, Critical = critical, Result = "done",
    };

    private static Mission MissionWith(string goal, MissionStatus status, params DomainTask[] tasks)
    {
        var m = new Mission { Goal = goal, Status = status };
        m.Tasks.AddRange(tasks);
        return m;
    }

    // ---- 1. MISSION OUTCOME -------------------------------------------------------------------------

    /// <summary>
    /// The complete v3.0.0 outcome truth table. Every V3 phase that touches mission evaluation must
    /// reproduce this table exactly, or state in the same commit which row it changes and why.
    /// </summary>
    [Theory]
    // structural, stop reason, verifier, deliverable-relevant goal, patches -> outcome
    [InlineData(MissionStatus.Complete, null, true, "research a topic", 0, MissionOutcome.CompletedVerified)]
    [InlineData(MissionStatus.Complete, null, false, "research a topic", 0, MissionOutcome.CompletedUnverified)]
    [InlineData(MissionStatus.Partial, null, true, "research a topic", 0, MissionOutcome.Partial)]
    [InlineData(MissionStatus.Failed, null, true, "research a topic", 0, MissionOutcome.FailedPermanent)]
    [InlineData(MissionStatus.Complete, "mission_cancelled", true, "research a topic", 0, MissionOutcome.Cancelled)]
    [InlineData(MissionStatus.Complete, "mission_timeout", true, "research a topic", 0, MissionOutcome.TimedOut)]
    [InlineData(MissionStatus.Complete, "adaptive_stop", true, "research a topic", 0, MissionOutcome.Escalated)]
    // deliverable layer active: a file-change goal with no patch cannot be verified
    [InlineData(MissionStatus.Complete, null, true, "add a changelog entry", 0, MissionOutcome.CompletedUnverified)]
    [InlineData(MissionStatus.Complete, null, true, "add a changelog entry", 1, MissionOutcome.CompletedVerified)]
    public void MissionOutcome_TruthTable(MissionStatus status, string? stop, bool verifierPasses,
        string goal, int patches, string expected)
    {
        AnthillRuntime.EnableObjectiveVerification = true;
        var mission = MissionWith(goal, status, Work(), Verifier(verifierPasses));
        Assert.Equal(expected, MissionEvaluator.Evaluate(mission, stop, patches).OutcomeCode);
    }

    /// <summary>Exactly one outcome code is positive. This is the single most load-bearing rule in
    /// the system: every learning, credit, and auto-apply path keys off it.</summary>
    [Fact]
    public void OnlyCompletedVerified_IsPositive()
    {
        foreach (var code in new[]
                 {
                     MissionOutcome.CompletedUnverified, MissionOutcome.Partial, MissionOutcome.Cancelled,
                     MissionOutcome.TimedOut, MissionOutcome.Escalated, MissionOutcome.FailedPermanent,
                     MissionOutcome.FailedRetryable,
                 })
            Assert.False(MissionOutcome.IsPositiveSuccess(code), $"'{code}' must not be positive");

        Assert.True(MissionOutcome.IsPositiveSuccess(MissionOutcome.CompletedVerified));
    }

    /// <summary>A mission with no verifier at all is "not run" — distinct from failed for the
    /// operator, and equally not a pass.</summary>
    [Fact]
    public void AMissionWithNoVerifier_IsNotRun_AndNotAPass()
    {
        var e = MissionEvaluator.Evaluate(MissionWith("g", MissionStatus.Complete, Work()), null, 0);
        Assert.Equal(MissionEvaluation.Verification.NotRun, e.VerificationStatus);
        Assert.False(e.IsPositive);
    }

    // ---- 2. VERIFICATION AND EVIDENCE ---------------------------------------------------------------

    /// <summary>The verdict vocabulary, pinned. A verifier's prose decides the gate, so the exact
    /// phrases it must produce are behaviour, not implementation detail.</summary>
    [Theory]
    [InlineData("Verification Passed\nReasoning: ok.", true)]
    [InlineData("Verification Failed\nReasoning: no.", false)]
    [InlineData("Needs Improvement — close but not there.", false)]
    [InlineData("The work looks good to me.", false)]              // no recognised verdict = unknown
    [InlineData("Verification Passed ... Verification Failed", false)] // ambiguous = unknown
    [InlineData("", false)]
    public void VerdictParsing_IsPinned(string text, bool isPass) =>
        Assert.Equal(isPass, VerificationVerdict.TextIsPass(text));

    /// <summary>Promotable requires deterministic evidence intrinsically (v2.26.0). A semantic-only
    /// bundle is never promotable no matter how many required verifiers passed.</summary>
    [Fact]
    public void Promotable_RequiresDeterministicEvidence()
    {
        VerificationBundle Bundle(bool deterministic) => new()
        {
            TaskType = "t", Required = { "v" },
            Results = { new VerificationResult("v", true, deterministic, "s", Array.Empty<VerificationEvidence>()) },
        };
        Assert.False(Bundle(deterministic: false).Promotable);
        Assert.True(Bundle(deterministic: true).Promotable);
    }

    // ---- 3. LEARNING --------------------------------------------------------------------------------

    /// <summary>
    /// The three-way skill outcome split, pinned: promotable evidence advances standing, a verified
    /// mission with semantic-only evidence is NEUTRAL (no movement either way), and no evidence is
    /// a failure. v3.7.0 touches this; it must reproduce or explicitly restate it.
    /// </summary>
    [Fact]
    public void SkillOutcomes_SplitThreeWays()
    {
        VerificationBundle Bundle(bool deterministic, bool passed = true) => new()
        {
            TaskType = "t", Required = { "v" },
            Results = { new VerificationResult("v", passed, deterministic, "s", Array.Empty<VerificationEvidence>()) },
        };

        var registry = new SkillRegistry();
        registry.RegisterCandidate("s", "a procedure");

        registry.RecordOutcome("s", Bundle(deterministic: true));           // promotable -> success
        Assert.Equal(1, registry.Get("s")!.SuccessCount);
        Assert.Equal(0, registry.Get("s")!.FailureCount);

        registry.RecordOutcome("s", Bundle(deterministic: false));          // verified, semantic -> neutral
        Assert.Equal(1, registry.Get("s")!.SuccessCount);
        Assert.Equal(0, registry.Get("s")!.FailureCount);

        registry.RecordOutcome("s", null);                                   // no evidence -> failure
        Assert.Equal(1, registry.Get("s")!.SuccessCount);
        Assert.Equal(1, registry.Get("s")!.FailureCount);
    }

    /// <summary>Pheromone signal categories decide what may steer planning. Only procedural and
    /// routing categories are learning-bearing; telemetry and heuristics are not.</summary>
    [Theory]
    [InlineData("planner_pattern", "procedural_learning")]
    [InlineData("worker_pattern", "procedural_learning")]
    [InlineData("ant", "procedural_learning")]
    [InlineData("model_route", "routing_preference")]
    [InlineData("source_domain", "quality_signal")]
    [InlineData("tool", "reliability_signal")]
    [InlineData("something_unclassified", "operational_telemetry")]
    public void PheromoneSignalCategories_ArePinned(string trailType, string expected) =>
        Assert.Equal(expected, SqliteMemory.SignalCategoryFor(trailType));

    // ---- 4. AUTONOMY -------------------------------------------------------------------------------

    /// <summary>The canonical evaluation persists and reloads identically. v3.1.0 moves who computes
    /// it; the persisted answer must not move with it.</summary>
    [Fact]
    public void TheCanonicalEvaluation_RoundTrips()
    {
        var mem = Memory();
        var mission = MissionWith("research a topic", MissionStatus.Complete, Work(), Verifier(true));
        mem.SaveMission(mission);

        var live = MissionEvaluator.Evaluate(mission, null, 0);
        mem.SaveMissionEvaluation(live);
        var restored = mem.LoadMissionEvaluation(mission.Id)!;

        Assert.Equal(live.OutcomeCode, restored.OutcomeCode);
        Assert.Equal(live.VerificationStatus, restored.VerificationStatus);
        Assert.Equal(live.DeliverableStatus, restored.DeliverableStatus);
        Assert.Equal(live.IsPositive, restored.IsPositive);
    }

    /// <summary>A mission row with no persisted evaluation is LEGACY and is never retroactively
    /// treated as verified.</summary>
    [Fact]
    public void ALegacyMissionRow_HasNoEvaluation()
    {
        var mem = Memory();
        var mission = MissionWith("g", MissionStatus.Complete, Work(), Verifier(true));
        mem.SaveMission(mission);
        Assert.Null(mem.LoadMissionEvaluation(mission.Id));
    }

    /// <summary>Constraint parsing decides whether a mission may propose changes at all. Pinned
    /// because v3.1.0 moves it to a single intake-time resolution.</summary>
    [Theory]
    [InlineData("add a changelog entry", false)]
    [InlineData("review the code and do not modify any files", true)]
    [InlineData("verify the build without making changes", true)]
    public void ConstraintParsing_IsPinned(string goal, bool blocksPatches) =>
        Assert.Equal(blocksPatches, Anthill.Core.Common.MissionConstraints.Parse(goal).BlocksPatches);

    // ---- 5. ACTION LIFECYCLE ------------------------------------------------------------------------

    /// <summary>The legacy action states map onto the canonical lifecycle exactly this way, and an
    /// unknown state is terminal — nothing transitions out of it by accident.</summary>
    [Theory]
    [InlineData("pending", "WaitingForApproval")]
    [InlineData("approved", "Approved")]
    [InlineData("rejected", "Failed")]
    [InlineData("superseded", "Failed")]
    [InlineData("executed", "CompletedVerified")]
    [InlineData("nonsense", "Escalated")]
    public void ActionStateMapping_IsPinned(string legacy, string expected) =>
        Assert.Equal(expected, ActionLifecycleBridge.ToCanonical(legacy).ToString());

    /// <summary>No action state reaches Executing without passing through approval.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("superseded")]
    [InlineData("executed")]
    [InlineData("nonsense")]
    public void NoActionState_ReachesExecutingWithoutApproval(string legacy) =>
        Assert.False(ActionLifecycleBridge.Guard(legacy, Anthill.Core.SafeAction.ActionState.Executing).Ok);

    // ---- 6. TASK OUTCOME MAPPING --------------------------------------------------------------------

    /// <summary>An ant's declared status code maps to a task action exactly this way. v3.2.0's
    /// universal protocol must preserve the mapping while widening who produces it.</summary>
    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("succeeded_with_warnings", true)]
    [InlineData("failed_retryable", false)]
    [InlineData("failed_permanent", false)]
    [InlineData("blocked", false)]
    [InlineData("skipped", false)]
    [InlineData("cancelled", false)]
    [InlineData("timed_out", false)]
    [InlineData("unrecognised_status", false)]
    public void AntStatusCode_MapsToCompletion(string statusCode, bool completes)
    {
        var result = AntExecutionResult.Succeeded("s") with { StatusCode = statusCode };
        Assert.Equal(completes, TaskOutcomeMapper.Map(result).Action == TaskOutcomeAction.Complete);
    }
}
