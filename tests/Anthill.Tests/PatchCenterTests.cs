using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.16 Patch Center backend queries executed against a real SQLite database (not a mock), so a
/// dialect or column error surfaces here instead of as a 500 in the live console. Covers the
/// filterable list (status / mission / objective / file), the per-mission and per-objective patch
/// rollups, and the ended-objectives listing.
/// </summary>
public class PatchCenterTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly Mission _mission;

    public PatchCenterTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_patchcenter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
        _mission = new Mission { Goal = "patch center test", Status = MissionStatus.Complete };
        _mission.Tasks.Add(new Task { Title = "coder", AssignedAnt = "coder" });
        _memory.SaveMission(_mission);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PatchProposal SavePatch(string file, PatchStatus status, string risk = "low")
    {
        var proposal = new PatchProposal
        {
            FilePath = file, ChangeType = PatchChangeType.Modify, Reason = "r", Risk = risk,
            OldContent = "old", NewContent = "new", RequiresApproval = true, Status = status,
        };
        _memory.SavePatchSet(new PatchSet
        {
            MissionId = _mission.Id, TaskId = _mission.Tasks[0].Id, Summary = "s", Proposals = new() { proposal },
        });
        _memory.UpdatePatchStatus(proposal.Id, status);
        _memory.SaveApprovalRequest(new ApprovalRequest
        {
            MissionId = _mission.Id, TaskId = _mission.Tasks[0].Id, TargetId = proposal.Id,
            Title = $"Approve {file}", Description = "d", Status = ApprovalStatus.Pending,
        });
        return proposal;
    }

    [Fact]
    public void ListPatchesForCenter_ReturnsRowsWithApprovalAndMissionGoal()
    {
        var p = SavePatch("src/A.cs", PatchStatus.Proposed);
        var rows = _memory.ListPatchesForCenter();
        var row = Assert.Single(rows);
        Assert.Equal(p.Id, row.GetValueOrDefault("id")?.ToString());
        Assert.Equal("src/A.cs", row.GetValueOrDefault("file_path")?.ToString());
        Assert.Equal("patch center test", row.GetValueOrDefault("mission_goal")?.ToString());
        Assert.NotNull(row.GetValueOrDefault("approval_id")); // approval joined
    }

    [Fact]
    public void ListPatchesForCenter_FiltersByStatusAndFile()
    {
        SavePatch("src/A.cs", PatchStatus.Proposed);
        SavePatch("src/B.cs", PatchStatus.Applied);

        Assert.Single(_memory.ListPatchesForCenter(status: PatchStatus.Applied));
        Assert.Single(_memory.ListPatchesForCenter(filePathContains: "B.cs"));
        Assert.Equal(2, _memory.ListPatchesForCenter().Count);
    }

    [Fact]
    public void PatchCountsForMission_RollsUpByStatus()
    {
        SavePatch("src/A.cs", PatchStatus.Proposed);
        SavePatch("src/B.cs", PatchStatus.Applied);
        SavePatch("src/C.cs", PatchStatus.Applied);

        var counts = _memory.PatchCountsForMission(_mission.Id);
        Assert.Equal(3, Convert.ToInt32(counts["total"]));
        Assert.Equal(2, Convert.ToInt32(counts["applied"]));
        Assert.Equal(1, Convert.ToInt32(counts["pending"])); // "pending" mirrors "proposed"
    }

    [Fact]
    public void PatchCountsForObjective_JoinsThroughAutonomyRuns()
    {
        SavePatch("src/A.cs", PatchStatus.Applied);
        var objective = new Objective { Title = "Obj", Charter = "c" };
        _memory.SaveObjective(objective);
        _memory.SaveAutonomyRun(new AutonomyRun
        {
            ObjectiveId = objective.Id, MissionId = _mission.Id, GeneratedGoal = "g", MissionStatus = "complete",
        });

        var counts = _memory.PatchCountsForObjective(objective.Id);
        Assert.Equal(1, Convert.ToInt32(counts["total"]));
        Assert.Equal(1, Convert.ToInt32(counts["applied"]));
    }

    [Fact]
    public void EmptyMission_YieldsAllZeroRollup()
    {
        var counts = _memory.PatchCountsForMission("no-such-mission");
        Assert.Equal(0, Convert.ToInt32(counts["total"]));
    }

    [Fact]
    public void ListEndedObjectives_IncludesDoneAndRetired_NotActive()
    {
        var active = new Objective { Title = "Active", Charter = "c", Status = ObjectiveStatus.Active };
        var done = new Objective { Title = "Done", Charter = "c", Status = ObjectiveStatus.Done };
        var retired = new Objective { Title = "Retired", Charter = "c", Status = ObjectiveStatus.Paused };
        retired.Metadata["retired_code"] = "looping_goals";
        _memory.SaveObjective(active);
        _memory.SaveObjective(done);
        _memory.SaveObjective(retired);

        var ended = _memory.ListEndedObjectives();
        Assert.Contains(ended, o => o.Title == "Done");
        Assert.Contains(ended, o => o.Title == "Retired");
        Assert.DoesNotContain(ended, o => o.Title == "Active");
    }
}
