namespace Anthill.Core.Models;

/// <summary>How the circuit breaker should treat a model-call outcome.</summary>
public enum CircuitSignal
{
    /// <summary>The provider answered (or failed for a definitively non-transport reason). Clears the breaker.</summary>
    Healthy,
    /// <summary>The provider was slow or unreachable — the exact condition that pins the single-writer queue.</summary>
    TransientFault,
    /// <summary>Tells us nothing about provider health (mission cancelled, or an unclassifiable error). Leaves state untouched.</summary>
    Neutral,
}

/// <summary>
/// Classifies the sentinel strings the model clients return (they never throw across the ant
/// boundary — see <see cref="IModelClient"/>) into a small, stable outcome vocabulary. This is the
/// one place that knows those strings, so the router can log a precise <c>outcome</c> and the
/// circuit breaker can tell a provider-is-down fault from a config error or a mission cancellation.
/// </summary>
public enum ModelCallOutcome
{
    Ok,
    Empty,
    Cancelled,
    Timeout,
    ConnectError,
    HttpError,
    AuthError,
    NotAvailable,
    ConfigError,
    Error,
}

public static class ModelCallOutcomeExtensions
{
    /// <summary>
    /// Maps a client response to an outcome. Order matters: the more specific sentinels are tested
    /// before the generic <c>ERROR:</c> fallthrough. Anything that is not an error string (and not an
    /// "empty response" notice) is a successful generation.
    /// </summary>
    public static ModelCallOutcome Classify(string? response)
    {
        if (string.IsNullOrEmpty(response)) return ModelCallOutcome.Empty;
        var r = response;

        // Mission stopped this call — it is never evidence about the provider's health.
        if (Has(r, "cancelled because the mission was stopped")) return ModelCallOutcome.Cancelled;
        // Slow / unreachable — the queue-pinning conditions the breaker exists to short-circuit.
        if (Has(r, "timed out")) return ModelCallOutcome.Timeout;
        if (Has(r, "Could not connect") || Has(r, "Could not reach")) return ModelCallOutcome.ConnectError;
        // Definitive, non-transient responses: the provider answered or the request is misconfigured.
        if (Has(r, "API key not configured")) return ModelCallOutcome.ConfigError;
        if (r.Contains("(401)") || r.Contains("(403)") || Has(r, "Unauthorized") || Has(r, "Forbidden"))
            return ModelCallOutcome.AuthError;
        if (Has(r, "is not available")) return ModelCallOutcome.NotAvailable;
        if (Has(r, "answered HTTP") || Has(r, "request failed (")) return ModelCallOutcome.HttpError;
        if (Has(r, "returned an empty response")) return ModelCallOutcome.Empty;
        if (r.StartsWith("ERROR:", StringComparison.Ordinal)) return ModelCallOutcome.Error;
        return ModelCallOutcome.Ok;
    }

    /// <summary>The lowercase name recorded in <c>model_call</c> event metadata for operator dashboards.</summary>
    public static string Name(this ModelCallOutcome outcome) => outcome switch
    {
        ModelCallOutcome.Ok => "ok",
        ModelCallOutcome.Empty => "empty",
        ModelCallOutcome.Cancelled => "cancelled",
        ModelCallOutcome.Timeout => "timeout",
        ModelCallOutcome.ConnectError => "connect_error",
        ModelCallOutcome.HttpError => "http_error",
        ModelCallOutcome.AuthError => "auth_error",
        ModelCallOutcome.NotAvailable => "not_available",
        ModelCallOutcome.ConfigError => "config_error",
        _ => "error",
    };

    /// <summary>How the breaker should treat this outcome.</summary>
    public static CircuitSignal ToCircuitSignal(this ModelCallOutcome outcome) => outcome switch
    {
        // The provider was slow or unreachable.
        ModelCallOutcome.Timeout or ModelCallOutcome.ConnectError => CircuitSignal.TransientFault,
        // We stopped the call ourselves, or couldn't classify it — no signal about provider health.
        ModelCallOutcome.Cancelled or ModelCallOutcome.Error => CircuitSignal.Neutral,
        // Everything else means the provider actually responded (even a 401 or "model not pulled").
        _ => CircuitSignal.Healthy,
    };

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
