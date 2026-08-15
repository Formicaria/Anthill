namespace Anthill.Modules.Micromound;

/// <summary>
/// Persistence for the mound registry, kept behind an interface so M1's logic is provable without
/// a database — the same move the homelab made with its mock-provider harness, and the reason its
/// 240 tests run network-free.
/// </summary>
public interface IMoundStore
{
    IReadOnlyList<MoundRecord> ListMounds();
    MoundRecord? GetMound(string moundId);
    void UpsertMound(MoundRecord mound);
    bool RemoveMound(string moundId);

    void PutEnrollmentToken(EnrollmentToken token);
    EnrollmentToken? GetEnrollmentToken(string moundId);

    void RecordBeat(MoundBeat beat);
    IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit);

    /// <summary>Widget payloads, keyed the same way integration_state keys them.</summary>
    void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt);
    (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind);
}

/// <summary>
/// The reference implementation. Deterministic, network-free, and the one every test uses; the
/// SQLite store lands with the Api wiring, against these same semantics.
/// </summary>
public sealed class InMemoryMoundStore : IMoundStore
{
    private readonly Dictionary<string, MoundRecord> _mounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnrollmentToken> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MoundBeat>> _beats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Payload, string UpdatedAt)> _widgets =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyList<MoundRecord> ListMounds()
    {
        lock (_gate) return _mounds.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    public MoundRecord? GetMound(string moundId)
    {
        lock (_gate) return _mounds.GetValueOrDefault(moundId);
    }

    public void UpsertMound(MoundRecord mound)
    {
        ArgumentNullException.ThrowIfNull(mound);
        lock (_gate) _mounds[mound.MoundId] = mound;
    }

    public bool RemoveMound(string moundId)
    {
        lock (_gate)
        {
            _tokens.Remove(moundId);
            _beats.Remove(moundId);
            return _mounds.Remove(moundId);
        }
    }

    public void PutEnrollmentToken(EnrollmentToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate) _tokens[token.MoundId] = token;
    }

    public EnrollmentToken? GetEnrollmentToken(string moundId)
    {
        lock (_gate) return _tokens.GetValueOrDefault(moundId);
    }

    public void RecordBeat(MoundBeat beat)
    {
        ArgumentNullException.ThrowIfNull(beat);
        lock (_gate)
        {
            if (!_beats.TryGetValue(beat.MoundId, out var list))
            {
                list = [];
                _beats[beat.MoundId] = list;
            }

            list.Add(beat);

            // Ring buffer, sized like a device's own: history is useful, unbounded history is a leak.
            if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
        }
    }

    public IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit)
    {
        lock (_gate)
        {
            if (!_beats.TryGetValue(moundId, out var list)) return [];
            return list.AsEnumerable().Reverse().Take(Math.Max(limit, 0)).ToList();
        }
    }

    public void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt)
    {
        lock (_gate) _widgets[widgetKind] = (payloadJson, updatedAt);
    }

    public (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind)
    {
        lock (_gate) return _widgets.TryGetValue(widgetKind, out var found) ? found : null;
    }
}
