namespace Anthill.SDK.Reasoning;

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
/// boundary — see <see cref="IReasoningProvider"/>) into a small, stable outcome vocabulary. This is the
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

/// <summary>
/// v2.26.0 pre-V3 hardening: the TYPED model-call result. Providers still transport failure as
/// in-band sentinel strings (they never throw across the ant boundary), but callers no longer
/// branch on <c>StartsWith("ERROR:")</c> — the status is classified ONCE, here, by the same
/// classifier the router's telemetry already records. Content survives for narrative/fallback
/// use; the status is the authority. An Empty response is never Ok.
/// </summary>
public sealed record ModelCallResult(ModelCallOutcome Status, string Content)
{
    /// <summary>
    /// The provider and model that ACTUALLY served this call. v0.3.8.57.
    ///
    /// <c>ModelResponse</c> has carried both since v3.4.0 and <c>ToCallResult()</c> discarded them,
    /// so no ant ever learned which model produced its output — and an artifact could therefore
    /// never say. The alternative, asking the router for the CONFIGURED route afterwards, answers a
    /// question adjacent to the one asked: a rerouted call would record the model that did not run.
    ///
    /// Null means no model served it, which is a real and useful state: a deterministic ant, or a
    /// call that failed before a provider was chosen.
    /// </summary>
    public string? Provider { get; init; }
    public string? Model { get; init; }

    public bool Ok => Status == ModelCallOutcome.Ok;
    /// <summary>Transient statuses a retry may cure (mirrors the circuit breaker's transient set).</summary>
    public bool Retryable => Status is ModelCallOutcome.Timeout or ModelCallOutcome.ConnectError
        or ModelCallOutcome.Empty or ModelCallOutcome.HttpError;

    public static ModelCallResult From(string? response) =>
        new(ModelCallOutcomeExtensions.Classify(response), response ?? "");
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

    /// <summary>
    /// THE COLONY stopped this call — an operator cancel or a mission deadline — so the outcome is
    /// evidence about us and never about the route. v0.3.8.81 (PLAN.md §2 R3).
    ///
    /// This predicate exists because the rule already had TWO implementations that disagreed, four
    /// lines apart inside one method. <see cref="ToCircuitSignal"/> has read Cancelled as
    /// <see cref="CircuitSignal.Neutral"/> since it was written, with the comment "we stopped the
    /// call ourselves — no signal about provider health". <c>ModelRouter.SendCore</c> derived its
    /// pheromone delta from <see cref="ModelCallResult.Ok"/> alone, and <c>Ok</c> is false for a
    /// cancelled call — so the same outcome wrote <c>success: false</c> and -0.01 against
    /// <c>model:{provider}:{model}:{role}</c>.
    ///
    /// The breaker's copy is transient state and the trail's copy is DURABLE, so the wrong one was
    /// the one that outlived the mission: every operator stop taught the colony a little more firmly
    /// that the model its cancelled role was using is unsuited to that role. Nothing in the mission
    /// looked wrong afterwards, which is why this survived — the damage is in the memory, and the
    /// memory is not read again until routing next asks it a question.
    ///
    /// Both readers now answer from HERE. That is the only arrangement in which they cannot drift
    /// apart again, and "two implementations of one rule" is a defect class this repository names.
    /// </summary>
    public static bool IsColonyStopped(this ModelCallOutcome outcome) =>
        outcome is ModelCallOutcome.Cancelled;

    /// <summary>How the breaker should treat this outcome.</summary>
    public static CircuitSignal ToCircuitSignal(this ModelCallOutcome outcome) => outcome switch
    {
        // The provider was slow or unreachable.
        ModelCallOutcome.Timeout or ModelCallOutcome.ConnectError => CircuitSignal.TransientFault,
        // We stopped the call ourselves — no signal about provider health. Read from the shared
        // predicate rather than naming the enum member again, so the trail and the breaker agree
        // by construction instead of by two people remembering the same thing.
        _ when outcome.IsColonyStopped() => CircuitSignal.Neutral,
        // Unclassifiable: also no signal, and deliberately NOT folded into IsColonyStopped — an
        // Error is a call we could not read, not a call we stopped, and only the second is
        // guaranteed to say nothing about the route.
        ModelCallOutcome.Error => CircuitSignal.Neutral,
        // Everything else means the provider actually responded (even a 401 or "model not pulled").
        _ => CircuitSignal.Healthy,
    };

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
