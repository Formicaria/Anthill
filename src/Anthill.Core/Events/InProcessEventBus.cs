using System.Collections.Concurrent;
using System.Threading.Channels;
using Anthill.SDK.Events;
using Microsoft.Extensions.Logging;

namespace Anthill.Core.Events;

/// <summary>
/// The colony's event spine: one process, many subscribers, no ceremony.
///
/// Three properties matter more than throughput, because of what publishes here. The publishers are
/// the Queen, the execution service, the scheduler and the tool layer, calling from inside the
/// mission hot path — so:
///
/// 1. <b>Publish never blocks and never throws.</b> A dashboard that stopped reading, a subscriber
///    that deadlocked, a queue that filled — none of these may slow or fail a mission. The bounded
///    channel drops the oldest event under sustained backpressure rather than applying it upstream.
///    Dropping is the correct failure here: the durable record already went to the event log before
///    publication, so what is lost is liveness, not history.
///
/// 2. <b>One bad subscriber cannot affect another, or the colony.</b> Handlers run inside a catch.
///    An exception is logged once with the subscriber's own identity and the event type, then
///    discarded.
///
/// 3. <b>Dispatch is off the publisher's thread.</b> A single background pump preserves publication
///    ORDER, which matters: a UI that receives <c>task_completed</c> before <c>task_started</c>
///    renders a task that finished before it began, and the bug looks like a scheduler bug.
///
/// Deliberately in-process and deliberately not durable. <see cref="Anthill.SDK.Memory.IEventLog"/>
/// is the durable side; this is the live one. Conflating them would produce a bus that is slow
/// because it writes, and a log that is lossy because it drops.
/// </summary>
public sealed class InProcessEventBus : IEventBus, IDisposable
{
    /// <summary>
    /// Deep enough to absorb a burst from a fully parallel mission, shallow enough that a stalled
    /// consumer is noticed in seconds rather than becoming an unbounded memory leak that presents
    /// as "the colony got slow" hours later.
    /// </summary>
    private const int QueueCapacity = 2048;

    private readonly Channel<ColonyEvent> _queue;
    private readonly ConcurrentDictionary<long, Subscription> _subscribers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger? _log;
    private long _nextId;
    private long _dropped;
    private bool _disposed;

    /// <summary>Events discarded under backpressure since start. Non-zero means a subscriber is
    /// not keeping up — surface it rather than letting the loss stay silent.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public InProcessEventBus(ILogger<InProcessEventBus>? log = null)
    {
        _log = log;
        _queue = Channel.CreateBounded<ColonyEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            // Oldest-first. When the colony is producing faster than the dashboard consumes, the
            // events worth keeping are the recent ones — a viewer catching up on a stale backlog is
            // reading history, and history is what the event log is for.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _ = System.Threading.Tasks.Task.Run(PumpAsync);
    }

    public void Publish(ColonyEvent colonyEvent)
    {
        if (colonyEvent is null || _disposed) return;

        // TryWrite, never WriteAsync. On a bounded DropOldest channel this always succeeds and
        // always returns immediately; the only false is a completed writer, i.e. shutdown.
        if (!_queue.Writer.TryWrite(colonyEvent))
            Interlocked.Increment(ref _dropped);
    }

    public IDisposable Subscribe(Action<ColonyEvent> handler) => Add(null, handler);

    public IDisposable Subscribe(string eventType, Action<ColonyEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        return Add(eventType, handler);
    }

    private IDisposable Add(string? eventType, Action<ColonyEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Interlocked.Increment(ref _nextId);
        var sub = new Subscription(id, eventType, handler, this);
        _subscribers[id] = sub;
        return sub;
    }

    private async System.Threading.Tasks.Task PumpAsync()
    {
        try
        {
            await foreach (var ev in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                Dispatch(ev);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            // The pump dying silently would leave a colony that looks healthy and observes nothing.
            _log?.LogError(ex, "ANTHILL event bus pump stopped unexpectedly; live events are no longer being delivered.");
        }
    }

    private void Dispatch(ColonyEvent ev)
    {
        foreach (var sub in _subscribers.Values)
        {
            if (sub.EventType is not null &&
                !string.Equals(sub.EventType, ev.EventType, StringComparison.Ordinal))
                continue;

            try
            {
                sub.Handler(ev);
            }
            catch (Exception ex)
            {
                // Logged, not rethrown, not unsubscribed. Not rethrown because an observer must not
                // be able to stop the stream for everyone else. Not unsubscribed because a handler
                // that throws on one malformed event is usually still correct for the next one, and
                // silently detaching a dashboard is a harder failure to diagnose than a noisy log.
                _log?.LogWarning(ex, "Event subscriber {SubscriptionId} threw handling {EventType}; the event was delivered to the other subscribers.",
                    sub.Id, ev.EventType);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _subscribers.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InProcessEventBus _bus;

        public Subscription(long id, string? eventType, Action<ColonyEvent> handler, InProcessEventBus bus)
        {
            Id = id;
            EventType = eventType;
            Handler = handler;
            _bus = bus;
        }

        public long Id { get; }

        /// <summary>Null means "every event".</summary>
        public string? EventType { get; }

        public Action<ColonyEvent> Handler { get; }

        /// <summary>Idempotent: an SSE connection may dispose on both client disconnect and
        /// request-scope teardown, and those can race.</summary>
        public void Dispose() => _bus._subscribers.TryRemove(Id, out _);
    }
}
