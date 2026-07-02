using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Approval-queue dedupe (v1.8.14.2): autonomous objectives re-propose the same fix run after
/// run while the first request sits unreviewed. An identical change (same file, change type, and
/// old/new content) already pending must not create a second approval request; anything that
/// differs — content, file, or the prior request no longer pending — must.
/// </summary>
public class ApprovalDedupeTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly Mission _mission;

    public ApprovalDedupeTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_dedupe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
        _mission = new Mission { Goal = "dedupe test", Status = MissionStatus.Running };
        _mission.Tasks.Add(new Task { Title = "propose", AssignedAnt = "coder" });
        _memory.SaveMission(_mission);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PatchProposal NewProposal(string file = "src/Foo.cs", string? oldC = "a", string? newC = "b") => new()
    {
        FilePath = file, ChangeType = PatchChangeType.Modify, Reason = "test", Risk = "low",
        OldContent = oldC, NewContent = newC, RequiresApproval = true, Status = PatchStatus.Proposed,
    };

    private void SaveWithApproval(PatchProposal proposal, ApprovalStatus status = ApprovalStatus.Pending)
    {
        var set = new PatchSet { MissionId = _mission.Id, TaskId = _mission.Tasks[0].Id, Summary = "s", Proposals = new() { proposal } };
        _memory.SavePatchSet(set);
        _memory.SaveApprovalRequest(new ApprovalRequest
        {
            MissionId = _mission.Id, TaskId = _mission.Tasks[0].Id, TargetId = proposal.Id,
            Title = $"Approve {proposal.FilePath}", Description = "test", Status = status,
        });
    }

    [Fact]
    public void IdenticalPendingChange_IsDetectedAsDuplicate()
    {
        SaveWithApproval(NewProposal());
        Assert.True(_memory.HasDuplicatePendingApproval(NewProposal()));
    }

    [Fact]
    public void DifferentContentOrFile_IsNotADuplicate()
    {
        SaveWithApproval(NewProposal());
        Assert.False(_memory.HasDuplicatePendingApproval(NewProposal(newC: "different")));
        Assert.False(_memory.HasDuplicatePendingApproval(NewProposal(file: "src/Bar.cs")));
    }

    [Fact]
    public void DecidedApprovals_DoNotBlockNewRequests()
    {
        // Once the prior request is approved/rejected it is no longer "stacking" — a fresh
        // identical proposal may legitimately queue again (e.g. the file changed back).
        SaveWithApproval(NewProposal(), ApprovalStatus.Rejected);
        Assert.False(_memory.HasDuplicatePendingApproval(NewProposal()));
    }

    [Fact]
    public void NullContents_CompareCorrectly()
    {
        SaveWithApproval(NewProposal(oldC: null, newC: "created"));
        Assert.True(_memory.HasDuplicatePendingApproval(NewProposal(oldC: null, newC: "created")));
        Assert.False(_memory.HasDuplicatePendingApproval(NewProposal(oldC: "x", newC: "created")));
    }
}
