using Anthill.Core.Agents;
using Anthill.Core.Contracts;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 3 — the structured result decides the task outcome.
///
/// Queen.RunSingleTask previously called MarkComplete on every result that did not throw, never
/// reading Success, StatusCode, Failure, Evidence or Handoffs. An ant reporting failed_retryable
/// was therefore recorded as a completed task.
///
/// The brief requires every status code proven before any specialist is migrated, because the
/// specialists are the ants that actually produce failures and handoffs.
/// </summary>
public class TaskOutcomeMapperTests
{
    private static AntExecutionResult WithStatus(string status) =>
        new() { Success = false, StatusCode = status, Summary = "s" };

    // ---- the governing rule -----------------------------------------------------------------------

    /// <summary>Only an explicit success completes a task. This is the whole point of the stage.</summary>
    [Theory]
    [InlineData("succeeded")]
    [InlineData("succeeded_with_warnings")]
    public void OnlyExplicitSuccess_CompletesATask(string status)
    {
        Assert.Equal(TaskOutcomeAction.Complete, TaskOutcomeMapper.Map(WithStatus(status)).Action);
        Assert.True(TaskOutcomeMapper.IsCompleting(WithStatus(status)));
    }

    [Theory]
    [InlineData("failed_retryable")]
    [InlineData("failed_permanent")]
    [InlineData("blocked")]
    [InlineData("skipped")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    public void NoOtherStatus_EverCompletesATask(string status)
    {
        Assert.NotEqual(TaskOutcomeAction.Complete, TaskOutcomeMapper.Map(WithStatus(status)).Action);
        Assert.False(TaskOutcomeMapper.IsCompleting(WithStatus(status)));
    }

    /// <summary>
    /// The exact regression: a specialist reporting a retryable failure. Before v2.19.0 this was
    /// recorded as a completed task.
    /// </summary>
    [Fact]
    public void ARetryableFailure_IsNotCompleted_AndIsRetryable()
    {
        var result = AntExecutionResult.Failed(FailureClass.TransientProviderFailure, "provider down");
        var decision = TaskOutcomeMapper.Map(result);

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.True(decision.Retryable);
        Assert.Equal("transient_provider_failure", decision.FailureType);
        Assert.Equal("provider down", decision.Reason);
    }

    [Fact]
    public void APermanentFailure_IsNotRetried()
    {
        var result = AntExecutionResult.Failed(FailureClass.VerificationFailure, "checks failed");
        var decision = TaskOutcomeMapper.Map(result);

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("verification_failure", decision.FailureType);
    }

    /// <summary>
    /// A policy refusal must never be retried — retrying cannot change an authorization answer,
    /// and no later handoff may widen authority to get around it.
    /// </summary>
    [Fact]
    public void ABlockedResult_IsPermanent_AndTypedAsBlocked()
    {
        var decision = TaskOutcomeMapper.Map(AntExecutionResult.Blocked("capability not granted: apply_patch"));

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("blocked", decision.FailureType);
        Assert.Contains("apply_patch", decision.Reason);
    }

    [Fact]
    public void ASkippedResult_IsSkipped_NotFailedAndNotCompleted()
    {
        var decision = TaskOutcomeMapper.Map(AntExecutionResult.Skipped("source budget exhausted"));

        Assert.Equal(TaskOutcomeAction.Skip, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("skipped", decision.FailureType);
        Assert.Equal("source budget exhausted", decision.Reason);
    }

    [Fact]
    public void ACancelledResult_IsSkipped_WithACancelledReasonType()
    {
        var decision = TaskOutcomeMapper.Map(WithStatus("cancelled"));
        Assert.Equal(TaskOutcomeAction.Skip, decision.Action);
        Assert.Equal("cancelled", decision.FailureType);
    }

    [Fact]
    public void ATimeout_IsRetryable_BecauseTheNextAttemptMayBeQuicker()
    {
        var decision = TaskOutcomeMapper.Map(WithStatus("timed_out"));
        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.True(decision.Retryable);
        Assert.Equal("timeout", decision.FailureType);
    }

    // ---- failing closed ---------------------------------------------------------------------------

    /// <summary>
    /// An unrecognised code must not pass through as a success. A future status added without
    /// updating this mapper degrades to a permanent failure, which is visible, rather than to a
    /// silent completion, which is not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("SUCCEEDED")]
    [InlineData("done")]
    [InlineData("partially_succeeded")]
    public void AnUnrecognisedStatus_FailsClosed(string status)
    {
        var decision = TaskOutcomeMapper.Map(WithStatus(status));

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("unknown_status", decision.FailureType);
        Assert.Contains("unrecognised status", decision.Reason);
    }

    [Fact]
    public void ANullResult_IsADefect_NotASuccess()
    {
        var decision = TaskOutcomeMapper.Map(null);

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("missing_result", decision.FailureType);
        Assert.False(TaskOutcomeMapper.IsCompleting(null));
    }

    // ---- warnings and reasons ---------------------------------------------------------------------

    [Fact]
    public void SucceededWithWarnings_CompletesAndCarriesTheWarnings()
    {
        var result = AntExecutionResult.SucceededWithWarnings("done", new[] { "risky path", "no coverage" });
        var decision = TaskOutcomeMapper.Map(result);

        Assert.Equal(TaskOutcomeAction.Complete, decision.Action);
        Assert.Equal(2, decision.Warnings.Count);
    }

    [Fact]
    public void APlainSuccess_CarriesNoWarningsAndNoFailureType()
    {
        var decision = TaskOutcomeMapper.Map(AntExecutionResult.Succeeded("done"));

        Assert.Equal(TaskOutcomeAction.Complete, decision.Action);
        Assert.Empty(decision.Warnings);
        Assert.Equal("", decision.FailureType);
    }

    /// <summary>The ant's own failure reason is preserved rather than replaced with a generic one.</summary>
    [Fact]
    public void TheFailureReason_ComesFromTheAnt()
    {
        var result = AntExecutionResult.Failed(FailureClass.Timeout, "the check ran for 400s");
        Assert.Equal("the check ran for 400s", TaskOutcomeMapper.Map(result).Reason);
    }

    /// <summary>A failure with no Failure object still yields a usable reason and type.</summary>
    [Fact]
    public void AFailureWithoutAFailureObject_StillProducesAReason()
    {
        var bare = new AntExecutionResult { Success = false, StatusCode = "failed_permanent", Summary = "it broke" };
        var decision = TaskOutcomeMapper.Map(bare);

        Assert.Equal("it broke", decision.Reason);
        Assert.Equal("execution_error", decision.FailureType);
    }

    // ---- consistency ------------------------------------------------------------------------------

    /// <summary>
    /// IsCompleting and Map must never disagree — they are the same rule asked two ways, and a
    /// caller using one while the scheduler uses the other is how a failure gets recorded as done.
    /// </summary>
    [Theory]
    [InlineData("succeeded")]
    [InlineData("succeeded_with_warnings")]
    [InlineData("failed_retryable")]
    [InlineData("failed_permanent")]
    [InlineData("blocked")]
    [InlineData("skipped")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("nonsense")]
    public void IsCompleting_AgreesWithMap(string status)
    {
        var result = WithStatus(status);
        Assert.Equal(TaskOutcomeMapper.Map(result).Action == TaskOutcomeAction.Complete,
                     TaskOutcomeMapper.IsCompleting(result));
    }

    /// <summary>
    /// Success is decided by StatusCode, not by the Success flag — the two are set together by the
    /// factories, but if they ever disagree the status code is authoritative and must not complete
    /// a task on the strength of a stray boolean.
    /// </summary>
    [Fact]
    public void AMismatchedSuccessFlag_DoesNotCompleteTheTask()
    {
        var lying = new AntExecutionResult { Success = true, StatusCode = "failed_permanent", Summary = "actually failed" };
        Assert.Equal(TaskOutcomeAction.Fail, TaskOutcomeMapper.Map(lying).Action);
        Assert.False(TaskOutcomeMapper.IsCompleting(lying));
    }
}
