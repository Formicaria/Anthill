using Anthill.SDK.Events;

namespace Anthill.SDK.Memory;

/// <summary>
/// The durable side of the event stream.
///
/// <see cref="IEventBus"/> and this interface are two views of one act. The bus is live and lossy
/// by nature — a subscriber that was not listening at the moment of publication never sees the
/// event. The log is durable and replayable, and it is what a dashboard opened five minutes into a
/// mission reads to find out what it missed.
///
/// Keeping them separate matters for ordering. The colony's rule is persist-then-publish, never the
/// reverse: an event that used to be durable must not become merely broadcast because a bus was
/// introduced. Two interfaces make that ordering something you can state and test, rather than a
/// convention living in one method body.
/// </summary>
public interface IEventLog
{
    /// <summary>Record an event durably. Returns the stored event, with its assigned id.</summary>
    ColonyEvent Append(ColonyEvent colonyEvent);

    /// <summary>
    /// Most recent first. Both filters are optional and combine; this is the replay a late
    /// subscriber uses to catch up before switching to the live stream.
    /// </summary>
    IReadOnlyList<ColonyEvent> Recent(int limit = 30, string? eventType = null,
        string? missionId = null);
}
