using System.Diagnostics;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Autonomy;

/// <summary>Result of one backend health probe: whether it answered, and how fast.</summary>
public sealed record BackendProbe(bool Ok, double LatencyMs);

/// <summary>
/// Outcome of a ResourceGovernor sizing pass. <see cref="EffectiveConcurrency"/> is what the
/// Director may actually run right now; <see cref="Code"/>/<see cref="Reason"/> name the binding
/// constraint (or "ok"), and <see cref="Signals"/> carries the raw readings for the status
/// endpoint / audit trail.
/// </summary>
public sealed record GovernorDecision(int EffectiveConcurrency, string Code, string Reason, Dictionary<string, object?> Signals);

/// <summary>
/// Phase 3 concurrency governor. Sizes how many autonomous missions may run at once: it starts
/// from the configured cap (<see cref="AnthillRuntime.AutonomyConcurrency"/>) and only ever
/// lowers it — never raises it — based on three cheap signals: normalized CPU load
/// (1-minute loadavg per core), available-memory fraction, and a latency probe against the
/// Ollama backend. Full VRAM tracking is deliberately deferred to a later hardware-aware
/// scheduler phase; this keeps the host responsive without GPU-specific dependencies.
///
/// Failure posture: an unreachable model backend clamps to 1 (missions would fail anyway, so
/// don't multiply the failures), while an unreadable host signal is simply skipped (fail-open to
/// the configured cap — e.g. non-Linux hosts without /proc). Signal readers are injectable for
/// deterministic tests.
/// </summary>
public sealed class ResourceGovernor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // Soft limits halve the configured cap; hard limits clamp it to 1.
    internal const double LoadSoftPerCore = 1.25;
    internal const double LoadHardPerCore = 2.0;
    internal const double MemSoftAvailableFraction = 0.20;
    internal const double MemHardAvailableFraction = 0.10;
    internal const double BackendSlowMs = 2500;
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(15);

    private readonly Func<double?> _readLoadPerCore;
    private readonly Func<double?> _readAvailableMemoryFraction;
    private readonly Func<BackendProbe?> _probeBackend;

    private BackendProbe? _cachedProbe;
    private DateTime _cachedProbeAt = DateTime.MinValue;

    public ResourceGovernor(
        Func<double?>? readLoadPerCore = null,
        Func<double?>? readAvailableMemoryFraction = null,
        Func<BackendProbe?>? probeBackend = null)
    {
        _readLoadPerCore = readLoadPerCore ?? ReadLoadPerCore;
        _readAvailableMemoryFraction = readAvailableMemoryFraction ?? ReadAvailableMemoryFraction;
        _probeBackend = probeBackend ?? ProbeOllama;
    }

    /// <summary>Sizes effective concurrency for this cycle. Never returns less than 1 or more than <paramref name="requested"/>.</summary>
    public GovernorDecision Evaluate(int requested)
    {
        requested = Math.Max(1, requested);
        var halved = Math.Max(1, requested / 2);
        var effective = requested;
        var code = "ok";
        var reason = "Within host and backend limits; full configured concurrency available.";
        var signals = new Dictionary<string, object?> { ["requested"] = requested };

        void Clamp(int cap, string clampCode, string clampReason)
        {
            if (cap >= effective) return;
            effective = cap;
            code = clampCode;
            reason = clampReason;
        }

        var load = SafeRead(_readLoadPerCore);
        if (load is { } l)
        {
            signals["load_per_core"] = Math.Round(l, 3);
            if (l >= LoadHardPerCore) Clamp(1, "load_critical", $"CPU load {l:0.00}/core ≥ {LoadHardPerCore:0.00}; concurrency clamped to 1.");
            else if (l >= LoadSoftPerCore) Clamp(halved, "load_high", $"CPU load {l:0.00}/core ≥ {LoadSoftPerCore:0.00}; concurrency halved.");
        }

        var mem = SafeRead(_readAvailableMemoryFraction);
        if (mem is { } m)
        {
            signals["memory_available_fraction"] = Math.Round(m, 3);
            if (m <= MemHardAvailableFraction) Clamp(1, "memory_critical", $"Only {m:P0} of memory available (≤ {MemHardAvailableFraction:P0}); concurrency clamped to 1.");
            else if (m <= MemSoftAvailableFraction) Clamp(halved, "memory_low", $"Only {m:P0} of memory available (≤ {MemSoftAvailableFraction:P0}); concurrency halved.");
        }

        var probe = CachedProbe();
        if (probe is { } p)
        {
            signals["backend_ok"] = p.Ok;
            signals["backend_latency_ms"] = Math.Round(p.LatencyMs, 1);
            if (!p.Ok) Clamp(1, "backend_unreachable", "Model backend probe failed; concurrency clamped to 1.");
            else if (p.LatencyMs >= BackendSlowMs) Clamp(halved, "backend_slow", $"Model backend answered in {p.LatencyMs:0}ms (≥ {BackendSlowMs:0}ms); concurrency halved.");
        }

        signals["effective"] = effective;
        return new GovernorDecision(effective, code, reason, signals);
    }

    private BackendProbe? CachedProbe()
    {
        var now = AnthillTime.NowUtc();
        if (now - _cachedProbeAt < ProbeTtl) return _cachedProbe;
        _cachedProbe = SafeRead(_probeBackend);
        _cachedProbeAt = now;
        return _cachedProbe;
    }

    private static T? SafeRead<T>(Func<T?> reader)
    {
        try { return reader(); }
        catch { return default; }
    }

    // ---- default signal readers -------------------------------------------

    /// <summary>1-minute load average divided by core count. Null when /proc/loadavg is unavailable (fail-open).</summary>
    private static double? ReadLoadPerCore()
    {
        const string path = "/proc/loadavg";
        if (!File.Exists(path)) return null;
        var first = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!double.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var load)) return null;
        return load / Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>MemAvailable/MemTotal from /proc/meminfo. Null when unavailable (fail-open).</summary>
    private static double? ReadAvailableMemoryFraction()
    {
        const string path = "/proc/meminfo";
        if (!File.Exists(path)) return null;
        double? total = null, available = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseMeminfoKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = ParseMeminfoKb(line);
            if (total is not null && available is not null) break;
        }
        if (total is not > 0 || available is null) return null;
        return available.Value / total.Value;
    }

    private static double? ParseMeminfoKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }

    /// <summary>
    /// Cheap health/latency probe against the Ollama backend (GET /api/version). Returns null —
    /// signal not applicable — when Ollama is disabled, so offline installs are never clamped by it.
    /// </summary>
    private static BackendProbe? ProbeOllama()
    {
        if (!AnthillRuntime.UseOllama) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = Http.GetAsync($"{AnthillRuntime.OllamaHost.TrimEnd('/')}/api/version").GetAwaiter().GetResult();
            sw.Stop();
            return new BackendProbe(response.IsSuccessStatusCode, sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            sw.Stop();
            return new BackendProbe(false, sw.Elapsed.TotalMilliseconds);
        }
    }
}
