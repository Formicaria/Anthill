using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Evidence is about a TREE, and only evidence about THIS tree may promote work. v0.3.8.57.
///
/// v3.8.54's structural repair §3 stamps the revision on the TASK — `RanRevisionId` — and
/// `MissionVerification.HasFreshEvidenceForLatestRevision` pairs on it, so "a tester ran inside
/// revision B" is answerable and a repaired generation cannot inherit generation A's tester run.
/// That is the scheduling claim and it was already sound.
///
/// WHAT WAS STILL MISSING. The EVIDENCE said nothing. `ToolEvidence.For` writes the tester's actual
/// `command_check` verdict and stamped no revision at all, so the row that survives the mission —
/// the one a replay reads, and the one `Evidence.Judges` was built for — could not say which bytes
/// it judged. And `Evidence.Judges`, added earlier in this very release, was called by nothing: I
/// introduced a declared-and-unreachable while removing three others.
///
/// The two checks fail differently, which is why both exist. A task can be stamped with a revision
/// and produce no evidence; an evidence row can name a revision whose task record was pruned.
/// Neither implies the other, and a write to the live tree should require the stronger.
/// </summary>
public class RevisionBoundEvidenceTests
{
    private const string Rev = "rev:ps-B";
    private const string Tree = "sha256:bbbb";

    private static Evidence Check(bool deterministic, bool passed, string? revision, string? tree) =>
        Evidence.Create(
            kind: deterministic ? EvidenceKinds.CommandCheck : EvidenceKinds.ModelReview,
            deterministic: deterministic, passed: passed, missionId: "m1",
            detail: "check", revisionId: revision, patchSetHash: "sha256:pp", treeHash: tree);

    // -------------------------------------------------------------------------------------------
    // The rule
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DeterministicPassingEvidenceForTheRevision_Satisfies() =>
        Assert.True(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(true, true, Rev, Tree) }, Rev, Tree));

    /// <summary>
    /// The repaired-generation case, which is the whole point. Patch set A's green run sits in the
    /// store while patch set B is the one about to be written.
    /// </summary>
    [Fact]
    public void EvidenceForAnEarlierRevision_DoesNotSatisfy() =>
        Assert.False(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(true, true, "rev:ps-A", "sha256:aaaa") }, Rev, Tree));

    /// <summary>
    /// Right revision id, DIFFERENT TREE. This is the subtle one: a revision re-materialized from
    /// the same patch set can land differently, and the tree hash is what says so. Checking only the
    /// revision id would accept a verdict about bytes that are not the bytes being applied.
    /// </summary>
    [Fact]
    public void EvidenceForTheSameRevisionButADifferentTree_DoesNotSatisfy() =>
        Assert.False(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(true, true, Rev, "sha256:cccc") }, Rev, Tree));

    [Fact]
    public void FailingEvidence_DoesNotSatisfy() =>
        Assert.False(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(true, false, Rev, Tree) }, Rev, Tree));

    /// <summary>
    /// A model review naming the right tree is still not grounds to apply anything — v3.8.22's rule,
    /// and the entire reason `Evidence.Deterministic` exists as a field rather than a comment.
    /// </summary>
    [Fact]
    public void NonDeterministicEvidence_NeverSatisfies() =>
        Assert.False(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(false, true, Rev, Tree) }, Rev, Tree));

    [Fact]
    public void UnidentifiedEvidence_DoesNotSatisfy() =>
        Assert.False(MissionVerification.EvidenceJudgesRevision(
            new[] { Check(true, true, null, null) }, Rev, Tree));

    // -------------------------------------------------------------------------------------------
    // The producer
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The tester's check evidence names the tree it ran in, taken from the AMBIENT SCOPE.
    ///
    /// From the scope rather than a parameter, because the scope is what actually decided which tree
    /// the command ran against — ExecutionService enters it around the dispatch. Taking the identity
    /// from anywhere else risks recording a revision the check did not run in, which is exactly the
    /// "true statement about the wrong workspace" failure v3.8.22 shipped.
    /// </summary>
    [Fact]
    public void ToolEvidence_StampsTheRevisionItRanIn()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Tools", "ToolEvidence.cs")));

        Assert.Contains("MissionWorkspaceScope.Current", source);
        Assert.Contains("revisionId: scope?.RevisionId", source);
        Assert.Contains("treeHash: scope?.TreeHash", source);
    }

    // -------------------------------------------------------------------------------------------
    // The consumer
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// `Evidence.Judges` has a real caller. It was added earlier in this release and read by nothing
    /// — the exact defect this release has spent its length removing, reintroduced by me. A predicate
    /// with no consumer is indistinguishable from a working guarantee right up until it matters.
    /// </summary>
    [Fact]
    public void EvidenceJudges_IsActuallyCalled()
    {
        var verification = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Outcomes", "MissionVerification.cs")));

        Assert.Contains(".Judges(", verification);
    }

    /// <summary>
    /// And the promotion path refuses a set whose evidence is about another revision. Auto-apply
    /// writes to the LIVE TREE, so it is the strongest place this can be enforced — everything
    /// upstream decides what to propose, and this decides what actually lands.
    /// </summary>
    [Fact]
    public void AutoApply_RefusesASetWhoseEvidenceJudgesAnotherRevision()
    {
        var runner = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "AutoApplyRunner.cs")));

        Assert.Contains("RefuseEvidenceAboutAnotherRevision", runner);
        Assert.Contains("autonomy_autoapply_stale_evidence", runner);
        // It defers to MissionVerification's rule rather than restating what counts as usable
        // evidence — two definitions of "deterministic and passing" would eventually disagree.
        Assert.Contains("MissionVerification.EvidenceJudgesRevision", runner);
    }

    /// <summary>
    /// REVERSED at v0.3.8.61 (PLAN.md §1b S3). This test used to pin the opposite: legacy
    /// unidentified evidence flowed through auto-apply untouched, on the reasoning that the missing
    /// identity was a fact about when the row was written. True about the ROW — and irrelevant to a
    /// LIVE WRITE happening now, which either has evidence naming the bytes it judged or does not.
    /// Legacy evidence stays readable for history and the manual apply path stays open; what a row
    /// without identity can no longer do is authorise an unattended write to the operator's tree.
    /// </summary>
    [Fact]
    public void LegacyEvidenceWithNoIdentity_IsManualApplyOnly()
    {
        var runner = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "AutoApplyRunner.cs")));

        Assert.Contains("no revision-identified evidence", runner);
        Assert.DoesNotContain("if (identified.Count == 0) return refusals;", runner);
    }
}
