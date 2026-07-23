using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.24 Patch Center 2.0 operator surface: approving/rejecting pending patches that have no
/// approval record (the record is created first, then the normal transition runs), and offering
/// operator-edited ALTERNATIVE patches that go through the same approval gate. All against a real
/// SQLite database. Verification-runner behavior (apply→verify→always-restore) is exercised in CI
/// by the endpoint wiring; the state transitions it relies on are covered here.
/// </summary>
public class PatchOperatorActionTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly Queen _queen;
    private readonly Mission _mission;

    public PatchOperatorActionTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_patchops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
        _queen = new Queen(_memory);
        _mission = new Mission { Goal = "patch operator actions test", Status = MissionStatus.Complete };
        _mission.Tasks.Add(new Task { Title = "coder", AssignedAnt = "coder" });
        _memory.SaveMission(_mission);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A pending proposal saved WITHOUT any approval record — the orphan case.</summary>
    private PatchProposal SaveOrphanPatch(string file = "src/Orphan.cs")
    {
        var proposal = new PatchProposal
        {
            FilePath = file, ChangeType = PatchChangeType.Modify, Reason = "orphan test", Risk = "low",
            OldContent = "old", NewContent = "new", RequiresApproval = true, Status = PatchStatus.Proposed,
        };
        _memory.SavePatchSet(new PatchSet
        {
            MissionId = _mission.Id, TaskId = _mission.Tasks[0].Id, Summary = "orphan set", Proposals = new() { proposal },
        });
        return proposal;
    }

    private string Status(string patchId) =>
        _memory.GetPatchProposal(patchId)?.GetValueOrDefault("status")?.ToString() ?? "";

    [Fact]
    public void EnsurePatchApproval_CreatesMissingApprovalRecord()
    {
        var p = SaveOrphanPatch();
        Assert.Null(_memory.GetApprovalForTarget(p.Id));
        var (ok, approvalId, _) = _queen.EnsurePatchApproval(p.Id);
        Assert.True(ok);
        Assert.False(string.IsNullOrWhiteSpace(approvalId));
        var approval = _memory.GetApprovalForTarget(p.Id);
        Assert.NotNull(approval);
        Assert.Equal("pending", approval!.GetValueOrDefault("status")?.ToString());
    }

    [Fact]
    public void EnsurePatchApproval_ReusesExistingRecordAndFailsForUnknownPatch()
    {
        var p = SaveOrphanPatch();
        var first = _queen.EnsurePatchApproval(p.Id);
        var second = _queen.EnsurePatchApproval(p.Id);
        Assert.Equal(first.ApprovalId, second.ApprovalId);
        Assert.False(_queen.EnsurePatchApproval(Guid.NewGuid().ToString()).Ok);
    }

    [Fact]
    public void ApprovePatchDirect_ApprovesOrphanPendingPatch()
    {
        var p = SaveOrphanPatch();
        var msg = _queen.ApprovePatchDirect(p.Id);
        Assert.Contains("approved", msg, StringComparison.OrdinalIgnoreCase);
        var approval = _memory.GetApprovalForTarget(p.Id);
        Assert.Equal("approved", approval!.GetValueOrDefault("status")?.ToString());
    }

    [Fact]
    public void RejectPatchDirect_RejectsOrphanPatchAndMarksProposalRejected()
    {
        var p = SaveOrphanPatch();
        _queen.RejectPatchDirect(p.Id, "not wanted");
        var approval = _memory.GetApprovalForTarget(p.Id);
        Assert.Equal("rejected", approval!.GetValueOrDefault("status")?.ToString());
        Assert.Equal("rejected", Status(p.Id));
    }

    [Fact]
    public void ProposeAlternativePatch_CreatesPendingAlternativeAndSupersedesOriginal()
    {
        var p = SaveOrphanPatch("src/Alt.cs");
        var (ok, newId, _) = _queen.ProposeAlternativePatch(p.Id, "edited content", "operator improvement");
        Assert.True(ok);
        Assert.NotEqual(p.Id, newId);

        var alt = _memory.GetPatchProposal(newId);
        Assert.NotNull(alt);
        Assert.Equal("proposed", alt!.GetValueOrDefault("status")?.ToString());
        Assert.Equal("src/Alt.cs", alt.GetValueOrDefault("file_path")?.ToString());
        Assert.Equal("edited content", alt.GetValueOrDefault("new_content")?.ToString());
        Assert.Contains(p.Id, alt.GetValueOrDefault("reason")?.ToString() ?? "");

        // Alternative goes through the standard approval gate.
        var altApproval = _memory.GetApprovalForTarget(newId);
        Assert.NotNull(altApproval);
        Assert.Equal("pending", altApproval!.GetValueOrDefault("status")?.ToString());

        // Original superseded.
        Assert.Equal("superseded", Status(p.Id));
    }

    [Fact]
    public void ProposeAlternativePatch_SupersedeResolvesOriginalPendingApproval()
    {
        var p = SaveOrphanPatch("src/AltApproval.cs");
        var (_, approvalId, _) = _queen.EnsurePatchApproval(p.Id);
        _queen.ProposeAlternativePatch(p.Id, "edited content", "");
        var origApproval = _memory.GetApprovalRequest(approvalId);
        Assert.Equal("rejected", origApproval!.GetValueOrDefault("status")?.ToString());
    }

    [Fact]
    public void ProposeAlternativePatch_RejectsIdenticalOrEmptyContent()
    {
        var p = SaveOrphanPatch("src/Same.cs");
        Assert.False(_queen.ProposeAlternativePatch(p.Id, "new", "same as original").Ok);
        Assert.False(_queen.ProposeAlternativePatch(p.Id, "", "empty").Ok);
        Assert.False(_queen.ProposeAlternativePatch(Guid.NewGuid().ToString(), "x", "missing").Ok);
    }

    [Fact]
    public void ProposeAlternativePatch_KeepOriginalWhenRequested()
    {
        var p = SaveOrphanPatch("src/Keep.cs");
        var (ok, _, _) = _queen.ProposeAlternativePatch(p.Id, "edited", "", supersedeOriginal: false);
        Assert.True(ok);
        Assert.Equal("proposed", Status(p.Id));
    }

    // ---- v2.7.0 manual revert ------------------------------------------------------------------

    [Fact]
    public void RevertAppliedPatch_RefusesAPatchThatIsNotApplied()
    {
        var p = SaveOrphanPatch("src/NotApplied.cs"); // status: proposed
        var msg = _queen.RevertAppliedPatch(p.Id);
        Assert.Contains("Only an applied patch can be reverted", msg);
        Assert.Equal("proposed", Status(p.Id)); // guard did not mutate state
    }

    [Fact]
    public void RevertAppliedPatch_UnknownIdIsReportedNotThrown()
    {
        var msg = _queen.RevertAppliedPatch(Guid.NewGuid().ToString());
        Assert.Contains("No patch proposal found", msg);
    }

    [Fact]
    public void RevertAppliedPatch_MarksAppliedPatchReverted()
    {
        var p = SaveOrphanPatch("src/RevertMe.cs");
        _memory.UpdatePatchStatus(p.Id, PatchStatus.Applied, AnthillTime.NowUtc().ToIso());
        Assert.Equal("applied", Status(p.Id));

        var msg = _queen.RevertAppliedPatch(p.Id);
        Assert.Contains("reverted", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("reverted", Status(p.Id)); // terminal state recorded even with no backup on record
    }
}
