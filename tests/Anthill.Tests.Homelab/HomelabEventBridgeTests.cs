using Anthill.Core.Events;
using Anthill.Modules.Homelab;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v3.8.7 — the homelab joins the colony's live event stream.
///
/// <c>RecordEvent</c> has been the homelab's own event stream since v1.9.0: its own table, its own
/// severity vocabulary, nineteen call sites — and no live outlet, exactly like
/// <c>SqliteMemory.LogEvent</c> before v3.8.3. A VM restarting, a credential being used, an
/// inventory drifting: all durable, none of it visible on the console's stream.
/// </summary>
public class HomelabEventBridgeTests : IDisposable
{
    private readonly string _dir;

    public HomelabEventBridgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_hl_events_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private HomelabRepository NewRepo() =>
        new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static HomelabEvent Event(string id = "e1", string type = "inventory_changed") => new()
    {
        Id = id,
        EventType = type,
        SubjectKind = "vm",
        SubjectId = "vm-101",
        Severity = "warning",
        Message = "inventory drifted",
    };

    /// <summary>
    /// The regression that matters: an unwired repository behaves exactly as it did before this
    /// property existed. Every homelab test constructs one without a bus.
    /// </summary>
    [Fact]
    public void RecordEvent_still_persists_with_no_bus_wired()
    {
        using var repo = NewRepo();

        repo.RecordEvent(Event());

        Assert.Single(repo.RecentEvents(10));
        Assert.Same(NullEventBus.Instance, repo.EventBus);
    }

    [Fact]
    public void A_recorded_event_reaches_the_colony_stream()
    {
        using var bus = new InProcessEventBus();
        using var repo = NewRepo();
        repo.EventBus = bus;
        var seen = new TaskCompletionSource<ColonyEvent>();
        using var _ = bus.Subscribe(seen.SetResult);

        repo.RecordEvent(Event());

        Assert.True(seen.Task.Wait(Timeout));
        var published = seen.Task.Result;

        // Prefixed, not passed through: homelab draws from a different type vocabulary, and a
        // console filtering on a bare name would mix infrastructure activity into mission panels
        // the first time the two vocabularies agreed on a word.
        Assert.Equal("homelab_inventory_changed", published.EventType);
        Assert.Equal("inventory drifted", published.Message);
        Assert.Equal("inventory_changed", published.Metadata["homelab_event_type"]);
        Assert.Equal("vm", published.Metadata["subject_kind"]);
        Assert.Equal("vm-101", published.Metadata["subject_id"]);
        Assert.Equal("warning", published.Metadata["severity"]);
    }

    /// <summary>
    /// The wrinkle the mission log does not have. Homelab inserts are OR IGNORE because providers
    /// use stable ids (<c>pve-task:&lt;UPID&gt;</c>), so a re-sync re-offers events already stored.
    /// Publishing those would make every Proxmox re-sync replay recent history onto the console —
    /// a stream full of things that did not just happen.
    /// </summary>
    [Fact]
    public void A_duplicate_event_is_not_announced_a_second_time()
    {
        using var bus = new InProcessEventBus();
        using var repo = NewRepo();
        repo.EventBus = bus;

        var published = 0;
        var second = new TaskCompletionSource();
        using var _ = bus.Subscribe(ev =>
        {
            Interlocked.Increment(ref published);
            if (ev.Metadata.TryGetValue("subject_id", out var s) && (string?)s == "vm-999") second.TrySetResult();
        });

        repo.RecordEvent(Event(id: "stable-id"));
        repo.RecordEvent(Event(id: "stable-id"));   // the re-sync

        // A distinct event afterwards gives a deterministic point to assert at, rather than
        // sleeping and hoping the duplicate did not arrive late.
        var distinct = Event(id: "e2");
        distinct.SubjectId = "vm-999";
        repo.RecordEvent(distinct);

        Assert.True(second.Task.Wait(Timeout));
        Assert.Equal(2, published);
        Assert.Equal(2, repo.RecentEvents(10).Count);
    }

    /// <summary>
    /// Persist-then-publish. A subscriber must never observe an event that is not already durable,
    /// or a later database failure turns the audit log into a best-effort one.
    /// </summary>
    [Fact]
    public void An_event_is_already_stored_when_subscribers_see_it()
    {
        using var bus = new InProcessEventBus();
        using var repo = NewRepo();
        repo.EventBus = bus;
        var readBack = new TaskCompletionSource<int>();
        using var _ = bus.Subscribe(_ => readBack.TrySetResult(repo.RecentEvents(10).Count));

        repo.RecordEvent(Event());

        Assert.True(readBack.Task.Wait(Timeout));
        Assert.Equal(1, readBack.Task.Result);
    }
}
