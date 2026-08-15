using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// TRANSACTIONAL PATCH APPLICATION, PROVEN BY BREAKING IT. v0.3.8.62, PLAN.md §1b S4.
///
/// Every test here injects a fault — a write that throws at the worst moment, a process that
/// "crashes" mid-batch, a backup that vanishes, a file edited underneath the rollback — and then
/// asserts the one thing S4 is about: the restored tree is BYTE-IDENTICAL to the pre-apply tree,
/// or the failure to make it so is a durable, loud, halting state. The predecessor test asserted
/// that the source contained a rollback call; the review called that "a check answering a
/// question adjacent to the one asked", and it was right.
/// </summary>
public class ApplyTransactionTests : IDisposable
{
    private readonly string _root;

    public ApplyTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anthill-tx-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
        ApplyTransaction.WriteFault = null;
    }

    public void Dispose()
    {
        ApplyTransaction.WriteFault = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string P(string rel) => Path.Combine(_root, rel);
    private string Seed(string rel, string content)
    {
        var path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // -------------------------------------------------------------------------------------------
    // The happy path earns its place: commit keeps, and cleans up after itself
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void CommitKeepsTheWrites_AndRemovesTheJournal()
    {
        var a = Seed("a.txt", "old-a");
        var tx = ApplyTransaction.Begin(_root);
        tx.WriteFile(a, "new-a");
        tx.WriteFile(P("b.txt"), "new-b", op: "add");
        tx.Commit();

        Assert.Equal("new-a", File.ReadAllText(a));
        Assert.Equal("new-b", File.ReadAllText(P("b.txt")));
        Assert.False(ApplyTransaction.HasRollbackFailure(_root));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ApplyTransaction.JournalDirectoryName)));
    }

    // -------------------------------------------------------------------------------------------
    // Injected mid-write failure (disk-full / permission change stand-in)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AWriteThatThrowsMidBatch_RollsBackToByteIdenticalTree()
    {
        var a = Seed("a.txt", "original-a");
        var b = Seed("b.txt", "original-b");

        var tx = ApplyTransaction.Begin(_root);
        tx.WriteFile(a, "patched-a");
        ApplyTransaction.WriteFault = path => path.EndsWith("b.txt") ? new IOException("disk full") : null;
        Assert.Throws<IOException>(() => tx.WriteFile(b, "patched-b"));
        ApplyTransaction.WriteFault = null;

        var report = tx.Rollback();

        Assert.True(report.Clean, string.Join("; ", report.Conflicts.Concat(report.Failures)));
        Assert.Equal("original-a", File.ReadAllText(a));   // byte-identical, not merely present
        Assert.Equal("original-b", File.ReadAllText(b));   // the FAILED op's target is untouched
    }

    /// <summary>The staged write means the fault can never leave a half-written target.</summary>
    [Fact]
    public void AFaultedWrite_NeverLeavesAPartialTarget()
    {
        var a = Seed("a.txt", "original-a");
        var tx = ApplyTransaction.Begin(_root);
        ApplyTransaction.WriteFault = _ => new IOException("disk full");
        Assert.Throws<IOException>(() => tx.WriteFile(a, "patched-a-that-would-have-been-much-longer"));
        ApplyTransaction.WriteFault = null;

        // The target holds the OLD bytes in full — not a truncation, not the new bytes.
        Assert.Equal("original-a", File.ReadAllText(a));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(a)!, "*.tmp-*"));
    }

    // -------------------------------------------------------------------------------------------
    // Rename, both directions
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ARolledBackRename_PutsTheFileBack_AndEmptiesTheDestination()
    {
        var src = Seed("src.txt", "the-bytes");
        var tx = ApplyTransaction.Begin(_root);
        tx.MoveFile(src, P("dest.txt"));

        var report = tx.Rollback();

        Assert.True(report.Clean);
        Assert.Equal("the-bytes", File.ReadAllText(src));
        Assert.False(File.Exists(P("dest.txt")));   // not a duplicated file — an undone one
    }

    // -------------------------------------------------------------------------------------------
    // Process crash: an open journal recovered at startup
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ACrashMidBatch_IsRecoveredAtStartup_ByteIdentically()
    {
        var a = Seed("a.txt", "original-a");
        var b = Seed("b.txt", "original-b");

        // The "crash": a transaction applies two files and simply never commits or rolls back —
        // the object is abandoned, exactly as a killed process abandons it. The journal survives.
        var tx = ApplyTransaction.Begin(_root);
        tx.WriteFile(a, "patched-a");
        tx.WriteFile(b, "patched-b");

        var results = ApplyTransaction.Recover(_root);

        Assert.Single(results);
        Assert.Contains("rolled back cleanly", results[0]);
        Assert.Equal("original-a", File.ReadAllText(a));
        Assert.Equal("original-b", File.ReadAllText(b));
        Assert.False(ApplyTransaction.HasRollbackFailure(_root));
    }

    // -------------------------------------------------------------------------------------------
    // The hash rule: newer work is not rollback's to destroy
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AFileEditedAfterApply_IsLeftAlone_AndReportedAsConflict()
    {
        var a = Seed("a.txt", "original-a");
        var b = Seed("b.txt", "original-b");
        var tx = ApplyTransaction.Begin(_root);
        tx.WriteFile(a, "patched-a");
        tx.WriteFile(b, "patched-b");

        File.WriteAllText(b, "operator-edit-after-apply");   // concurrent edit

        var report = tx.Rollback();

        Assert.False(report.Clean);
        Assert.Single(report.Conflicts);
        Assert.Equal("original-a", File.ReadAllText(a));                  // the untouched one restored
        Assert.Equal("operator-edit-after-apply", File.ReadAllText(b));   // the newer work preserved
        // And the incomplete rollback is a durable halting state, not a log line.
        Assert.True(ApplyTransaction.HasRollbackFailure(_root));
    }

    [Fact]
    public void AMissingBackup_IsAFailure_AndADurableHalt()
    {
        var a = Seed("a.txt", "original-a");
        var tx = ApplyTransaction.Begin(_root);
        var entry = tx.WriteFile(a, "patched-a");

        File.Delete(entry.Backup!);   // the backup vanishes (cleanup script, disk repair, sabotage)

        var report = tx.Rollback();

        Assert.False(report.Clean);
        Assert.Single(report.Failures);
        Assert.True(ApplyTransaction.HasRollbackFailure(_root));
    }

    /// <summary>Recovery applies the same hash rule: a crash plus a concurrent edit halts.</summary>
    [Fact]
    public void RecoveryOfACrashedBatch_AlsoRefusesToDestroyNewerWork()
    {
        var a = Seed("a.txt", "original-a");
        var tx = ApplyTransaction.Begin(_root);
        tx.WriteFile(a, "patched-a");
        File.WriteAllText(a, "edited-after-crash");

        var results = ApplyTransaction.Recover(_root);

        Assert.Contains(results, r => r.Contains("INCOMPLETE"));
        Assert.Equal("edited-after-crash", File.ReadAllText(a));
        Assert.True(ApplyTransaction.HasRollbackFailure(_root));
    }

    // -------------------------------------------------------------------------------------------
    // Deletes restore; adds delete; the journal knows before the mutation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ARolledBackDelete_RestoresTheExactBytes()
    {
        var a = Seed("a.txt", "precious-content");
        var tx = ApplyTransaction.Begin(_root);
        tx.DeleteFile(a);
        Assert.False(File.Exists(a));

        var report = tx.Rollback();

        Assert.True(report.Clean);
        Assert.Equal("precious-content", File.ReadAllText(a));
    }

    [Fact]
    public void TheJournalIsDurable_BeforeTheFirstMutation()
    {
        var a = Seed("a.txt", "original-a");
        var tx = ApplyTransaction.Begin(_root);

        // Stage records intent and pre-state durably; the mutation has not happened yet.
        var entry = tx.StageExternal(a, "modify");

        var journalDir = Path.Combine(_root, ApplyTransaction.JournalDirectoryName);
        var journal = File.ReadAllText(Directory.GetFiles(journalDir, "*.journal.json").Single());
        Assert.Contains("a.txt", journal);
        Assert.Contains(entry.PreHash!, journal);
        Assert.Equal("original-a", File.ReadAllText(a));   // and the tree is untouched
    }
}
