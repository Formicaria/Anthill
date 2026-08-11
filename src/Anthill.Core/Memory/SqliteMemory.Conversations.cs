using Anthill.Core.Common;
using Anthill.Core.Conversations;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.7.0 — conversations, turns and escalation decisions, persisted.
///
/// The phase's exit gates are unmeetable without this. "A conversation survives process restart
/// with its transcript and route intact" is self-evidently storage. "The transcript of an escalated
/// run shows the conversation and the mission as one history" is subtler: a restart mid-mission
/// would otherwise leave the mission with an audit trail whose first half is missing — and the
/// missing half is the one explaining why the work was started.
///
/// Decisions are their own table rather than a column on the turn. An operator answering "why was
/// this allowed" is asking about ACTIONS, not turns, and one turn can take several.
/// </summary>
public sealed partial class SqliteMemory
{
    public void SaveConversation(Conversation conversation)
    {
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO conversations
                    (id, title, role, policy, policy_set_by, policy_set_at, mission_ids_json,
                     cancelled, pinned, project_id, created_at, updated_at)
                  VALUES (@id, @title, @role, @policy, @by, @at, @missions, @cancelled, @pinned, @project, @created, @updated)
                  ON CONFLICT(id) DO UPDATE SET
                    title=@title, role=@role, policy=@policy, policy_set_by=@by, policy_set_at=@at,
                    mission_ids_json=@missions, cancelled=@cancelled, pinned=@pinned, project_id=@project, updated_at=@updated",
                ("@id", conversation.Id),
                ("@title", conversation.Title),
                ("@role", conversation.Role),
                // By NAME, never by ordinal — an enum's numeric value reorders the moment someone
                // inserts a policy in the middle, and a database of integers that silently mean a
                // DIFFERENT permission level is the worst version of that mistake.
                ("@policy", conversation.Policy.ToString()),
                ("@by", (object?)conversation.PolicySetBy ?? DBNull.Value),
                ("@at", (object?)conversation.PolicySetAt?.ToIso() ?? DBNull.Value),
                ("@missions", Json.SafeDumps(conversation.MissionIds)),
                ("@cancelled", conversation.Cancelled ? 1 : 0),
                ("@pinned", conversation.Pinned ? 1 : 0),
                ("@project", (object?)conversation.ProjectId ?? DBNull.Value),
                ("@created", conversation.CreatedAt.ToIso()),
                ("@updated", conversation.UpdatedAt.ToIso()));
        }
    }

    public Conversation? LoadConversation(string id) =>
        Query("SELECT * FROM conversations WHERE id=@id", ("@id", id ?? "")).Select(ReadConversation).FirstOrDefault();

    /// <summary>Pinned first, then most recently touched — the rail's order, decided here once.</summary>
    public IReadOnlyList<Conversation> LoadConversations() =>
        Query("SELECT * FROM conversations ORDER BY pinned DESC, updated_at DESC").Select(ReadConversation).ToList();

    /// <summary>
    /// v0.3.8.46: search over what is actually stored — titles and turn content, case-insensitive,
    /// pinned-then-recent like the rail. The match is plain substring (SQL LIKE with escaped
    /// wildcards), which is exactly what the search box claims and nothing more.
    /// </summary>
    public IReadOnlyList<Conversation> SearchConversations(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return LoadConversations();
        var like = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return Query(
            @"SELECT DISTINCT c.* FROM conversations c
              LEFT JOIN conversation_turns t ON t.conversation_id = c.id
              WHERE c.title LIKE @q ESCAPE '\' OR t.content LIKE @q ESCAPE '\'
              ORDER BY c.pinned DESC, c.updated_at DESC
              LIMIT @limit",
            ("@q", like), ("@limit", Math.Clamp(limit, 1, 200)))
            .Select(ReadConversation).ToList();
    }

    public void SaveConversationTurn(ConversationTurn turn)
    {
        if (turn is null || string.IsNullOrWhiteSpace(turn.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO conversation_turns
                    (id, conversation_id, ordinal, role, content, provider, model,
                     tools_offered_json, tools_called_json, mission_id, prompt_tokens,
                     completion_tokens, created_at)
                  VALUES (@id, @cid, @ord, @role, @content, @provider, @model,
                          @offered, @called, @mission, @ptok, @ctok, @created)
                  ON CONFLICT(id) DO UPDATE SET content=@content, tools_called_json=@called, mission_id=@mission",
                ("@id", turn.Id), ("@cid", turn.ConversationId), ("@ord", turn.Ordinal),
                ("@role", turn.Role), ("@content", turn.Content),
                ("@provider", (object?)turn.Provider ?? DBNull.Value),
                ("@model", (object?)turn.Model ?? DBNull.Value),
                ("@offered", Json.SafeDumps(turn.ToolsOffered)),
                ("@called", Json.SafeDumps(turn.ToolsCalled)),
                ("@mission", (object?)turn.MissionId ?? DBNull.Value),
                ("@ptok", (object?)turn.PromptTokens ?? DBNull.Value),
                ("@ctok", (object?)turn.CompletionTokens ?? DBNull.Value),
                ("@created", turn.CreatedAt.ToIso()));
        }
    }

    /// <summary>The transcript, in order. Ordinal rather than timestamp: two turns can share a clock tick.</summary>
    public IReadOnlyList<ConversationTurn> LoadConversationTurns(string conversationId) =>
        Query("SELECT * FROM conversation_turns WHERE conversation_id=@cid ORDER BY ordinal",
            ("@cid", conversationId ?? ""))
        .Select(row => new ConversationTurn(
            row.GetValueOrDefault("id")?.ToString() ?? "",
            row.GetValueOrDefault("conversation_id")?.ToString() ?? "",
            Convert.ToInt32(row.GetValueOrDefault("ordinal") ?? 0),
            row.GetValueOrDefault("role")?.ToString() ?? "",
            row.GetValueOrDefault("content")?.ToString() ?? "")
        {
            Provider = row.GetValueOrDefault("provider")?.ToString(),
            Model = row.GetValueOrDefault("model")?.ToString(),
            ToolsOffered = Json.SafeLoadList(row.GetValueOrDefault("tools_offered_json")?.ToString()),
            ToolsCalled = Json.SafeLoadList(row.GetValueOrDefault("tools_called_json")?.ToString()),
            MissionId = row.GetValueOrDefault("mission_id")?.ToString(),
            PromptTokens = ReadNullableInt(row.GetValueOrDefault("prompt_tokens")),
            CompletionTokens = ReadNullableInt(row.GetValueOrDefault("completion_tokens")),
            CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        }).ToList();

    /// <summary>Null stays null: an unreported token count must never rehydrate as zero.</summary>
    private static int? ReadNullableInt(object? value) =>
        value is null || value is DBNull ? null : Convert.ToInt32(value);

    // ---- v0.3.8.47: attachments -----------------------------------------------------------------

    public void SaveAttachment(ConversationAttachment attachment)
    {
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.Id)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO conversation_attachments
                    (id, conversation_id, turn_id, filename, bytes, content, created_at)
                  VALUES (@id, @cid, @tid, @name, @bytes, @content, @created)",
                ("@id", attachment.Id), ("@cid", attachment.ConversationId),
                ("@tid", attachment.TurnId), ("@name", attachment.Filename),
                ("@bytes", attachment.Bytes), ("@content", attachment.Content),
                ("@created", attachment.CreatedAt.ToIso()));
        }
    }

    public IReadOnlyList<ConversationAttachment> LoadTurnAttachments(string turnId) =>
        Query("SELECT * FROM conversation_attachments WHERE turn_id=@tid ORDER BY filename",
            ("@tid", turnId ?? ""))
        .Select(row => new ConversationAttachment(
            row.GetValueOrDefault("id")?.ToString() ?? "",
            row.GetValueOrDefault("conversation_id")?.ToString() ?? "",
            row.GetValueOrDefault("turn_id")?.ToString() ?? "",
            row.GetValueOrDefault("filename")?.ToString() ?? "",
            Convert.ToInt64(row.GetValueOrDefault("bytes") ?? 0L),
            row.GetValueOrDefault("content")?.ToString() ?? "")
        { CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()) })
        .ToList();

    /// <summary>
    /// Record what was decided about one side-effecting action.
    ///
    /// Every decision is stored, including refusals. An audit asking "did the colony try to do X"
    /// needs the attempts that were REFUSED as much as the ones that went ahead — arguably more,
    /// since a refused attempt is the one nobody saw happen.
    /// </summary>
    public void SaveEscalationDecision(EscalationDecision decision)
    {
        if (decision is null || string.IsNullOrWhiteSpace(decision.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO escalation_decisions
                    (id, conversation_id, action, allowed, policy, decided_by, decided_at, reason)
                  VALUES (@id, @cid, @action, @allowed, @policy, @by, @at, @reason)",
                ("@id", decision.Id), ("@cid", decision.ConversationId), ("@action", decision.Action),
                ("@allowed", decision.Allowed ? 1 : 0), ("@policy", decision.Policy.ToString()),
                ("@by", decision.DecidedBy), ("@at", decision.DecidedAt.ToIso()),
                ("@reason", (object?)decision.Reason ?? DBNull.Value));
        }
    }

    public IReadOnlyList<EscalationDecision> LoadEscalationDecisions(string conversationId) =>
        Query("SELECT * FROM escalation_decisions WHERE conversation_id=@cid ORDER BY decided_at",
            ("@cid", conversationId ?? ""))
        .Select(row => new EscalationDecision(
            row.GetValueOrDefault("id")?.ToString() ?? "",
            row.GetValueOrDefault("conversation_id")?.ToString() ?? "",
            row.GetValueOrDefault("action")?.ToString() ?? "",
            Convert.ToInt64(row.GetValueOrDefault("allowed") ?? 0L) != 0,
            Enum.TryParse<EscalationPolicy>(row.GetValueOrDefault("policy")?.ToString(), out var p)
                ? p : EscalationPolicy.Ask,
            row.GetValueOrDefault("decided_by")?.ToString() ?? "",
            AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("decided_at")?.ToString()),
            row.GetValueOrDefault("reason")?.ToString())).ToList();

    private static Conversation ReadConversation(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Title = row.GetValueOrDefault("title")?.ToString() ?? "",
        Role = row.GetValueOrDefault("role")?.ToString() ?? "researcher",
        // An unreadable policy reads as Ask. Fail closed: the cost of asking when you did not need
        // to is an interruption; the cost of the reverse is unattributed side effects.
        Policy = Enum.TryParse<EscalationPolicy>(row.GetValueOrDefault("policy")?.ToString(), out var policy)
            ? policy : EscalationPolicy.Ask,
        PolicySetBy = row.GetValueOrDefault("policy_set_by")?.ToString(),
        PolicySetAt = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("policy_set_at")?.ToString()),
        MissionIds = Json.SafeLoadList(row.GetValueOrDefault("mission_ids_json")?.ToString()),
        Cancelled = Convert.ToInt64(row.GetValueOrDefault("cancelled") ?? 0L) != 0,
        Pinned = Convert.ToInt64(row.GetValueOrDefault("pinned") ?? 0L) != 0,
        ProjectId = row.GetValueOrDefault("project_id") is null or DBNull ? null : row.GetValueOrDefault("project_id")?.ToString(),
        CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        UpdatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("updated_at")?.ToString()),
    };
}
