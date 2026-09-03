using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE POLICY SEAM — §17. v0.3.8.114.
///
/// This release does not give the Queen autonomous physical control. What it must not do is make
/// that a later architectural change, so the seam exists now and every origin passes through it:
/// the only thing a future release changes is a value on a mound record.
///
/// The convergence fact at the bottom is the one the brief asks for by name — a Queen-originated
/// and a user-originated request that are otherwise identical must reach the SAME execution path
/// after policy evaluation, because a second path is how the safety properties diverge.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class AutonomyPolicyTests
{
    private static PolicyVerdict Evaluate(
        AutonomyPolicy policy, PhysicalOrigin origin,
        ActionClass ceiling = ActionClass.Benign, bool stopped = false) =>
        MicromoundAutonomy.Evaluate(policy, origin, ceiling, stopped);

    /// <summary>
    /// A NEWLY ENROLLED MOUND IS MANUAL-ONLY. The default is the enum's zero value on purpose: a
    /// record written before this field existed, or by a version that did not know about it, reads
    /// as the most conservative state rather than the most convenient one.
    /// </summary>
    [Fact]
    public void TheDefaultPolicy_IsManualOnly()
    {
        Assert.Equal(AutonomyPolicy.ManualOnly, default(AutonomyPolicy));
        Assert.Equal(AutonomyPolicy.ManualOnly, MicromoundAutonomy.Parse(null));
        Assert.Equal(AutonomyPolicy.ManualOnly, MicromoundAutonomy.Parse(""));
        Assert.Equal(AutonomyPolicy.ManualOnly, new MoundRecord().AutonomyPolicy);
    }

    /// <summary>
    /// A STOP OUTRANKS EVERY POLICY, INCLUDING THE MOST PERMISSIVE ONE, FOR EVERY ORIGIN.
    ///
    /// SAFETY.md gives stop precedence over "missions, configuration, routine work, autonomy,
    /// backlog" — autonomy is named there, so a policy able to reach past a stop is the exact thing
    /// that sentence forbids. Checked before the policy is read at all, so there is no ordering to
    /// get wrong later.
    /// </summary>
    [Theory]
    [InlineData(PhysicalOrigin.User)]
    [InlineData(PhysicalOrigin.Queen)]
    [InlineData(PhysicalOrigin.Automation)]
    [InlineData(PhysicalOrigin.System)]
    public void AStop_RefusesEveryOriginUnderEveryPolicy(PhysicalOrigin origin)
    {
        foreach (var policy in Enum.GetValues<AutonomyPolicy>())
        {
            var verdict = Evaluate(policy, origin, ActionClass.Benign, stopped: true);

            Assert.False(verdict.Allowed, $"{policy} + {origin} was allowed through a stop");
            Assert.Contains("stop", verdict.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// HAZARDOUS IS REFUSED UNCONDITIONALLY, whoever asks and whatever the policy. SAFETY.md
    /// authorizes it per action, expiring on use — and says plainly that "until that pipeline ships
    /// with tests, hazardous actions are refused unconditionally". It has not shipped.
    /// </summary>
    [Fact]
    public void HazardousWork_IsRefusedEvenForAnOperatorOnThePermissivePolicy()
    {
        var verdict = Evaluate(AutonomyPolicy.WithinCharter, PhysicalOrigin.User, ActionClass.Hazardous);

        Assert.False(verdict.Allowed);
        Assert.Contains("hazardous", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A PERSON MAY ALWAYS ASK. `ManualOnly` bounds AUTONOMY, not the operator — reading it as
    /// "nothing may happen" would leave a mound nobody could drive, which is not what any of the
    /// three states mean.
    /// </summary>
    [Fact]
    public void AnOperator_MayActUnderManualOnly()
    {
        var verdict = Evaluate(AutonomyPolicy.ManualOnly, PhysicalOrigin.User);

        Assert.True(verdict.Allowed);
        Assert.False(verdict.RequiresApproval);
    }

    /// <summary>And nothing else may, under that policy.</summary>
    [Theory]
    [InlineData(PhysicalOrigin.Queen)]
    [InlineData(PhysicalOrigin.Workflow)]
    [InlineData(PhysicalOrigin.Automation)]
    [InlineData(PhysicalOrigin.System)]
    public void ManualOnly_RefusesEveryNonHumanOrigin(PhysicalOrigin origin)
    {
        var verdict = Evaluate(AutonomyPolicy.ManualOnly, origin);

        Assert.False(verdict.Allowed);
        Assert.Contains("manual-only", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE MIDDLE STATE LETS THE QUEEN ASK AND A PERSON ANSWER — the state a fleet spends most of
    /// its life in, and the one this release builds the seam for.
    /// </summary>
    [Fact]
    public void ApprovalRequired_AdmitsTheQueenBehindAnApproval()
    {
        var verdict = Evaluate(AutonomyPolicy.ApprovalRequired, PhysicalOrigin.Queen);

        Assert.True(verdict.Allowed);
        Assert.True(verdict.RequiresApproval);
    }

    /// <summary>And the permissive state lets it act without one — within its charter, never beyond.</summary>
    [Fact]
    public void WithinCharter_AdmitsTheQueenWithoutAnApproval()
    {
        var verdict = Evaluate(AutonomyPolicy.WithinCharter, PhysicalOrigin.Queen);

        Assert.True(verdict.Allowed);
        Assert.False(verdict.RequiresApproval);
    }

    /// <summary>
    /// AN UNKNOWN POLICY RESOLVES DOWNWARD. A colony reading a record written by a newer version
    /// must refuse rather than guess — the guess that costs nothing to make is the one that acts.
    /// </summary>
    [Fact]
    public void AnUnknownPolicy_Refuses()
    {
        var verdict = Evaluate((AutonomyPolicy)99, PhysicalOrigin.Queen);

        Assert.False(verdict.Allowed);
        Assert.Contains("unknown", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Round-trips through the stored spelling, and anything unrecognised lands on manual.</summary>
    [Fact]
    public void ThePolicyVocabulary_RoundTrips()
    {
        foreach (var policy in Enum.GetValues<AutonomyPolicy>())
            Assert.Equal(policy, MicromoundAutonomy.Parse(MicromoundAutonomy.Value(policy)));

        Assert.Equal(AutonomyPolicy.ManualOnly, MicromoundAutonomy.Parse("something_from_the_future"));
    }

    /// <summary>
    /// EVERY VERDICT CARRIES A REASON, including the allows. "Silent failure" is a contract
    /// violation in SAFETY.md and a refusal without a reason is itself one — but the audit trail
    /// wants the allows too, because "why was this permitted" is the question asked afterwards.
    /// </summary>
    [Fact]
    public void EveryVerdict_SaysWhy()
    {
        foreach (var policy in Enum.GetValues<AutonomyPolicy>())
            foreach (var origin in Enum.GetValues<PhysicalOrigin>())
                foreach (var stopped in new[] { true, false })
                {
                    var verdict = Evaluate(policy, origin, ActionClass.Benign, stopped);
                    Assert.False(string.IsNullOrWhiteSpace(verdict.Reason),
                        $"{policy} + {origin} + stopped={stopped} gave no reason");
                }
    }

    /// <summary>
    /// THE CONVERGENCE FACT THE BRIEF ASKS FOR BY NAME.
    ///
    /// "A Queen-originated request and user-originated request that are otherwise identical must
    /// converge on the same controller execution path after policy evaluation. There must not be a
    /// ManualMicromoundController and an AutonomousMicromoundController."
    ///
    /// This asserts the property at the seam: once policy has spoken, the only difference between
    /// the two is whether an approval is owed. Both produce the same `PolicyVerdict` type, both are
    /// allowed, and there is no second decision for a caller to make — so there is nothing for a
    /// second code path to be FOR. `MissionDispatchTests` asserts the same convergence at the
    /// dispatch layer, where the mission is actually built.
    /// </summary>
    [Fact]
    public void AQueenRequestAndAUserRequest_DifferOnlyByWhetherAnApprovalIsOwed()
    {
        var user = Evaluate(AutonomyPolicy.ApprovalRequired, PhysicalOrigin.User);
        var queen = Evaluate(AutonomyPolicy.ApprovalRequired, PhysicalOrigin.Queen);

        Assert.True(user.Allowed);
        Assert.True(queen.Allowed);

        Assert.False(user.RequiresApproval);
        Assert.True(queen.RequiresApproval);

        // And under the permissive policy they are indistinguishable, which is the end state the
        // seam exists to make reachable without rebuilding anything.
        var userWithin = Evaluate(AutonomyPolicy.WithinCharter, PhysicalOrigin.User);
        var queenWithin = Evaluate(AutonomyPolicy.WithinCharter, PhysicalOrigin.Queen);

        Assert.Equal(userWithin.Allowed, queenWithin.Allowed);
        Assert.Equal(userWithin.RequiresApproval, queenWithin.RequiresApproval);
    }
}
