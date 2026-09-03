using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// One physical action as the colony holds it: what was asked, what ran, what proved it, and
/// whether the colony's own re-run of the evidence gate agrees with the mound's verdict.
/// </summary>
/// <param name="Record">The action record exactly as the mound reported it. Never rewritten.</param>
/// <param name="ColonyOutcome">
/// What the evidence gate says here, re-run over the evidence that actually arrived. Equal to
/// <c>Record.Outcome</c> in the ordinary case; lower when the proof did not survive the trip.
/// </param>
/// <param name="Reason">Why the colony's verdict differs, or empty when it does not.</param>
public sealed record IngestedAction(ActionRecord Record, string ColonyOutcome, string Reason)
{
    /// <summary>True when the colony had to lower the mound's own verdict.</summary>
    public bool Degraded => !string.Equals(Record.Outcome, ColonyOutcome, StringComparison.Ordinal);

    /// <summary>Did physical work provably happen? The only question a mission may branch on.</summary>
    public bool Verified => string.Equals(ColonyOutcome, ActionOutcomes.Succeeded, StringComparison.Ordinal)
                         || string.Equals(ColonyOutcome, ActionOutcomes.Clamped, StringComparison.Ordinal);
}

/// <summary>What one uplink batch of evidence and action records did to the colony's picture.</summary>
/// <param name="Actions">Each action record with the colony's verdict beside the device's.</param>
/// <param name="EvidenceItems">How many items arrived, including any that were refused.</param>
/// <param name="StoredEvidenceIds">
/// The ids the colony actually holds now — what an <c>ack</c> may name, and nothing more.
///
/// PROTOCOL.md §6 lets a mound evict acknowledged proof under storage pressure, so naming an id in
/// an ack is telling the device it is safe to lose. Deriving that list here rather than in the sync
/// path is the point: "which items were stored" is a rule with one implementation, and a second one
/// computing it from the batch would eventually name an item this method refused.
/// </param>
/// <param name="Refusals">Everything that could not be stored, and why.</param>
public sealed record EvidenceIngest(
    IReadOnlyList<IngestedAction> Actions,
    int EvidenceItems,
    IReadOnlyList<string> StoredEvidenceIds,
    IReadOnlyList<string> Refusals);

/// <summary>
/// STRUCTURED PHYSICAL EVIDENCE — §21, and PROTOCOL.md §6. v0.3.8.114.
///
/// THE ONE RULE EVERYTHING ELSE SERVES: `unverified` is never turned into success. The brief says
/// it outright, SAFETY.md Layer 3 says `unverified` actions gate missions AS FAILURES, and
/// UPSTREAM.md tells a controller to "treat `unverified` as failed-until-proven when gating
/// anything." So this type has no path that raises an outcome, and
/// <see cref="IngestedAction.Verified"/> is true for exactly two of the six.
///
/// THE COLONY RE-RUNS THE GATE, and that is not distrust of the mound. `EvidenceGate` is a pure
/// function in the shared contract precisely so both sides can reach the same verdict — the mound
/// decides with the evidence it holds, and the colony decides with the evidence that ARRIVED. Those
/// differ whenever a bundle is still queued, was evicted under pressure, or was spilled past the
/// hard ceiling (PROTOCOL.md §6 reports both losses rather than hiding them). A mound that said
/// `succeeded` while its proof is still on the device is not lying; the colony simply cannot claim
/// the same thing yet, and recording that as `succeeded` would be the colony asserting a physical
/// fact on the strength of a message.
///
/// SO THE VERDICT CAN ONLY GO DOWN. `confirms` works the same way on the device — "confirmation can
/// only lower a verdict, never raise one" — and for the same reason: a reading taken afterwards
/// proves the state of the world afterwards, never that a command caused it.
///
/// WHAT IS NOT REWRITTEN. `Record` is stored exactly as the mound sent it, including its own
/// outcome, and the colony's verdict lives beside it. Overwriting the device's report with our own
/// conclusion would destroy the disagreement, and the disagreement is the interesting part: it is
/// how an operator learns that proof is missing rather than that a valve failed.
/// </summary>
public sealed class MicromoundEvidence(IMoundStore store, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly IEventBus _events = events;

    /// <summary>
    /// Ingest one batch: the evidence items that arrived, and the action records that reference
    /// them. Both come from a verified, chain-checked uplink — this decides what they MEAN, not
    /// whether they are authentic.
    /// </summary>
    public EvidenceIngest Ingest(
        string moundId,
        IReadOnlyList<ActionRecord> records,
        IReadOnlyList<EvidenceItem> items,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(items);

        var refusals = new List<string>();
        var stored = new List<string>();

        // Store the evidence first, so an action arriving in the same batch as its proof can see it.
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.EvidenceId))
            {
                refusals.Add("an evidence item arrived with no id and cannot be referenced");
                continue;
            }

            _store.PutEvidence(moundId, item);
            stored.Add(item.EvidenceId);
        }

        // The gate reads by id across everything the colony holds for this mound, not just this
        // batch: PROTOCOL.md §6 drains a backlog oldest-first, so an action and the evidence that
        // proves it routinely arrive in different beats.
        var known = _store.EvidenceFor(moundId).ToDictionary(e => e.EvidenceId, StringComparer.Ordinal);

        var ingested = new List<IngestedAction>();
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.ActionId))
            {
                refusals.Add("an action record arrived with no action_id");
                continue;
            }

            // The evidence policy that governs this action is the one from the charter it cites —
            // not the mound's current charter. An action is judged under the authority it ran
            // under, and a later charter with a laxer policy must not retroactively verify work
            // that was done before it existed.
            var policy = _store.GetCharter(record.CharterId)?.Evidence ?? new EvidencePolicy();

            var outcome = EvidenceGate.Gate(record, policy, known, now, out var reason);

            _store.PutAction(moundId, record, outcome, reason);
            ingested.Add(new IngestedAction(record, outcome, reason));

            if (!string.Equals(record.Outcome, outcome, StringComparison.Ordinal))
                Publish(MicromoundEvents.ActionDegraded,
                    $"Micromound '{moundId}' reported '{record.Outcome}' for {record.Capability}; "
                  + $"the colony can only record '{outcome}'.",
                    new Dictionary<string, object?>
                    {
                        ["mound_id"] = moundId,
                        ["action_id"] = record.ActionId,
                        ["mission_id"] = record.MissionId,
                        ["charter_id"] = record.CharterId,
                        ["capability"] = record.Capability,
                        ["device_outcome"] = record.Outcome,
                        ["colony_outcome"] = outcome,
                        ["reason"] = reason,
                    });
        }

        if (ingested.Count > 0 || items.Count > 0)
            Publish(MicromoundEvents.EvidenceIngested,
                $"Micromound '{moundId}': {ingested.Count} action record(s), {items.Count} evidence item(s).",
                new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["actions"] = ingested.Count,
                    ["evidence_items"] = items.Count,
                    ["verified"] = ingested.Count(a => a.Verified),
                    ["degraded"] = ingested.Count(a => a.Degraded),
                });

        return new EvidenceIngest(ingested, items.Count, stored, refusals);
    }

    /// <summary>
    /// How a dispatched mission actually turned out, as the colony can prove it.
    ///
    /// A mission is verified only when every action it produced is verified. That is deliberately
    /// stricter than "the mound said completed": SAFETY.md Layer 3 gates missions on `unverified`
    /// AS FAILURES, so one unproven actuation is enough to withhold the claim — and withholding it
    /// costs an operator a question, where granting it wrongly costs them a belief about the
    /// physical world.
    /// </summary>
    public MissionEvidenceSummary SummarizeMission(string moundId, string missionId)
    {
        var actions = _store.ActionsForMission(moundId, missionId);

        if (actions.Count == 0) return new MissionEvidenceSummary(missionId, 0, 0, false, "no action records yet");

        var verified = actions.Count(a => a.Verified);
        var unproven = actions.Where(a => !a.Verified).ToList();

        return new MissionEvidenceSummary(
            missionId,
            actions.Count,
            verified,
            AllVerified: unproven.Count == 0,
            Detail: unproven.Count == 0
                ? "every action is proven"
                : string.Join("; ", unproven.Select(a =>
                    $"{a.Record.Capability} is '{a.ColonyOutcome}'"
                  + (string.IsNullOrEmpty(a.Reason) ? "" : $" ({a.Reason})"))));
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }
}

/// <summary>What the colony can say about one mission's physical work.</summary>
public sealed record MissionEvidenceSummary(
    string MissionId, int Actions, int Verified, bool AllVerified, string Detail);
