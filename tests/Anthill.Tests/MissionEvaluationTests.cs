using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.26.0 pre-V3 hardening — mission truth. ONE evaluation, computed once, persisted before
/// completion is published, consumed by every positive path. These tests pin the rules the
/// evaluator owns and the persistence that makes restored state agree with live state.
/// </summary>
[Collection("specialist-gates")]
public class MissionEvaluationTests : IDisposable
{

    /// <summary>
    /// v3.1.0 (ADR-001/ADR-002): the evaluator is a pure function — it takes the mission's
    /// constraints and the run's verification policy explicitly instead of reading a static and
    /// re-parsing the goal. These tests resolve both exactly as mission intake does, so what they
    /// pin is still the production rule and not a test-only shortcut.
    /// </summary>
    private static MissionEvaluation Evaluate(Mission mission, string? stopReason, int patchProposalCount) =>
        MissionEvaluator.Evaluate(mission, stopReason, patchProposalCount,
            Anthill.Core.Common.MissionConstraints.Parse(mission.Goal),
            AnthillRuntime.EnableObjectiveVerification);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_eval_" + Guid.NewGuid().ToString("N"));
    private readonly bool _objVerify;

    public MissionEvaluationTests()
    {
        AnthillRuntime.Initialize();
        _objVerify = AnthillRuntime.EnableObjectiveVerification;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnableObjectiveVerification = _objVerify;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteMemory Memory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    private static DomainTask Verifier(bool pass) => new()
    {
        Title = "Verify", AssignedAnt = "verifier", TaskType = "verification",
        Status = TaskStatus.Complete,
        // The REAL verdict vocabulary (VerificationVerdict.Phrases) — an invented phrasing parses
        // as Unknown and fails closed, which the first run of this suite demonstrated nicely.
        Result = pass ? "Verification Passed\nReasoning: output present and checked."
                      : "Verification Failed\nReasoning: required work missing.",
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

    // ---- the outcome rules -------------------------------------------------------------------------

    [Fact]
    public void ACompleteVerifiedMission_IsCompletedVerified()
    {
        var m = MissionWith("research topic", MissionStatus.Complete, Work(), Verifier(pass: true));
        var e = Evaluate(m, stopReason: null, patchProposalCount: 0);
        Assert.Equal(MissionOutcome.CompletedVerified, e.OutcomeCode);
        Assert.True(e.IsPositive);
    }

    /// <summary>Verifier PASS alone is insufficient: a file-change goal that produced no patch
    /// proposal is completed_unverified even though the verifier honestly passed.</summary>
    [Fact]
    public void AFileChangeGoalWithNoPatch_CannotBeVerified_EvenWithAVerifierPass()
    {
        AnthillRuntime.EnableObjectiveVerification = true;
        var m = MissionWith("add a changelog entry for the release", MissionStatus.Complete,
            Work(), Verifier(pass: true));

        var e = Evaluate(m, stopReason: null, patchProposalCount: 0);
        Assert.Equal(MissionEvaluation.Verification.Passed, e.VerificationStatus);   // the PASS is real
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, e.DeliverableStatus); // the work is not
        Assert.Equal(MissionOutcome.CompletedUnverified, e.OutcomeCode);
        Assert.False(e.IsPositive);

        // With the deliverable produced, the same mission verifies.
        var delivered = Evaluate(m, stopReason: null, patchProposalCount: 1);
        Assert.Equal(MissionOutcome.CompletedVerified, delivered.OutcomeCode);
    }

    [Theory]
    [InlineData("mission_cancelled", MissionOutcome.Cancelled)]
    [InlineData("mission_timeout", MissionOutcome.TimedOut)]
    [InlineData("adaptive_stop", MissionOutcome.Escalated)]
    public void AnInterruptedMission_IsNeverAnyFlavourOfCompleted(string stop, string expected)
    {
        // Even with every task complete and a passing verifier: interruption overrides.
        var m = MissionWith("g", MissionStatus.Complete, Work(), Verifier(pass: true));
        var e = Evaluate(m, stop, patchProposalCount: 0);
        Assert.Equal(expected, e.OutcomeCode);
        Assert.False(e.IsPositive);
    }

    [Fact]
    public void PartialIsNotSuccess_AndFailedIsNotSuccess()
    {
        var partial = Evaluate(
            MissionWith("g", MissionStatus.Partial, Work(), Work(TaskStatus.Skipped, critical: false)), null, 0);
        Assert.Equal(MissionOutcome.Partial, partial.OutcomeCode);
        Assert.False(partial.IsPositive);

        var failed = Evaluate(
            MissionWith("g", MissionStatus.Failed, Work(TaskStatus.Failed)), null, 0);
        Assert.Equal(MissionOutcome.FailedPermanent, failed.OutcomeCode);
        Assert.False(failed.IsPositive);
    }

    [Fact]
    public void NoVerifierTask_MeansNotRun_WhichIsNotAPass()
    {
        var m = MissionWith("g", MissionStatus.Complete, Work());
        var e = Evaluate(m, null, 0);
        Assert.Equal(MissionEvaluation.Verification.NotRun, e.VerificationStatus);
        Assert.Equal(MissionOutcome.CompletedUnverified, e.OutcomeCode);
    }

    /// <summary>A disabled deliverable layer reads "not_checked" — visible, and distinct from a
    /// pass — and keeps pre-v2.26 behaviour (the interim gate alone).</summary>
    [Fact]
    public void ADisabledDeliverableLayer_IsVisiblyNotChecked_NeverSilentlyPassed()
    {
        AnthillRuntime.EnableObjectiveVerification = false;
        var e = Evaluate(
            MissionWith("add a changelog entry", MissionStatus.Complete, Work(), Verifier(true)), null, 0);
        Assert.Equal(MissionEvaluation.Deliverable.NotChecked, e.DeliverableStatus);
        Assert.Equal(MissionOutcome.CompletedVerified, e.OutcomeCode);   // pre-v2.26 behaviour preserved
    }

    // ---- persistence: restored state answers what live state answered ------------------------------

    [Fact]
    public void TheEvaluation_PersistsAndReloadsUnchanged()
    {
        var mem = Memory();
        var m = MissionWith("research topic", MissionStatus.Complete, Work(), Verifier(true));
        mem.SaveMission(m);

        var live = Evaluate(m, null, 0);
        mem.SaveMissionEvaluation(live);

        var restored = mem.LoadMissionEvaluation(m.Id)!;
        Assert.Equal(live.OutcomeCode, restored.OutcomeCode);
        Assert.Equal(live.VerificationStatus, restored.VerificationStatus);
        Assert.Equal(live.DeliverableStatus, restored.DeliverableStatus);
        Assert.Equal(live.StopReason, restored.StopReason);
        Assert.Equal(MissionEvaluator.Version, restored.EvaluatorVersion);
        Assert.Equal(live.IsPositive, restored.IsPositive);
    }

    /// <summary>A row that predates persisted evaluation loads as null — LEGACY — and callers must
    /// treat legacy as never-verified rather than re-deriving a promotion.</summary>
    [Fact]
    public void ALegacyRow_HasNoEvaluation_AndIsNeverRetroactivelyVerified()
    {
        var mem = Memory();
        var m = MissionWith("old mission", MissionStatus.Complete, Work(), Verifier(true));
        mem.SaveMission(m);   // saved WITHOUT an evaluation, as every pre-v2.26 mission was

        Assert.Null(mem.LoadMissionEvaluation(m.Id));
    }

    // ---- the consumers ------------------------------------------------------------------------------

    /// <summary>
    /// Every downstream positive path consumes the persisted evaluation. Wiring pinned at the
    /// source (the behaviour rules are pinned above; this pins that no consumer re-derives):
    /// the Director reads LoadMissionEvaluation with a never-positive legacy fallback; auto-apply
    /// refuses without a persisted positive evaluation; skill credit and pheromone learning take
    /// the evaluation's answer; candidate routes register at finalization from the evaluation
    /// (the old per-task path evaluated a RUNNING mission and never registered anything).
    /// </summary>
    [Fact]
    public void EveryPositivePath_ConsumesThePersistedEvaluation()
    {
        string CodeOnly(string src) => string.Join("\n", src.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        var director = CodeOnly(Read("src", "Anthill.Api", "ColonyDirector.cs"));
        Assert.Contains("LoadMissionEvaluation", director);
        Assert.Contains("return (legacyOutcome, score, false)", director);   // legacy is never positive

        // v0.3.8.94: auto-apply consumes the evaluation THROUGH the promotion gate — its private
        // LoadMissionEvaluation copy folded into PatchPromotionGate's Automation arm, which is the
        // same read for every unattended lane rather than one per caller. The chain is pinned in
        // both links: the runner consults the gate as Automation, and the gate's Automation arm is
        // where the persisted evaluation is consumed.
        var autoApply = CodeOnly(Read("src", "Anthill.Api", "AutoApplyRunner.cs"));
        Assert.Contains("PatchPromotionGate.Evaluate", autoApply);
        Assert.Contains("PromotionActor.Automation", autoApply);
        var gate = CodeOnly(Read("src", "Anthill.Core", "Verification", "PatchPromotionGate.cs"));
        Assert.Contains("LoadMissionEvaluation", gate);
        Assert.Contains("evaluation.IsPositive", gate);

        var queen = CodeOnly(Read("src", "Anthill.Core", "Orchestration", "Queen.cs"));
        Assert.Contains("SaveMissionEvaluation(evaluation)", queen);
        // v3.1.0: pheromones, credit and route registration moved behind ILearningRecorder. The
        // Queen still consumes the ONE evaluation and hands it to learning; the recorder is where
        // each consumer lives. Both are checked so neither half can quietly stop happening.
        Assert.Contains("_learning.Record(mission, context, evaluation)", queen);

        var learning = CodeOnly(Read("src", "Anthill.Core", "Orchestration", "LearningRecorder.cs"));
        Assert.Contains("UpdateMissionPheromones(mission, evaluation.OutcomeCode)", learning);
        Assert.Contains("RegisterProceduralRoutes(mission, evaluation)", learning);
        Assert.Contains("CreditSkills(mission, context, evaluation)", learning);
        Assert.Contains("evaluation.IsPositive", learning);   // never re-derived
    }

    /// <summary>Criticality persists, so row-based evaluation can never disagree with the live
    /// mission object about which failures fail the mission.</summary>
    [Fact]
    public void TaskCriticality_AndCancellationReason_Persist()
    {
        var mem = Memory();
        var m = new Mission { Goal = "g" };
        mem.SaveMission(m);
        mem.SaveTask(m.Id, new DomainTask
        {
            Title = "non-critical", AssignedAnt = "researcher", Critical = false,
            CancellationReason = "mission timed out during drain",
        });

        var row = mem.GetTasksForMission(m.Id).Single();
        Assert.Equal(0L, Convert.ToInt64(row["critical"]));
        Assert.Equal("mission timed out during drain", row["cancellation_reason"]?.ToString());
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
