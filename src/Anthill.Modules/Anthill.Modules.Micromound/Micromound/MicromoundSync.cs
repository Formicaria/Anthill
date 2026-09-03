using System.Text.Json;
using Micromound.Crypto;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// THE MODULE'S SHORT NAMES FOR THE COLONY'S EVENT VOCABULARY.
///
/// v0.3.8.114 — every value here USED to be a literal declared only in this class, which meant the
/// SDK's `EventTypes` — the vocabulary a subscriber filters against — named none of them, and the
/// vocabulary sweep could not see them either: it reads `EventType = "..."` and this module
/// publishes `EventType = eventType` through one helper. Twenty names, emitted by a live module,
/// that nothing downstream could name.
///
/// So the strings moved to `EventTypes` and these became aliases. One string per event, the module
/// keeps the short spelling its call sites read better with, and the sweep now sees all of them.
/// </summary>
public static class MicromoundEvents
{
    public const string EnrollmentTokenMinted = EventTypes.MicromoundEnrollmentTokenMinted;
    public const string MoundEnrolled = EventTypes.MicromoundMoundEnrolled;
    public const string EnrollmentRefused = EventTypes.MicromoundEnrollmentRefused;
    public const string SyncAccepted = EventTypes.MicromoundSyncAccepted;
    public const string SyncRefused = EventTypes.MicromoundSyncRefused;
    public const string ChainBroken = EventTypes.MicromoundChainBroken;
    public const string StopInEffect = EventTypes.MicromoundStopInEffect;

    /// <summary>A stop lifted. Never automatic — SAFETY.md makes resume an explicit act.</summary>
    public const string StopCleared = EventTypes.MicromoundStopCleared;

    // v0.3.8.114 — the command path. `.60` declared the Approve permission with nothing using it
    // "so the tiering is settled before anything can be tempted to skip it"; these are the events
    // that permission now governs.
    public const string CharterIssued = EventTypes.MicromoundCharterIssued;
    public const string CharterRefused = EventTypes.MicromoundCharterRefused;
    public const string ConfigurationIssued = EventTypes.MicromoundConfigurationIssued;
    public const string ConfigurationRefused = EventTypes.MicromoundConfigurationRefused;
    public const string MissionDispatched = EventTypes.MicromoundMissionDispatched;
    public const string MissionRefused = EventTypes.MicromoundMissionRefused;
    public const string MissionApprovalRequired = EventTypes.MicromoundMissionApprovalRequired;
    public const string EvidenceIngested = EventTypes.MicromoundEvidenceIngested;
    public const string ActionDegraded = EventTypes.MicromoundActionDegraded;

    /// <summary>A mound reported what became of a mission — its claim, beside the colony's proof.</summary>
    public const string MissionReported = EventTypes.MicromoundMissionReported;

    /// <summary>
    /// A mound refused something the colony sent it — an invalid charter, an inapplicable manifest.
    /// It arrives as an uplink `ack` carrying <c>AckStatuses.Refused</c>, and it is published
    /// because a downlink that was signed, delivered and then declined is otherwise invisible: the
    /// colony's own record says "issued", and only this says the device disagreed.
    /// </summary>
    public const string DownlinkRefused = EventTypes.MicromoundDownlinkRefused;

    /// <summary>
    /// A mound's lease lapsed and it entered `safe_state` — PROTOCOL.md §5. Published because the
    /// remedy is authority rather than a reconnection, and an operator reading "offline" would go
    /// looking at the network.
    /// </summary>
    public const string MoundQuiesced = EventTypes.MicromoundMoundQuiesced;

    /// <summary>
    /// An operator removed a device. Everything keyed to it went with it, and the device is not
    /// told: its next beat is refused as an unknown mound, which is the correct answer.
    /// </summary>
    public const string MoundUnlinked = EventTypes.MicromoundMoundUnlinked;
}

/// <summary>
/// THE SYNC BEAT — PROTOCOL.md §1, §2, §5, §6 and §7 — from the colony's side, and as of
/// v0.3.8.114 it is TWO-WAY.
///
/// WHAT M1 LEFT UNDONE, and why it was not cosmetic. `.60` verified and recorded an uplink and
/// answered with nothing signed, naming a stop KIND in a JSON field instead. Three protocol
/// obligations went unmet by that, and each of them breaks a fleet on its own:
///
///   1. **THE ACK.** §6's retention rule is written in terms of exactly one message: "until an ack
///      covers a sequence number, the uplink queue must retain the envelope and the evidence store
///      must retain the proof." A device that never receives one never releases anything, and its
///      queue and evidence store grow until they spill.
///   2. **THE LEASE.** §5 — "each acknowledged `mound_sync` renews the lease. That is the only
///      renewal path; nothing on-device can extend a lease." The device renews when it sees an ack
///      covering its beat's sequence number, so a colony that sends no ack does not merely fail to
///      renew: every mound it holds runs its lease down and enters `safe_state` on schedule, while
///      beating perfectly and looking healthy in the console.
///   3. **THE DOWNLINK.** §1 — "the response carries any pending downlink." A charter this colony
///      signs and queues is not delivered by being queued.
///
/// So the beat now: verifies, ingests what arrived, renews the lease, drains the queue, and answers
/// with a signed ack. Everything the command path builds waits on this method.
///
/// VERIFICATION IS ALL-OR-NOTHING PER BATCH. A backlog with one bad envelope is not partially
/// believed and trimmed to the good prefix: a chain that has been tampered with tells you nothing
/// trustworthy about the envelopes before the break either, and silently accepting a prefix is
/// exactly how a gap becomes invisible.
///
/// A RE-DELIVERY IS NOT A REPLAY ATTACK, and treating it as one deadlocks the fleet. The ack rides
/// the sync RESPONSE, so a response lost in transit means the device re-sends the identical batch —
/// the ordinary case, not an exotic one. Refusing it would leave the mound re-sending forever
/// against a colony refusing forever. So an already-acknowledged prefix is answered with the SAME
/// ack and processed a second time by nothing, which is the property that actually matters.
/// </summary>
public sealed class MicromoundSync(
    IMoundStore store,
    IEventBus events,
    MicromoundIdentity identity,
    MicromoundCharters charters,
    MicromoundEvidence evidence)
{
    private readonly IMoundStore _store = store;
    private readonly IEventBus _events = events;
    private readonly MicromoundIdentity _identity = identity;
    private readonly MicromoundCharters _charters = charters;
    private readonly MicromoundEvidence _evidence = evidence;

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

        // SIGNATURE FIRST, ALWAYS — before the duplicate check below, and before anything is read
        // out of a body. "This is a re-delivery" is a claim about sequence numbers, and an
        // unverified envelope's sequence number is whatever an attacker chose to write there.
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

        if (refusals.Count > 0)
            return Refuse(moundId, refusals, mound.LastDigest, stop);

        // WHAT THIS COLONY HAS ALREADY ACKNOWLEDGED IS DROPPED, NOT REFUSED — and note that this is
        // trimming a prefix the colony ALREADY BELIEVED and processed, which is a different act
        // from trimming a batch to its good prefix. Nothing is skipped; the remainder still has to
        // chain from the anchor those very envelopes produced.
        var fresh = envelopes.Where(e => e.Seq > mound.LastSeq).ToList();

        if (fresh.Count < envelopes.Count && fresh.Count > 0)
            Publish(MicromoundEvents.SyncAccepted,
                $"Micromound '{moundId}' re-sent {envelopes.Count - fresh.Count} already-acknowledged "
              + "envelope(s); they were dropped and the rest processed.",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["redelivered"] = envelopes.Count - fresh.Count,
                    ["fresh"] = fresh.Count,
                });

        if (fresh.Count == 0)
        {
            // NOTHING NEW TO BELIEVE — either a pure re-delivery, or an empty batch. The same ack
            // goes back and nothing else happens: no beat recorded (it would pollute the evidence
            // feed with a beat that carried no news), no body read twice, and the queue is NOT
            // drained, because whatever was in it left with the response this one is repeating.
            //
            // `LastSeen` and the lease DO move. The device is demonstrably there, and §5 renews on
            // the acknowledged beat — which this is, since the ack below covers the same sequence
            // the previous one did.
            mound.LastSeen = now.ToWire();
            _store.UpsertMound(mound);
            if (!stop) _charters.RenewLease(mound, now);

            var repeated = envelopes.Count > 0;

            return new SyncOutcome(true, [], mound.LastSeq, mound.LastDigest, stop)
            {
                Downlink =
                [
                    SignAck(moundId, repeated ? envelopes[^1].Id : "", mound.LastSeq, [],
                        repeated ? "duplicate" : "nothing new", now),
                ],
                Quiesced = mound.Quiesced,
                Duplicate = repeated,
            };
        }

        // The chain must continue from the last envelope this colony actually acknowledged.
        var chain = EnvelopeValidator.ValidateChain(fresh, mound.LastDigest);
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

        if (fresh.Count > 0 && fresh[0].Seq != mound.LastSeq + 1)
            refusals.Add(
                $"seq does not resume: expected {mound.LastSeq + 1}, got {fresh[0].Seq}");

        if (refusals.Count > 0)
            return Refuse(moundId, refusals, mound.LastDigest, stop);

        // Accepted. Only now is a body read: a batch is authentic before it is meaningful.
        //
        // `reported` stays null when the batch carried no `mound_sync`, which is an ordinary batch
        // rather than a broken one — a mound draining a backlog sends action records and evidence
        // bundles as their own envelopes. The distinction matters below: a batch that said nothing
        // about the device's state must not be read as the device saying it is no longer quiesced.
        string? reported = null;
        var last = mound.LastDigest;

        var records = new List<ActionRecord>();
        var items = new List<EvidenceItem>();
        var reports = new List<MissionReport>();
        var deviceAcks = new List<AckBody>();

        foreach (var envelope in fresh)
        {
            switch (envelope.Kind)
            {
                case EnvelopeKinds.MoundSync:
                    reported = ReadState(envelope) ?? reported;
                    break;
                case EnvelopeKinds.ActionRecord:
                    if (Body<ActionRecord>(envelope) is { } record) records.Add(record);
                    break;
                case EnvelopeKinds.EvidenceBundle:
                    if (Body<EvidenceBundle>(envelope) is { } bundle) items.AddRange(bundle.Items);
                    break;
                case EnvelopeKinds.MissionReport:
                    if (Body<MissionReport>(envelope) is { } report) reports.Add(report);
                    break;
                case EnvelopeKinds.Ack:
                    if (Body<AckBody>(envelope) is { } ack) deviceAcks.Add(ack);
                    break;
            }

            last = envelope.Digest();
        }

        var state = reported ?? "unknown";

        mound.LastSeq = fresh[^1].Seq;
        mound.LastDigest = last;
        mound.LastSeen = now.ToWire();

        // §5 — quiesced is what a mound calls itself after its lease lapsed and it entered
        // `safe_state`. It is a report, so it is believed as one, and only when it was actually
        // made: a backlog batch of action records says nothing about the device's current state,
        // and reading its silence as "no longer quiesced" would clear the flag by accident.
        var wasQuiesced = mound.Quiesced;
        if (reported is not null)
            mound.Quiesced = string.Equals(reported, "quiesced", StringComparison.Ordinal);

        _store.UpsertMound(mound);

        // THE LEASE, RENEWED ON THE ACKNOWLEDGED BEAT AND NOWHERE ELSE (§5). Not while a stop is in
        // force: a stop halts mound-directed action, and handing back fresh authority in the same
        // response that carries the stop order would be the colony arguing with itself.
        if (!stop) _charters.RenewLease(mound, now);

        var ingest = _evidence.Ingest(moundId, records, items, now);

        foreach (var report in reports) RecordReport(moundId, report);
        foreach (var ack in deviceAcks) NoteDeviceAck(moundId, ack);

        _store.RecordBeat(new MoundBeat
        {
            MoundId = moundId,
            ReceivedAt = now.ToWire(),
            Seq = mound.LastSeq,
            State = stop ? "stopped" : state,
            EnvelopeCount = fresh.Count,
            Accepted = true
        });

        Publish(MicromoundEvents.SyncAccepted,
            $"Micromound '{moundId}' synced {fresh.Count} envelope(s) through seq {mound.LastSeq}.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["envelopes"] = fresh.Count,
                ["through_seq"] = mound.LastSeq,
                ["state"] = state,
                ["stop_in_effect"] = stop,
                ["action_records"] = ingest.Actions.Count,
                ["evidence_items"] = ingest.EvidenceItems,
            });

        if (mound.Quiesced && !wasQuiesced)
            Publish(MicromoundEvents.MoundQuiesced,
                $"Micromound '{moundId}' reports quiesced: its lease lapsed and it is in its safe state. "
              + "It needs fresh authority, not a reconnection.",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["charter_id"] = mound.CharterId,
                    ["lease_expires_at"] = mound.LeaseExpiresAt,
                });

        var downlink = new List<Envelope>();

        if (stop)
        {
            Publish(MicromoundEvents.StopInEffect,
                $"Stop is in effect for Micromound '{moundId}'; the sync response carries a stop order.",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["global"] = MicromoundStop.IsEngaged(options),
                    ["per_mound"] = mound.Stopped,
                    ["discarded_downlink"] = _store.PendingDownlinkCount(moundId),
                });

            // §7 — a stop precedes everything queued, and what was queued does not survive it.
            // "Clearing a stop restores nothing": a charter issued before the stop and handed over
            // after the resume would reinstate exactly the authority the stop ended.
            _store.DiscardDownlink(moundId);
            downlink.Add(SignStop(mound, options, now));
        }
        else
        {
            // Drained on ACKNOWLEDGEMENT, and therefore here — after the batch was believed and
            // recorded, in the same response as the ack that says so.
            downlink.AddRange(_store.DrainDownlink(moundId));
        }

        downlink.Add(SignAck(moundId, fresh[^1].Id, mound.LastSeq, ingest.StoredEvidenceIds, "", now));

        return new SyncOutcome(true, [], mound.LastSeq, mound.LastDigest, stop)
        {
            Downlink = downlink,
            Quiesced = mound.Quiesced,
        };
    }

    /// <summary>
    /// The kinds actually travelling back, read off the envelopes rather than re-derived.
    ///
    /// It used to answer from the stop flag alone — correct while a stop was the entire downlink
    /// vocabulary, and a second implementation of "what is in this response" the moment it was not.
    /// </summary>
    public static IReadOnlyList<string> DownlinkKindsFor(SyncOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return [.. outcome.Downlink.Select(e => e.Kind)];
    }

    /// <summary>
    /// The ack — PROTOCOL.md §2 and §6. This is the message that lets a mound let go of records,
    /// so what it names is a promise the colony is holding them.
    ///
    /// `evidenceIds` therefore comes from what the ingest actually STORED, never from what the
    /// batch contained: an item refused for having no id is one the device must keep.
    /// </summary>
    private Envelope SignAck(string moundId, string refersTo, long throughSeq,
        IReadOnlyList<string> evidenceIds, string detail, DateTimeOffset now) =>
        SignDownlink(moundId, EnvelopeKinds.Ack, new AckBody
        {
            Status = AckStatuses.Ok,
            RefersTo = refersTo,
            ThroughSeq = throughSeq,
            EvidenceIds = [.. evidenceIds],
            Detail = detail,
        }, now);

    private Envelope SignStop(MoundRecord mound, MicromoundOptions options, DateTimeOffset now) =>
        SignDownlink(mound.MoundId, EnvelopeKinds.Stop, new
        {
            reason = MicromoundStop.IsEngaged(options)
                ? "colony-wide stop: " + MicromoundStop.PathFor(options)
                : "operator stop for this mound",
        }, now);

    /// <summary>
    /// Every downlink envelope is minted here, and `prev_digest` stays empty on purpose: only the
    /// UPLINK stream is chained (PROTOCOL.md §2), and downlink is authenticated by signature alone.
    /// `seq` stays zero for the same reason — the device dedupes downlink by envelope id and never
    /// reads it, so a controller-side counter would be state nobody consults.
    /// </summary>
    private Envelope SignDownlink<T>(string moundId, string kind, T body, DateTimeOffset now) =>
        _identity.Sign(new Envelope
        {
            MoundId = moundId,
            Kind = kind,
            SentAt = now.ToWire(),
            Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
            PrevDigest = "",
        });

    private void RecordReport(string moundId, MissionReport report)
    {
        _store.PutMissionReport(moundId, report);

        var summary = _evidence.SummarizeMission(moundId, report.MissionId);

        Publish(MicromoundEvents.MissionReported,
            $"Micromound '{moundId}' reports mission '{report.MissionId}' as '{report.State}'.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["mission_id"] = report.MissionId,
                ["charter_id"] = report.CharterId,
                // The device's word and what the colony can prove, side by side and never merged.
                ["device_state"] = report.State,
                ["colony_verified"] = summary.AllVerified,
                ["actions"] = summary.Actions,
                ["verified_actions"] = summary.Verified,
                ["detail"] = report.Detail,
            });
    }

    private void NoteDeviceAck(string moundId, AckBody ack)
    {
        if (string.Equals(ack.Status, AckStatuses.Ok, StringComparison.Ordinal)) return;

        Publish(MicromoundEvents.DownlinkRefused,
            $"Micromound '{moundId}' refused a downlink envelope: {ack.Detail}",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["status"] = ack.Status,
                ["refers_to"] = ack.RefersTo,
                ["detail"] = ack.Detail,
            });
    }

    private static T? Body<T>(Envelope envelope) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(envelope.Body.GetRawText(), ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

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
