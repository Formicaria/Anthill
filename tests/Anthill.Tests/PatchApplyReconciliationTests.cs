using Anthill.Core.Memory;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// An apply a crash interrupted is finished or discarded, never guessed at. v0.3.8.91.
///
/// THE STATE THIS RESOLVES. `ApplyApprovedPatch` wrote to disk and then made four separate,
/// un-transacted database updates. A process death between them left the file changed and the patch
/// still `approved`. On restart the Patch Center offered Apply again, the recompute found the file
/// no longer matching its base hash, the patch was marked FAILED — and `RevertAppliedPatch` refused,
/// because only an APPLIED patch can be reverted. A change that really landed, recorded as never
/// having happened, and unrevertable.
///
/// `ApplyTransaction.Recover` could not help: it replays the FILESYSTEM journal, which the manual
/// lane never wrote, and it knows nothing about database rows. This is the database half.
/// </summary>
public class PatchApplyReconciliationTests : IDisposable
{
    private readonly string _path;
    private readonly SqliteMemory _memory;

    public PatchApplyReconciliationTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"anthill-intent-{Guid.NewGuid():N}.db");
        _memory = new SqliteMemory(_path);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private PatchApplyIntent Begin(string patchId, string? pre = null) =>
        _memory.BeginApplyIntent(patchId, approvalId: null, patchSetId: "set-1",
            missionId: "m1", targetPath: null, preHash: pre);

    /// <summary>The journal records what is in flight, and only what is in flight.</summary>
    [Fact]
    public void AnIntent_IsOpenUntilItIsClosed()
    {
        var intent = Begin("p1");

        Assert.Contains(_memory.OpenApplyIntents(), i => i.Id == intent.Id);
        Assert.Equal(PatchApplyPhase.Prepared, _memory.OpenApplyIntents().Single(i => i.Id == intent.Id).Phase);

        _memory.CloseApplyIntent(intent.Id);
        Assert.DoesNotContain(_memory.OpenApplyIntents(), i => i.Id == intent.Id);
    }

    [Fact]
    public void ThePhaseAdvances_AndThePostHashIsRecordedWhenTheWriteLands()
    {
        var intent = Begin("p1", pre: "before");

        _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Mutating);
        Assert.Equal(PatchApplyPhase.Mutating, _memory.OpenApplyIntents().Single().Phase);

        _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Applied, "after");

        var now = _memory.OpenApplyIntents().Single();
        Assert.Equal(PatchApplyPhase.Applied, now.Phase);
        Assert.Equal("after", now.PostHash);
        // And the pre-hash is not lost by advancing — reconciliation needs both.
        Assert.Equal("before", now.PreHash);
    }

    /// <summary>
    /// PREPARED: the crash landed before anything was touched. Discard it.
    ///
    /// The patch is untouched and still proposed; an operator can apply it normally. Leaving the row
    /// would make every later sweep re-examine an apply that never began.
    /// </summary>
    [Fact]
    public void AnInterruptionBeforeTheWrite_IsDiscarded()
    {
        Begin("p1");

        var outcome = PatchApplyReconciler.Reconcile(_memory);

        Assert.Equal(1, outcome.Discarded);
        Assert.Equal(0, outcome.Completed);
        Assert.Equal(0, outcome.NeedsOperator);
        Assert.Empty(_memory.OpenApplyIntents());
    }

    /// <summary>
    /// APPLIED: the bytes landed and the records did not. FINISH THE RECORDS.
    ///
    /// This is the case that used to become an unrevertable phantom. Reconciliation does not re-run
    /// the apply — the disk is already correct — it makes the database say what the disk says.
    /// </summary>
    [Fact]
    public void AWriteThatLandedWithoutItsRecords_IsCompleted()
    {
        var intent = Begin("p1");
        _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Applied, "after");

        var outcome = PatchApplyReconciler.Reconcile(_memory);

        Assert.Equal(1, outcome.Completed);
        Assert.Equal(0, outcome.NeedsOperator);
        Assert.Empty(_memory.OpenApplyIntents());
    }

    /// <summary>
    /// MUTATING with the file still holding its pre-apply bytes: the write never landed. Discard.
    ///
    /// The hashes are what make this decidable instead of a guess, which is the whole reason the
    /// intent carries them.
    /// </summary>
    [Fact]
    public void AnInterruptedWriteThatNeverLanded_IsDiscarded()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"anthill-absent-{Guid.NewGuid():N}.txt");

        var intent = _memory.BeginApplyIntent("p1", null, "set-1", "m1", missing, preHash: null);
        _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Mutating);

        var outcome = PatchApplyReconciler.Reconcile(_memory);

        // The target does not exist and no pre-hash was recorded — a create that never happened.
        // Nothing is completed on that evidence.
        Assert.Equal(0, outcome.Completed);
    }

    /// <summary>
    /// AND THE AMBIGUOUS CASE IS LEFT FOR A HUMAN.
    ///
    /// Interrupted mid-write, and the file matches neither hash. Something else moved those bytes,
    /// or the write was partial. This process will not decide whose they are: completing an apply
    /// whose result nothing verified is precisely the failure this release exists to remove, and
    /// doing it during RECOVERY — where nobody is watching — would be the worst place to do it.
    /// </summary>
    [Fact]
    public void AnInterruptedWriteMatchingNeitherHash_IsLeftForAnOperator()
    {
        var file = Path.Combine(Path.GetTempPath(), $"anthill-amb-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "bytes nobody expected\n");
        try
        {
            var intent = _memory.BeginApplyIntent("p1", null, "set-1", "m1", file, preHash: "some-other-hash");
            _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Mutating, "and-another");

            var outcome = PatchApplyReconciler.Reconcile(_memory);

            Assert.Equal(1, outcome.NeedsOperator);
            Assert.Equal(0, outcome.Completed);
            Assert.Contains(outcome.Notes, n => n.Contains("neither", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Reconciliation is idempotent: a second sweep has nothing left to do.</summary>
    [Fact]
    public void ASecondSweep_FindsNothing()
    {
        var intent = Begin("p1");
        _memory.AdvanceApplyIntent(intent.Id, PatchApplyPhase.Applied, "after");

        PatchApplyReconciler.Reconcile(_memory);
        var again = PatchApplyReconciler.Reconcile(_memory);

        Assert.Equal(0, again.Completed);
        Assert.Equal(0, again.Discarded);
        Assert.Equal(0, again.NeedsOperator);
    }
}
