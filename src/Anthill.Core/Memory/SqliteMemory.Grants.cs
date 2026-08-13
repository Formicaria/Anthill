using Anthill.Core.Common;

namespace Anthill.Core.Memory;

/// <summary>
/// v0.3.8.51 (field report) — a DIRECTORY GATE the operator opened: this project's colony may
/// reach this path. Mirrors the approval gate's shape — attributed, explicit, revocable — because
/// it is the same kind of decision aimed at the filesystem instead of at side effects.
/// </summary>
public sealed record ProjectGrant(string Id, string ProjectId, string Path)
{
    public string GrantedBy { get; init; } = "";
    public DateTime GrantedAt { get; init; } = AnthillTime.NowUtc();
}

public sealed partial class SqliteMemory
{
    public void SaveProjectGrant(ProjectGrant grant)
    {
        if (grant is null || string.IsNullOrWhiteSpace(grant.ProjectId) || string.IsNullOrWhiteSpace(grant.Path))
            return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR REPLACE INTO project_grants (id, project_id, path, granted_by, granted_at)
                  VALUES (@id, @pid, @path, @by, @at)",
                ("@id", grant.Id), ("@pid", grant.ProjectId), ("@path", grant.Path),
                ("@by", grant.GrantedBy), ("@at", grant.GrantedAt.ToIso()));
        }
    }

    public void DeleteProjectGrant(string id)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM project_grants WHERE id=@id", ("@id", id ?? ""));
        }
    }

    public IReadOnlyList<ProjectGrant> LoadProjectGrants(string projectId) =>
        Query("SELECT id, project_id, path, granted_by, granted_at FROM project_grants WHERE project_id=@pid ORDER BY granted_at",
            ("@pid", projectId ?? ""))
        .Select(row => new ProjectGrant(
            row.GetValueOrDefault("id")?.ToString() ?? "",
            row.GetValueOrDefault("project_id")?.ToString() ?? "",
            row.GetValueOrDefault("path")?.ToString() ?? "")
        {
            GrantedBy = row.GetValueOrDefault("granted_by")?.ToString() ?? "",
            GrantedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("granted_at")?.ToString()),
        }).ToList();

    /// <summary>
    /// The conversation that owns a mission, found through the turn that started it — the join
    /// v0.3.8.48 made real ("the turn says which mission it started"). Null for missions no
    /// conversation started (API-submitted, schedules run their own path through conversations
    /// anyway), which callers must treat as "governed by defaults", never as a grant.
    /// </summary>
    public Conversations.Conversation? FindConversationForMission(string missionId)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return null;
        var conversationId = Query(
            "SELECT conversation_id FROM conversation_turns WHERE mission_id=@mid LIMIT 1",
            ("@mid", missionId))
            .Select(r => r.GetValueOrDefault("conversation_id")?.ToString())
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(conversationId) ? null : LoadConversation(conversationId!);
    }
}
