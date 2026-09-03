using Anthill.Core.ColonyLive;

namespace Anthill.Api;

/// <summary>
/// THE COLONY LIVE READ MODEL — v0.3.8.115.
///
/// WHAT THIS ADDS, AND WHAT IT DELIBERATELY DOES NOT. The console already polls `/graph`,
/// `/colony/registry` and the approvals list, and already holds an `/events/stream` subscription.
/// Colony Live consumes those; it does not re-fetch them, and this file adds no second poll of
/// anything the console can already see. That boundary is `.111`'s and it still holds: "there is no
/// second fetch anywhere in the feature."
///
/// Two things the existing endpoints genuinely could not answer, which is why this exists at all:
///
///   1. **Which sector does a role live in?** `/colony/registry` returns roles with their `Colony`,
///      and the console mapped colony→sector with a hand-written table that silently filed anything
///      it did not recognise under the QUEEN. The mapping belongs to the server, beside the registry
///      it maps (see <see cref="ColonySectors"/>).
///
///   2. **Where does the snapshot end and the stream begin?** Without a watermark a client that
///      hydrates while events are arriving either drops what landed during hydration or applies it
///      twice. The snapshot names the newest event it accounted for; the reducer discards anything
///      at or before it and applies the rest exactly once.
///
/// Everything served here is a projection over existing repositories. There is no Colony Live table
/// and no second truth: if this file and the registry disagree, the registry is right and this is a
/// bug.
/// </summary>
public static partial class ApiHost
{
    /// <summary>
    /// How many recent events one records read may SCAN. Bounded because the console asks for a
    /// chamber's contents and the table holds the colony's whole history — an unbounded read here
    /// is a query that gets slower every day a colony stays up.
    ///
    /// The response says when the scan hit this bound rather than presenting a truncated page as a
    /// complete one, which is the same honesty the growth-playback partial-history indicator needs.
    /// </summary>
    private const int ColonyLiveRecordScan = 600;

    private const int ColonyLiveRecordPageMax = 200;

    private static void MapColonyLiveEndpoints(WebApplication app)
    {
        // ---- The initial snapshot: structure, and where the stream picks up -------------------
        app.MapGet("/colony/live/snapshot", (HttpContext ctx) =>
        {
            // Same permission as /colony/registry: this is the registry, grouped. It must not
            // become a way to read the roster without the permission that guards the roster.
            var auth = RequireAuth(ctx, "read_graph"); if (auth is not null) return auth;

            // The watermark is read BEFORE the projection below, never after. Between the two the
            // colony may log an event; taking the mark first means that event falls AFTER the
            // watermark and is applied from the stream. Taking it last would put it before the mark
            // and inside neither, which is the one outcome a watermark exists to prevent.
            var newest = Queen.Memory.GetRecentEvents(1);
            var watermarkId = newest.Count > 0 ? newest[0].GetValueOrDefault("id")?.ToString() : null;
            var watermarkAt = newest.Count > 0 ? newest[0].GetValueOrDefault("created_at")?.ToString() : null;

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["snapshot_at"] = AnthillTime.NowUtc().ToIso(),
                // Null when the colony has never logged an event. The reducer must treat that as
                // "apply everything the stream sends", not as "drop everything".
                ["watermark"] = new Dictionary<string, object?>
                {
                    ["event_id"] = watermarkId,
                    ["created_at"] = watermarkAt,
                },
                ["sectors"] = ColonyLiveProjection.Sectors(),
                ["runtime"] = ColonyLiveProjection.Runtime(),
                // Stated rather than left for the client to infer from an empty sector list: a
                // console that cannot tell "no roles here" from "the projection failed" will show
                // the same empty chamber for both.
                ["unassigned_sector"] = ColonySectors.Unassigned,
            });
        });

        // ---- Chamber interiors: the persisted records a sector actually holds -----------------
        //
        // Typed, bounded, paginated, permission-aware, and filterable by the identifiers the view
        // already keys on. Backed entirely by the events table — a "record" here is a row the colony
        // WROTE, decided by `ColonyLiveProjection.CreatesDurableRecord`, not by an event having
        // happened. Most events are the colony saying something; only some are it storing something.
        app.MapGet("/colony/live/records", (HttpContext ctx) =>
        {
            // read_events, not read_graph: this returns event CONTENT, and the stream that carries
            // the same rows is guarded by read_events.
            var auth = RequireAuth(ctx, "read_events"); if (auth is not null) return auth;

            var sector = ctx.Request.Query["sector"].FirstOrDefault();
            var mission = ctx.Request.Query["mission_id"].FirstOrDefault();
            var type = ctx.Request.Query["type"].FirstOrDefault();
            var since = ctx.Request.Query["since"].FirstOrDefault();

            var limit = ColonyLiveRecordPageMax;
            if (int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var asked))
                limit = Math.Clamp(asked, 1, ColonyLiveRecordPageMax);

            // One registry read for the whole page. Resolving a role per row would re-walk the
            // roster once per event for no gain.
            var sectorOfRole = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ColonyLiveProjection.Sectors())
                foreach (var resident in s.Residents)
                    sectorOfRole[resident.RoleId] = s.SectorId;

            var scanned = Queen.Memory.GetRecentEvents(ColonyLiveRecordScan, type, mission);
            var truncated = scanned.Count >= ColonyLiveRecordScan;

            var items = new List<Dictionary<string, object?>>();
            foreach (var row in scanned)
            {
                var eventType = row.GetValueOrDefault("event_type")?.ToString();
                if (!ColonyLiveProjection.CreatesDurableRecord(eventType)) continue;

                var createdAt = row.GetValueOrDefault("created_at")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(since) && string.CompareOrdinal(createdAt, since) <= 0) continue;

                var ant = row.GetValueOrDefault("ant_name")?.ToString() ?? "";

                // An event whose ant this colony does not recognise is UNASSIGNED, never Queen.
                // That was the client bug this endpoint exists to make impossible.
                var recordSector = string.IsNullOrEmpty(ant)
                    ? ColonySectors.Unassigned
                    : sectorOfRole.GetValueOrDefault(ant, ColonySectors.Unassigned);

                if (!string.IsNullOrEmpty(sector) && !string.Equals(sector, recordSector, StringComparison.Ordinal))
                    continue;

                items.Add(new Dictionary<string, object?>
                {
                    // The stable identity everything downstream keys on — placement, dedup and
                    // selection. Never the message text, which is display and can repeat.
                    ["record_id"] = row.GetValueOrDefault("id"),
                    ["sector"] = recordSector,
                    ["record_type"] = eventType,
                    ["title"] = row.GetValueOrDefault("message"),
                    ["ant"] = ant,
                    ["mission_id"] = row.GetValueOrDefault("mission_id"),
                    ["task_id"] = row.GetValueOrDefault("task_id"),
                    ["created_at"] = createdAt,
                });

                if (items.Count >= limit) break;
            }

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = items,
                ["count"] = items.Count,
                // The cursor is the oldest row on this page; the next call passes it as `before`.
                // Newest-first, so paging walks backwards through history.
                ["next_before"] = items.Count > 0 ? items[^1].GetValueOrDefault("created_at") : null,
                // "There is older history this read did not reach", said plainly. A view that
                // silently shows a bounded window as the whole colony is the shape §14's
                // partial-history indicator exists to prevent.
                ["scan_truncated"] = truncated,
                ["scan_limit"] = ColonyLiveRecordScan,
            });
        });
    }
}
