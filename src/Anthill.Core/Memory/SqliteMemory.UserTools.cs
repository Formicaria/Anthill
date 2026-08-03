using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Tools;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.4.1 (ADR-006) — operator-defined tools, persisted.
///
/// They have to survive a restart or the feature is a demo: a tool an operator defined, granted to a
/// role and used in a mission would vanish on the next process start, and the transcript that
/// mentions it would become unexplainable.
///
/// Disabled definitions are KEPT. Revoking a tool and never having defined it are different facts,
/// and only the stored row can tell an audit which one happened.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// Save or replace one definition. Name is the primary key: re-registering a name REPLACES it,
    /// which is what an operator editing a tool means, and avoids accumulating versions of a tool
    /// that only ever has one live meaning.
    /// </summary>
    public void SaveToolDefinition(ToolDefinition definition)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO tool_definitions
                    (name, description, kind, parameters_json, config_json, allowed_roles_json,
                     enabled, created_by, created_at)
                  VALUES (@name, @desc, @kind, @params, @config, @roles, @enabled, @by, @at)
                  ON CONFLICT(name) DO UPDATE SET
                    description=@desc, kind=@kind, parameters_json=@params, config_json=@config,
                    allowed_roles_json=@roles, enabled=@enabled",
                ("@name", definition.Name),
                ("@desc", definition.Description),
                ("@kind", definition.Kind.ToString()),
                ("@params", definition.ParametersJson),
                ("@config", Json.SafeDumps(definition.Config)),
                ("@roles", Json.SafeDumps(definition.AllowedRoles)),
                ("@enabled", definition.Enabled ? 1 : 0),
                ("@by", definition.CreatedBy ?? "operator"),
                ("@at", definition.CreatedAt.ToIso()));
        }
    }

    /// <summary>
    /// Every stored definition, enabled or not. Callers wanting only the live ones filter — the
    /// registrar does, and the operator's tool list deliberately does not.
    /// </summary>
    public IReadOnlyList<ToolDefinition> LoadToolDefinitions()
    {
        var rows = Query("SELECT * FROM tool_definitions ORDER BY name");
        var definitions = new List<ToolDefinition>();

        foreach (var row in rows)
        {
            // A single corrupt row must not take out the whole tool inventory. Losing one definition
            // is recoverable by an operator; losing all of them because one config blob is malformed
            // means the colony starts with no user tools and no explanation.
            try
            {
                definitions.Add(new ToolDefinition
                {
                    Name = row.GetValueOrDefault("name")?.ToString() ?? "",
                    Description = row.GetValueOrDefault("description")?.ToString() ?? "",
                    Kind = ToolKinds.Parse(row.GetValueOrDefault("kind")?.ToString()),
                    ParametersJson = row.GetValueOrDefault("parameters_json")?.ToString()
                                     ?? """{"type":"object","properties":{}}""",
                    Config = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        row.GetValueOrDefault("config_json")?.ToString() ?? "{}") ?? new(),
                    AllowedRoles = JsonSerializer.Deserialize<List<string>>(
                        row.GetValueOrDefault("allowed_roles_json")?.ToString() ?? "[]") ?? new(),
                    Enabled = Convert.ToInt64(row.GetValueOrDefault("enabled") ?? 0L) != 0,
                    CreatedBy = row.GetValueOrDefault("created_by")?.ToString() ?? "operator",
                    CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
                });
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidCastException)
            {
                // Reported, not swallowed: a definition that silently disappears is
                // indistinguishable from one that was never saved. The system mission is ensured
                // first because events carry a foreign key to missions.
                try
                {
                    EnsureSystemMission(AnthillRuntime.SystemApiMissionId, "System API events");
                    LogEvent(AnthillRuntime.SystemApiMissionId, "tool_definition_unreadable",
                        $"Stored tool definition '{row.GetValueOrDefault("name")}' could not be read: {error.Message}");
                }
                catch { /* diagnostics must never be able to break startup */ }
            }
        }

        return definitions;
    }

    /// <summary>
    /// Switch a definition off (or back on) WITHOUT deleting it. The ordinary revoke path: the row
    /// stays so an audit can still explain a transcript that called the tool.
    /// </summary>
    public bool SetToolDefinitionEnabled(string name, bool enabled)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tool_definitions SET enabled=@e WHERE name=@n";
            cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@n", name ?? "");
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Permanent removal. Separate from disabling because the two intents differ.</summary>
    public bool DeleteToolDefinition(string name)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tool_definitions WHERE name=@n";
            cmd.Parameters.AddWithValue("@n", name ?? "");
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
