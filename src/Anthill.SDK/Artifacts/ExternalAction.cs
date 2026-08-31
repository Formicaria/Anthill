using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// AN EXTERNAL ACTION AS A RECORD — where it went, who allowed it, and what came back. v0.3.8.103.
///
/// THE FAILURE THIS TYPE MAKES DETECTABLE. "I've posted the summary to the team" reads identically
/// whether or not anything left the machine. `.102` typed the OPERATION so before/receipt/after
/// could each be refused by name; this types the SEND, and its centre is different because the
/// consequence is different: an infrastructure action is reversible by its paired action, and a
/// message that reached a third party is not reversible at all. There is no before-state to
/// restore. What matters instead is WHERE IT WENT — and whether that is where the human agreed it
/// would go.
///
/// THE THREE TARGET FIELDS ARE NOT REDUNDANT, and collapsing them is how this record would stop
/// being able to catch anything. <see cref="RequestedTarget"/> is the operator's own words, kept so
/// the approval can be audited against what was asked. <see cref="ResolvedTarget"/> is the concrete
/// destination the human approved. <see cref="ExecutedTarget"/> is where the adapter reports it
/// actually went. A record holding only one of them cannot express the failure this class exists
/// for: an approval of one destination and a send to another.
///
/// EVERY FIELD IS STAMPED DETERMINISTICALLY — from the adapter's own resolution and receipt, and
/// from the escalation lane's own decision. Never written by a model, for the standing `.100`
/// reason: an identity a model wrote is an identity it could have invented.
///
/// A NOT-SENT RECORD IS A COMPLETE RECORD. <see cref="Outcome"/> and <see cref="RefusedBecause"/>
/// exist so that "nothing left, and here is why" is a thing this type can SAY rather than a gap a
/// reader has to infer from empty fields. That distinction is what lets the answer be rendered from
/// the record in both directions — which is the only defence against prose that reports a send the
/// colony refused.
/// </summary>
public sealed record ExternalAction(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("action_type")] string ActionType,
    [property: JsonPropertyName("requested_target")] string RequestedTarget,
    [property: JsonPropertyName("resolved_target")] string ResolvedTarget,
    [property: JsonPropertyName("executed_target")] string ExecutedTarget,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("request_summary")] string RequestSummary,
    [property: JsonPropertyName("receipt")] string Receipt,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("refused_because")] string RefusedBecause,
    [property: JsonPropertyName("approved_by")] string ApprovedBy)
{
    /// <summary>The two things that can have happened. Spelled once, because the gate refuses on
    /// one and the rendering branches on it.</summary>
    public static class Outcomes
    {
        public const string Sent = "sent";
        public const string NotSent = "not_sent";
    }

    public bool WasSent => string.Equals(Outcome, Outcomes.Sent, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE OUTCOME LINE THE ANSWER LEADS WITH.
    ///
    /// Rendered from the record rather than composed from the builder's prose, and that is the
    /// whole mechanism. A model whose send was refused several steps upstream still writes about a
    /// send — it has no way to know a tool said no — so the channel that reports what happened must
    /// be the channel that knows. `.99` established this for citations; the cost of being wrong is
    /// higher here.
    ///
    /// Both directions name the destination, because an operator reading "it was not sent" still
    /// needs to know what was not sent WHERE before they can decide what to do about it.
    /// </summary>
    public string Render()
    {
        var where = string.IsNullOrWhiteSpace(ResolvedTarget)
            ? $"'{RequestedTarget}' (which did not resolve to a destination)"
            : ResolvedTarget;

        return WasSent
            ? $"SENT — {Method} to {where}, approved by {ApprovedBy}. Response: {Receipt}"
            : $"NOT SENT — nothing was delivered to {where}. {RefusedBecause}".TrimEnd();
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ExternalAction? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ExternalAction>(json!, Options); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
