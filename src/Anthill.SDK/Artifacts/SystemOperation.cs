using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// A SYSTEM OPERATION AS A RECORD — what was, what ran, what is, and who allowed it. v0.3.8.102.
///
/// THE FAILURE THIS TYPE MAKES DETECTABLE. An infrastructure action's prose account — "I restarted
/// the container and it came back healthy" — reads identically whether or not anything was
/// restarted, approved, or probed afterwards. `.100` typed the created deliverable so its bytes
/// could be checked; `.101` typed the diagnosis so its receipts could be resolved; this types the
/// OPERATION so the exit line's three nouns are each a field a gate can refuse by name: a
/// BEFORE-STATE captured before anything changed, a RECEIPT of the execution the pipeline actually
/// performed, and an AFTER-STATE probed once it had. The ROLLBACK NOTE rides with them because the
/// executor already mandates it before execution — reversibility as a precondition, made visible
/// in the record rather than trusted to the pipeline's memory.
///
/// EVERY FIELD IS STAMPED DETERMINISTICALLY from the homelab pipeline's own rows and the
/// escalation lane's own decision — never written by a model, for the standing `.100` reason: an
/// identity a model wrote is an identity it could have invented. <see cref="ApprovedBy"/> carries
/// the escalation decision's identity ("operator:&lt;decision-id&gt;"), which is a DIFFERENT
/// authority than the proposing ant — the distinctness the whole approval design exists to keep.
///
/// WHAT THIS RECORD DOES NOT CLAIM: that the operation was wise, or that the after-state is the
/// state the operator wanted. Semantic judgments, the standing line; what is checkable is that the
/// pieces exist and agree with the pipeline's own lifecycle.
/// </summary>
public sealed record SystemOperation(
    [property: JsonPropertyName("proposal_id")] string ProposalId,
    [property: JsonPropertyName("action_type")] string ActionType,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("rollback_note")] string RollbackNote,
    [property: JsonPropertyName("before_state")] string BeforeState,
    [property: JsonPropertyName("receipt")] string Receipt,
    [property: JsonPropertyName("after_state")] string AfterState,
    [property: JsonPropertyName("approved_by")] string ApprovedBy)
{
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SystemOperation? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SystemOperation>(json!, Options); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
