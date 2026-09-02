using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// AN APPROVED DECISION REPLAYS THE REFUSED STEP. v0.3.8.110, PLAN.md §2b `.110`.
///
/// THE ITEM THIS CLOSES was deferred from `.105` to `.106` to `.110`, and §2c said the same thing
/// each time: "replaying the step needs a mission to re-enter execution at a task, and no lane does
/// that today." That was exact. Nothing in this tree could read a finished mission back as an object
/// graph — `GetMission` returns a dictionary, `new Mission` appears four times and every one of them
/// CREATES a mission — so the in-memory graph died with `RunMission` and there was nothing to
/// re-enter execution with.
///
/// THE PART THAT WAS NOT OBVIOUS, and would have made a rehydrator alone useless: approving wrote to
/// `approval_requests` and the mission-lane gate read `escalation_decisions`. Two disjoint tables.
/// An operator's approval was recorded, visible in the UI, and completely invisible to the runtime —
/// so a replay would have refused identically and filed the same question again, and the feature
/// would have looked implemented while changing nothing. The first three tests below are about that
/// gap, not about resumption, because that gap is what made resumption impossible.
/// </summary>
public class MissionResumptionTests : IDisposable
{
    private readonly string _dir;

    public MissionResumptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-resume-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"r-{Guid.NewGuid():N}.db"));

    private const string Action = "external_action_execute";

    /// <summary>A saved mission with one refused task, and the refusal event that names it.</summary>
    private static Mission Paused(SqliteMemory memory)
    {
        var mission = new Mission
        {
            Goal = "Post the release summary to the team's incident webhook.",
            Status = MissionStatus.Failed,
        };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "Perform the send",
            Description = "Resolve the destination and deliver the message.",
            AssignedAnt = "tester",
            TaskType = "external_action",
            Status = TaskStatus.Failed,
            FailureReason = "escalation_refused: no operator decision is recorded",
            FailureType = "authorization_failure",
            FailedAt = DateTime.UtcNow,
            ElapsedSeconds = 0.4,
        });
        memory.SaveMission(mission);

        memory.LogEvent(mission.Id, "escalation_refused",
            $"Tool REFUSED pending operator decision: {Action}",
            mission.Tasks[0].Id, "tester",
            new() { ["tool_name"] = Action, ["awaiting_decision"] = true });

        return mission;
    }

    // -------------------------------------------------------------------------------------------
    // The gap that made resumption impossible
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT, STATED AS THE PROPERTY THAT WOULD HAVE CAUGHT IT. An approved `ToolUse` request
    /// is an operator decision, and until `.110` the mission lane could not read one.
    /// </summary>
    [Fact]
    public void AnApprovedRequest_IsADecisionTheMissionLaneCanRead()
    {
        using var memory = Memory();
        var mission = Paused(memory);

        OperatorDecisions.Request(memory, mission.Id, Action, "queen");
        Assert.Null(OperatorDecisions.Decided(memory, mission.Id, Action));   // pending is not an answer

        var pending = memory.ApprovalsForMission(mission.Id)
            .First(a => a.ActionType == ApprovalActionType.ToolUse);
        memory.UpdateApprovalStatus(pending.Id, ApprovalStatus.Approved, "yes");

        var decided = OperatorDecisions.Decided(memory, mission.Id, Action);

        Assert.NotNull(decided);
        Assert.True(decided!.Allowed);
        Assert.Equal("operator", decided.DecidedBy);
        Assert.False(decided.AwaitingDecision,
            "a decision attributed to nobody keeps re-filing the question it just answered.");
    }

    /// <summary>A rejection is an ANSWER, not an absence — and it must not read as permission.</summary>
    [Fact]
    public void ARejectedRequest_IsADecisionThatRefuses()
    {
        using var memory = Memory();
        var mission = Paused(memory);

        OperatorDecisions.Request(memory, mission.Id, Action, "queen");
        var pending = memory.ApprovalsForMission(mission.Id)
            .First(a => a.ActionType == ApprovalActionType.ToolUse);
        memory.UpdateApprovalStatus(pending.Id, ApprovalStatus.Rejected, "no");

        var decided = OperatorDecisions.Decided(memory, mission.Id, Action);

        Assert.NotNull(decided);
        Assert.False(decided!.Allowed);
    }

    /// <summary>
    /// AND `ForMission` HONOURS IT — the read every tool actually goes through. Without this the
    /// replay refuses identically and files the same question, which is the state `.105` shipped.
    /// </summary>
    [Fact]
    public void ForMission_ReadsTheApprovalLedger_AndStopsReAskingOnceAnswered()
    {
        using var memory = Memory();
        var mission = Paused(memory);

        // No decision anywhere: null, and the question is filed.
        Assert.Null(OperatorDecisions.ForMission(memory, mission.Id, Action));
        var pending = memory.ApprovalsForMission(mission.Id)
            .First(a => a.ActionType == ApprovalActionType.ToolUse);

        memory.UpdateApprovalStatus(pending.Id, ApprovalStatus.Approved, "yes");

        var decision = OperatorDecisions.ForMission(memory, mission.Id, Action);
        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
        Assert.Empty(memory.PendingOperatorDecisions(mission.Id));
    }

    // -------------------------------------------------------------------------------------------
    // Rehydration
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE PIECE THAT DID NOT EXIST. A mission read back as the graph it was, not as rows.
    /// </summary>
    [Fact]
    public void AFinishedMission_ReadsBackAsItsOwnGraph()
    {
        using var memory = Memory();
        var mission = Paused(memory);

        var loaded = MissionRehydration.Load(memory, mission.Id);

        Assert.NotNull(loaded);
        Assert.Equal(mission.Id, loaded!.Id);
        Assert.Equal(mission.Goal, loaded.Goal);
        Assert.Equal(MissionStatus.Failed, loaded.Status);

        var task = Assert.Single(loaded.Tasks);
        Assert.Equal(mission.Tasks[0].Id, task.Id);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal("tester", task.AssignedAnt);
        Assert.Equal("external_action", task.TaskType);
    }

    /// <summary>
    /// A MISSION THE STORE DOES NOT HOLD IS NULL, NOT AN EMPTY MISSION. "This does not exist" and
    /// "this did nothing" are different facts, and only one of them is safe to act on — an empty
    /// graph handed to a resumption would report that there was nothing to replay, which is true of
    /// both and useful for neither.
    /// </summary>
    [Fact]
    public void AMissionTheStoreDoesNotHold_IsNull()
    {
        using var memory = Memory();

        Assert.Null(MissionRehydration.Load(memory, "m_does_not_exist"));
        Assert.Null(MissionRehydration.Load(memory, ""));
        Assert.Null(MissionRehydration.Load(memory, null));
    }

    /// <summary>
    /// The status vocabulary round-trips. `ParseTaskStatus` has existed since the enum was written
    /// and had NO CALLER anywhere in the tree — declared and reaching nobody, this repository's
    /// house defect, and the rehydrator is its first consumer. Its mission-status twin did not exist
    /// at all and is added here.
    /// </summary>
    [Fact]
    public void TheStatusVocabularies_RoundTrip()
    {
        foreach (var status in Enum.GetValues<MissionStatus>())
            Assert.Equal(status, MissionRehydration.ParseMissionStatus(status.Value()));

        foreach (var status in Enum.GetValues<TaskStatus>())
            Assert.Equal(status, EnumExtensions.ParseTaskStatus(status.Value()));
    }
}
