using System.Collections.Concurrent;
using Anthill.SDK.Knowledge;

namespace Anthill.Modules.Knowledge;

/// <summary>
/// A small, short-lived, SCOPE-PARTITIONED cache for knowledge reads.
///
/// The reason this is not a general-purpose cache: every entry is keyed on a
/// <see cref="KnowledgeScope.CacheKey"/> prefix, and there is no API that can read an entry without
/// naming a scope. Rule 12 says knowledge never crosses a project boundary, and a shared cache is
/// the classic way that rule gets broken by accident — one query warms an entry, a differently
/// scoped query hits it, and nothing in the call path looks wrong. Partitioning by construction is
/// cheaper than auditing for it.
///
/// TTLs are deliberately short (tens of seconds). This exists to stop a console poll and an agent's
/// iterative retrieval from hammering FORAGER with the same question, not to be a read model. Stale
/// knowledge presented as current is a correctness bug, so the cache errs toward missing.
/// </summary>
internal sealed class KnowledgeCache
{
    private sealed record Entry(object Value, DateTime ExpiresUtc, string ScopePrefix);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Hard ceiling on entries. A colony that queries many scopes must not grow this without bound;
    /// when it is hit the cache is cleared wholesale rather than evicted cleverly, because an LRU
    /// here would be more machinery than the problem deserves and a cold cache costs one round trip.
    /// </summary>
    private const int MaxEntries = 512;

    public bool TryGet<T>(string key, out T? value) where T : class
    {
        value = null;
        if (!_entries.TryGetValue(key, out var entry)) return false;

        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = entry.Value as T;

        // A key whose value is the wrong type is a defect, not a miss to be papered over — but it
        // must not throw on a read path. Dropping it means the next call re-fetches correctly.
        if (value is null) _entries.TryRemove(key, out _);
        return value is not null;
    }

    public void Set<T>(string key, T value, int seconds) where T : class
    {
        if (seconds <= 0) return;
        if (_entries.Count >= MaxEntries) _entries.Clear();

        // The scope prefix is recovered from the key so invalidation can target one scope. Keys are
        // built as "op|scopeCacheKey|...", so the second field is the partition.
        var parts = key.Split('|');
        var prefix = parts.Length > 1 ? parts[1] : "";

        _entries[key] = new Entry(value, DateTime.UtcNow.AddSeconds(seconds), prefix);
    }

    /// <summary>
    /// Drop everything cached for one scope. Called after any ingestion or review that could have
    /// changed what the scope knows. Scoped rather than global: another project's cached reads are
    /// not stale because this project ingested, and flushing them would turn every write anywhere
    /// into a colony-wide cache miss.
    /// </summary>
    public void InvalidateScope(KnowledgeScope scope)
    {
        var prefix = scope.CacheKey;
        foreach (var pair in _entries)
            if (string.Equals(pair.Value.ScopePrefix, prefix, StringComparison.Ordinal))
                _entries.TryRemove(pair.Key, out _);
    }

    /// <summary>Drop everything. For a configuration change, where the endpoint itself may have moved.</summary>
    public void Clear() => _entries.Clear();

    internal int Count => _entries.Count;
}
