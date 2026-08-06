namespace Anthill.SDK.Events;

/// <summary>
/// One thing that happened in the colony.
///
/// The shape is a deliberate, field-for-field mirror of <c>Anthill.Core.Domain.Event</c> — the row
/// <c>SqliteMemory.LogEvent</c> already writes. That is not a coincidence and it is not laziness:
/// the colony has been emitting a perfectly good event stream since long before it had a bus, into
/// a database table, where roughly eighty-five call sites across the Queen, the execution service,
/// the tool layer and the memory itself already produce it. Seventy-odd distinct event types are in
/// use today.
///
/// So the bus is not a new vocabulary to be adopted. It is a second outlet for one that already
/// exists. Because this record carries exactly the fields <c>Event</c> carries, the retrofit in
/// Phase 1 is lossless in both directions — <c>LogEvent</c> persists as it always has and then
/// publishes, and no existing call site changes at all.
///
/// If a future field is added here that <c>Event</c> does not have, that symmetry breaks and
/// subscribers start seeing something the durable log does not. Add it to both, or to neither.
/// </summary>
public sealed record ColonyEvent
{
    public required string EventType { get; init; }

    /// <summary>The mission this belongs to. Empty only for events that genuinely predate a
    /// mission — runtime configuration findings, self-test probes — never as a shrug.</summary>
    public string MissionId { get; init; } = "";

    public string? TaskId { get; init; }

    /// <summary>Which ant. Null for events the colony itself raised rather than a worker.</summary>
    public string? AntName { get; init; }

    public string Message { get; init; } = "";

    /// <summary>
    /// Structured detail. Subscribers should read named keys and tolerate their absence: metadata
    /// is written by many hands over many versions, and a UI that throws on a missing key turns a
    /// cosmetic gap into a dead dashboard.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    public string Id { get; init; } = Guid.NewGuid().ToString();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
