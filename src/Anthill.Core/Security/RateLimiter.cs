using System.Diagnostics;

namespace Anthill.Core.Security;

/// <summary>
/// Sliding-window in-memory rate limiter keyed by client IP.
///
/// Thread-safe for single-process deployments (the default ANTHILL posture). For
/// multi-process or distributed setups, swap this for a Redis-backed limiter — the
/// same caveat the Python build documented. Uses a monotonic stopwatch clock so the
/// window is immune to wall-clock adjustments.
/// </summary>
public sealed class RateLimiter
{
    private readonly double _windowSeconds;
    private readonly int _maxCalls;
    private readonly Dictionary<string, List<double>> _buckets = new();
    private readonly object _lock = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public RateLimiter(int windowSeconds, int maxCalls)
    {
        _windowSeconds = windowSeconds;
        _maxCalls = maxCalls;
    }

    private static double Now() => Clock.Elapsed.TotalSeconds;

    private List<double> ActiveCallsLocked(string key, double now)
    {
        var cutoff = now - _windowSeconds;
        var calls = _buckets.TryGetValue(key, out var existing)
            ? existing.Where(t => t > cutoff).ToList()
            : new List<double>();
        _buckets[key] = calls;
        return calls;
    }

    /// <summary>Record a call and return whether it is still within the limit.</summary>
    public bool IsAllowed(string key)
    {
        var now = Now();
        lock (_lock)
        {
            var calls = ActiveCallsLocked(key, now);
            if (calls.Count >= _maxCalls) return false;
            calls.Add(now);
            _buckets[key] = calls;
            return true;
        }
    }

    /// <summary>Check whether a key is currently limited without recording a new call.</summary>
    public bool IsLimited(string key)
    {
        var now = Now();
        lock (_lock) return ActiveCallsLocked(key, now).Count >= _maxCalls;
    }

    /// <summary>Record an attempt without returning an allow/deny decision (failed-auth bucket).</summary>
    public void RecordAttempt(string key)
    {
        var now = Now();
        lock (_lock)
        {
            var calls = ActiveCallsLocked(key, now);
            calls.Add(now);
            _buckets[key] = calls;
        }
    }

    /// <summary>Clear a key after successful authentication so good clients are never throttled.</summary>
    public void Clear(string key)
    {
        lock (_lock) _buckets.Remove(key);
    }
}
