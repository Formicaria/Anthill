using Anthill.SDK.Contracts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The plan's failure taxonomy, reconciled against the one the code actually has.
///
/// v0.3.8.37 wrote this file as a map with four RECORDED GAPS — plan classes the code could not
/// express (permanent_provider, tool_failure, cancellation, test_failure) and four it expressed
/// only by collapsing onto a neighbour (policy_denial→AuthorizationFailure,
/// invalid_artifact→ValidationFailure, patch_conflict→TargetRejection, security_failure→UnsafeState).
///
/// The structural-repair release closes them: the enum now carries every distinction the plan
/// draws, the boundary produces a typed failure_context carrying the class, and UNKNOWN is a class
/// of its own that never masquerades as InternalDefect. This file's job flips accordingly — from
/// documenting the gaps to proving they stay closed.
/// </summary>
public class FailureTaxonomyTests
{
    /// <summary>The plan's classes, each mapped to a DISTINCT code class. No nulls remain.</summary>
    private static readonly Dictionary<string, FailureClass> PlanToCode = new(StringComparer.Ordinal)
    {
        ["transient_provider"] = FailureClass.TransientProviderFailure,
        ["permanent_provider"] = FailureClass.PermanentProviderFailure,
        ["missing_model"] = FailureClass.ModelRoutingFailure,
        ["tool_failure"] = FailureClass.ToolFailure,
        ["authorization_failure"] = FailureClass.AuthorizationFailure,
        ["policy_denial"] = FailureClass.PolicyDenial,
        ["validation_failure"] = FailureClass.ValidationFailure,
        ["invalid_artifact"] = FailureClass.InvalidArtifact,
        ["dependency_failure"] = FailureClass.DependencyFailure,
        ["patch_conflict"] = FailureClass.PatchConflict,
        ["build_failure"] = FailureClass.BuildFailure,
        ["test_failure"] = FailureClass.TestFailure,
        ["security_failure"] = FailureClass.SecurityFailure,
        ["verification_failure"] = FailureClass.VerificationFailure,
        ["timeout"] = FailureClass.Timeout,
        ["cancellation"] = FailureClass.Cancellation,
        ["internal_runtime_failure"] = FailureClass.InternalDefect,
        ["unknown_failure"] = FailureClass.UnknownFailure,
    };

    /// <summary>Eighteen classes, every one expressible, no recorded gaps left.</summary>
    [Fact]
    public void EveryPlanFailureClass_IsExpressible()
    {
        Assert.Equal(18, PlanToCode.Count);
        Assert.All(PlanToCode.Values, c => Assert.True(Enum.IsDefined(c), $"{c} is not a FailureClass member"));
    }

    /// <summary>
    /// No two plan classes collapse onto one code class — a collapse would mean the runtime cannot
    /// tell them apart, which is the same as not having the distinction at all. This is exactly
    /// the property the pre-repair map could not have (policy denials were authorization failures,
    /// patch conflicts were target rejections).
    /// </summary>
    [Fact]
    public void TheMappingIsInjective()
    {
        var mapped = PlanToCode.Values.ToList();
        Assert.Equal(mapped.Count, mapped.Distinct().Count());
    }

    /// <summary>The retry set is transient conditions only — including none of the new classes.</summary>
    [Fact]
    public void TheRetrySet_IsTransientConditionsOnly()
    {
        foreach (var retryable in new[]
                 {
                     FailureClass.TransientProviderFailure, FailureClass.RateLimit,
                     FailureClass.Timeout, FailureClass.Conflict,
                 })
            Assert.True(FailureClassify.IsRetryable(retryable), $"{retryable} should be retryable");

        // Retrying these cannot change the answer, so they must terminate immediately — and the
        // unknown class must not auto-retry either: it triggers evidence gathering, not a loop.
        foreach (var terminal in new[]
                 {
                     FailureClass.ValidationFailure, FailureClass.AuthorizationFailure,
                     FailureClass.TargetRejection, FailureClass.VerificationFailure,
                     FailureClass.UnsafeState, FailureClass.InternalDefect,
                     FailureClass.PermanentProviderFailure, FailureClass.ModelRoutingFailure,
                     FailureClass.ToolFailure, FailureClass.PolicyDenial, FailureClass.InvalidArtifact,
                     FailureClass.PatchConflict, FailureClass.BuildFailure, FailureClass.TestFailure,
                     FailureClass.SecurityFailure, FailureClass.Cancellation, FailureClass.UnknownFailure,
                 })
            Assert.False(FailureClassify.IsRetryable(terminal), $"{terminal} must not be retryable");
    }

    /// <summary>
    /// UNKNOWN STAYS UNKNOWN — the class exists precisely so "we could not classify this" never
    /// becomes "internal defect". IsKnown is the gate recovery consults before diagnosing.
    /// </summary>
    [Fact]
    public void UnknownIsItsOwnClass_AndIsNeverKnown()
    {
        Assert.NotEqual(FailureClass.InternalDefect, FailureClass.UnknownFailure);
        Assert.False(FailureClassify.IsKnown(FailureClass.UnknownFailure));
        Assert.False(FailureClassify.IsKnown(FailureClass.None));
        Assert.True(FailureClassify.IsKnown(FailureClass.InternalDefect));
        Assert.True(FailureClassify.IsKnown(FailureClass.TestFailure));
    }

    /// <summary>Policy, security and authorization denials must escalate — recovery never routes
    /// around a deterministic "no".</summary>
    [Fact]
    public void PolicyShapedFailures_MustEscalate()
    {
        Assert.True(FailureClassify.MustEscalate(FailureClass.PolicyDenial));
        Assert.True(FailureClassify.MustEscalate(FailureClass.SecurityFailure));
        Assert.True(FailureClassify.MustEscalate(FailureClass.AuthorizationFailure));
        Assert.False(FailureClassify.MustEscalate(FailureClass.TestFailure));
        Assert.False(FailureClassify.MustEscalate(FailureClass.UnknownFailure));
    }

    /// <summary>
    /// Every code class round-trips through the one converter — including the appended members.
    /// This is the property that actually makes the taxonomy canonical, and the one whose absence
    /// charged six releases of provider outages to the wrong ant.
    /// </summary>
    [Fact]
    public void EveryCodeClass_HasExactlyOneWireForm()
    {
        var wire = Enum.GetValues<FailureClass>().Select(FailureClassNames.Wire).ToList();

        Assert.Equal(wire.Count, wire.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<FailureClass>(), c =>
        {
            Assert.True(FailureClassNames.TryParse(FailureClassNames.Wire(c), out var back));
            Assert.Equal(c, back);
        });
    }
}
