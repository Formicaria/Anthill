using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The verdict a mission's stored evidence supports, computed without asking anything. v3.8.27.
///
/// The colony's founding rule is that only reproducible evidence may carry a mission to a verified
/// outcome. Everything since v3.8.19 built the evidence to enforce it, and the last consumer was
/// still a model: <c>VerifierAnt</c> asked one for prose and <c>VerificationVerdict.Parse</c> read
/// the words "verification passed" back out. This is what replaced that.
/// </summary>
public class EvidenceVerdictTests
{
    private static Evidence Row(string kind, bool deterministic, bool passed) =>
        Evidence.Create(kind, deterministic, passed, missionId: "m1");

    /// <summary>
    /// THE headline. A compiler said no; nothing outvotes it — not a passing check beside it, and
    /// certainly not a model review.
    /// </summary>
    [Fact]
    public void ADeterministicFailure_Decides()
    {
        var result = EvidenceVerdict.For(new[]
        {
            Row(EvidenceKinds.Build, deterministic: true, passed: false),
            Row(EvidenceKinds.CommandCheck, deterministic: true, passed: true),
            Row(EvidenceKinds.ModelReview, deterministic: false, passed: true),
        });

        Assert.Equal(VerificationVerdict.Failed, result.Verdict);
        Assert.False(result.IsPass);
    }

    /// <summary>Reproducible checks passed and none failed — the only route to a pass.</summary>
    [Fact]
    public void DeterministicPasses_WithNoFailures_Pass()
    {
        var result = EvidenceVerdict.For(new[]
        {
            Row(EvidenceKinds.Build, true, true),
            Row(EvidenceKinds.TestRun, true, true),
        });

        Assert.Equal(VerificationVerdict.Passed, result.Verdict);
        Assert.True(result.IsPass);
        Assert.Equal(2, result.DeterministicPassed);
    }

    /// <summary>
    /// THE case that used to read as a pass whenever a model wrote the right words. Model reviews
    /// alone are `unknown` — the absence of proof is not proof, and semantic judgment cannot verify.
    /// </summary>
    [Fact]
    public void ModelReviewsAlone_NeverPass()
    {
        var result = EvidenceVerdict.For(new[]
        {
            Row(EvidenceKinds.ModelReview, deterministic: false, passed: true),
            Row(EvidenceKinds.OperatorJudgment, deterministic: false, passed: true),
        });

        Assert.Equal(VerificationVerdict.Unknown, result.Verdict);
        Assert.False(result.IsPass);
        Assert.False(result.HasDeterministicEvidence);
        Assert.Equal(2, result.NonDeterministicRecorded);
    }

    /// <summary>No evidence is not a pass either. "Nobody checked" and "we checked and it was fine"
    /// are different facts, and the old prose verdict could not express the difference.</summary>
    [Fact]
    public void NoEvidence_IsUnknown_NotPassed()
    {
        var result = EvidenceVerdict.For(Array.Empty<Evidence>());

        Assert.Equal(VerificationVerdict.Unknown, result.Verdict);
        Assert.False(result.IsPass);
        Assert.False(result.HasDeterministicEvidence);
    }

    /// <summary>
    /// A failure decides regardless of ORDER. Evidence arrives in whatever sequence the verifiers
    /// ran, and a rule sensitive to that would give different verdicts for the same facts.
    /// </summary>
    [Fact]
    public void OrderDoesNotChangeTheVerdict()
    {
        var forward = EvidenceVerdict.For(new[] { Row("build", true, true), Row("test_run", true, false) });
        var reverse = EvidenceVerdict.For(new[] { Row("test_run", true, false), Row("build", true, true) });

        Assert.Equal(forward.Verdict, reverse.Verdict);
        Assert.Equal(VerificationVerdict.Failed, forward.Verdict);
    }

    /// <summary>The failing check is NAMED. A verdict an operator cannot act on is a verdict that
    /// gets overridden by whoever is in a hurry.</summary>
    [Fact]
    public void TheExplanation_NamesTheFailingCheck()
    {
        var result = EvidenceVerdict.For(new[] { Row(EvidenceKinds.Build, true, false) });

        Assert.Contains("build", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Non-deterministic evidence is counted and REPORTED but never promotes. This is the assertion
    /// that catches someone "simplifying" the rule into a majority vote.
    /// </summary>
    [Fact]
    public void SemanticEvidence_IsRecordedButNeverDecisive()
    {
        var manyReviews = Enumerable.Range(0, 20)
            .Select(_ => Row(EvidenceKinds.ModelReview, false, true))
            .Append(Row(EvidenceKinds.Build, true, false))
            .ToList();

        var result = EvidenceVerdict.For(manyReviews);

        Assert.Equal(VerificationVerdict.Failed, result.Verdict);
        Assert.Equal(20, result.NonDeterministicRecorded);
    }
}
