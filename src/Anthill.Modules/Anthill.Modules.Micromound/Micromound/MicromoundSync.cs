using System.Text.Json;
using Micromound.Crypto;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>Event vocabulary this module contributes to the colony stream.</summary>
public static class MicromoundEvents
{
    public const string EnrollmentTokenMinted = "micromound_enrollment_token_minted";
    public const string MoundEnrolled = "micromound_mound_enrolled";
    public const string EnrollmentRefused = "micromound_enrollment_refused";
    public const string SyncAccepted = "micromound_sync_accepted";
    public const string SyncRefused = "micromound_sync_refused";
    public const string ChainBroken = "micromound_chain_broken";
    public const string StopInEffect = "micromound_stop_in_effect";
}

/// <summary>
/// The sync beat — PROTOCOL.md §1 — from the colony's side, in its M1 shape: **read-only**.
///
/// A mound dials in, hands over a backlog, and the colony verifies and records it. What the
/// colony does not do in M1 is send anything back that directs work: there are no charters, no
/// missions, and the only downlink that exists at all is a stop, which is not a command to act
/// but a command to stop acting. That asymmetry is design rule 1 — observe before act — and it is
/// why this class can exist before the approval pipeline does.
///
/// Verification is all-or-nothing per batch. A backlog with one bad envelope is not partially
/// believed and trimmed to the good prefix: a chain that has been tampered with tells you nothing
/// trustworthy about the envelopes before the break either, and silently accepting a prefix is
/// exactly how a gap becomes invisible.
/// </summary>
public sealed class MicromoundSync(IMoundStore store, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly IEventBus _events = events;

    public SyncOutcome AcceptUplink(string moundId, IReadOnlyList<Envelope> envelopes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        var options = MicromoundRuntime.Options;
        var mound = _store.GetMound(moundId);

        if (mound is null)
            return Refuse(moundId, ["unknown mound; enrollment is the only way a key becomes known"], "", false);

        var stop = MicromoundStop.AppliesTo(mound, options);

        if (string.IsNullOrEmpty(mound.PublicKey))
            return Refuse(moundId, ["mound has no bound key; it has not completed enrollment"], "", stop);

        if (!MicromoundEnrollment.TryDecodeKey(mound.PublicKey, out var publicKey))
            return Refuse(moundId, ["stored public key is unreadable; re-enrollment required"], "", stop);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(moundId, publicKey);
        var verifier = new Ed25519EnvelopeVerifier(directory);

        var reduced = mound.Tier == MoundTiers.DeterministicController;
        var refusals = new List<string>();

        for (var i = 0; i < envelopes.Count; i++)
        {
            var envelope = envelopes[i];

            if (!string.Equals(envelope.MoundId, moundId, StringComparison.Ordinal))
            {
                refusals.Add($"index {i}: envelope claims mound_id '{envelope.MoundId}'");
                continue;
            }

            var result = EnvelopeValidator.Validate(envelope, verifier, moundId, reduced);
            if (!result.IsValid)
                refusals.AddRange(result.Errors.Select(e => $"index {i} (seq {envelope.Seq}): {e}"));
        }

        // The chain must continue from the last envelope this colony actually acknowledged.
        var chain = EnvelopeValidator.ValidateChain(envelopes, mound.LastDigest);
        if (!chain.IsValid)
        {
            refusals.AddRange(chain.Errors);
            Publish(MicromoundEvents.ChainBroken,
                $"Micromound '{moundId}' uplink chain does not continue from the last acknowledged digest.",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["anchor_digest"] = mound.LastDigest,
                    ["errors"] = chain.Errors.Count
                });
        }

        if (envelopes.Count > 0 && envelopes[0].Seq != mound.LastSeq + 1)
            refusals.Add(
                $"seq does not resume: expected {mound.LastSeq + 1}, got {envelopes[0].Seq}");

        if (refusals.Count > 0)
            return Refuse(moundId, refusals, mound.LastDigest, stop);

        // Accepted. Note what is recorded and what is not: telemetry, sequence, and the new chain
        // anchor. No charter is issued, no work is authorized, and nothing here can raise a
        // ceiling — M1 has no command path at all.
        var state = "unknown";
        var last = mound.LastDigest;

        foreach (var envelope in envelopes)
        {
            if (envelope.Kind == EnvelopeKinds.MoundSync)
                state = ReadState(envelope) ?? state;

            last = envelope.Digest();
        }

        if (envelopes.Count > 0)
        {
            mound.LastSeq = envelopes[^1].Seq;
            mound.LastDigest = last;
        }

        mound.LastSeen = now.ToWire();
        _store.UpsertMound(mound);

        _store.RecordBeat(new MoundBeat
        {
            MoundId = moundId,
            ReceivedAt = now.ToWire(),
            Seq = mound.LastSeq,
            State = stop ? "stopped" : state,
            EnvelopeCount = envelopes.Count,
            Accepted = true
        });

        Publish(MicromoundEvents.SyncAccepted,
            $"Micromound '{moundId}' synced {envelopes.Count} envelope(s) through seq {mound.LastSeq}.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["envelopes"] = envelopes.Count,
                ["through_seq"] = mound.LastSeq,
                ["state"] = state,
                ["stop_in_effect"] = stop
            });

        if (stop)
            Publish(MicromoundEvents.StopInEffect,
                $"Stop is in effect for Micromound '{moundId}'; the sync response carries a stop order.",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["global"] = MicromoundStop.IsEngaged(options),
                    ["per_mound"] = mound.Stopped
                });

        return new SyncOutcome(true, [], mound.LastSeq, mound.LastDigest, stop);
    }

    /// <summary>
    /// What travels back down. In M1 this is a stop order or nothing — the entire downlink
    /// vocabulary the colony is allowed to speak until M2.
    /// </summary>
    public IReadOnlyList<string> DownlinkKindsFor(SyncOutcome outcome) =>
        outcome.StopInEffect ? [EnvelopeKinds.Stop] : [];

    private SyncOutcome Refuse(string moundId, IReadOnlyList<string> refusals, string anchor, bool stop)
    {
        _store.RecordBeat(new MoundBeat
        {
            MoundId = moundId,
            ReceivedAt = AnthillTime.NowUtc().ToIso(),
            Seq = -1,
            State = "refused",
            Accepted = false,
            Refusals = [.. refusals]
        });

        Publish(MicromoundEvents.SyncRefused,
            $"Micromound '{moundId}' uplink refused ({refusals.Count} reason(s)).",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["reasons"] = string.Join("; ", refusals.Take(10))
            });

        return SyncOutcome.Refused(refusals, anchor, stop);
    }

    private static string? ReadState(Envelope envelope)
    {
        try
        {
            if (envelope.Body.ValueKind != JsonValueKind.Object) return null;
            return envelope.Body.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                ? state.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }
}
