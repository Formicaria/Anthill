using Anthill.Core.Common;
using Anthill.Core.Workspaces;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.5.0 — mission workspaces, persisted.
///
/// The row is the point. A workspace that exists only as a directory and an in-memory object cannot
/// answer either of the questions the roadmap's exit gates ask: what was this change based on, and
/// what survived the restart. Both are answered from here, and both keep being answerable after the
/// directory is gone — which is why <see cref="WorkspaceState.Cleaned"/> keeps its row.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Insert or update. Id is the primary key, so a state change is an upsert.</summary>
    public void SaveWorkspace(MissionWorkspace workspace)
    {
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO mission_workspaces
                    (id, mission_id, root, mode, source_root, base_revision, repository_fingerprint,
                     branch, state, retained_by, retain_reason, note, created_at, updated_at)
                  VALUES (@id, @mission, @root, @mode, @source, @base, @finger,
                          @branch, @state, @by, @why, @note, @created, @updated)
                  ON CONFLICT(id) DO UPDATE SET
                    root=@root, mode=@mode, base_revision=@base, repository_fingerprint=@finger,
                    branch=@branch, state=@state, retained_by=@by, retain_reason=@why,
                    note=@note, updated_at=@updated",
                ("@id", workspace.Id),
                ("@mission", workspace.MissionId),
                ("@root", workspace.Root),
                ("@mode", workspace.Mode),
                ("@source", workspace.SourceRoot),
                ("@base", workspace.BaseRevision),
                ("@finger", workspace.RepositoryFingerprint),
                ("@branch", workspace.Branch),
                // Stored as the NAME, not the ordinal. An enum's numeric value is an implementation
                // detail that reorders the moment someone inserts a state in the middle, and a
                // database full of integers that silently mean something else is unrecoverable.
                ("@state", workspace.State.ToString()),
                ("@by", (object?)workspace.RetainedBy ?? DBNull.Value),
                ("@why", (object?)workspace.RetainReason ?? DBNull.Value),
                ("@note", (object?)workspace.Note ?? DBNull.Value),
                ("@created", workspace.CreatedAt.ToIso()),
                ("@updated", workspace.UpdatedAt.ToIso()));
        }
    }

    public MissionWorkspace? LoadWorkspace(string id) =>
        Query("SELECT * FROM mission_workspaces WHERE id=@id", ("@id", id ?? ""))
            .Select(Read).FirstOrDefault();

    /// <summary>
    /// Every workspace, newest first, INCLUDING cleaned and orphaned ones. Callers filter by state;
    /// hiding the finished ones here would make the history this table exists to keep invisible.
    /// </summary>
    public IReadOnlyList<MissionWorkspace> LoadWorkspaces() =>
        Query("SELECT * FROM mission_workspaces ORDER BY created_at DESC").Select(Read).ToList();

    public IReadOnlyList<MissionWorkspace> LoadWorkspacesForMission(string missionId) =>
        Query("SELECT * FROM mission_workspaces WHERE mission_id=@m ORDER BY created_at DESC",
            ("@m", missionId ?? "")).Select(Read).ToList();

    private static MissionWorkspace Read(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
        Root = row.GetValueOrDefault("root")?.ToString() ?? "",
        Mode = row.GetValueOrDefault("mode")?.ToString() ?? "worktree",
        SourceRoot = row.GetValueOrDefault("source_root")?.ToString() ?? "",
        BaseRevision = row.GetValueOrDefault("base_revision")?.ToString() ?? "",
        RepositoryFingerprint = row.GetValueOrDefault("repository_fingerprint")?.ToString() ?? "",
        Branch = row.GetValueOrDefault("branch")?.ToString() ?? "",
        // An unparseable state reads as Orphaned rather than as Ready. Fail closed: the cost of
        // mislabelling a healthy workspace is an operator note, and the cost of the reverse is an
        // agent dispatched into a directory nothing can vouch for.
        State = Enum.TryParse<WorkspaceState>(row.GetValueOrDefault("state")?.ToString(), out var state)
            ? state : WorkspaceState.Orphaned,
        RetainedBy = row.GetValueOrDefault("retained_by")?.ToString(),
        RetainReason = row.GetValueOrDefault("retain_reason")?.ToString(),
        Note = row.GetValueOrDefault("note")?.ToString(),
        CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        UpdatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("updated_at")?.ToString()),
    };
}
