using Anthill.Core.Common;
using Anthill.Core.Projects;

namespace Anthill.Core.Memory;

/// <summary>v0.3.8.47 — projects, persisted. See <see cref="Project"/> for what one is.</summary>
public sealed partial class SqliteMemory
{
    public void SaveProject(Project project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO projects (id, name, description_md, path, archived, default_policy,
                    default_policy_by, default_policy_at, default_provider, default_model,
                    created_at, updated_at)
                  VALUES (@id, @name, @desc, @path, @archived, @dpol, @dby, @dat, @dprov, @dmodel, @created, @updated)
                  ON CONFLICT(id) DO UPDATE SET
                    name=@name, description_md=@desc, path=@path, archived=@archived,
                    default_policy=@dpol, default_policy_by=@dby, default_policy_at=@dat,
                    default_provider=@dprov, default_model=@dmodel, updated_at=@updated",
                ("@id", project.Id), ("@name", project.Name), ("@desc", project.DescriptionMd),
                ("@path", (object?)project.Path ?? DBNull.Value),
                ("@archived", project.Archived ? 1 : 0),
                ("@dpol", project.DefaultPolicy.ToString()),
                ("@dby", (object?)project.DefaultPolicyBy ?? DBNull.Value),
                ("@dat", (object?)project.DefaultPolicyAt?.ToIso() ?? DBNull.Value),
                ("@dprov", (object?)project.DefaultProvider ?? DBNull.Value),
                ("@dmodel", (object?)project.DefaultModel ?? DBNull.Value),
                ("@created", project.CreatedAt.ToIso()), ("@updated", project.UpdatedAt.ToIso()));
        }
    }

    public Project? LoadProject(string id) =>
        Query("SELECT * FROM projects WHERE id=@id", ("@id", id ?? "")).Select(ReadProject).FirstOrDefault();

    /// <summary>Active first, most recently touched first; archived projects sort last, kept — a
    /// container full of history is closed, never erased.</summary>
    public IReadOnlyList<Project> LoadProjects() =>
        Query("SELECT * FROM projects ORDER BY archived, updated_at DESC").Select(ReadProject).ToList();

    /// <summary>The conversations that live in one project, rail order.</summary>
    public IReadOnlyList<Conversations.Conversation> LoadProjectConversations(string projectId) =>
        Query("SELECT * FROM conversations WHERE project_id=@pid ORDER BY pinned DESC, updated_at DESC",
            ("@pid", projectId ?? "")).Select(ReadConversation).ToList();

    private static Project ReadProject(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Name = row.GetValueOrDefault("name")?.ToString() ?? "",
        DescriptionMd = row.GetValueOrDefault("description_md")?.ToString() ?? "",
        Path = row.GetValueOrDefault("path") is null or DBNull ? null : row.GetValueOrDefault("path")?.ToString(),
        Archived = Convert.ToInt64(row.GetValueOrDefault("archived") ?? 0L) != 0,
        DefaultPolicy = Enum.TryParse<Conversations.EscalationPolicy>(
            row.GetValueOrDefault("default_policy")?.ToString(), out var dp) ? dp : Conversations.EscalationPolicy.Ask,
        DefaultPolicyBy = row.GetValueOrDefault("default_policy_by") is null or DBNull ? null : row.GetValueOrDefault("default_policy_by")?.ToString(),
        DefaultPolicyAt = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("default_policy_at")?.ToString()),
        DefaultProvider = row.GetValueOrDefault("default_provider") is null or DBNull ? null : row.GetValueOrDefault("default_provider")?.ToString(),
        DefaultModel = row.GetValueOrDefault("default_model") is null or DBNull ? null : row.GetValueOrDefault("default_model")?.ToString(),
        CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        UpdatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("updated_at")?.ToString()),
    };
}
