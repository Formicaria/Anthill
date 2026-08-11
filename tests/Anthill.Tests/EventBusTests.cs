using Anthill.Core.Events;
using Anthill.Core.Memory;
using Anthill.SDK.Events;
using Xunit;
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.8.3 — the event bus, which is the colony's communication spine and therefore has to be
/// boring under stress rather than fast on a good day.
///
/// Dispatch is asynchronous, so every assertion here waits on a signal with a timeout rather than
/// sleeping for a fixed interval. A <c>Thread.Sleep(50)</c> would pass on a developer machine and
/// fail on a loaded CI runner, and the resulting flake would be blamed on the bus rather than on
/// the test.
/// </summary>
public class EventBusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ColonyEvent Ev(string type = "task_started", string mission = "m1") =>
        new() { EventType = type, MissionId = mission, Message = "hello" };

    [Fact]
    public async ThreadingTask Subscriber_receives_published_event()
    {
        using var bus = new InProcessEventBus();
        var seen = new TaskCompletionSource<ColonyEvent>();
        using var _ = bus.Subscribe(seen.SetResult);

        bus.Publish(Ev());

        var ev = await seen.Task.WaitAsync(Timeout);
        Assert.Equal("task_started", ev.EventType);
        Assert.Equal("m1", ev.MissionId);
    }

    [Fact]
    public async ThreadingTask Every_subscriber_receives_every_event()
    {
        using var bus = new InProcessEventBus();
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();
        using var s1 = bus.Subscribe(_ => a.TrySetResult());
        using var s2 = bus.Subscribe(_ => b.TrySetResult());

        bus.Publish(Ev());

        await ThreadingTask.WhenAll(a.Task, b.Task).WaitAsync(Timeout);
    }

    [Fact]
    public async ThreadingTask Typed_subscription_ignores_other_event_types()
    {
        using var bus = new InProcessEventBus();
        var wanted = new TaskCompletionSource();
        var unwantedCount = 0;

        using var s1 = bus.Subscribe(EventTypes.MissionOutcome, _ => Interlocked.Increment(ref unwantedCount));
        using var s2 = bus.Subscribe(EventTypes.TaskStarted, _ => wanted.TrySetResult());

        bus.Publish(Ev(EventTypes.TaskStarted));

        await wanted.Task.WaitAsync(Timeout);
        Assert.Equal(0, unwantedCount);
    }

    /// <summary>
    /// The property the whole design rests on. An observer is not permitted to break the colony or
    /// the other observers — if it were, adding a dashboard could fail a mission.
    /// </summary>
    [Fact]
    public async ThreadingTask A_throwing_subscriber_does_not_stop_the_others()
    {
        using var bus = new InProcessEventBus();
        var survivor = new TaskCompletionSource();

        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("subscriber is broken"));
        using var good = bus.Subscribe(_ => survivor.TrySetResult());

        bus.Publish(Ev());

        await survivor.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async ThreadingTask A_throwing_subscriber_stays_subscribed_for_the_next_event()
    {
        using var bus = new InProcessEventBus();
        var calls = 0;
        var twice = new TaskCompletionSource();

        using var flaky = bus.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref calls) == 2) twice.TrySetResult();
            throw new InvalidOperationException("always throws");
        });

        bus.Publish(Ev());
        bus.Publish(Ev());

        // Detaching a handler on its first exception would silently kill a dashboard over one
        // malformed event — a far harder failure to diagnose than a repeated log line.
        await twice.Task.WaitAsync(Timeout);
    }

    [Fact]
    public void Publish_never_throws()
    {
        using var bus = new InProcessEventBus();
        bus.Publish(null!);          // defensive: a publisher bug must not surface as a colony crash
        bus.Dispose();
        bus.Publish(Ev());           // after disposal, publication is a no-op rather than an error
    }

    [Fact]
    public async ThreadingTask Disposing_a_subscription_detaches_it()
    {
        using var bus = new InProcessEventBus();
        var afterDispose = 0;
        var marker = new TaskCompletionSource();

        var sub = bus.Subscribe(_ => Interlocked.Increment(ref afterDispose));
        sub.Dispose();
        using var witness = bus.Subscribe(_ => marker.TrySetResult());

        bus.Publish(Ev());

        await marker.Task.WaitAsync(Timeout);
        Assert.Equal(0, afterDispose);
    }

    [Fact]
    public void Disposing_a_subscription_twice_is_harmless()
    {
        using var bus = new InProcessEventBus();
        var sub = bus.Subscribe(_ => { });
        sub.Dispose();
        sub.Dispose();   // an SSE connection can dispose on both client abort and scope teardown
    }

    // ---- the LogEvent retrofit ---------------------------------------------

    /// <summary>
    /// A memory with the sentinel mission row already seeded.
    ///
    /// <c>events</c> carries a foreign key to <c>missions</c>, so an event logged against a mission
    /// that was never saved fails at the insert — which is the correct behaviour and a real
    /// constraint of the store, not an inconvenience to be worked around. Production reaches the
    /// same guarantee through <see cref="SqliteMemory.EnsureSystemMission"/>, which is exactly what
    /// system-level emitters (the self-test probes, the <c>system_api</c> channel) call before
    /// logging outside any real mission. These tests do the same rather than inventing a looser
    /// path, so they exercise the constraint the colony actually runs under.
    /// </summary>
    private static SqliteMemory MemoryWithMission(string missionId = "m1", IEventBus? bus = null)
    {
        var memory = new SqliteMemory(":memory:");
        memory.EnsureSystemMission(missionId);
        if (bus is not null) memory.EventBus = bus;
        return memory;
    }

    /// <summary>
    /// The regression that matters most in Phase 1: an unwired memory must behave exactly as it did
    /// before the bus existed. Every caller that constructs its own <c>SqliteMemory</c> — the CLI,
    /// several hundred tests — takes this path.
    /// </summary>
    [Fact]
    public void LogEvent_still_persists_when_no_bus_is_wired()
    {
        using var memory = MemoryWithMission();

        memory.LogEvent("m1", EventTypes.TaskStarted, "started");

        var rows = memory.GetRecentEvents(10, EventTypes.TaskStarted);
        Assert.Single(rows);
        Assert.Same(NullEventBus.Instance, memory.EventBus);
    }

    [Fact]
    public async ThreadingTask LogEvent_publishes_what_it_persisted()
    {
        using var bus = new InProcessEventBus();
        using var memory = MemoryWithMission(bus: bus);
        var seen = new TaskCompletionSource<ColonyEvent>();
        using var _ = bus.Subscribe(seen.SetResult);

        // No taskId: `tasks` has its own foreign key, and this test is about the event's fields
        // surviving publication, not about task persistence.
        var stored = memory.LogEvent("m1", EventTypes.ToolCalled, "ran a tool", antName: "researcher");

        var published = await seen.Task.WaitAsync(Timeout);

        // Field for field. ColonyEvent mirrors Domain.Event precisely so that publication drops
        // nothing on the way to a subscriber; if this drifts, one of the two shapes is wrong.
        Assert.Equal(stored.Id, published.Id);
        Assert.Equal(stored.MissionId, published.MissionId);
        Assert.Equal(stored.TaskId, published.TaskId);
        Assert.Equal(stored.AntName, published.AntName);
        Assert.Equal(stored.EventType, published.EventType);
        Assert.Equal(stored.Message, published.Message);
        Assert.Equal(stored.CreatedAt, published.CreatedAt);
    }

    /// <summary>
    /// Persist-then-publish, not the reverse. A subscriber must never be able to observe an event
    /// that is not already in the durable log, because acting on one that a later database failure
    /// leaves unrecorded turns a durable log into a best-effort one.
    /// </summary>
    [Fact]
    public async ThreadingTask An_event_is_already_readable_from_storage_when_subscribers_see_it()
    {
        using var bus = new InProcessEventBus();
        using var memory = MemoryWithMission(bus: bus);
        var readBack = new TaskCompletionSource<int>();

        using var _ = bus.Subscribe(ev =>
            readBack.TrySetResult(memory.GetRecentEvents(10, ev.EventType, ev.MissionId).Count));

        memory.LogEvent("m1", EventTypes.MissionStarted, "go");

        var readBackCount = await readBack.Task.WaitAsync(Timeout);
        Assert.Equal(1, readBackCount);
    }
}
