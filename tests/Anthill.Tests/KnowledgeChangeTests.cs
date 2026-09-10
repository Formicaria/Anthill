using System;
using System.IO;
using System.Linq;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT CHANGED, AND WHAT THE COLONY DID ABOUT IT. v0.3.9.3 (A4).
///
/// A4's claim is narrow and worth pinning exactly: the colony can tell a document it has never
/// studied from one whose content has MOVED since it did, it can record that as a finding without
/// acting on it, and the operator decides what becomes a mission. Everything else in the lane —
/// which documents FORAGER holds, whether the mission answers well — belongs to other tests.
///
/// THE PROPERTY THAT MATTERS MOST IS IDEMPOTENCE, and it is the one this repository has got wrong
/// before in this exact shape. A pass that ran every six hours and filed a fresh finding each time
/// would bury the operator in one row a day about a document nobody has touched, and the row would
/// look correct: it WAS detected then. The unique index on the action key is what stops that, so it
/// is asserted directly rather than inferred from a count.
///
/// THE MODE ITSELF — `off | suggest | on`, and what a typo in it reads as — is pinned in
/// `KnowledgeStudyStagerTests` beside the pass that obeys it, rather than a second time here. Two
/// tests asserting one rule is the shape this suite spends most of its time removing.
/// </summary>
public class KnowledgeChangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_kchange_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static SqliteMemory.KnowledgeChange Finding(
        string source, string hash, string? previous = null, string project = "kb-1") => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProjectRef = project,
        AnthillProjectId = "p1",
        SourceId = source,
        SourceName = source + ".md",
        Kind = previous is null ? "new" : "changed",
        PreviousHash = previous,
        CurrentHash = hash,
        ActionKey = SqliteMemory.SeedActionKey("inst", "gen", project, source, hash, "study_source", "v1"),
    };

    [Fact]
    public void AFinding_IsRecordedOnce_HoweverManyPassesSeeIt()
    {
        var mem = Memory();
        var finding = Finding("doc-a", "hash-1");

        Assert.True(mem.TryRecordKnowledgeChange(finding));

        // The same document at the same version, seen by the next pass — and the pass after that.
        // A new row id changes nothing: the ACTION KEY is the identity.
        Assert.False(mem.TryRecordKnowledgeChange(Finding("doc-a", "hash-1")));
        Assert.False(mem.TryRecordKnowledgeChange(Finding("doc-a", "hash-1")));

        Assert.Single(mem.KnowledgeChanges("open"));
    }

    [Fact]
    public void ADocumentThatMoves_IsANewFinding_BecauseItIsANewVersion()
    {
        var mem = Memory();
        Assert.True(mem.TryRecordKnowledgeChange(Finding("doc-a", "hash-1")));

        // Somebody republished it. The content hash is part of the action key, so this is a
        // different finding — which is the whole point of keying on the hash rather than the id.
        Assert.True(mem.TryRecordKnowledgeChange(Finding("doc-a", "hash-2", previous: "hash-1")));

        var open = mem.KnowledgeChanges("open");
        Assert.Equal(2, open.Count);
        Assert.Contains(open, c => c.Kind == "changed" && c.PreviousHash == "hash-1");
    }

    [Fact]
    public void QueueingAFinding_AttachesTheMission_AndTakesItOffTheOpenList()
    {
        var mem = Memory();
        var finding = Finding("doc-a", "hash-1");
        mem.TryRecordKnowledgeChange(finding);

        mem.MarkKnowledgeChangeQueued(finding.ActionKey, "job-77");

        Assert.Empty(mem.KnowledgeChanges("open"));
        var queued = mem.KnowledgeChanges("queued");
        Assert.Single(queued);
        Assert.Equal("job-77", queued[0].MissionId);
        Assert.NotNull(queued[0].DecidedAt);
    }

    /// <summary>
    /// DISMISSING KEEPS THE ROW. A finding the operator looked at and declined is the record that
    /// somebody decided; deleting it would make the next pass rediscover the same document and ask
    /// the same question until it got a different answer.
    /// </summary>
    [Fact]
    public void ADismissedFinding_IsKept_AndCannotBeDismissedTwice()
    {
        var mem = Memory();
        var finding = Finding("doc-a", "hash-1");
        mem.TryRecordKnowledgeChange(finding);
        var id = mem.KnowledgeChanges("open").Single().Id;

        var dismissed = mem.DismissKnowledgeChange(id, "superseded internally");
        Assert.NotNull(dismissed);
        Assert.Equal("dismissed", dismissed!.Status);
        Assert.Equal("superseded internally", dismissed.Note);

        // A second decision on one finding is not an update — the record already says what happened.
        Assert.Null(mem.DismissKnowledgeChange(id, "again"));

        Assert.Empty(mem.KnowledgeChanges("open"));
        Assert.Single(mem.KnowledgeChanges("dismissed"));
        Assert.NotNull(mem.KnowledgeChangeById(id));
    }

    [Fact]
    public void AQueuedFinding_CannotBeDismissed()
    {
        var mem = Memory();
        var finding = Finding("doc-a", "hash-1");
        mem.TryRecordKnowledgeChange(finding);
        var id = mem.KnowledgeChanges("open").Single().Id;

        mem.MarkKnowledgeChangeQueued(finding.ActionKey, "job-9");
        Assert.Null(mem.DismissKnowledgeChange(id, "changed my mind"));
        Assert.Equal("queued", mem.KnowledgeChangeById(id)!.Status);
    }

    /// <summary>
    /// THE WATERMARK IS DERIVED, so there is no second fact that can disagree with the rows. A stored
    /// cursor column is the shape that eventually gets written when the rows do not, or the reverse.
    /// </summary>
    [Fact]
    public void TheWatermark_IsTheNewestFinding_AndIsPerKnowledgeBase()
    {
        var mem = Memory();
        Assert.Null(mem.KnowledgeChangeWatermark("kb-1"));

        mem.TryRecordKnowledgeChange(Finding("doc-a", "hash-1"));
        var first = mem.KnowledgeChangeWatermark("kb-1");
        Assert.NotNull(first);

        // A different base has its own answer, and does not inherit this one.
        Assert.Null(mem.KnowledgeChangeWatermark("kb-2"));
        mem.TryRecordKnowledgeChange(Finding("doc-z", "hash-9", project: "kb-2"));
        Assert.NotNull(mem.KnowledgeChangeWatermark("kb-2"));
    }

    /// <summary>
    /// THE COMPARISON A4 RESTS ON is a read of records `.154` has been writing for 30 releases. The
    /// answer to "what changed" was already in the database and nothing had ever asked for it, which
    /// is this repository's most-named defect wearing a different hat: declared and reaching nobody.
    /// </summary>
    [Fact]
    public void StudiedHashes_ReadBackWhatTheSeedingPassRecorded_NewestPerSourceWinning()
    {
        var mem = Memory();
        Assert.Empty(mem.StudiedContentHashes("kb-1"));

        mem.TryRecordSeedIntent(new SqliteMemory.KnowledgeSeedReceipt
        {
            EventId = Guid.NewGuid().ToString(),
            ActionKey = "key-1", ProjectRef = "kb-1", SourceId = "doc-a",
            SourceName = "doc-a.md", LogicalContentHash = "hash-1",
        });

        var studied = mem.StudiedContentHashes("kb-1");
        Assert.Equal("hash-1", studied["doc-a"]);

        // Studied again at a new version: two receipts, one belief, and it is the LATEST.
        mem.TryRecordSeedIntent(new SqliteMemory.KnowledgeSeedReceipt
        {
            EventId = Guid.NewGuid().ToString(),
            ActionKey = "key-2", ProjectRef = "kb-1", SourceId = "doc-a",
            SourceName = "doc-a.md", LogicalContentHash = "hash-2",
            ReceivedAt = DateTime.UtcNow.AddMinutes(1),
        });

        Assert.Equal("hash-2", mem.StudiedContentHashes("kb-1")["doc-a"]);

        // And a receipt in another base is not this base's belief.
        Assert.Empty(mem.StudiedContentHashes("kb-2"));
    }
}
