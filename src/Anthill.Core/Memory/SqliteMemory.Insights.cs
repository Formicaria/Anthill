using Microsoft.Data.Sqlite;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Memory;

/// <summary>
/// Structured (JSON-shaped) reads that power the live dashboard: a filterable event feed and a
/// pheromone-memory view with pruning. The text formatters in <see cref="SqliteMemory"/> stay as
/// they are for the CLI; these return raw rows so the web console can filter, sort, and render
/// them however the human has arranged their colony.
/// </summary>
public sealed partial class SqliteMemory
{
    // ---- filterable event feed -------------------------------------------

    /// <summary>
    /// Returns recent events with optional facets: a specific ant, an event-type substring, a
    /// minimum timestamp (ISO-8601), and a severity bucket. "Severity" is derived from the event
    /// type so the UI can pull just alarms/errors without knowing every event name.
    /// </summary>
    public List<Dictionary<string, object?>> QueryEventsRich(
        string? ant = null, string? typeContains = null, string? sinceIso = null,
        string? level = null, string? missionId = null, int limit = 200)
    {
        var sql = "SELECT id, mission_id, task_id, ant_name, event_type, message, metadata_json, created_at FROM events";
        var conditions = new List<string>();
        var args = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(ant)) { conditions.Add("ant_name = @ant"); args.Add(("@ant", ant)); }
        if (!string.IsNullOrWhiteSpace(typeContains)) { conditions.Add("event_type LIKE @type"); args.Add(("@type", $"%{typeContains}%")); }
        if (!string.IsNullOrWhiteSpace(sinceIso)) { conditions.Add("created_at >= @since"); args.Add(("@since", sinceIso)); }
        if (!string.IsNullOrWhiteSpace(missionId)) { conditions.Add("mission_id = @mid"); args.Add(("@mid", missionId)); }

        // Severity buckets are expressed as event-type sets so a non-technical filter ("errors",
        // "alarms") maps onto the colony's actual event vocabulary.
        var normalizedLevel = (level ?? "").Trim().ToLowerInvariant();
        if (normalizedLevel is "error" or "errors" or "alarm" or "alarms")
        {
            var failureTypes = AnthillRuntime.FailureEventTypes.ToList();
            var ph = string.Join(",", failureTypes.Select((_, i) => $"@lvl{i}"));
            conditions.Add($"(event_type IN ({ph}) OR event_type LIKE '%fail%' OR event_type LIKE '%error%' OR event_type LIKE '%timeout%')");
            for (var i = 0; i < failureTypes.Count; i++) args.Add(($"@lvl{i}", failureTypes[i]));
        }

        if (conditions.Count > 0) sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY created_at DESC LIMIT @lim";
        args.Add(("@lim", Math.Clamp(limit, 1, 2000)));
        return Query(sql, args.ToArray());
    }

    /// <summary>Distinct ant names that have logged at least one event — for populating filter menus.</summary>
    public List<string> DistinctEventAnts() =>
        Query("SELECT DISTINCT ant_name FROM events WHERE ant_name IS NOT NULL AND ant_name <> '' ORDER BY ant_name")
            .Select(r => r.GetValueOrDefault("ant_name")?.ToString() ?? "").Where(s => s.Length > 0).ToList();

    /// <summary>
    /// v1.8.22 Ant Performance Observatory: per-ant task aggregates across all missions —
    /// total/complete/failed/skipped/running counts and average elapsed seconds. Grouped straight
    /// from the tasks table, so it reflects the whole run history the DB still holds.
    /// Returns ant_name → { total, complete, failed, skipped, running, avg_seconds }.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> AntTaskStats()
    {
        var rows = Query(
            @"SELECT assigned_ant, status, COUNT(*) AS n, AVG(elapsed_seconds) AS avg_sec
              FROM tasks WHERE assigned_ant IS NOT NULL AND assigned_ant <> ''
              GROUP BY assigned_ant, status");
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var r in rows)
        {
            var ant = r.GetValueOrDefault("assigned_ant")?.ToString() ?? "";
            if (ant.Length == 0) continue;
            var status = (r.GetValueOrDefault("status")?.ToString() ?? "").ToLowerInvariant();
            var n = (int)AsLong(r.GetValueOrDefault("n"));
            if (!result.TryGetValue(ant, out var s))
                result[ant] = s = new Dictionary<string, object?>
                {
                    ["total"] = 0, ["complete"] = 0, ["failed"] = 0, ["skipped"] = 0, ["running"] = 0,
                    ["avg_seconds"] = 0.0, ["_elapsed_weight"] = 0,
                };
            s["total"] = (int)s["total"]! + n;
            if (s.ContainsKey(status)) s[status] = (int)s[status]! + n;
            // Weighted average of elapsed across status buckets that have a value.
            var avg = r.GetValueOrDefault("avg_sec");
            if (avg is not null && double.TryParse(avg.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var a))
            {
                var w = (int)s["_elapsed_weight"]!;
                var cur = (double)s["avg_seconds"]!;
                s["avg_seconds"] = Math.Round((cur * w + a * n) / Math.Max(1, w + n), 2);
                s["_elapsed_weight"] = w + n;
            }
        }
        foreach (var s in result.Values) s.Remove("_elapsed_weight");
        return result;
    }

    /// <summary>Distinct event types seen — for the type filter and at-a-glance vocabulary.</summary>
    public List<string> DistinctEventTypes() =>
        Query("SELECT DISTINCT event_type FROM events ORDER BY event_type")
            .Select(r => r.GetValueOrDefault("event_type")?.ToString() ?? "").Where(s => s.Length > 0).ToList();

    // ---- pheromone memory (list + prune) ---------------------------------

    /// <summary>
    /// All pheromone trails, strongest first, with a derived net score (success − failure) so the
    /// UI can show at a glance which patterns are pulling their weight.
    /// </summary>
    public List<Dictionary<string, object?>> ListPheromoneTrails(int limit = 300)
    {
        var rows = Query(
            @"SELECT trail_key, trail_type, strength, success_count, failure_count, last_updated, metadata_json
              FROM pheromone_trails ORDER BY strength DESC, success_count DESC LIMIT @lim",
            ("@lim", Math.Clamp(limit, 1, 2000)));
        foreach (var r in rows)
        {
            var s = (int)AsLong(r.GetValueOrDefault("success_count"));
            var f = (int)AsLong(r.GetValueOrDefault("failure_count"));
            r["net_count"] = s - f;
            r["total_count"] = s + f;
        }
        return rows;
    }

    /// <summary>
    /// Throws out pheromone trails that have proven unusable: weak (strength below
    /// <paramref name="minStrength"/>) or failure-dominant (more failures than successes and not
    /// strongly reinforced). This is the "keep what's useful, drop what errored" cleanup the
    /// colony's memory needs to stay sharp. Returns the number of trails removed.
    /// </summary>
    public int PrunePheromones(double minStrength = 0.15, bool dropFailureDominant = true)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            var conditions = new List<string> { "strength < @minS" };
            if (dropFailureDominant) conditions.Add("(failure_count > success_count AND strength < 0.5)");
            var sql = "DELETE FROM pheromone_trails WHERE " + string.Join(" OR ", conditions);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@minS", minStrength);
            var removed = cmd.ExecuteNonQuery();
            InvalidateCache();
            return removed;
        }
    }
}
