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

    /* ---- Per-project model routes. v0.3.8.124 -------------------------------------------------
       The per-ROLE half of a project's routing. Its priority model is two columns on `projects`
       above; a role override needs a row, and absence of a row means the role inherits the colony's
       route rather than having none. See `ProjectRoutingScope` for the precedence this feeds. */

    /// <summary>Every role this project overrides, role → (provider, model). Empty is the norm.</summary>
    public IReadOnlyDictionary<string, (string Provider, string Model)> LoadProjectRoutes(string projectId)
    {
        var routes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(projectId)) return routes;

        foreach (var row in Query("SELECT role, provider, model FROM project_model_routes WHERE project_id=@pid",
                     ("@pid", projectId)))
        {
            var role = row.GetValueOrDefault("role")?.ToString() ?? "";
            var provider = row.GetValueOrDefault("provider")?.ToString() ?? "";
            var model = row.GetValueOrDefault("model")?.ToString() ?? "";

            // A half-written row is not a route. Both halves or the role inherits — the same rule
            // `HasModelPriority` applies colony-wide, applied where the row is read so a row that
            // somehow lost one half cannot route an ant to an empty model.
            if (role.Length == 0 || provider.Length == 0 || model.Length == 0) continue;
            routes[role] = (provider, model);
        }

        return routes;
    }

    /// <summary>Set one role's route for one project. Replaces; there is one route per role.</summary>
    public void SaveProjectRoute(string projectId, string role, string provider, string model, string by)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(role)) return;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO project_model_routes (project_id, role, provider, model, updated_by, updated_at)
                  VALUES (@pid, @role, @prov, @model, @by, @at)
                  ON CONFLICT(project_id, role) DO UPDATE SET
                    provider=@prov, model=@model, updated_by=@by, updated_at=@at",
                ("@pid", projectId), ("@role", role), ("@prov", provider), ("@model", model),
                ("@by", by ?? ""), ("@at", AnthillTime.NowUtc().ToIso()));
        }
    }

    /// <summary>Clear one role's override, so it inherits the colony's route again.</summary>
    public void DeleteProjectRoute(string projectId, string role)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(role)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "DELETE FROM project_model_routes WHERE project_id=@pid AND role=@role",
                ("@pid", projectId), ("@role", role));
        }
    }

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
