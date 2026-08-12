using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.Core.Verification;
using Anthill.Core.Workspaces;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Structural repair §3/§4 — the mission revision: the patched tree outlives its verification call,
/// downstream checks run against IT, and evidence binds to the revision it judged. The stale-
/// evidence scenario (J) is the merge-blocking case: PatchSet B may never be verified on PatchSet
/// A's green tester run.
/// </summary>
public class MissionRevisionTests : IDisposable
{
    private readonly string _dir;

    public MissionRevisionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-rev-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "existing.txt"), "original content\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PatchSet NewPatchSet(string missionId, string content) => new()
    {
        MissionId = missionId,
        TaskId = Guid.NewGuid().ToString(),
        Summary = "test patch",
        Proposals =
        {
            new PatchProposal
            {
                FilePath = "added.txt", ChangeType = PatchChangeType.Add, NewContent = content,
            },
        },
    };

    // ---- the registry owns the tree -----------------------------------------------------------

    [Fact]
    public void RegisteringASecondRevision_DisposesTheFirstTree()
    {
        var missionId = "m-" + Guid.NewGuid().ToString("N")[..8];
        var a = PatchSetMaterializer.Materialize(NewPatchSet(missionId, "version A"), _dir);
        Assert.True(a.Ok, a.Problem);
        var revA = MissionRevisionRegistry.Register(missionId, "task-a", a.Materialized!);
        Assert.True(Directory.Exists(revA.Root), "revision A's tree must stay alive after registration");

        var b = PatchSetMaterializer.Materialize(NewPatchSet(missionId, "version B"), _dir);
        Assert.True(b.Ok, b.Problem);
        var revB = MissionRevisionRegistry.Register(missionId, "task-b", b.Materialized!);

        Assert.False(Directory.Exists(revA.Root),
            "revision A's tree must be DISPOSED when B replaces it — its evidence is stale by construction");
        Assert.True(Directory.Exists(revB.Root));
        Assert.Equal(revB.RevisionId, MissionRevisionRegistry.CurrentFor(missionId)!.RevisionId);

        MissionRevisionRegistry.ReleaseMission(missionId);
        Assert.False(Directory.Exists(revB.Root), "release at finalization disposes the tree");
        Assert.Null(MissionRevisionRegistry.CurrentFor(missionId));
    }

    [Fact]
    public void TwoMissions_KeepIndependentRevisions()
    {
        var m1 = "m1-" + Guid.NewGuid().ToString("N")[..8];
        var m2 = "m2-" + Guid.NewGuid().ToString("N")[..8];
        var a = PatchSetMaterializer.Materialize(NewPatchSet(m1, "for m1"), _dir);
        var b = PatchSetMaterializer.Materialize(NewPatchSet(m2, "for m2"), _dir);
        var revA = MissionRevisionRegistry.Register(m1, "t1", a.Materialized!);
        var revB = MissionRevisionRegistry.Register(m2, "t2", b.Materialized!);

        Assert.True(Directory.Exists(revA.Root));
        Assert.True(Directory.Exists(revB.Root));

        MissionRevisionRegistry.ReleaseMission(m1);
        Assert.False(Directory.Exists(revA.Root));
        Assert.True(Directory.Exists(revB.Root), "releasing one mission must not touch another's tree");
        MissionRevisionRegistry.ReleaseMission(m2);
    }

    /// <summary>The identity a downstream verdict binds to: the registered revision carries the
    /// hashes the materializer computed, not re-derived copies.</summary>
    [Fact]
    public void TheRevisionCarriesTheMaterializedIdentity()
    {
        var missionId = "m-" + Guid.NewGuid().ToString("N")[..8];
        var result = PatchSetMaterializer.Materialize(NewPatchSet(missionId, "identity"), _dir);
        var materialized = result.Materialized!;
        var expectedPatchHash = materialized.PatchSetHash;
        var expectedTreeHash = materialized.AppliedTreeHash;

        var rev = MissionRevisionRegistry.Register(missionId, "task-x", materialized);

        Assert.Equal(expectedPatchHash, rev.PatchSetHash);
        Assert.Equal(expectedTreeHash, rev.TreeHash);
        Assert.False(string.IsNullOrEmpty(rev.RevisionId));
        MissionRevisionRegistry.ReleaseMission(missionId);
    }

    // ---- scenario J: stale evidence is refused, fresh evidence satisfies ----------------------

    private static Mission VerifiedShapedMission()
    {
        // The shape every prior gate accepts: a completed verifier with an unambiguous pass.
        var m = new Mission { Goal = "change code", Status = MissionStatus.Complete };
        m.Tasks.Add(new DomainTask
        {
            Title = "verify", AssignedAnt = "verifier", TaskType = "verification",
            Status = Anthill.Core.Domain.TaskStatus.Complete, Result = "Verdict: Verification Passed",
        });
        return m;
    }

    [Fact]
    public void PatchB_CannotBeVerified_WithPatchAsTesterEvidence()
    {
        var m = VerifiedShapedMission();
        m.Tasks.Add(new DomainTask
        {
            Title = "coder A", AssignedAnt = "coder", TaskType = "patch_proposal",
            Status = Anthill.Core.Domain.TaskStatus.Complete,
            ProducedRevisionId = "rev_A", FinishedAt = DateTime.UtcNow.AddMinutes(-10),
        });
        m.Tasks.Add(new DomainTask
        {
            Title = "tester for A", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Complete,
            RanRevisionId = "rev_A", FinishedAt = DateTime.UtcNow.AddMinutes(-9),
        });
        // The repair: PatchSet B becomes the LATEST revision. No fresh tester yet.
        m.Tasks.Add(new DomainTask
        {
            Title = "coder B (repair)", AssignedAnt = "coder", TaskType = "code_change",
            Status = Anthill.Core.Domain.TaskStatus.Complete,
            ProducedRevisionId = "rev_B", FinishedAt = DateTime.UtcNow.AddMinutes(-5),
        });

        Assert.False(MissionVerification.IsSatisfied(m.Tasks),
            "A's green tester run must not verify B");
        Assert.Contains("stale evidence", MissionVerification.Explain(m.Tasks));

        // The fresh retest arrives — bound to B — and verification is satisfiable again.
        m.Tasks.Add(new DomainTask
        {
            Title = "tester for B", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Complete,
            RanRevisionId = "rev_B", FinishedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        Assert.True(MissionVerification.IsSatisfied(m.Tasks));
    }

    /// <summary>A tester that ran against the UNPATCHED tree (null revision) is not evidence for a
    /// revisioned candidate — the exact defect §3 names.</summary>
    [Fact]
    public void AnUnpatchedTreeTesterRun_DoesNotVerifyARevisionedPatch()
    {
        var m = VerifiedShapedMission();
        m.Tasks.Add(new DomainTask
        {
            Title = "coder", AssignedAnt = "coder", TaskType = "patch_proposal",
            Status = Anthill.Core.Domain.TaskStatus.Complete, ProducedRevisionId = "rev_X",
        });
        m.Tasks.Add(new DomainTask
        {
            Title = "tester (ambient mission workspace)", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Complete, RanRevisionId = null,
        });

        Assert.False(MissionVerification.IsSatisfied(m.Tasks),
            "a PASS about the unpatched tree says nothing about the patch");
    }

    /// <summary>Missions that never materialized a revision keep the pre-repair semantics.</summary>
    [Fact]
    public void MissionsWithoutARevision_AreUnaffected()
    {
        var m = VerifiedShapedMission();
        m.Tasks.Add(new DomainTask
        {
            Title = "research", AssignedAnt = "researcher", TaskType = "research",
            Status = Anthill.Core.Domain.TaskStatus.Complete,
        });
        Assert.True(MissionVerification.IsSatisfied(m.Tasks));
    }
}
