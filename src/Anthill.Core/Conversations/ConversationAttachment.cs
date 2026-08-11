using Anthill.Core.Common;

namespace Anthill.Core.Conversations;

/// <summary>
/// v0.3.8.47 — a text file the operator handed a turn. TEXT on purpose: the conversation prompt
/// is plain text end to end (the least capable transport — a prompt on argv — sets the contract),
/// so a binary the model could never actually read is refused at the API instead of stored as a
/// lie. Content is capped there too; the cap is stated to the operator, not silently applied.
/// </summary>
public sealed record ConversationAttachment(
    string Id,
    string ConversationId,
    string TurnId,
    string Filename,
    long Bytes,
    string Content)
{
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
}
