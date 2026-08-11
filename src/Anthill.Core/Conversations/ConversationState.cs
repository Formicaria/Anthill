using Anthill.Core.Memory;

namespace Anthill.Core.Conversations;

/// <summary>
/// What a conversation is doing right now, what it has done, and what it is waiting on.
///
/// Three separate questions, kept separate. Collapsing them into one "status" string is the usual
/// design and it is why status displays go stale: "running" cannot express that the colony finished
/// its work forty seconds ago and is now waiting for a human, which is the single most common state
/// an operator needs to notice and act on.
/// </summary>
public sealed record ConversationState(
    string ConversationId,
    string Doing,
    IReadOnlyList<string> Did,
    IReadOnlyList<string> WaitingOn,
    EscalationPolicy Policy,
    bool Cancelled)
{
    /// <summary>
    /// True when the colony can make no further progress without a human. The one condition worth
    /// making a first-class property, because it is the only state where WAITING is caused by the
    /// operator's own inaction rather than by work in flight.
    /// </summary>
    public bool NeedsOperator => WaitingOn.Count > 0;
}

/// <summary>
/// v3.7.0 — "what is it doing", answered from what was actually recorded.
///
/// Derived, never stored. A stored status is a second thing to keep in step with reality, and it
/// goes wrong in exactly the situation where an operator relies on it: a process that died leaves
/// its last write saying "running" forever. Everything here is computed from the transcript and the
/// decision log, so the answer cannot outlive the facts it is drawn from.
/// </summary>
public static class ConversationStateReader
{
    /// <summary>Turns summarised in the "what did it do" list. Beyond this an operator reads the transcript.</summary>
    public const int RecentTurns = 10;

    public static ConversationState Read(SqliteMemory memory, string conversationId)
    {
        var conversation = memory.LoadConversation(conversationId);
        if (conversation is null)
            return new ConversationState(conversationId, "no such conversation",
                Array.Empty<string>(), Array.Empty<string>(), EscalationPolicy.Ask, false);

        var turns = memory.LoadConversationTurns(conversationId);
        var decisions = memory.LoadEscalationDecisions(conversationId);

        // What it is WAITING ON: actions that were refused for want of a decision, and have not
        // since been allowed. A refusal that was later approved is not still waiting — showing it
        // would train an operator to ignore the list, which is the only way this fails.
        var allowedActions = decisions.Where(d => d.Allowed)
            .Select(d => d.Action).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var waiting = decisions
            .Where(d => !d.Allowed && d.DecidedBy == "nobody")
            .Select(d => d.Action)
            .Where(a => !allowedActions.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        var did = turns.TakeLast(RecentTurns).Select(Describe).ToList();

        return new ConversationState(
            conversationId,
            Doing: Doing(memory, conversation, turns, waiting),
            Did: did,
            WaitingOn: waiting,
            conversation.EffectivePolicy,
            conversation.Cancelled);
    }

    /// <summary>
    /// The present tense, in the order an operator cares about: cancelled beats waiting, waiting
    /// beats working, and "nothing yet" is a real answer rather than a blank.
    /// </summary>
    private static string Doing(SqliteMemory memory, Conversation conversation,
        IReadOnlyList<ConversationTurn> turns, IReadOnlyList<string> waiting)
    {
        if (conversation.Cancelled) return "cancelled";
        if (waiting.Count > 0)
            return $"waiting for an operator decision on: {string.Join(", ", waiting)}";
        if (conversation.MissionIds.Count > 0)
        {
            // v0.3.8.42, found by the live walkthrough: this said "running mission X" FOREVER —
            // the exact stored-status lie the class doc above warns about, computed once and never
            // re-checked. The canonical evaluation is written when a mission settles; its absence
            // means the work is genuinely still in flight. A settled mission's account lives in
            // the transcript and mission history, and idle is spelled "".
            var last = conversation.MissionIds[^1];
            if (memory.LoadMissionEvaluation(last) is null)
                return $"running mission {last}";
            return "";
        }
        if (turns.Count == 0) return "no turns yet";
        // v0.3.8.42: "conversational work" used to be returned here FOREVER — replies are produced
        // synchronously inside the turn, so a persistent working state was a claim about work this
        // reader could never see, and the console faithfully rendered it as an eternal spinner. An
        // operator turn with no reply after it is the one persistent state worth naming: it means
        // the answer failed or was interrupted. Everything else is idle, and idle is spelled "".
        return string.Equals(turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
            ? "unanswered — no reply was recorded for the last message"
            : "";
    }

    private static string Describe(ConversationTurn turn)
    {
        var what = turn.MissionId is { Length: > 0 }
            ? $"escalated into mission {turn.MissionId}"
            : turn.ToolsCalled.Count > 0
                ? $"called {string.Join(", ", turn.ToolsCalled)}"
                : turn.Role;

        // The model is named when one produced the turn. Capability-aware routing can substitute a
        // model mid-conversation, and an operator reading back a surprising answer needs to know
        // which model actually produced it rather than which one was configured.
        return turn.Model is { Length: > 0 }
            ? $"#{turn.Ordinal} {what} [{turn.Model}]"
            : $"#{turn.Ordinal} {what}";
    }
}
