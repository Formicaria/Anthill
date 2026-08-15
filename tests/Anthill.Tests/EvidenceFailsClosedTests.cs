using Anthill.Api;
using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// EVIDENCE FAILS CLOSED. v0.3.8.61, PLAN.md §1b S3 — the third P0.
///
/// The finding was two consecutive fail-open boundaries pointing the wrong way: a verifier whose
/// evidence store threw fell through to prose parsed out of model text, and an auto-apply whose
/// store threw returned zero refusals and kept writing. In both, a STORE FAILURE WIDENED AUTHORITY
/// — the exact direction a failure must never move. These tests are behavioural where the seams
/// allow (a throwing store, a stub store, the real gate function) because the .57 review's sharpest
/// lesson was that a test asserting something adjacent to the claim passes while the claim is
/// false.
/// </summary>
public class EvidenceFailsClosedTests
{
    // -------------------------------------------------------------------------------------------
    // Stubs: one store that answers, one that is down
    // -------------------------------------------------------------------------------------------

    private sealed class ThrowingEvidenceStore : IEvidenceStore
    {
        public string Put(Evidence evidence) => throw new InvalidOperationException("store is down");
        public IReadOnlyList<Evidence> ForMission(string missionId, int limit = 200)
            => throw new InvalidOperationException("store is down");
        public IReadOnlyList<Evidence> ForArtifact(string artifactId)
            => throw new InvalidOperationException("store is down");
        public bool HasDeterministicPass(string missionId)
            => throw new InvalidOperationException("store is down");
    }

    private sealed class StubEvidenceStore(IReadOnlyList<Evidence> rows) : IEvidenceStore
    {
        public string Put(Evidence evidence) => evidence.Id;
        public IReadOnlyList<Evidence> ForMission(string missionId, int limit = 200) => rows;
        public IReadOnlyList<Evidence> ForArtifact(string artifactId) => rows;
        public bool HasDeterministicPass(string missionId)
            => rows.Any(e => e.Deterministic && e.Passed);
    }

    private static Evidence Row(bool deterministic, bool passed,
        string? revision, string? patchSetHash, string? tree) =>
        Evidence.Create(
            kind: deterministic ? EvidenceKinds.CommandCheck : EvidenceKinds.ModelReview,
            deterministic: deterministic, passed: passed, missionId: "m1",
            detail: "check", revisionId: revision, patchSetHash: patchSetHash, treeHash: tree);

    // -------------------------------------------------------------------------------------------
    // The verifier: a broken store is not an absent store
    // -------------------------------------------------------------------------------------------

    private static (Mission, Task) VerifiableMission()
    {
        // Enough completed output that the STATIC check alone would say "Verification Passed" —
        // which is the point: these tests prove that verdict is unreachable when the store fails.
        var builder = new Task { Title = "build", AssignedAnt = "builder", Status = TaskStatus.Complete, Result = "the answer" };
        var research = new Task { Title = "research", AssignedAnt = "researcher", Status = TaskStatus.Complete, Result = "notes" };
        var verify = new Task { Title = "verify", AssignedAnt = "verifier", Status = TaskStatus.Running };
        var mission = new Mission { Id = "m1", Goal = "answer the question" };
        mission.Tasks.Add(builder); mission.Tasks.Add(research); mission.Tasks.Add(verify);
        return (mission, verify);
    }

    [Fact]
    public void AStoreThatThrows_ProducesVerificationUnavailable_NeverProse()
    {
        var (mission, task) = VerifiableMission();
        var verifier = new VerifierAnt(false, null, evidence: new ThrowingEvidenceStore());

        var result = verifier.Execute(task, mission);

        var verdictRow = result.Evidence.Single(e => e.Kind == "verification_verdict");
        Assert.Equal(VerificationVerdict.Unavailable, verdictRow.Value);
        // The static check in the narrative says "Verification Passed"; the verdict must not.
        Assert.Contains("Verification Passed", result.Narrative);
        Assert.Equal("succeeded_with_warnings", result.StatusCode);
        // And the source row distinguishes "the store was down" from "nobody produced evidence".
        var source = result.Evidence.Single(e => e.Kind == "verdict_source");
        Assert.Equal("evidence_store_unavailable", source.Value);
    }

    /// <summary>
    /// NO store is the CLI and the older tests, and their contract stands: the static/prose path
    /// answers. Collapsing this configuration into "unavailable" would be rigour's costume on a
    /// regression — the fix distinguishes a store that failed from a store that never existed.
    /// </summary>
    [Fact]
    public void NoStoreAtAll_KeepsTheStaticPath()
    {
        var (mission, task) = VerifiableMission();
        var verifier = new VerifierAnt(false, null);

        var result = verifier.Execute(task, mission);

        var verdictRow = result.Evidence.Single(e => e.Kind == "verification_verdict");
        Assert.Equal(VerificationVerdict.Passed, verdictRow.Value);
    }

    [Fact]
    public void Unavailable_IsNotAPass() =>
        Assert.False(VerificationVerdict.IsPass(VerificationVerdict.Unavailable));

    /// <summary>Prose cannot claim the unavailable verdict: only the store read sets it.</summary>
    [Fact]
    public void Parse_NeverEmitsUnavailable() =>
        Assert.Equal(VerificationVerdict.Unknown,
            VerificationVerdict.Parse("Verdict: verification unavailable, verification_unavailable"));

    // -------------------------------------------------------------------------------------------
    // Auto-apply: the gate that writes to the live tree
    // -------------------------------------------------------------------------------------------

    private const string SetId = "ps-B";
    private const string Rev = "rev:ps-B";

    private static PatchProposal Proposal() => new()
    {
        FilePath = "src/File.cs",
        ChangeType = PatchChangeType.Modify,
        OldContent = "old",
        NewContent = "new",
    };

    private static List<(string, string?, string?, PatchProposal)> Eligible(string? setId = SetId) =>
        [("p1", setId, "t1", Proposal())];

    /// <summary>The content hash of exactly what <see cref="Eligible"/> would apply.</summary>
    private static string HashOfEligible() =>
        Anthill.Core.Verification.PatchSetMaterializer.HashPatchSet(new PatchSet
        {
            Id = SetId,
            MissionId = "m1",
            Proposals = [Proposal()],
        });

    [Fact]
    public void AnEvidenceQueryException_Refuses()
    {
        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(
            new ThrowingEvidenceStore(), "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("could not be read"));
    }

    [Fact]
    public void NoEvidenceAtAll_Refuses()
    {
        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(
            new StubEvidenceStore([]), "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("no revision-identified evidence"));
    }

    /// <summary>Legacy rows — real verdicts with no identity — are manual-apply only now.</summary>
    [Fact]
    public void LegacyUnidentifiedEvidence_Refuses()
    {
        var store = new StubEvidenceStore([Row(true, true, revision: null, patchSetHash: null, tree: null)]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("no revision-identified evidence"));
    }

    [Fact]
    public void ANullPatchSetId_Refuses()
    {
        var store = new StubEvidenceStore([Row(true, true, Rev, HashOfEligible(), "sha256:tt")]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible(setId: null));

        Assert.Contains(refusals, r => r.Contains("no patch_set_id"));
    }

    /// <summary>The repaired-generation case: set A's green run in the store, set B on deck.</summary>
    [Fact]
    public void EvidenceForAnotherRevisionOnly_Refuses()
    {
        var store = new StubEvidenceStore([Row(true, true, "rev:ps-A", "sha256:other", "sha256:aa")]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("none of them judge this set"));
    }

    /// <summary>
    /// Mixed rows: a deterministic PASS and a deterministic FAIL for the same revision. One green
    /// run does not answer a machine's standing objection — this is the "no deterministic failure"
    /// clause, which <c>EvidenceJudgesRevision</c> alone does not enforce.
    /// </summary>
    [Fact]
    public void MixedPassAndFailRows_Refuse()
    {
        var hash = HashOfEligible();
        var store = new StubEvidenceStore(
        [
            Row(true, true, Rev, hash, "sha256:tt"),
            Row(true, false, Rev, hash, "sha256:tt"),
        ]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("deterministic") && r.Contains("FAILED"));
    }

    [Fact]
    public void AWrongTreeHash_Refuses()
    {
        // The evidence names this revision but every row's own Judges() fails on tree identity —
        // EvidenceJudgesRevision compares against forThisSet[0].TreeHash, so a row set whose only
        // pass is NON-deterministic exercises the same refusal arm.
        var store = new StubEvidenceStore([Row(false, true, Rev, HashOfEligible(), "sha256:tt")]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("none of it is"));
    }

    /// <summary>
    /// Same revision, same tree, DIFFERENT BYTES. The evidence judged content this runner is not
    /// about to write — because the set was altered after verification, or because policy filtered
    /// it down to a subset. Either way the verdict does not transfer.
    /// </summary>
    [Fact]
    public void AContentHashMismatch_Refuses()
    {
        var store = new StubEvidenceStore([Row(true, true, Rev, "sha256:not-these-bytes", "sha256:tt")]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Contains(refusals, r => r.Contains("does not match"));
    }

    /// <summary>And the gate opens for exactly one shape: complete identity, judged, clean, same bytes.</summary>
    [Fact]
    public void CompleteMatchingEvidence_PassesTheGate()
    {
        var store = new StubEvidenceStore([Row(true, true, Rev, HashOfEligible(), "sha256:tt")]);

        var refusals = AutoApplyRunner.RefuseEvidenceAboutAnotherRevision(store, "m1", Eligible());

        Assert.Empty(refusals);
    }
}
