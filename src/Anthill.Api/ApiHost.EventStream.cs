using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Anthill.Core.Common;
using Anthill.SDK.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Api;

/// <summary>
/// v3.8.3 — the live event stream, and the first endpoint in the colony that the UI does not have
/// to poll for.
///
/// Before this, every observer read <c>/events/json</c> on a timer with a three-second client-side
/// cache, which meant the dashboard's picture of a mission was somewhere between zero and several
/// seconds stale and the server did the same query over and over whether or not anything had
/// happened. Server-Sent Events rather than WebSockets because the traffic is entirely one-way —
/// the colony tells, the browser listens — and SSE brings reconnection semantics with it for free,
/// where a socket would need them written by hand.
/// </summary>
public static partial class ApiHost
{
    /// <summary>
    /// Per-connection buffer. Bounded and drop-oldest for the same reason the bus itself is: a
    /// browser on a slow link must not be able to apply backpressure to the colony. It cannot here
    /// anyway — the bus dispatches into this channel with TryWrite and moves on — but the bound is
    /// what stops one stalled tab from growing without limit.
    /// </summary>
    private const int StreamBufferCapacity = 512;

    /// <summary>
    /// How often to send a comment line when nothing is happening. Idle SSE connections are killed
    /// by proxies and by some browsers after roughly a minute, and a colony that is quiet because
    /// no mission is running is exactly when the operator is most likely to be watching and least
    /// likely to notice the stream died.
    /// </summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static void MapEventStreamEndpoints(WebApplication app)
    {
        app.MapGet("/events/stream", async (HttpContext ctx) =>
        {
            // Same permission as /events/json. The stream is the same data by a faster route; it
            // must not become a way to see events a caller could not otherwise read.
            var auth = RequireAuth(ctx, "read_events");
            if (auth is not null)
            {
                await auth.ExecuteAsync(ctx);
                return;
            }

            var missionFilter = ctx.Request.Query["mission_id"].FirstOrDefault();
            var typeFilter = ctx.Request.Query["type"].FirstOrDefault();

            ctx.Response.Headers.ContentType = "text/event-stream";
            // Proxies buffer text/* by default, which for a stream means the operator sees nothing
            // until enough bytes accumulate to flush — indistinguishable from a colony doing nothing.
            ctx.Response.Headers.CacheControl = "no-cache, no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            // No explicit Connection header: Kestrel owns connection management for HTTP/1.1 and
            // HTTP/2 alike, and setting it by hand is at best ignored.

            var buffer = Channel.CreateBounded<ColonyEvent>(new BoundedChannelOptions(StreamBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            // Subscribe BEFORE the replay below reads history. The overlap is deliberate: an event
            // landing between "read the last N" and "start listening" would otherwise fall in the
            // gap and be seen by nobody. Duplicates are cheap to tolerate; a hole is not, so the
            // replay carries ids and the client discards what it has already seen.
            using var subscription = Queen.Events.Subscribe(ev =>
            {
                if (missionFilter is not null && !string.Equals(ev.MissionId, missionFilter, StringComparison.Ordinal)) return;
                if (typeFilter is not null && !string.Equals(ev.EventType, typeFilter, StringComparison.Ordinal)) return;
                buffer.Writer.TryWrite(ev);
            });

            await WriteCommentAsync(ctx, "connected", ctx.RequestAborted);

            foreach (var row in ReplayRows(missionFilter, typeFilter))
                await WriteEventAsync(ctx, row, ctx.RequestAborted);

            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    idle.CancelAfter(Heartbeat);
                    try
                    {
                        var ev = await buffer.Reader.ReadAsync(idle.Token);
                        await WriteEventAsync(ctx, Serialize(ev), ctx.RequestAborted);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        // Idle window elapsed, client still there. Prove the connection is alive.
                        await WriteCommentAsync(ctx, "keepalive", ctx.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The browser closed the tab. Ordinary, and not an error worth logging: with one
                // dashboard open and a page refresh, this fires constantly.
            }
            finally
            {
                buffer.Writer.TryComplete();
            }
        });
    }

    /// <summary>
    /// The last slice of history, so a dashboard opened mid-mission is not staring at a blank panel
    /// until the colony happens to do something next. Read from the durable log, which is the point
    /// of keeping <see cref="Anthill.SDK.Memory.IEventLog"/> and <see cref="IEventBus"/> distinct:
    /// the bus cannot replay, and the log cannot push.
    /// </summary>
    private static IEnumerable<string> ReplayRows(string? missionId, string? eventType)
    {
        var rows = Queen.Memory.GetRecentEvents(50, eventType, missionId);
        // GetRecentEvents returns newest-first; a stream must arrive oldest-first, or the client
        // renders the mission backwards.
        for (var i = rows.Count - 1; i >= 0; i--)
            yield return Serialize(rows[i]);
    }

    /// <summary>
    /// One wire shape for both halves of the stream.
    ///
    /// Replayed history comes off a database row and live events come off the bus, and it would be
    /// easy to let each serialise itself — the stored row already has usable keys. Then a client
    /// would need two parsers for one stream, and the difference (<c>metadata_json</c> as a string
    /// versus <c>metadata</c> as an object) would show up as a panel that renders history correctly
    /// and live events as blanks. Both paths go through here.
    /// </summary>
    private static string Serialize(ColonyEvent ev) => Serialize(new Dictionary<string, object?>
    {
        ["id"] = ev.Id,
        ["mission_id"] = ev.MissionId,
        ["task_id"] = ev.TaskId,
        ["ant_name"] = ev.AntName,
        ["event_type"] = ev.EventType,
        ["message"] = ev.Message,
        ["metadata"] = ev.Metadata,
        ["created_at"] = ev.CreatedAt.ToIso(),
    });

    private static string Serialize(Dictionary<string, object?> row) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = row.GetValueOrDefault("id"),
            ["mission_id"] = row.GetValueOrDefault("mission_id"),
            ["task_id"] = row.GetValueOrDefault("task_id"),
            ["ant_name"] = row.GetValueOrDefault("ant_name"),
            ["event_type"] = row.GetValueOrDefault("event_type"),
            ["message"] = row.GetValueOrDefault("message"),
            // TryParseObject, not the raw string: it returns an empty dictionary for null or
            // malformed JSON, so one bad metadata row cannot break the frame it is carried in.
            ["metadata"] = Json.TryParseObject(row.GetValueOrDefault("metadata_json") as string),
            ["created_at"] = row.GetValueOrDefault("created_at"),
        });

    private static async ThreadingTask WriteEventAsync(HttpContext ctx, string json, CancellationToken token)
    {
        // SSE frames are "data: <payload>\n\n". The payload must not contain a bare newline, which
        // is why it is JSON on one line — a pretty-printed object would be parsed by the client as
        // several truncated frames.
        await ctx.Response.WriteAsync("data: " + json + "\n\n", Encoding.UTF8, token);
        await ctx.Response.Body.FlushAsync(token);
    }

    private static async ThreadingTask WriteCommentAsync(HttpContext ctx, string note, CancellationToken token)
    {
        await ctx.Response.WriteAsync(": " + note + "\n\n", Encoding.UTF8, token);
        await ctx.Response.Body.FlushAsync(token);
    }
}
