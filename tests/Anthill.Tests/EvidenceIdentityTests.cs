using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Evidence says WHICH TREE it judged, and says it durably. v0.3.8.57.
///
/// A check is a statement about a specific set of bytes. Until this release an evidence row could
/// not say which: the tree hash appeared only inside `Detail`, truncated to twelve characters, in
/// prose. Readable by a person; useless to a query. So "does this build result belong to the
/// revision the verifier is about to promote?" had no answer the runtime could compute, and the
/// failure that guards against is silent — correct evidence attached to the wrong source tree reads
/// exactly like a pass.
///
/// That is not hypothetical in this repository. v3.8.22 shipped build verdicts computed against the
/// primary workspace rather than the patched sandbox — true statements about the wrong bytes — and
/// it took a release to notice. Structured identity is what lets the verifier reject that
/// mechanically instead of by inspection.
///
/// THE ROUND TRIP IS THE POINT. Adding fields to a record is easy and proves nothing: the INSERT did
/// not carry them, so a first cut of this change would have written the identity to memory and
/// dropped it at the database boundary — implemented, tested at the type level, and unreachable.
/// Every test here goes through the real store.
/// </summary>
public class EvidenceIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_ev_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private const string Rev = "rev:ps-1";
    private const string PatchHash = "aaaa1111";
    private const string TreeHash = "bbbb2222";

    // -------------------------------------------------------------------------------------------
    // The round trip
    // -------------------------------------------------------------------------------------------

    /// <summary>THE regression: identity survives the write and comes back on the read.</summary>
    [Fact]
    public void RevisionIdentity_SurvivesTheStore()
    {
        using var memory = Memory();
        var store = (IEvidenceStore)memory;
        var mission = new Anthill.Core.Domain.Mission { Goal = "identity" };
        memory.SaveMission(mission);

        store.Put(Evidence.Create(EvidenceKinds.Build, deterministic: true, passed: true,
            missionId: mission.Id, detail: "build ok",
            revisionId: Rev, patchSetHash: PatchHash, treeHash: TreeHash));

        var back = Assert.Single(store.ForMission(mission.Id, 10));

        Assert.Equal(Rev, back.RevisionId);
        Assert.Equal(PatchHash, back.PatchSetHash);
        Assert.Equal(TreeHash, back.TreeHash);
        Assert.True(back.IdentifiesARevision);
    }

    /// <summary>
    /// And the reconstructed row answers the question the verifier actually asks — matching on the
    /// TREE as well as the id, because an id can be reused by a re-materialization and a tree hash
    /// cannot.
    /// </summary>
    [Fact]
    public void StoredEvidence_JudgesOnlyItsOwnRevisionAndTree()
    {
        using var memory = Memory();
        var store = (IEvidenceStore)memory;
        var mission = new Anthill.Core.Domain.Mission { Goal = "match" };
        memory.SaveMission(mission);

        store.Put(Evidence.Create(EvidenceKinds.TestRun, true, true, mission.Id,
            revisionId: Rev, patchSetHash: PatchHash, treeHash: TreeHash));

        var back = Assert.Single(store.ForMission(mission.Id, 10));

        Assert.True(back.Judges(Rev, TreeHash));
        Assert.False(back.Judges(Rev, "a-different-tree"));   // same revision id, re-materialized
        Assert.False(back.Judges("rev:ps-2", TreeHash));      // a sibling revision
    }

    /// <summary>
    /// Evidence about no revision reads as exactly that. A model review on an informational mission
    /// judges no tree, and NULL must mean "not about a revision" rather than "about one, unrecorded"
    /// — a consumer that requires identity has to refuse it, not assume it matches.
    /// </summary>
    [Fact]
    public void EvidenceAboutNoRevision_DoesNotClaimOne()
    {
        using var memory = Memory();
        var store = (IEvidenceStore)memory;
        var mission = new Anthill.Core.Domain.Mission { Goal = "informational" };
        memory.SaveMission(mission);

        store.Put(Evidence.Create(EvidenceKinds.ModelReview, deterministic: false, passed: true,
            missionId: mission.Id, detail: "reads well"));

        var back = Assert.Single(store.ForMission(mission.Id, 10));

        Assert.Null(back.RevisionId);
        Assert.False(back.IdentifiesARevision);
        Assert.False(back.Judges(Rev, TreeHash));
    }

    /// <summary>
    /// Partial identity is NOT identity. A row carrying a revision id but no tree hash cannot be
    /// matched to a set of bytes, so it must not satisfy the check that gates promotion.
    /// </summary>
    [Fact]
    public void PartialIdentity_DoesNotCount()
    {
        var partial = Evidence.Create(EvidenceKinds.Build, true, true, "m1",
            revisionId: Rev, patchSetHash: null, treeHash: null);

        Assert.False(partial.IdentifiesARevision);
        Assert.False(partial.Judges(Rev, TreeHash));
    }

    /// <summary>
    /// A database created before v0.3.8.57 gains the columns and keeps its rows. The migration is
    /// additive and legacy rows read as NULL — "not about a revision" — which is the honest answer:
    /// those checks ran before anything recorded which tree they judged.
    /// </summary>
    [Fact]
    public void ALegacyDatabase_MigratesAndReadsItsOldRowsAsUnidentified()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "legacy.db");
        string missionId;

        using (var memory = new SqliteMemory(path))
        {
            var mission = new Anthill.Core.Domain.Mission { Goal = "legacy" };
            memory.SaveMission(mission);
            missionId = mission.Id;

            // Written the way a pre-v0.3.8.57 producer would: no identity at all.
            ((IEvidenceStore)memory).Put(Evidence.Create(
                EvidenceKinds.Build, true, true, missionId, detail: "old row"));
        }

        using (var reopened = new SqliteMemory(path))
        {
            var back = Assert.Single(((IEvidenceStore)reopened).ForMission(missionId, 10));

            Assert.Equal("old row", back.Detail);        // the row survived
            Assert.False(back.IdentifiesARevision);      // and does not pretend to identify a tree
        }
    }

    /// <summary>
    /// The deterministic-pass gate is unchanged by all this. Identity is additional information, not
    /// a new precondition — tightening that gate here would silently change which missions can reach
    /// a verified outcome, which is a decision for its own release with its own evidence.
    /// </summary>
    [Fact]
    public void AddingIdentity_DoesNotChangeWhatCountsAsADeterministicPass()
    {
        using var memory = Memory();
        var store = (IEvidenceStore)memory;
        var mission = new Anthill.Core.Domain.Mission { Goal = "gate" };
        memory.SaveMission(mission);

        store.Put(Evidence.Create(EvidenceKinds.Build, deterministic: true, passed: true,
            missionId: mission.Id));

        Assert.True(store.HasDeterministicPass(mission.Id));
    }

    // -------------------------------------------------------------------------------------------
    // The producer
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The verification path records identity on every verdict it writes.
    ///
    /// Asserted at the call site because the fields are optional: a producer that omits them
    /// compiles, writes NULLs, and every round-trip test above still passes. This is the assertion
    /// that would fail if the identity stopped being supplied.
    /// </summary>
    [Fact]
    public void TheVerificationPath_RecordsWhichTreeEachVerdictJudged()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        // Asserted over the WHOLE file, guarded by there being exactly one producer in it.
        //
        // A windowed search was the obvious way to write this and it does not work here:
        // `SourceText.CodeOnly` blanks comments but PRESERVES their length, so the ~1,200 characters
        // of comment between `Evidence.Create(` and its identity arguments push them outside any
        // window narrow enough to be meaningful. Pinning the producer count instead keeps the
        // assertion honest — it cannot drift onto some other Evidence.Create — without depending on
        // how much prose sits inside the call.
        var producers = System.Text.RegularExpressions.Regex
            .Matches(source, @"Evidence\.Create\(").Count;
        Assert.True(producers == 1,
            $"ExecutionService creates evidence in {producers} places; this assertion assumes one, "
          + "so a second producer needs its own identity check rather than inheriting this one.");

        Assert.Contains("revisionId:", source, StringComparison.Ordinal);
        Assert.Contains("patchSetHash: materialized.PatchSetHash", source, StringComparison.Ordinal);
        Assert.Contains("treeHash: materialized.AppliedTreeHash", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the store writes what the producer supplies. The INSERT did not carry these columns
    /// before this release, so the identity would have been dropped at the database boundary — the
    /// exact shape of "implemented and unreachable" this project keeps finding.
    /// </summary>
    [Fact]
    public void TheEvidenceInsert_CarriesTheIdentityColumns()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.Artifacts.cs")));

        Assert.Contains("revision_id, patch_set_hash, tree_hash", source, StringComparison.Ordinal);
        Assert.Contains("@rev, @psh, @tree", source, StringComparison.Ordinal);
    }
}
