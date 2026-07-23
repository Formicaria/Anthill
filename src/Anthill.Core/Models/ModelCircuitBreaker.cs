namespace Anthill.Core.Models;

/// <summary>A point-in-time view of one route's breaker: state is <c>closed</c>, <c>open</c>, or <c>half_open</c>.</summary>
public sealed record CircuitStatus(string Key, string State, int ConsecutiveFaults, double SecondsUntilClose);

/// <summary>
/// A per-provider circuit breaker for model calls. After <c>threshold</c> consecutive transport
/// faults (timeouts / connection failures) on the same route, the breaker <b>opens</b>: further
/// calls fast-fail in microseconds with a clear reason instead of each eating a full
/// <see cref="Configuration.AnthillRuntime.ModelCallTimeoutSeconds"/> wait. This is what stops a slow
/// or dead provider from re-pinning the single-writer job queue (the failure mode fixed in v2.6.6).
///
/// After the cooldown elapses the breaker goes <b>half-open</b> and lets a single probe through; a
/// healthy result <b>closes</b> it, another fault re-opens it. Any healthy response — even a 401 or a
/// "model not pulled" — closes the breaker, because it proves the provider is actually answering.
///
/// The clock is injectable so the state machine is fully unit-testable with no real waiting.
/// </summary>
public sealed class ModelCircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new();

    private sealed class State
    {
        public int ConsecutiveFaults;
        public DateTime? OpenUntil;   // non-null while the breaker is open
        public bool ProbeInFlight;    // half-open: one probe has been let through
    }

    public ModelCircuitBreaker(int threshold, int cooldownSeconds, Func<DateTime>? now = null)
    {
        _threshold = Math.Max(1, threshold);
        _cooldown = TimeSpan.FromSeconds(Math.Max(1, cooldownSeconds));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns <c>null</c> if a call on <paramref name="key"/> may proceed, or a human-readable reason
    /// if it must be short-circuited. When the cooldown has elapsed the first caller is admitted as a
    /// half-open probe (and returns null); concurrent callers stay blocked until that probe reports back.
    /// </summary>
    public string? Blocked(string key)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(key, out var s) || s.OpenUntil is null) return null;

            if (_now() >= s.OpenUntil.Value)
            {
                if (s.ProbeInFlight) return Reason(key, s); // a probe is already out — hold the rest back
                s.ProbeInFlight = true;
                return null;                                 // admit exactly one probe (half-open)
            }
            return Reason(key, s);
        }
    }

    /// <summary>Feeds a call's outcome back into the breaker for <paramref name="key"/>.</summary>
    public void Record(string key, CircuitSignal signal)
    {
        if (signal == CircuitSignal.Neutral) return; // tells us nothing — leave state exactly as-is

        lock (_lock)
        {
            var s = _states.TryGetValue(key, out var existing) ? existing : _states[key] = new State();
            if (signal == CircuitSignal.TransientFault)
            {
                s.ConsecutiveFaults++;
                s.ProbeInFlight = false;
                if (s.ConsecutiveFaults >= _threshold)
                    s.OpenUntil = _now().Add(_cooldown); // (re)open, or extend a half-open probe's failure
            }
            else // Healthy — the provider answered, so the transport is fine again.
            {
                s.ConsecutiveFaults = 0;
                s.OpenUntil = null;
                s.ProbeInFlight = false;
            }
        }
    }

    /// <summary>True if the breaker is currently open (blocking) for <paramref name="key"/>. Diagnostics only.</summary>
    public bool IsOpen(string key)
    {
        lock (_lock)
            return _states.TryGetValue(key, out var s) && s.OpenUntil is { } until && _now() < until;
    }

    /// <summary>A point-in-time view of every route the breaker has seen, for operator dashboards.</summary>
    public IReadOnlyList<CircuitStatus> Snapshot()
    {
        lock (_lock)
        {
            var now = _now();
            var list = new List<CircuitStatus>(_states.Count);
            foreach (var (key, s) in _states)
            {
                string state;
                double secondsUntilClose = 0;
                if (s.OpenUntil is { } until && now < until)
                {
                    state = "open";
                    secondsUntilClose = Math.Round((until - now).TotalSeconds, 1);
                }
                else if (s.OpenUntil is not null)
                {
                    state = "half_open"; // cooldown elapsed; the next call will be admitted as a probe
                }
                else
                {
                    state = "closed";
                }
                list.Add(new CircuitStatus(key, state, s.ConsecutiveFaults, secondsUntilClose));
            }
            return list;
        }
    }

    private string Reason(string key, State s) =>
        $"circuit open for {key} after {s.ConsecutiveFaults} consecutive transport failures; " +
        $"cooling down until {s.OpenUntil:HH:mm:ss}Z";
}
