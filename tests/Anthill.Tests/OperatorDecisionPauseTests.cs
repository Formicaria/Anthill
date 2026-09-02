using Anthill.Core.Common;
using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A MISSION WAITING ON A PERSON IS NOT A MISSION THAT FAILED. v0.3.8.105, PLAN.md §2b `.105`.
///
/// THE EXIT GATE'S SECOND CLAUSE: "a missing operator decision pauses rather than guesses."
///
/// WHAT WAS WRONG. Under `EscalationPolicy.Ask`, a side-effecting action with no recorded answer is
/// refused, and that rule is right and is untouched here — absence of an answer is not consent.
/// What was wrong is that the refusal was the ENTIRE response. Nothing recorded that an operator
/// had been left a decision to make, the task failed, and the mission was graded
/// `failed_permanent`. So the colony told its operator a falsehood about itself — nothing broke —
/// and invited the one response guaranteed to be useless, a retry that stops in the same place.
///
/// TWO REFUSALS THAT LOOKED IDENTICAL AND ARE NOT. A REJECTION is an answer: a human considered
/// this and said no, and the mission is finished with it. An ABSENT decision is a question. The
/// seam already existed in `EscalationDecision` — `Ask` with no answer records `DecidedBy` as
/// "nobody", every other path names a policy author or an operator — and nothing read it.
///
/// AND IT REUSES THE LEDGER THAT EXISTS. `approval_requests`, with
/// `ApprovalActionType.ToolUse` — declared since the enum was written, with no producer anywhere in
/// the tree. Every approval this colony has ever raised is a `PatchProposal`: three declared action
/// types, one reachable. This is the first caller of one of the other three.
/// </summary>
public class OperatorDecisionPauseTests : IDisposable
{
    private readonly string _dir;

    public OperatorDecisionPauseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-pause-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"p-{Guid.NewGuid():N}.db"));

    private static Mission Complete()
    {
        var mission = new Mission { Goal = "Send the weekly summary to the ops channel." };
        mission.Tasks.Add(new Anthill.Core.Domain.Task
        {
            Title = "Compose the summary", Description = "Compose the summary",
            AssignedAnt = "builder", TaskType = "build_answer",
            Status = TaskStatus.Complete, Result = "Summary composed.",
        });
        mission.Status = MissionStatus.Complete;
        return mission;
    }

    private static MissionEvaluation Grade(Mission mission, IReadOnlyList<string>? pending) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: false,
            pendingOperatorDecisions: pending);

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE PAUSE. An unanswered side-effecting action makes the mission `waiting_for_approval`,
    /// which is a code the vocabulary has carried since v2.19.0 and no mission has ever worn.
    /// </summary>
    [Fact]
    public void AnUnansweredSideEffect_PausesRatherThanFails()
    {
        var evaluation = Grade(Complete(), new[] { "send_external_message" });

        Assert.Equal(MissionOutcome.WaitingForApproval, evaluation.OutcomeCode);
        Assert.Equal(MissionStopReasons.AwaitingDecision, evaluation.StopReason);
        Assert.False(evaluation.IsPositive);
        // And NOT an escalation: both need a person, and they need different ones. An escalation
        // needs a human to look at something that went wrong; this needs one to answer a question.
        Assert.False(MissionStopReasons.IsEscalation(evaluation.StopReason));
        Assert.True(MissionStopReasons.IsPause(evaluation.StopReason));
    }

    /// <summary>A paused mission says WHAT it is waiting for. An outcome that cannot name the
    /// action leaves the operator exactly where the bare refusal did.</summary>
    [Fact]
    public void APausedMission_NamesWhatItIsWaitingFor()
    {
        var evaluation = Grade(Complete(), new[] { "send_external_message", "apply_patch" });

        Assert.Contains("send_external_message", evaluation.Explanation, StringComparison.Ordinal);
        Assert.Contains("apply_patch", evaluation.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MISSION WITH NO OUTSTANDING QUESTION IS GRADED EXACTLY AS BEFORE. Without this the pause
    /// above proves nothing — a change that graded everything as waiting would satisfy it.
    /// </summary>
    [Fact]
    public void AMissionWithNoPendingDecision_IsUnaffected()
    {
        Assert.Equal(Grade(Complete(), null).OutcomeCode,
                     Grade(Complete(), Array.Empty<string>()).OutcomeCode);
        Assert.NotEqual(MissionOutcome.WaitingForApproval, Grade(Complete(), null).OutcomeCode);
    }

    /// <summary>
    /// AND AN INTERRUPTED MISSION IS NOT "WAITING". A mission the operator cancelled, or one the
    /// clock stopped, was ended by something other than the question — saying it is waiting would
    /// be false, and would put a stopped mission in a state that invites an answer.
    /// </summary>
    [Fact]
    public void AStoppedMission_IsNotReportedAsWaiting()
    {
        var cancelled = MissionEvaluator.Evaluate(Complete(), MissionStopReasons.Cancelled, 0,
            MissionConstraints.None, false,
            pendingOperatorDecisions: new[] { "send_external_message" });

        Assert.Equal(MissionOutcome.Cancelled, cancelled.OutcomeCode);
        Assert.Equal(MissionStopReasons.Cancelled, cancelled.StopReason);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE SEAM, AT THE RECORD THAT CARRIES IT. A rejection and an unasked question both produce
    /// `Allowed: false`, and only one of them is a pause.
    /// </summary>
    [Fact]
    public void ARejectionIsAnAnswer_AndAnAbsentDecisionIsAQuestion()
    {
        var conversation = new Conversation { Id = "c1", Title = "t", Policy = EscalationPolicy.Ask };

        var unasked = EscalationGate.Evaluate(conversation, "apply_patch", operatorAnswer: null);
        var rejected = EscalationGate.Evaluate(conversation, "apply_patch", operatorAnswer: "reject");
        var approved = EscalationGate.Evaluate(conversation, "apply_patch", operatorAnswer: "approve");

        Assert.False(unasked.Allowed);
        Assert.True(unasked.AwaitingDecision);
        Assert.Equal(EscalationDecision.Undecided, unasked.DecidedBy);

        Assert.False(rejected.Allowed);
        Assert.False(rejected.AwaitingDecision);   // somebody said no; the mission is finished with it

        Assert.True(approved.Allowed);
        Assert.False(approved.AwaitingDecision);
    }

    /// <summary>A standing permission is a decision too, so it never reads as awaiting one.</summary>
    [Fact]
    public void AStandingPermission_IsNotAnAbsentDecision()
    {
        var standing = new Conversation
        {
            Id = "c2", Title = "t", Policy = EscalationPolicy.AutoApprove,
            PolicySetBy = "operator", PolicySetAt = AnthillTime.NowUtc(),
        };

        var decision = EscalationGate.Evaluate(standing, "apply_patch");
        Assert.True(decision.Allowed);
        Assert.False(decision.AwaitingDecision);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A PENDING PATCH APPROVAL DOES NOT PAUSE THE MISSION, and this narrowing is load-bearing
    /// rather than tidy.
    ///
    /// A pending patch approval is the normal, healthy end state of every coding mission this
    /// colony runs: the patch is proposed, the mission finishes, the operator reviews afterwards.
    /// Reading those as "waiting" would put every successful coding mission into
    /// `waiting_for_approval` and stop it ever reaching `completed_verified` — which auto-apply
    /// consumes, so the colony would become structurally incapable of applying its own patches.
    /// That is `.74`'s defect exactly, and this is the release where it could have been recommitted.
    /// </summary>
    [Fact]
    public void APendingPatchApproval_DoesNotPauseTheMission()
    {
        using var memory = Memory();
        var mission = Complete();
        memory.SaveMission(mission);

        memory.SaveApprovalRequest(new ApprovalRequest
        {
            MissionId = mission.Id, ActionType = ApprovalActionType.PatchProposal,
            TargetId = "patch-1", Status = ApprovalStatus.Pending, Title = "A proposed patch",
        });

        Assert.Empty(memory.PendingOperatorDecisions(mission.Id));
    }

    /// <summary>
    /// AND A PENDING TOOL-USE APPROVAL DOES, naming the tool. The store read and the outcome are
    /// the same fact reached from two ends — a reader must be able to get from the outcome back to
    /// the row the operator has to answer.
    /// </summary>
    [Fact]
    public void APendingToolDecision_IsReadBackByName()
    {
        using var memory = Memory();
        var mission = Complete();
        memory.SaveMission(mission);

        memory.SaveApprovalRequest(new ApprovalRequest
        {
            MissionId = mission.Id, ActionType = ApprovalActionType.ToolUse,
            TargetId = $"{mission.Id}:send_external_message", Status = ApprovalStatus.Pending,
            Title = "Approve 'send_external_message' for this mission?",
        });

        Assert.Equal(new[] { "send_external_message" }, memory.PendingOperatorDecisions(mission.Id));

        // An ANSWERED question is no longer outstanding — the pause must lift when it is settled,
        // or approving would leave the mission permanently described as waiting for the thing it
        // was just given.
        var raised = memory.ApprovalsForMission(mission.Id).Single();
        memory.UpdateApprovalStatus(raised.Id, ApprovalStatus.Approved);
        Assert.Empty(memory.PendingOperatorDecisions(mission.Id));
    }

    /// <summary>
    /// THE MISSION LANE FILES ITS OWN QUESTION, and this is the half that could most easily have
    /// been missed.
    ///
    /// A mission does not run inside the ambient `ConversationScope` — it runs in the background,
    /// deliberately, which is the `.102` finding that produced `OperatorDecisions` in the first
    /// place. So a pause wired only into the conversational escalation branch would have been
    /// invisible to every mission the colony has ever run, while passing every test written against
    /// that branch.
    ///
    /// `ForMission` is the one site in the mission lane that discovers the absence, so it is the
    /// site that records it. It still returns null — the caller refuses exactly as before, nothing
    /// is granted, and the only thing that changed is that the absence stopped being invisible.
    /// </summary>
    [Fact]
    public void TheMissionLane_FilesTheQuestionItRefusesOn()
    {
        using var memory = Memory();
        var mission = Complete();
        memory.SaveMission(mission);

        var decision = OperatorDecisions.ForMission(memory, mission.Id, "execute_external_action");

        Assert.Null(decision);   // unchanged: absence is not consent, and the caller refuses
        Assert.Equal(new[] { "execute_external_action" }, memory.PendingOperatorDecisions(mission.Id));

        // ASKED ONCE. A retried task reaches this again, and three identical pending rows would
        // make an operator answer the same question once per attempt to unblock one mission.
        OperatorDecisions.ForMission(memory, mission.Id, "execute_external_action");
        Assert.Single(memory.ApprovalsForMission(mission.Id));
    }

    /// <summary>
    /// AND A QUESTION ALREADY ANSWERED IS NOT ASKED AGAIN — including one answered NO. Re-raising a
    /// rejected request would put the mission back into "waiting" for something a human has already
    /// settled, which is worse than not asking: it invites them to answer it twice and reads as the
    /// colony ignoring the first answer.
    /// </summary>
    [Fact]
    public void AnAnsweredQuestion_IsNotAskedAgain()
    {
        using var memory = Memory();
        var mission = Complete();
        memory.SaveMission(mission);

        OperatorDecisions.Request(memory, mission.Id, "execute_external_action", "queen");
        var raised = memory.ApprovalsForMission(mission.Id).Single();
        memory.UpdateApprovalStatus(raised.Id, ApprovalStatus.Rejected, "no");

        OperatorDecisions.Request(memory, mission.Id, "execute_external_action", "queen");

        Assert.Single(memory.ApprovalsForMission(mission.Id));
        Assert.Empty(memory.PendingOperatorDecisions(mission.Id));
    }

    /// <summary>
    /// A PAUSED MISSION REINFORCES NOTHING, and this is the second half of a `.104` defect rather
    /// than a new rule.
    ///
    /// `UpdateMissionPheromones` scores three outcomes by name and sends everything else to
    /// `-0.08` — the heaviest negative in the switch, charged against every ant, worker and
    /// task-type path in the plan. `.104` added `blocked_missing_capability` to the vocabulary with
    /// documentation saying "nothing reinforces, promotes or retires on the strength of it", and
    /// then let it fall through to that default: a colony repeatedly asked for something it cannot
    /// do was demoting the workers that never ran. `waiting_for_approval` would have been the
    /// identical bug on its first day.
    /// </summary>
    [Theory]
    [InlineData(MissionOutcome.WaitingForApproval)]
    [InlineData(MissionOutcome.BlockedMissingCapability)]
    public void AnOutcomeThatIsNotEvidence_ReinforcesNothing(string outcome)
    {
        using var memory = Memory();
        var mission = Complete();
        memory.SaveMission(mission);

        memory.UpdateMissionPheromones(mission, outcome);

        var path = "planner_pattern:" + string.Join("_", mission.Tasks.Select(t => t.AssignedAnt));
        var trail = memory.GetPheromoneTrail(path);

        // Either no trail was written at all, or one was written with no negative movement. Both
        // are "this outcome taught the colony nothing"; neither is a demotion.
        if (trail is null) return;
        var strength = trail.Strength;
        Assert.True(strength >= 0.5,
            $"'{outcome}' moved the planner trail to {strength}. A mission that never ran, or one "
          + "that stopped to ask a question, is not evidence about the workers in its plan — and "
          + "the outcome's own documentation says so.");
    }
}
