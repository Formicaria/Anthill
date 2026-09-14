using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>What retiring a mound did, or why it did not happen.</summary>
/// <param name="Retired">True when the record now carries the retirement.</param>
/// <param name="Refusal">Why not, in operator terms. Empty on success.</param>
/// <param name="Mound">The record as it stands after the call, when there is one.</param>
/// <param name="DiscardedDownlink">Queued envelopes that did not survive the retirement.</param>
public sealed record RetireOutcome(bool Retired, string Refusal, MoundRecord? Mound, int DiscardedDownlink);

/// <summary>
/// RETIREMENT IS A STATE, NEVER A DELETE. P-3 — entitlement model §5.3 and §7.3, key management
/// §5.3, retention §4.4.
///
/// Until this existed the colony's one way to stop trusting a device was <c>RemoveMound</c>: the
/// row and every row keyed to it, evidence included, gone in one call. That is the wrong shape
/// twice over. Commercially, a replaced device kept beating, kept being acknowledged and kept
/// renewing its own lease, because the sync path had no fact to gate on (§7.3's overlap bound
/// depended on a field that did not exist). And for safety, the rows it deleted are the only
/// record of what a machine physically did — "why did the actuator fire" is asked long after the
/// device that fired it is gone, and retention §4.4 puts a twelve-month floor under exactly those
/// rows for exactly that reason.
///
/// So this sets three fields and discards one queue. Everything else a retired mound meets — the
/// refused charter, the refused mission, the unrenewed lease, the marked evidence, the re-mint
/// that is turned down — reads <see cref="MoundRecord.IsRetired"/> and acts on it where it lives.
/// The hard delete still exists, as <c>RemoveMound</c> behind a purge an operator must ask for
/// separately, after retirement, by name.
/// </summary>
public static class MicromoundRetirement
{
    public static RetireOutcome Retire(IMoundStore store, string moundId, string reason, string? replacedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(moundId)) return new RetireOutcome(false, "mound_id is required", null, 0);

        var mound = store.GetMound(moundId);
        if (mound is null) return new RetireOutcome(false, $"no such mound '{moundId}'", null, 0);

        // Idempotent in the only sense that is safe: a second retirement changes nothing and says
        // so, rather than overwriting the reason and date of the first.
        if (mound.IsRetired) return new RetireOutcome(false, "already " + MoundRetirement.Describe(mound), mound, 0);

        if (!MoundRetirement.IsKnown(reason))
            return new RetireOutcome(false,
                $"unknown retirement reason '{reason}'; one of {MoundRetirement.Unlinked}, "
              + $"{MoundRetirement.Replaced}, {MoundRetirement.Revoked}", mound, 0);

        var successor = (replacedBy ?? "").Trim();
        if (string.Equals(reason, MoundRetirement.Replaced, StringComparison.Ordinal))
        {
            // A replacement names its successor, and the successor has to be a different, live
            // mound: "replaced by itself" and "replaced by a device that is also retired" are
            // both ways of writing a record that says nothing.
            if (successor.Length == 0)
                return new RetireOutcome(false, "a replacement names the mound that took over: replaced_by is required", mound, 0);
            if (string.Equals(successor, moundId, StringComparison.Ordinal))
                return new RetireOutcome(false, "a mound cannot be replaced by itself", mound, 0);
            var next = store.GetMound(successor);
            if (next is null)
                return new RetireOutcome(false, $"replaced_by names '{successor}', which is not a mound this colony knows", mound, 0);
            if (next.IsRetired)
                return new RetireOutcome(false, $"replaced_by names '{successor}', which is itself retired", mound, 0);
        }
        else if (successor.Length > 0)
        {
            return new RetireOutcome(false, $"replaced_by only accompanies the '{MoundRetirement.Replaced}' reason", mound, 0);
        }

        // Queued authority does not survive retirement, for the same reason it does not survive a
        // stop: a charter queued before and delivered after would hand out exactly the authority
        // the retirement ended. Nothing new can be queued afterwards — every authoring path refuses
        // a retired mound — so this is the last time the queue holds anything.
        var discarded = store.PendingDownlinkCount(moundId);
        store.DiscardDownlink(moundId);

        mound.RetiredAt = now.ToWire();
        mound.RetirementReason = reason;
        mound.ReplacedBy = successor;
        store.UpsertMound(mound);

        return new RetireOutcome(true, "", mound, discarded);
    }
}
