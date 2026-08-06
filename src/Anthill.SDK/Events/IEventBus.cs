namespace Anthill.SDK.Events;

/// <summary>
/// The colony's single communication spine.
///
/// Deliberately synchronous in signature. Publishers are the Queen, the scheduler, the execution
/// service and the tool layer, and they call this from inside the mission hot path — a signature
/// that returns a <c>System.Threading.Tasks.Task</c> would invite an <c>await</c> at every one of
/// those call sites, which is exactly how a bus becomes a source of latency in the thing it was
/// meant to observe. It also collides with <c>Anthill.Core</c>'s global <c>using Task =
/// Anthill.Core.Domain.Task</c> alias, which would force fully-qualified noise on every publisher.
///
/// <c>Publish</c> therefore returns as soon as the event is accepted for delivery. Implementations
/// dispatch to subscribers off the caller's thread.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Accept an event for delivery. Must not throw, and must not block on subscribers.
    ///
    /// A bus that can throw would mean an observer failure can abort a mission — an inversion of
    /// the whole point of observability. Implementations swallow and log subscriber faults.
    /// </summary>
    void Publish(ColonyEvent colonyEvent);

    /// <summary>
    /// Observe every event. Dispose the returned handle to stop.
    ///
    /// The handle matters: the API's SSE endpoint subscribes per connected client, and a browser
    /// tab closing must actually detach the handler. Without disposal, a long-lived colony
    /// accumulates dead subscribers and pays to dispatch to all of them.
    /// </summary>
    IDisposable Subscribe(Action<ColonyEvent> handler);

    /// <summary>
    /// Observe one event type. Filtering at the bus rather than in the handler keeps a subscriber
    /// interested in <c>mission_completed</c> from being woken for every <c>tool_called</c> — and
    /// in a busy colony the tool traffic dominates by an order of magnitude.
    /// </summary>
    /// <param name="eventType">A value from <see cref="EventTypes"/>.</param>
    IDisposable Subscribe(string eventType, Action<ColonyEvent> handler);
}
