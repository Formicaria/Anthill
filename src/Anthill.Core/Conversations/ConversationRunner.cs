using Anthill.Core.Memory;

namespace Anthill.Core.Conversations;

/// <summary>How a turn is executed. The same conversation, two execution modes.</summary>
public enum ConversationMode
{
    /// <summary>
    /// Bounded work in the tool-calling loop: ask, run tools, feed results back, stop. Turn, tool
    /// call, wall-clock and repeat budgets all apply. The default, because most turns are questions.
    /// </summary>
    Chat = 0,

    /// <summary>
    /// The full mission pipeline: a plan, multiple tasks, specialists, verification. Reached only by
    /// ESCALATION — see <see cref="ConversationRunner.StartMissionAction"/>.
    /// </summary>
    Mission,
}

/// <summary>What one turn did, and where it went.</summary>
public sealed record ConversationOutcome(
    ConversationMode Mode,
    bool Started,
    string? MissionId,
    string Summary,
    EscalationDecision? Decision = null);

/// <summary>
/// v3.7.0 — the escalation boundary: what turns a conversation into a mission.
///
/// The phase asks for "one conversational surface that starts as chat and ESCALATES into autonomous
/// execution, with the escalation itself explicit, bounded and approved". The word doing the work is
/// EXPLICIT. Three designs were available and only one of them is defensible:
///
///   - the model decides when to escalate. Rejected: the agent's judgement about what deserves
///     autonomous multi-task execution would become a security-relevant decision, and a model that
///     wants to be helpful escalates.
///   - escalate automatically on complexity. Rejected for the same reason with extra steps — the
///     heuristic becomes the security boundary, and nobody can say what it will do next week.
///   - the CALLER asks for a mission, and that request is gated. Chosen.
///
/// So starting a mission is itself a side-effecting action, named
/// <see cref="StartMissionAction"/>, and it goes through the SAME <see cref="EscalationGate"/> as
/// apply_patch. That is the entire point of reusing the gate rather than inventing a second one: an
/// operator who set a standing policy has already answered this question, and one who did not gets
/// asked exactly once, in the place they already expect to be asked.
/// </summary>
public sealed class ConversationRunner
{
    /// <summary>
    /// The action name for "turn this conversation into a mission".
    ///
    /// Registered in <see cref="EscalationGate.SideEffecting"/> rather than special-cased here,
    /// because a boundary enforced in two places is a boundary that eventually disagrees with
    /// itself. It is not a tool and never will be — no model may call it.
    /// </summary>
    public const string StartMissionAction = "start_mission";

    private readonly SqliteMemory _memory;
    private readonly Func<string, CancellationToken, string> _startMission;

    /// <summary>
    /// <paramref name="startMission"/> is the mission pipeline, injected. The runner decides WHETHER
    /// a mission starts; the Queen decides what a mission does. Keeping those apart is what lets the
    /// escalation boundary be tested without standing up a colony.
    /// </summary>
    public ConversationRunner(SqliteMemory memory, Func<string, CancellationToken, string> startMission)
    {
        _memory = memory;
        _startMission = startMission;
    }

    /// <summary>
    /// Record a turn and, if it asks for one, escalate into a mission.
    ///
    /// <paramref name="answers"/> carries the operator's replies for this turn — the same shape the
    /// tool gate uses, so an operator answering "approve" for <see cref="StartMissionAction"/> is
    /// doing exactly what they do for any other side effect.
    /// </summary>
    public ConversationOutcome Run(
        Conversation conversation,
        string message,
        ConversationMode requested = ConversationMode.Chat,
        IReadOnlyDictionary<string, string>? answers = null,
        CancellationToken cancel = default)
    {
        if (conversation is null)
            return new ConversationOutcome(requested, false, null, "no conversation");

        // A cancelled conversation starts nothing. The exit gate says cancelling a conversation
        // cancels the work it started; refusing to start MORE work is the other half of that, and
        // the cheaper half — stopping something is harder than not beginning it.
        if (conversation.Cancelled)
            return new ConversationOutcome(requested, false, null,
                "this conversation is cancelled and cannot start new work");

        var ordinal = _memory.LoadConversationTurns(conversation.Id).Count + 1;

        if (requested == ConversationMode.Chat)
        {
            // Chat is not gated HERE. The tools it may call are gated at dispatch, by the same gate,
            // which is the correct place: a conversation that only reads needs no permission, and
            // one that tries to write is stopped at the write rather than at the sentence before it.
            RecordTurn(conversation, ordinal, message, null);
            return new ConversationOutcome(ConversationMode.Chat, true, null,
                "handled as bounded conversational work");
        }

        var decision = EscalationGate.Evaluate(conversation, StartMissionAction,
            answers?.GetValueOrDefault(StartMissionAction));
        try { _memory.SaveEscalationDecision(decision); } catch { }

        if (!decision.Allowed)
        {
            // The turn is recorded even though nothing ran. An attempt to escalate that was refused
            // is part of the conversation's history — arguably the most interesting part, since it
            // is the moment the colony wanted more authority than it had.
            RecordTurn(conversation, ordinal, message, null);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"escalation refused: {decision.Reason}", decision);
        }

        var missionId = _startMission(message, cancel);

        RecordTurn(conversation, ordinal, message, missionId);
        _memory.SaveConversation(conversation with
        {
            // The link that makes the conversation and the mission ONE history, which is the exit
            // gate. Recorded on both sides: the turn says which mission it started, and the
            // conversation lists what it has started, so the join works from either direction.
            MissionIds = conversation.MissionIds.Append(missionId).Distinct().ToList(),
            UpdatedAt = Common.AnthillTime.NowUtc(),
        });

        return new ConversationOutcome(ConversationMode.Mission, true, missionId,
            $"escalated into mission {missionId}", decision);
    }

    private void RecordTurn(Conversation conversation, int ordinal, string message, string? missionId) =>
        _memory.SaveConversationTurn(new ConversationTurn(
            Guid.NewGuid().ToString("N")[..12], conversation.Id, ordinal, "user", message ?? "")
        {
            MissionId = missionId,
        });
}
