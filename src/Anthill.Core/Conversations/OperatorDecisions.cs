using Anthill.Core.Memory;

namespace Anthill.Core.Conversations;

/// <summary>
/// v0.3.8.102 — the first CONSUMER of the rule v0.3.8.46 wrote: "an answer given IS an operator
/// decision; the record must say so whether or not the work ends up needing it."
///
/// A mission does not run inside the conversation's ambient <see cref="ConversationScope"/> — it
/// runs in the background, deliberately, and the .101 composed missions prove it: their checks
/// dispatch without per-tool answers. So a tool that needs the operator's decision MID-MISSION
/// cannot ask the scope; it must read the DURABLE record the runner saved at mission start. The
/// permission is the record, and this is where the record is read.
///
/// Resolution is by the mission's own lineage: the conversation whose MissionIds carry this
/// mission, then the LATEST saved decision for the action. Latest, because an operator who
/// refused and then approved has decided twice and the second answer is the standing one — the
/// same last-write-wins the settings store applies. No conversation, or no decision, returns
/// null: absence is not consent, and the caller says so.
/// </summary>
public static class OperatorDecisions
{
    public static EscalationDecision? ForMission(SqliteMemory memory, string? missionId, string action)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(action)) return null;
        try
        {
            var conversation = memory.LoadConversations()
                .FirstOrDefault(c => c.MissionIds.Contains(missionId, StringComparer.OrdinalIgnoreCase));
            if (conversation is null) return null;

            return memory.LoadEscalationDecisions(conversation.Id)
                .Where(d => string.Equals(d.Action, action, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.DecidedAt)
                .LastOrDefault();
        }
        catch
        {
            // An unreadable store yields NO decision, which the caller refuses on — the S3 rule:
            // an outage is never permission.
            return null;
        }
    }
}
