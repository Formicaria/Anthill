using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A patch set is applied as a unit, or not at all. v0.3.8.57.
///
/// THE DEFECT. `AutoApplyRunner` applied eligible patches one at a time:
///
/// <code>
/// foreach (var (patchId, taskId) in eligible)
/// {
///     var outcome = queen.ApplyPatchForAutomation(patchId, missionId, taskId);
///     if (outcome.Success) applied.Add(outcome);
///     else queen.Memory.LogEvent(... "could not write patch" ...);   // and CARRY ON
/// }
/// </code>
///
/// A set whose third patch was stale left the first two applied, skipped the third, and applied the
/// fourth on top — a repository in a state no revision ever had, mixing old and new. Rollback
/// existed but was reachable only from the VERIFY step further down, so a deployment with no verify
/// command configured, or a run that ended before reaching it, simply kept the mixture.
///
/// Two changes make the set atomic. A PREFLIGHT computes every proposal against the tree with no IO
/// and aborts the batch before a byte is written if any refuses; and a write that fails anyway —
/// preflight cannot see a race — rolls back everything already applied and abandons the batch.
///
/// The preflight is deliberately the SAME function the applier runs. A second hand-written checker
/// would drift from the applier, and a preflight that passes where the apply refuses is worse than
/// no preflight at all: it would promise atomicity it does not deliver.
/// </summary>
public class AutoApplyAtomicityTests
{
    private static string RunnerSource() =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "AutoApplyRunner.cs")));

    // -------------------------------------------------------------------------------------------
    // The preflight decision itself, driven through the real engine
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE property, expressed against `PatchApply` directly: a set containing one unappliable
    /// proposal is not partially appliable. If any member refuses, the batch must refuse.
    ///
    /// Driven through the real `Compute` rather than a stub, because the whole value of the
    /// preflight is that it asks the applier's own question.
    /// </summary>
    [Fact]
    public void ASetWithOneStaleMember_HasARefusalToFindBeforeAnyWrite()
    {
        var hash = PatchApply.HashOf("before old after");

        // Two that would apply cleanly.
        var first = PatchApply.Compute(PatchApply.Modify, "old", "new", "before old after", hash,
            requireBaseHash: true);
        var second = PatchApply.Compute(PatchApply.Add, null, "fresh", currentContent: null,
            expectedBaseHash: null, requireBaseHash: true);

        // One built against a base the tree has moved past.
        var stale = PatchApply.Compute(PatchApply.Modify, "old", "new", "the file changed since",
            hash, requireBaseHash: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.False(stale.Ok);

        // A preflight over the set finds exactly one reason to refuse the whole batch.
        var refusals = new[] { first, second, stale }.Where(r => !r.Ok).ToList();
        Assert.Single(refusals);
        Assert.Equal(PatchApplyStatus.RefusedStaleBase, refusals[0].Status);
    }

    /// <summary>A set whose members all apply cleanly produces no refusals, so the batch proceeds.</summary>
    [Fact]
    public void ASetThatFullyApplies_ProducesNoRefusals()
    {
        var hash = PatchApply.HashOf("before old after");

        var outcomes = new[]
        {
            PatchApply.Compute(PatchApply.Modify, "old", "new", "before old after", hash, requireBaseHash: true),
            PatchApply.Compute(PatchApply.Add, null, "fresh", currentContent: null, expectedBaseHash: null, requireBaseHash: true),
        };

        Assert.All(outcomes, o => Assert.True(o.Ok, o.Reason));
    }

    // -------------------------------------------------------------------------------------------
    // The wiring: preflight runs BEFORE the apply loop, and a failed write rolls the batch back
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Preflight is called, and called before anything is applied.
    ///
    /// Position is the property. A preflight that ran after the loop would compute correct refusals
    /// about a tree it had already changed — which is the shape of defect this repository keeps
    /// finding, so it is asserted by index rather than by presence.
    /// </summary>
    [Fact]
    public void ThePreflightRuns_BeforeTheFirstWrite()
    {
        var source = RunnerSource();

        var preflight = source.IndexOf("Preflight(eligible)", StringComparison.Ordinal);
        var firstApply = source.IndexOf("queen.ApplyPatchForAutomation(", StringComparison.Ordinal);

        Assert.True(preflight >= 0, "AutoApplyRunner no longer preflights the set");
        Assert.True(firstApply >= 0, "AutoApplyRunner no longer applies patches");
        Assert.True(preflight < firstApply,
            "The preflight runs after the first write, so it computes refusals about a tree it has "
          + "already modified — and the set can still be applied in part.");
    }

    /// <summary>
    /// A refused preflight applies NOTHING. Asserted as a return before the apply loop, because
    /// logging the refusal and carrying on is precisely the old behaviour.
    /// </summary>
    [Fact]
    public void ARefusedPreflight_AppliesNothing()
    {
        var source = RunnerSource();

        var refusalBlock = source.IndexOf("autonomy_autoapply_preflight_refused", StringComparison.Ordinal);
        var firstApply = source.IndexOf("queen.ApplyPatchForAutomation(", StringComparison.Ordinal);
        Assert.True(refusalBlock >= 0 && refusalBlock < firstApply);

        // Between the refusal log and the apply loop there must be a return.
        var between = source[refusalBlock..firstApply];
        Assert.Contains("return;", between, StringComparison.Ordinal);
    }

    /// <summary>
    /// A write that fails after preflight passed rolls back what was already written.
    ///
    /// This is the half preflight cannot provide: the tree can change between the check and the
    /// write. Without it, the run would leave a partial set behind exactly as before, just less
    /// often — which is worse, because it would be rarer and therefore less likely to be noticed.
    /// </summary>
    [Fact]
    public void AFailedWriteMidSet_RollsBackWhatWasAlreadyApplied()
    {
        var source = RunnerSource();

        var failed = source.IndexOf("autonomy_autoapply_apply_failed", StringComparison.Ordinal);
        Assert.True(failed >= 0, "the apply-failure path is no longer recognisable");

        var body = source[failed..Math.Min(source.Length, failed + 1200)];

        Assert.Contains("RollbackAutoApplied", body, StringComparison.Ordinal);
        Assert.Contains("autonomy_autoapply_batch_rolled_back", body, StringComparison.Ordinal);
        // And it stops rather than applying the rest on top of a set it knows is incomplete.
        Assert.Contains("return;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preflight asks the applier's own question — `PatchApply.Compute` — rather than
    /// reimplementing the rules. Two implementations of "can this patch apply" is how a preflight
    /// comes to pass where the apply refuses.
    /// </summary>
    [Fact]
    public void ThePreflight_UsesTheRealApplyEngine()
    {
        var source = RunnerSource();

        var start = source.IndexOf("private static List<string> Preflight(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Preflight is no longer recognisable");
        var body = source[start..Math.Min(source.Length, start + 2000)];

        Assert.Contains("PatchApply.Compute(", body, StringComparison.Ordinal);
        // And with the same strictness the live applier uses, or the batch would preflight green
        // and then be refused one patch at a time.
        Assert.Contains("requireBaseHash: true", body, StringComparison.Ordinal);
    }

}
