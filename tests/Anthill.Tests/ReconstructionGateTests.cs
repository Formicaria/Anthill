using Anthill.Core.Domain;
using Microsoft.Data.Sqlite;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md acceptance gate 10 — replaying artifact IDs reconstructs every role's inputs and evidence.
///
/// The gate has been open since it was written and could not be attempted, because two of its three
/// edges did not exist. Production was recorded from v3.8.19. CONSUMPTION was recorded nowhere, so
/// "what did the verifier read" had no answer at all; and evidence could not name the revision it
/// judged, so "what was proved, about which bytes" had no answer either. Both landed earlier in this
/// release, which is what makes the gate a query rather than an aspiration.
///
/// THE GATE IS THE GAPS. A reconstruction that only ever succeeds certifies nothing — the useful
/// assertion is that the specific ways a replay can be WRONG are detected: a mutated artifact, a
/// consumption pointing at something gone, evidence citing an artifact the store no longer holds.
/// Each of those would otherwise produce a reconstruction that is plausible and false, which is worse
/// than one that refuses.
/// </summary>
public class ReconstructionGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_replay_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string _dbPath = "";

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        return new SqliteMemory(_dbPath);
    }

    /// <summary>
    /// Corrupt the store OUT OF BAND, through a second connection.
    ///
    /// SqliteMemory exposes no way to mutate or delete an artifact, and that is the point — the store
    /// is append-only by construction. So the only faithful way to produce the states this gate
    /// detects is the way they actually occur: a bad migration, a manual edit, a restore from a
    /// mismatched backup. Adding a mutation method to the store just to test the detector would
    /// create the very hole the detector is looking for.
    /// </summary>
    private void OutOfBand(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A miniature but REAL mission: the coder produces a patch set, the tester and verifier read it
    /// through the same compiler production uses, and evidence is attached to the artifact. Every
    /// edge is written by the code under test rather than hand-seeded, which is the only way this
    /// proves the production path and not the fixture.
    /// </summary>
    private static (SqliteMemory Memory, string PatchId) Mission(SqliteMemory memory)
    {
        var artifacts = (IArtifactStore)memory;
        var evidence = (IEvidenceStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "reconstruct me" });

        var patchId = artifacts.Put(Artifact.Create(
            ArtifactSchemas.PatchSet, "coder", "m1", """{"proposals":["a"]}"""));

        // Read through the real compiler, so the consumption rows are the ones production writes.
        ArtifactContext.Compile(artifacts, "m1", 20_000, consumerRole: "tester", consumerTaskId: "t-test");
        ArtifactContext.Compile(artifacts, "m1", 20_000, consumerRole: "verifier", consumerTaskId: "t-verify");

        evidence.Put(Evidence.Create(
            kind: EvidenceKinds.Build, deterministic: true, passed: true,
            artifactIds: new[] { patchId }, detail: "build passed", missionId: "m1", taskId: "t-verify"));

        return (memory, patchId);
    }

    // -------------------------------------------------------------------------------------------
    // The gate
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AMissionsRolesInputsAndEvidence_ReconstructFromArtifactIdsAlone()
    {
        using var memory = Memory();
        var (_, patchId) = Mission(memory);

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.True(replay.IsConsistent, string.Join("; ", replay.Gaps));

        var coder = Assert.Single(replay.Roles, r => r.Role == "coder");
        Assert.Equal(new[] { patchId }, coder.ProducedArtifactIds);
        Assert.True(coder.OutputIsTyped);
        // The build evidence is attached to the coder's artifact, so it is what was proved about the
        // coder's work — regardless of which task ran the check.
        Assert.Single(coder.EvidenceIds);

        var tester = Assert.Single(replay.Roles, r => r.Role == "tester");
        Assert.Equal(new[] { patchId }, tester.ConsumedArtifactIds);

        var verifier = Assert.Single(replay.Roles, r => r.Role == "verifier");
        Assert.Equal(new[] { patchId }, verifier.ConsumedArtifactIds);
        // The verifier produced nothing typed, and that is SAID rather than shown as an empty list.
        Assert.False(verifier.OutputIsTyped);
    }

    /// <summary>
    /// Inputs come from what was DELIVERED, not from what was declared. A replay built on declarations
    /// would reconstruct a worker's context as something it never saw — the budget decides what
    /// arrives, and an artifact dropped for space was not an input.
    /// </summary>
    [Fact]
    public void TheReplayReportsWhatWasDelivered_NotWhatWasDeclared()
    {
        using var memory = Memory();
        var artifacts = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "budget" });

        var delivered = artifacts.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"proposals":[""" + new string('x', 300) + """]}"""));
        var dropped = artifacts.Put(Artifact.Create(ArtifactSchemas.PatchSet, "coder", "m1",
            """{"proposals":[""" + new string('y', 2_000) + """]}"""));

        ArtifactContext.Compile(artifacts, "m1", 1_000,
            declaredInputIds: new[] { delivered, dropped }, consumerRole: "tester", consumerTaskId: "t1");

        var tester = Assert.Single(
            MissionReconstruction.For(artifacts, (IEvidenceStore)memory, "m1").Roles, r => r.Role == "tester");

        Assert.Contains(delivered, tester.ConsumedArtifactIds);
        Assert.DoesNotContain(dropped, tester.ConsumedArtifactIds);
    }

    // -------------------------------------------------------------------------------------------
    // The ways a replay can be wrong
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Payload edited, hash left behind: the ARTIFACT's own hash catches it.
    ///
    /// This is the crude tampering — a manual edit or a bad migration that rewrites content and does
    /// not think about the hash column. `IsIntact()` recomputes and disagrees.
    ///
    /// Note what does NOT fire: `consumed_version_changed`. The consumption row recorded the hash as
    /// READ, the artifact's hash COLUMN is untouched, so the two still agree. My first draft asserted
    /// both and was wrong about the mechanism — the two checks catch different tamperings, which is
    /// the reason both exist, and the test below is the other one.
    /// </summary>
    [Fact]
    public void APayloadEditedBehindItsHash_IsReportedAsAMutation()
    {
        using var memory = Memory();
        Mission(memory);

        OutOfBand("UPDATE artifacts SET payload = 'tampered' WHERE producer_role = 'coder'");

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.False(replay.IsConsistent);
        Assert.Contains(replay.Gaps, g => g.StartsWith("artifact_mutated:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Payload AND hash rewritten together: the artifact is self-consistent and the CONSUMPTION LEDGER
    /// catches it.
    ///
    /// This is the tampering that would otherwise be invisible. `IsIntact()` passes — the row hashes
    /// to exactly what it says — and every id still resolves, so a replay would confidently
    /// reconstruct the verifier's input as content the verifier never saw. The only record that
    /// disagrees is the hash captured at READ TIME, which is the entire reason ArtifactConsumption
    /// stores one instead of just an id.
    ///
    /// It is not hypothetical: a restore from a backup taken at a different generation produces
    /// exactly this state.
    /// </summary>
    [Fact]
    public void ARowRewrittenWithAMatchingHash_IsCaughtByTheConsumptionLedger()
    {
        using var memory = Memory();
        Mission(memory);

        var replacement = Artifact.HashOf("a different patch set entirely");
        OutOfBand($"UPDATE artifacts SET payload = 'a different patch set entirely', "
                + $"content_hash = '{replacement}' WHERE producer_role = 'coder'");

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.False(replay.IsConsistent);
        // The artifact itself is beyond reproach — which is exactly why the second check is needed.
        Assert.DoesNotContain(replay.Gaps, g => g.StartsWith("artifact_mutated:", StringComparison.Ordinal));
        Assert.Contains(replay.Gaps, g => g.StartsWith("consumed_version_changed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AConsumedArtifactThatIsGone_IsReportedAsAGap()
    {
        using var memory = Memory();
        Mission(memory);

        OutOfBand("DELETE FROM artifacts WHERE producer_role = 'coder'");

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.False(replay.IsConsistent);
        Assert.Contains(replay.Gaps, g => g.StartsWith("consumed_artifact_missing:", StringComparison.Ordinal));
        Assert.Contains(replay.Gaps, g => g.StartsWith("evidence_artifact_missing:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Evidence attached to nothing cannot be replayed: "a build passed" with no artifact is a claim
    /// about an unnamed subject, which is exactly the unfalsifiable state the evidence model exists
    /// to eliminate.
    /// </summary>
    [Fact]
    public void EvidenceCitingNoArtifact_IsReportedAsAGap()
    {
        using var memory = Memory();
        memory.SaveMission(new Mission { Id = "m1", Goal = "orphan evidence" });

        ((IEvidenceStore)memory).Put(Evidence.Create(
            kind: EvidenceKinds.Build, deterministic: true, passed: true,
            artifactIds: Array.Empty<string>(), detail: "build passed", missionId: "m1"));

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.False(replay.IsConsistent);
        Assert.Contains(replay.Gaps, g => g.StartsWith("evidence_cites_nothing:", StringComparison.Ordinal));
    }

    /// <summary>
    /// An empty mission is CONSISTENT and EMPTY — not a pass worth reporting. Pinned because the
    /// obvious formulation of this gate ("the reconstruction succeeded") would certify a mission that
    /// produced nothing, and a gate that passes on nothing is the failure the acceptance list exists
    /// to prevent.
    /// </summary>
    [Fact]
    public void AnEmptyMission_IsConsistentButReconstructsNoRoles()
    {
        using var memory = Memory();
        memory.SaveMission(new Mission { Id = "m1", Goal = "nothing happened" });

        var replay = MissionReconstruction.For((IArtifactStore)memory, (IEvidenceStore)memory, "m1");

        Assert.True(replay.IsConsistent);
        Assert.Empty(replay.Roles);
    }

    /// <summary>
    /// The gate must be recorded as closed in the plan, with the version that closed it. A gate met
    /// in code and still shown open in PLAN.md means the next reader reopens work that is done; the
    /// reverse — ticked and unmet — is worse, which is why this test exists on the same commit.
    /// </summary>
    [Fact]
    public void PlanAcceptanceGateTen_IsRecordedAsClosed()
    {
        var gate = SourceText.PlanAcceptanceGate(10);

        Assert.Contains("✅", gate);
        Assert.Contains("v0.3.8.57", gate);
    }
}
