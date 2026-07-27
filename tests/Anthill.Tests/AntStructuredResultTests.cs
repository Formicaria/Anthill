using Anthill.Core.Agents;
using Anthill.Core.Contracts;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 1 — the structured ant contract.
///
/// Until this release an ant's only channel back to the orchestrator was prose. Specialists built
/// real <see cref="AntExecutionResult"/> objects and then serialised them into text, and the
/// executor marked every non-throwing task complete without reading them — so an ant could report
/// failed_retryable and have the mission record a success.
///
/// These tests pin the contract itself: that an outcome is DECLARED, never inferred from the
/// absence of an exception. The task-status mapping that consumes it is Stage 3.
/// </summary>
public class AntStructuredResultTests
{
    /// <summary>A text-producing ant whose output the test controls.</summary>
    private sealed class StubAnt : BaseAnt
    {
        private readonly Func<string> _produce;
        public StubAnt(string name, Func<string> produce) : base(name) => _produce = produce;
        public override string Run(DomainTask task, Mission mission) => _produce();
    }

    /// <summary>A specialist-shaped ant that declares its own structured outcome.</summary>
    private sealed class StructuredAnt : BaseAnt
    {
        private readonly AntExecutionResult _result;
        public StructuredAnt(string name, AntExecutionResult result) : base(name) => _result = result;
        public override string Run(DomainTask task, Mission mission) => _result.Summary;
        public override AntExecutionResult Execute(DomainTask task, Mission mission) => _result;
    }

    private static (DomainTask, Mission) Fixture() => (new DomainTask { Title = "t" }, new Mission { Goal = "g" });

    // ---- classification of the text-producing (compatibility) path ---------------------------------

    [Fact]
    public void PlainOutput_IsSucceeded_AndCarriesTheTextAsAnArtifact()
    {
        var (task, mission) = Fixture();
        var result = new StubAnt("researcher", () => "a useful summary").Execute(task, mission);

        Assert.True(result.Success);
        Assert.Equal("succeeded", result.StatusCode);
        Assert.Null(result.Failure);

        // The prose survives as an ARTIFACT and a narrative — it just no longer decides anything.
        Assert.Contains(result.Artifacts, a => a.Content == "a useful summary");
        Assert.Equal("a useful summary", result.Narrative);
        Assert.Equal("a useful summary".Length, result.Metrics.OutputChars);
    }

    /// <summary>
    /// ModelRouter reports provider failure in-band as an "ERROR:" string rather than throwing.
    /// Before v2.19.0 that string became the task result and the task was marked complete.
    /// </summary>
    [Fact]
    public void InBandModelError_IsAFailure_NotASuccessfulTextResult()
    {
        var (task, mission) = Fixture();
        var result = new StubAnt("coder", () => "ERROR: ollama temporarily unavailable").Execute(task, mission);

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Contains("routed model call failed", result.Summary);
    }

    /// <summary>
    /// A provider being briefly unreachable must be RETRYABLE. FailureClassify.IsRetryable covers
    /// only TransientProviderFailure / RateLimit / Timeout / Conflict — classifying this as
    /// DependencyFailure (the intuitive choice) would yield failed_permanent and forbid the retry
    /// that would most likely succeed.
    /// </summary>
    [Fact]
    public void InBandModelError_IsClassifiedRetryable()
    {
        var (task, mission) = Fixture();
        var result = new StubAnt("verifier", () => "ERROR: connection refused").Execute(task, mission);

        Assert.Equal("failed_retryable", result.StatusCode);
        Assert.Equal(FailureClass.TransientProviderFailure, result.Failure!.Class);
        Assert.True(result.Failure.Retryable);
        Assert.True(FailureClassify.IsRetryable(result.Failure.Class));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \n")]
    public void EmptyOutput_IsAPermanentFailure_NotAnEmptySuccess(string produced)
    {
        var (task, mission) = Fixture();
        var result = new StubAnt("builder", () => produced).Execute(task, mission);

        Assert.False(result.Success);
        Assert.Equal("failed_permanent", result.StatusCode);
        Assert.Equal(FailureClass.InternalDefect, result.Failure!.Class);
        Assert.False(result.Failure.Retryable);
    }

    [Fact]
    public void NullOutput_IsHandledAsEmpty_NotAsACrash()
    {
        var (task, mission) = Fixture();
        var result = new StubAnt("file", () => null!).Execute(task, mission);
        Assert.Equal("failed_permanent", result.StatusCode);
    }

    /// <summary>
    /// An exception is NOT quietly converted into a result. Ordinary failure is a failure status;
    /// an exception means something unexpected happened and the executor must see it, so the
    /// contract deliberately lets it propagate.
    /// </summary>
    [Fact]
    public void Exceptions_PropagateRatherThanBecomingASilentResult()
    {
        var (task, mission) = Fixture();
        var ant = new StubAnt("web", () => throw new InvalidOperationException("tool exploded"));
        var error = Assert.Throws<InvalidOperationException>(() => { ant.Execute(task, mission); });
        Assert.Equal("tool exploded", error.Message);
    }

    // ---- the declared (structured) path -----------------------------------------------------------

    /// <summary>An ant that declares its own outcome is taken at its word, not re-derived.</summary>
    [Fact]
    public void DeclaredFailure_IsNotOverriddenByTheTextFallback()
    {
        var (task, mission) = Fixture();
        var declared = AntExecutionResult.Failed(FailureClass.VerificationFailure, "checks did not pass");
        var result = new StructuredAnt("tester", declared).Execute(task, mission);

        Assert.False(result.Success);
        Assert.Equal("failed_permanent", result.StatusCode);
        Assert.Equal(FailureClass.VerificationFailure, result.Failure!.Class);
    }

    [Fact]
    public void SucceededWithWarnings_IsSuccessful_AndKeepsTheWarnings()
    {
        var result = AntExecutionResult.SucceededWithWarnings(
            "patch proposed", new[] { "touched a risky path", "no test covers this file" });

        Assert.True(result.Success);
        Assert.Equal("succeeded_with_warnings", result.StatusCode);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void SucceededWithWarnings_DiscardsBlankWarnings()
    {
        var result = AntExecutionResult.SucceededWithWarnings("done", new[] { "real", "", "  ", null! });
        Assert.Single(result.Warnings);
        Assert.Equal("real", result.Warnings[0]);
    }

    /// <summary>Skipped is not a failure and must never be reinforced as a success either.</summary>
    [Fact]
    public void Skipped_IsNeitherSuccessNorFailure()
    {
        var result = AntExecutionResult.Skipped("source budget exhausted");

        Assert.False(result.Success);
        Assert.Equal("skipped", result.StatusCode);
        Assert.Null(result.Failure);
        Assert.Equal("source budget exhausted", result.Summary);
    }

    [Fact]
    public void Blocked_IsPermanent_AndClassifiedAsAuthorizationFailure()
    {
        var result = AntExecutionResult.Blocked("capability not granted: apply_patch");

        Assert.False(result.Success);
        Assert.Equal("blocked", result.StatusCode);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure!.Class);
        Assert.False(result.Failure.Retryable);
    }

    // ---- metrics are observability only -----------------------------------------------------------

    /// <summary>
    /// Metrics must never imply an outcome. A cheap failure and an expensive success are both
    /// perfectly legal, so cost is recorded beside the status and never folded into it.
    /// </summary>
    [Fact]
    public void Metrics_AreIndependentOfOutcome()
    {
        var expensiveFailure = AntExecutionResult.Failed(FailureClass.Timeout, "gave up")
            with { Metrics = new AntMetrics { ModelCalls = 9, ToolCalls = 4, ElapsedSeconds = 120.5 } };
        Assert.False(expensiveFailure.Success);
        Assert.Equal(9, expensiveFailure.Metrics.ModelCalls);

        var cheapSuccess = AntExecutionResult.Succeeded("done")
            with { Metrics = new AntMetrics { ModelCalls = 0 } };
        Assert.True(cheapSuccess.Success);
        Assert.Equal(0, cheapSuccess.Metrics.ModelCalls);
    }

    [Fact]
    public void Metrics_DefaultToZero_RatherThanNull()
    {
        var result = AntExecutionResult.Succeeded("done");
        Assert.NotNull(result.Metrics);
        Assert.Equal(0, result.Metrics.ModelCalls);
        Assert.Null(result.Metrics.EnvironmentFingerprint);
    }

    // ---- the contract itself ----------------------------------------------------------------------

    /// <summary>
    /// Every ant answers the structured contract, whether or not it has been migrated yet. This is
    /// what lets the executor stop reading prose before the individual ants are converted.
    /// </summary>
    [Fact]
    public void EveryAnt_AnswersTheStructuredContract()
    {
        var method = typeof(BaseAnt).GetMethod(nameof(BaseAnt.Execute));
        Assert.NotNull(method);
        Assert.Equal(typeof(AntExecutionResult), method!.ReturnType);
        Assert.True(method.IsVirtual);
    }

    /// <summary>
    /// Ant instances are shared across the colony and parallel execution runs several tasks through
    /// the same object at once, so an implementation may not keep per-run state. Two interleaved
    /// executions must not contaminate each other.
    /// </summary>
    [Fact]
    public void Execute_IsSafeForConcurrentUseOfOneInstance()
    {
        var counter = 0;
        var ant = new StubAnt("researcher", () => $"output {Interlocked.Increment(ref counter)}");
        var (task, mission) = Fixture();

        var results = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => ant.Execute(task, mission))
            .ToList();

        Assert.All(results, r => Assert.Equal("succeeded", r.StatusCode));
        Assert.Equal(64, results.Select(r => r.Narrative).Distinct().Count());
    }
}
