using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 2 — only completed_verified may reinforce anything.
///
/// The defect: ColonyDirector computed `success = status is "complete" or "partial"`, and that one
/// flag drove objective success EMA, autonomous follow-up creation, objective lifecycle closure,
/// and AUTO-APPLY of patches. A partially-failed mission therefore reinforced learning and could
/// automatically apply code.
///
/// These tests pin the predicate every positive path now routes through.
/// </summary>
public class MissionOutcomeSemanticsTests
{
    /// <summary>A verifier's actual PASS output. Stage 6 requires the verdict, not just completion.</summary>
    private const string PassText = "Verification Passed\nReasoning: output present and checked.";
    private const string FailText = "Verification Failed\nReasoning: one or more tasks failed before verification.";
    private const string NeedsText = "Needs Improvement\nReasoning: not enough completed task output.";

    private static DomainTask T(string ant, TaskStatus status, string type = "general", bool critical = true, string? result = null) =>
        new() { Title = ant + ":" + type, AssignedAnt = ant, TaskType = type, Status = status, Critical = critical, Result = result };

    /// <summary>A verifier task that actually passed — the only shape that verifies a mission.</summary>
    private static DomainTask PassingVerifier() => T("verifier", TaskStatus.Complete, result: PassText);

    private static Dictionary<string, object?> Row(string ant, string status, string type = "general", string? result = null) =>
        new() { ["assigned_ant"] = ant, ["task_type"] = type, ["status"] = status, ["result"] = result };

    /// <summary>
    /// The row APIs take IReadOnlyList, which target-typed `new()` cannot instantiate — it is an
    /// interface. This states the concrete type once.
    /// </summary>
    private static List<Dictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.ToList();

    // ---- the predicate ----------------------------------------------------------------------------

    [Fact]
    public void OnlyCompletedVerified_IsPositiveSuccess()
    {
        Assert.True(MissionOutcome.IsPositiveSuccess(MissionOutcome.CompletedVerified));

        foreach (var outcome in new[]
        {
            MissionOutcome.CompletedUnverified, MissionOutcome.Partial, MissionOutcome.FailedRetryable,
            MissionOutcome.FailedPermanent, MissionOutcome.TimedOut, MissionOutcome.Cancelled,
            MissionOutcome.Queued, MissionOutcome.Running, MissionOutcome.WaitingForApproval,
            MissionOutcome.WaitingForVerification, MissionOutcome.Compensating,
            MissionOutcome.Compensated, MissionOutcome.RollbackFailed,
        })
            Assert.False(MissionOutcome.IsPositiveSuccess(outcome), $"{outcome} must not count as success");
    }

    /// <summary>An unknown or future outcome is non-reinforcing until someone decides otherwise.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("complete")]              // the OLD status text must not sneak through
    [InlineData("COMPLETED_VERIFIED")]    // exact match only
    [InlineData("something_new")]
    public void UnknownOutcomes_AreNotSuccess(string? outcome) =>
        Assert.False(MissionOutcome.IsPositiveSuccess(outcome));

    // ---- resolution -------------------------------------------------------------------------------

    [Fact]
    public void CompleteWithoutVerification_ResolvesToCompletedUnverified_NotSuccess()
    {
        var outcome = MissionOutcome.Resolve(MissionStatus.Complete, verificationSatisfied: false);
        Assert.Equal(MissionOutcome.CompletedUnverified, outcome);
        Assert.False(MissionOutcome.IsPositiveSuccess(outcome));
    }

    [Fact]
    public void CompleteWithVerification_ResolvesToCompletedVerified()
    {
        var outcome = MissionOutcome.Resolve(MissionStatus.Complete, verificationSatisfied: true);
        Assert.Equal(MissionOutcome.CompletedVerified, outcome);
        Assert.True(MissionOutcome.IsPositiveSuccess(outcome));
    }

    /// <summary>Partial stays partial even when a verifier happened to run and pass.</summary>
    [Fact]
    public void Partial_IsNeverPromotedByVerification()
    {
        Assert.Equal(MissionOutcome.Partial, MissionOutcome.Resolve(MissionStatus.Partial, true));
        Assert.Equal(MissionOutcome.Partial, MissionOutcome.Resolve(MissionStatus.Partial, false));
        Assert.False(MissionOutcome.IsPositiveSuccess(MissionOutcome.Resolve(MissionStatus.Partial, true)));
    }

    [Fact]
    public void StatusTextResolution_MatchesTheObjectPath_AndFailsClosed()
    {
        Assert.Equal(MissionOutcome.CompletedVerified, MissionOutcome.ResolveFromStatusText("complete", true));
        Assert.Equal(MissionOutcome.CompletedUnverified, MissionOutcome.ResolveFromStatusText("complete", false));
        Assert.Equal(MissionOutcome.Partial, MissionOutcome.ResolveFromStatusText("partial", true));
        Assert.Equal(MissionOutcome.Cancelled, MissionOutcome.ResolveFromStatusText("cancelled", true));
        // Anything unrecognised — including null — fails closed.
        Assert.Equal(MissionOutcome.FailedPermanent, MissionOutcome.ResolveFromStatusText(null, true));
        Assert.Equal(MissionOutcome.FailedPermanent, MissionOutcome.ResolveFromStatusText("weird", true));
    }

    // ---- the interim verification gate ------------------------------------------------------------

    [Fact]
    public void AMissionThatRanNoVerificationStep_IsNotVerified()
    {
        var tasks = new List<DomainTask> { T("researcher", TaskStatus.Complete), T("coder", TaskStatus.Complete) };
        Assert.False(MissionVerification.IsSatisfied(tasks));
        Assert.Contains("no verification step", MissionVerification.Explain(tasks));
    }

    [Fact]
    public void ACompletedVerifier_SatisfiesTheInterimGate()
    {
        var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), PassingVerifier() };
        Assert.True(MissionVerification.IsSatisfied(tasks));
    }

    // ---- Stage 6: completion is necessary but not sufficient ---------------------------------------

    /// <summary>
    /// The defect this stage closes. A verifier that ran to completion and said the mission FAILED
    /// used to satisfy the gate, because the gate only asked whether the task finished. That made
    /// the mission completed_verified: positive learning, and the auto-apply precondition met, on
    /// the strength of a verdict that said the opposite.
    /// </summary>
    [Fact]
    public void AVerifierThatCompletedButReportedFailure_DoesNotVerifyTheMission()
    {
        var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Complete, result: FailText) };
        Assert.False(MissionVerification.IsSatisfied(tasks));
        Assert.Contains("Verification Failed", MissionVerification.Explain(tasks));
    }

    [Fact]
    public void NeedsImprovement_IsNotAPass()
    {
        var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Complete, result: NeedsText) };
        Assert.False(MissionVerification.IsSatisfied(tasks));
    }

    [Fact]
    public void AVerifierWithNoRecordedVerdict_FailsClosed()
    {
        var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), T("verifier", TaskStatus.Complete) };
        Assert.False(MissionVerification.IsSatisfied(tasks));
    }

    /// <summary>
    /// Tester and soldier are verification steps but do not speak the verifier's verdict
    /// vocabulary. Requiring a verdict from them would parse Unknown and fail every mission they
    /// touch, so their completion remains the signal.
    /// </summary>
    [Fact]
    public void NonVerifierVerificationRoles_AreNotRequiredToEmitAVerdict()
    {
        var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), T("tester", TaskStatus.Complete, result: "3 checks, exit_code=0") };
        Assert.True(MissionVerification.IsSatisfied(tasks));
    }

    [Theory]
    [InlineData("verifier")]
    [InlineData("tester")]
    [InlineData("soldier")]
    public void VerificationRoles_AreRecognised(string role) =>
        Assert.True(MissionVerification.IsVerificationTask(T(role, TaskStatus.Complete)));

    [Fact]
    public void AVerifierThatDidNotComplete_DoesNotSatisfyTheGate()
    {
        foreach (var status in new[] { TaskStatus.Failed, TaskStatus.Skipped, TaskStatus.Blocked })
        {
            var tasks = new List<DomainTask> { T("coder", TaskStatus.Complete), T("verifier", status) };
            Assert.False(MissionVerification.IsSatisfied(tasks), $"verifier {status} must not verify");
        }
    }

    [Fact]
    public void ACriticalFailure_DisqualifiesEvenWithAPassingVerifier()
    {
        var tasks = new List<DomainTask>
        {
            T("coder", TaskStatus.Failed, critical: true),
            PassingVerifier(),
        };
        Assert.False(MissionVerification.IsSatisfied(tasks));
        Assert.Contains("critical task failed", MissionVerification.Explain(tasks));
    }

    // ---- the row path the Director actually uses --------------------------------------------------

    [Fact]
    public void RowGate_RequiresACompletedVerificationRow()
    {
        Assert.False(MissionVerification.IsSatisfiedFromRows(Rows()));
        Assert.False(MissionVerification.IsSatisfiedFromRows(Rows(Row("researcher", "complete"))));
        Assert.True(MissionVerification.IsSatisfiedFromRows(Rows(Row("coder", "complete"), Row("verifier", "complete", result: PassText))));
        // Completed, but the verdict says otherwise — the Director must not read this as success.
        Assert.False(MissionVerification.IsSatisfiedFromRows(Rows(Row("coder", "complete"), Row("verifier", "complete", result: FailText))));
        Assert.False(MissionVerification.IsSatisfiedFromRows(Rows(Row("coder", "complete"), Row("verifier", "complete"))));
    }

    /// <summary>
    /// The tasks table does not persist a `critical` column, so the row gate cannot tell critical
    /// from non-critical failure and disqualifies on ANY failure. Stricter is the safe direction
    /// for a gate that decides whether work may auto-apply.
    /// </summary>
    [Fact]
    public void RowGate_DisqualifiesOnAnyFailure_BecauseCriticalityIsNotPersisted()
    {
        var rows = Rows(Row("coder", "failed"), Row("verifier", "complete"));
        Assert.False(MissionVerification.IsSatisfiedFromRows(rows));
        Assert.Contains("a task failed", MissionVerification.ExplainRows(rows));
    }

    [Fact]
    public void RowGate_RecognisesVerificationByTaskType_NotOnlyByRole()
    {
        var rows = Rows(Row("builder", "complete", "verify"));
        Assert.True(MissionVerification.IsSatisfiedFromRows(rows));
    }

    // ---- the regression this stage exists to prevent ----------------------------------------------

    /// <summary>
    /// The exact shape of the original defect: a mission that finished partially. Under the old
    /// rule this was success and reached objective EMA, follow-up creation, lifecycle closure and
    /// auto-apply. It must now reach none of them.
    /// </summary>
    [Fact]
    public void APartialMission_CannotReinforceAnything()
    {
        var outcome = MissionOutcome.ResolveFromStatusText("partial", verificationSatisfied: true);

        Assert.Equal(MissionOutcome.Partial, outcome);
        Assert.False(MissionOutcome.IsPositiveSuccess(outcome),
            "a partial mission must not drive EMA, follow-ups, auto-apply, pheromones, skill promotion, or objective completion");
    }

    /// <summary>
    /// The subtler regression: a mission whose tasks all completed but which verified nothing.
    /// Structural completion alone is not proof.
    /// </summary>
    [Fact]
    public void AFullyCompleteButUnverifiedMission_CannotReinforceAnything()
    {
        var rows = Rows(Row("researcher", "complete"), Row("builder", "complete"));
        var outcome = MissionOutcome.ResolveFromStatusText("complete", MissionVerification.IsSatisfiedFromRows(rows));

        Assert.Equal(MissionOutcome.CompletedUnverified, outcome);
        Assert.False(MissionOutcome.IsPositiveSuccess(outcome));
    }
}
