using Anthill.Core.Common;
using Anthill.SDK.Events;
using Anthill.SDK.Memory;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.8.6 — the narrow views of this store that a module is allowed to hold.
///
/// <see cref="SqliteMemory"/> has 177 public methods across twenty partial files: pheromones,
/// missions, tasks, jobs, shadow runs, workspaces, users, provider credentials, skills, readiness,
/// the repository index, fault injection. Handing that to a module would make the module boundary
/// decorative — every module could reach everything, including other modules' credentials.
///
/// So modules get <see cref="IPheromoneMemory"/> and <see cref="IEventLog"/> and nothing else. Both
/// are implemented EXPLICITLY, which is the point rather than a style choice: an explicit
/// implementation is reachable only through the interface, so no core call site can drift into
/// using the module-facing shape by accident, and the two surfaces can evolve apart.
///
/// This is phase 3 of the refactor plan done for a concrete reason rather than speculatively. The
/// plan proposed carving role interfaces over the whole class; what actually forced the work was
/// that <c>IModuleContext</c> could not be implemented without them, so <c>IAnthillModule</c> had
/// been declared in phase 0 and never once invoked.
/// </summary>
public sealed partial class SqliteMemory : IPheromoneMemory, IEventLog
{
    // ---- IPheromoneMemory ---------------------------------------------------

    void IPheromoneMemory.Reinforce(string trailKey, string trailType, bool success, double strengthDelta,
        IReadOnlyDictionary<string, object?>? metadata) =>
        UpdatePheromoneTrail(trailKey, trailType, success, strengthDelta,
            metadata is null ? null : new Dictionary<string, object?>(metadata));

    /// <summary>
    /// Only learning-bearing trails, because this is the one a module uses to make a decision.
    ///
    /// NOT simply the head of <see cref="IPheromoneMemory.ListAll"/>: operational telemetry and
    /// heuristic source-quality signals are recorded but never allowed to steer strategy, and the
    /// filtering lives in the query behind <c>GetTopPheromoneTrails</c>. A module reading the
    /// unfiltered list to choose an action would be reinforcing on evidence the colony has
    /// deliberately excluded from planning.
    /// </summary>
    IReadOnlyList<PheromoneTrail> IPheromoneMemory.Top(int limit) =>
        GetTopPheromoneTrails(limit).Select(ToTrail).ToList();

    IReadOnlyList<PheromoneTrail> IPheromoneMemory.ListAll(int limit) =>
        ListPheromoneTrails(limit).Select(ToTrail).ToList();

    int IPheromoneMemory.Prune(double minStrength, bool dropFailureDominant) =>
        PrunePheromones(minStrength, dropFailureDominant);

    /// <summary>
    /// One row to one record. Tolerant by construction: <c>GetTopPheromoneTrails</c> selects fewer
    /// columns than <c>ListPheromoneTrails</c> (no metadata, no legacy flag), so every read is
    /// defaulted rather than indexed. A mapper that assumed the wider shape would throw on the
    /// query a module is most likely to call.
    /// </summary>
    private static PheromoneTrail ToTrail(Dictionary<string, object?> row)
    {
        var success = (int)AsLong(row.GetValueOrDefault("success_count"));
        var failure = (int)AsLong(row.GetValueOrDefault("failure_count"));
        return new PheromoneTrail(
            TrailKey: row.GetValueOrDefault("trail_key")?.ToString() ?? "",
            TrailType: row.GetValueOrDefault("trail_type")?.ToString() ?? "",
            Strength: AsDouble(row.GetValueOrDefault("strength")),
            SuccessCount: success,
            FailureCount: failure,
            LastUpdated: AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("last_updated")?.ToString()))
        {
            Metadata = Json.TryParseObject(row.GetValueOrDefault("metadata_json") as string),
            Legacy = AsLong(row.GetValueOrDefault("legacy")) != 0,
        };
    }

    private static double AsDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        string s when double.TryParse(s, out var p) => p,
        _ => 0d,
    };

    // ---- IEventLog ----------------------------------------------------------

    /// <summary>
    /// Append through the SAME method the colony has always used, so a module's event is
    /// indistinguishable from the core's — same table, same publication to the bus, same ordering
    /// guarantee. Routing module events down a separate path would have produced a second event
    /// stream that the dashboard did not know about.
    /// </summary>
    ColonyEvent IEventLog.Append(ColonyEvent colonyEvent)
    {
        var stored = LogEvent(
            colonyEvent.MissionId,
            colonyEvent.EventType,
            colonyEvent.Message,
            colonyEvent.TaskId,
            colonyEvent.AntName,
            new Dictionary<string, object?>(colonyEvent.Metadata));
        return ToColonyEvent(stored);
    }

    IReadOnlyList<ColonyEvent> IEventLog.Recent(int limit, string? eventType, string? missionId) =>
        GetRecentEvents(limit, eventType, missionId).Select(row => new ColonyEvent
        {
            Id = row.GetValueOrDefault("id")?.ToString() ?? "",
            MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
            TaskId = row.GetValueOrDefault("task_id")?.ToString(),
            AntName = row.GetValueOrDefault("ant_name")?.ToString(),
            EventType = row.GetValueOrDefault("event_type")?.ToString() ?? "",
            Message = row.GetValueOrDefault("message")?.ToString() ?? "",
            Metadata = Json.TryParseObject(row.GetValueOrDefault("metadata_json") as string),
            CreatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("created_at")?.ToString()),
        }).ToList();
}
