namespace Anthill.SDK.External;

/// <summary>
/// What a requested destination RESOLVED TO, or why it could not. v0.3.8.103.
///
/// Resolution is a verdict, not a string: "the team's incident webhook" either names a configured,
/// allowlisted destination or it does not, and the difference decides whether a human is ever asked
/// to approve anything. Returning an empty target for the failure case would make "unresolvable"
/// and "resolved to nothing" the same value, and the second is not a thing that exists.
/// </summary>
/// <param name="Target">The concrete destination — after any templating, as the adapter will hit
/// it. Empty when <paramref name="Ok"/> is false.</param>
/// <param name="Method">What will be done to it, in the adapter's own vocabulary (`POST`, `PUT`).
/// Recorded because "sent to X" does not say whether X was appended to or replaced.</param>
public sealed record ExternalTargetResolution(bool Ok, string Target, string Method, string Reason)
{
    public static ExternalTargetResolution Resolved(string target, string method) =>
        new(true, target, method, "");

    /// <summary>Unresolvable, with the reason an operator would need to fix it. The reason travels
    /// into the record and then into the answer, so it is written for a person.</summary>
    public static ExternalTargetResolution Unresolvable(string reason) =>
        new(false, "", "", reason);
}

/// <summary>
/// What the destination said back, and WHERE IT ACTUALLY WENT. v0.3.8.103.
///
/// <see cref="Target"/> is reported by the adapter rather than echoed by the caller, and that is
/// the entire reason this is a record instead of a string. The gate compares it to the destination
/// the operator approved; a comparison against a value the calling code supplied would compare the
/// caller to itself and agree every time — the `.99` fixture defect, which passed for exactly that
/// reason.
/// </summary>
public sealed record ExternalSendReceipt(bool Ok, string Target, string Receipt, string Reason)
{
    public static ExternalSendReceipt Accepted(string target, string receipt) =>
        new(true, target, receipt, "");

    public static ExternalSendReceipt Refused(string reason) => new(false, "", "", reason);
}

/// <summary>
/// THE ONE SEAM THROUGH WHICH ANYTHING LEAVES THE COLONY ON A MISSION'S BEHALF. v0.3.8.103.
///
/// WHY AN INTERFACE RATHER THAN A CALL. `.102` reached infrastructure through
/// `IHomelabActionRunner`, and the shape earned its keep immediately: the composed acceptance
/// mission ran against the module's deterministic runner while production ran against Proxmox and
/// Docker, through the same executor and the same gates. This is that shape for the outside world.
/// An adapter is what makes "did anything actually leave" answerable by something other than the
/// mission's own account of itself.
///
/// TWO OPERATIONS, DELIBERATELY SEPARATE. Resolution happens BEFORE approval is offered and sending
/// happens after, because an operator cannot consent to an alias — asking a human to approve a name
/// the colony has not yet turned into a destination is how a signature gets attached to whatever
/// that name happens to mean later. An adapter that resolved during the send would collapse the two
/// and there would be nothing for the approval to be OF.
///
/// WHAT AN ADAPTER MUST NOT DO: decide whether it is allowed to send. That decision belongs to the
/// escalation lane and the authority ceiling, both outside this interface — an adapter that could
/// authorize itself would be an authority the operator never granted. It resolves, it sends, it
/// reports honestly, and it refuses when its own destination policy says no; it never asks whether
/// the mission may.
/// </summary>
public interface IExternalActionAdapter
{
    /// <summary>The family this adapter serves — `webhook`, `http`. Recorded on the action so an
    /// operator reading the record knows what kind of thing left.</summary>
    string Kind { get; }

    /// <summary>Turn the operator's words into a concrete destination, or say why not. Must not
    /// send anything, and must not have side effects the operator has not approved.</summary>
    ExternalTargetResolution Resolve(string requestedTarget);

    /// <summary>Send, and report where it actually went. Called only under a recorded operator
    /// decision — enforcing that is the caller's job, not this one's.</summary>
    ExternalSendReceipt Send(string resolvedTarget, string method, string body);
}
