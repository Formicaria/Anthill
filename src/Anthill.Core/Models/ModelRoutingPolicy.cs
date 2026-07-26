namespace Anthill.Core.Models;

/// <summary>
/// v2.11.1 (NORTH_STAR V3-track — model routing intelligence). Pure, deterministic helpers that
/// let ANTHILL pick the right model for a task instead of always taking the statically-configured
/// route. Two responsibilities, both side-effect-free and unit-testable without a DB or a network:
///
///  * <see cref="ModelStats"/> aggregates recorded <see cref="ModelCallRecord"/>s into per-route
///    health (success rate + average latency), the evidence a routing decision is justified by.
///  * <see cref="ModelRoutingPolicy"/> chooses among candidate routes given the task's risk class
///    and that health, and — crucially — returns a human-readable REASON for the choice, so the
///    Console can explain "why this model" the way the hardware scheduler explains concurrency.
///
/// Nothing here changes behavior on its own: <c>ModelRouter.GetRoute</c> opts in by consulting
/// <see cref="ModelRoutingPolicy.Choose"/>. Absent any statistics, the configured route is kept.
/// </summary>
public sealed record ModelCallRecord(string Provider, string Model, bool Success, double LatencyMs)
{
    public static ModelCallRecord From(string provider, string model, ModelCallOutcome outcome, double latencyMs) =>
        new(provider, model, outcome == ModelCallOutcome.Ok, latencyMs);
}

/// <summary>Aggregated health of one <c>provider:model</c> route.</summary>
public sealed record RouteHealth(string Route, int Calls, int Successes, double SuccessRate, double AvgLatencyMs)
{
    /// <summary>Below this many calls we have too little evidence to condemn a route, so it is
    /// treated as healthy (benefit of the doubt) — we never strand a new route on one bad sample.</summary>
    public const int MinCallsForVerdict = 3;
    public const double HealthyThreshold = 0.6;

    public bool Healthy => Calls < MinCallsForVerdict || SuccessRate >= HealthyThreshold;
}

public static class ModelStats
{
    public static string Key(string provider, string model) => $"{provider}:{model}";

    /// <summary>Fold a flat list of call records into per-route health. Deterministic; empty in → empty out.</summary>
    public static IReadOnlyDictionary<string, RouteHealth> Aggregate(IEnumerable<ModelCallRecord> records)
    {
        var result = new Dictionary<string, RouteHealth>(StringComparer.Ordinal);
        foreach (var g in records.GroupBy(r => Key(r.Provider, r.Model), StringComparer.Ordinal))
        {
            var calls = 0; var successes = 0; double latencyTotal = 0;
            foreach (var r in g)
            {
                calls++;
                if (r.Success) successes++;
                latencyTotal += r.LatencyMs;
            }
            var rate = calls == 0 ? 0d : (double)successes / calls;
            var avg = calls == 0 ? 0d : latencyTotal / calls;
            result[g.Key] = new RouteHealth(g.Key, calls, successes, rate, avg);
        }
        return result;
    }
}

/// <summary>The route chosen for a task, plus the reason it was chosen (for the Console / audit).</summary>
public sealed record RouteChoice(string Provider, string Model, string Reason)
{
    public string Route => ModelStats.Key(Provider, Model);
}

public static class ModelRoutingPolicy
{
    /// <summary>
    /// Choose a route for a task. Rules, in order:
    ///  1. Candidate pool = the configured route plus any alternates, de-duplicated.
    ///  2. Drop routes proven unhealthy by <paramref name="stats"/> (unknown/low-sample = kept).
    ///  3. If everything is unhealthy, keep the configured route (never route to nothing) and say so.
    ///  4. High/critical risk favors STABILITY: keep the configured route while it is healthy;
    ///     only switch if it is proven unhealthy, and then to the healthiest alternate.
    ///  5. Low/medium risk favors SPEED: among healthy routes pick the lowest average latency,
    ///     breaking ties by higher success rate.
    /// </summary>
    public static RouteChoice Choose(
        string riskClass,
        (string Provider, string Model) configured,
        IReadOnlyList<(string Provider, string Model)> alternates,
        IReadOnlyDictionary<string, RouteHealth> stats)
    {
        var configuredKey = ModelStats.Key(configured.Provider, configured.Model);

        var pool = new List<(string Provider, string Model)> { configured };
        foreach (var a in alternates)
            if (!pool.Any(p => p.Provider == a.Provider && p.Model == a.Model))
                pool.Add(a);

        bool Healthy((string Provider, string Model) r) =>
            !stats.TryGetValue(ModelStats.Key(r.Provider, r.Model), out var h) || h.Healthy;

        RouteHealth? H((string Provider, string Model) r) =>
            stats.TryGetValue(ModelStats.Key(r.Provider, r.Model), out var h) ? h : null;

        var healthy = pool.Where(Healthy).ToList();
        if (healthy.Count == 0)
            return new RouteChoice(configured.Provider, configured.Model,
                $"kept configured route {configuredKey}: every candidate is currently unhealthy");

        var highRisk = riskClass is "high" or "critical";
        if (highRisk)
        {
            if (Healthy(configured))
                return new RouteChoice(configured.Provider, configured.Model,
                    $"{riskClass}-risk task kept the configured route {configuredKey} for stability{Metrics(H(configured))}");

            var best = healthy
                .OrderByDescending(r => H(r)?.SuccessRate ?? 1d)
                .ThenBy(r => H(r)?.AvgLatencyMs ?? double.MaxValue)
                .First();
            return new RouteChoice(best.Provider, best.Model,
                $"{riskClass}-risk task rerouted off unhealthy {configuredKey} to healthiest alternate {ModelStats.Key(best.Provider, best.Model)}{Metrics(H(best))}");
        }

        var fastest = healthy
            .OrderBy(r => H(r)?.AvgLatencyMs ?? double.MaxValue)
            .ThenByDescending(r => H(r)?.SuccessRate ?? 1d)
            .First();

        var reason = ModelStats.Key(fastest.Provider, fastest.Model) == configuredKey
            ? $"{riskClass}-risk task kept the configured route {configuredKey}{Metrics(H(fastest))}"
            : $"{riskClass}-risk task chose faster healthy route {ModelStats.Key(fastest.Provider, fastest.Model)} over {configuredKey}{Metrics(H(fastest))}";
        return new RouteChoice(fastest.Provider, fastest.Model, reason);
    }

    private static string Metrics(RouteHealth? h) =>
        h is null ? " (no stats yet)" : $" ({h.SuccessRate:P0} success, {h.AvgLatencyMs:F0}ms avg over {h.Calls} calls)";
}
