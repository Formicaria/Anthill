using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>What an operator asked the colony to grant. Not the charter — the request for one.</summary>
/// <param name="MoundId">The device. A charter names exactly one.</param>
/// <param name="Capabilities">Capability ids, never routine ids — the protocol refuses that mix.</param>
/// <param name="Routines">Routine ids the charter enables. A routine is the unit of delegation.</param>
/// <param name="ActionCeiling">observe | benign | controlled. Never hazardous.</param>
/// <param name="Duration">How long the charter is valid for, in total.</param>
/// <param name="LeaseTtl">How long one acknowledged beat buys. Renewed only by the colony.</param>
/// <param name="Limits">Per-capability or per-routine bounds. Keys must be granted above.</param>
/// <param name="MissionRef">The mission this authority exists to serve, or empty for standing work.</param>
public sealed record CharterRequest(
    string MoundId,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Routines,
    string ActionCeiling,
    TimeSpan Duration,
    TimeSpan LeaseTtl,
    IReadOnlyDictionary<string, CapabilityLimits>? Limits = null,
    string MissionRef = "",
    string SafeState = "all_actuators_off",
    IReadOnlyList<string>? EvidenceRequiredFor = null,
    int EvidenceMinIntervalSeconds = 60);

/// <summary>The outcome of asking. A refusal names every reason, never just the first.</summary>
public sealed record CharterIssue(bool Issued, IReadOnlyList<string> Refusals, Charter? Charter, Envelope? Envelope)
{
    public static CharterIssue Refused(params string[] reasons) => new(false, reasons, null, null);

    public static CharterIssue Refused(IReadOnlyList<string> reasons) => new(false, reasons, null, null);
}

/// <summary>
/// CHARTER ISSUANCE — PROTOCOL.md §4, and the first thing the colony has ever been able to TELL a
/// mound. v0.3.8.114.
///
/// A charter is a complete replacement, never a diff, and absence of one means `observe` only. So
/// this type has no "amend" and no "extend": issuing is the whole operation, and the previous
/// charter stops mattering the moment a new one is accepted. That is the protocol's design and not
/// a simplification of it — a diff-able charter is a charter whose effective content nobody can
/// state without replaying history.
///
/// FOUR REFUSALS THIS COLONY MAKES BEFORE THE WIRE, each of them a rule the mound would enforce
/// anyway. Duplicating them here is deliberate and is not defect class 5: the mound is the
/// AUTHORITY and this is a controller declining to ask for something it knows is wrong, which
/// turns a round trip and an audited device-side refusal into an immediate answer for the operator
/// who typed it. Where the two disagree the mound wins, always.
///
///   1. `hazardous` is never a legal ceiling. Hazardous work is authorized per action and expires
///      on use; a standing grant of it is the one thing SAFETY.md Layer 2 will not have.
///   2. A charter is not accepted while a stop is in force (§4), so it is not issued while one is
///      either — "paperwork must not be able to substitute for" clearing a stop, and issuing into
///      a stopped mound is exactly that attempt made from the other end.
///   3. Nothing may be granted that the device did not report. `MoundRecord.Capabilities` is what
///      the mound said it physically has — a fact, never a grant — and granting beyond it produces
///      a charter the mound refuses whole.
///   4. An unenrolled mound has no identity to bind authority to. There is nothing to sign FOR.
///
/// Then <see cref="CharterValidator"/> — the protocol's own — runs over the finished document
/// before it is signed. Everything above would be caught there too; the point of the four is that
/// they answer in the operator's words rather than in a validator's.
/// </summary>
public sealed class MicromoundCharters(IMoundStore store, MicromoundIdentity identity, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly MicromoundIdentity _identity = identity;
    private readonly IEventBus _events = events;

    /// <summary>
    /// Build, validate, sign and record a charter. The envelope is returned rather than sent: this
    /// colony never dials a mound (PROTOCOL.md §1), so a charter waits in the downlink queue until
    /// the device beats and collects it.
    /// </summary>
    public CharterIssue Issue(CharterRequest request, string issuedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mound = _store.GetMound(request.MoundId);
        if (mound is null) return Refuse(request.MoundId, "no such mound");

        if (string.IsNullOrEmpty(mound.PublicKey))
            return Refuse(request.MoundId,
                "mound is not enrolled, so there is no identity to bind this authority to");

        if (MicromoundStop.AppliesTo(mound, MicromoundRuntime.Options))
            return Refuse(request.MoundId,
                "a stop is in force; clearing it is an explicit act and a charter must not substitute for one");

        if (string.Equals(request.ActionCeiling, "hazardous", StringComparison.Ordinal))
            return Refuse(request.MoundId,
                "'hazardous' is never a legal charter ceiling — hazardous work is authorized per action");

        // What the device reported it physically has. A grant beyond it is a charter the mound
        // refuses whole, so the operator hears about it now rather than after a round trip.
        var present = mound.Capabilities.ToHashSet(StringComparer.Ordinal);
        var ungranted = request.Capabilities
            .Where(c => !CapabilityId.IsRoutine(c))
            .Where(c => !present.Contains(c))
            .ToList();

        if (ungranted.Count > 0)
            return Refuse(request.MoundId,
                "this mound did not report: " + string.Join(", ", ungranted));

        var charter = new Charter
        {
            CharterId = Guid.NewGuid().ToString(),
            MoundId = request.MoundId,
            MissionRef = request.MissionRef,
            IssuedAt = now.ToWire(),
            ExpiresAt = now.Add(request.Duration).ToWire(),
            LeaseTtlSeconds = (int)request.LeaseTtl.TotalSeconds,
            ActionCeiling = request.ActionCeiling,
            Capabilities = [.. request.Capabilities],
            Routines = [.. request.Routines],
            Limits = request.Limits?.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal) ?? [],
            Evidence = new EvidencePolicy
            {
                RequiredFor = [.. request.EvidenceRequiredFor ?? ["act.*", "routine.*"]],
                MinIntervalSeconds = request.EvidenceMinIntervalSeconds,
            },
            SafeState = request.SafeState,
            SyncIntervalSeconds = mound.SyncIntervalSeconds,
        };

        // THE PROTOCOL'S OWN VALIDATOR, over the finished document, before anything is signed.
        // Consuming it rather than re-deriving it is the whole reason the wire contract is a
        // project reference: a controller that validated charters its own way would eventually
        // issue one this colony believed in and no mound would accept.
        var validation = CharterValidator.Validate(charter, request.MoundId, now, present);
        if (!validation.IsValid) return Refuse(request.MoundId, validation.Errors);

        var envelope = _identity.Sign(new Envelope
        {
            MoundId = request.MoundId,
            Kind = EnvelopeKinds.Charter,
            SentAt = now.ToWire(),
            Body = System.Text.Json.JsonSerializer.SerializeToElement(charter, ProtocolJson.Options),
        });

        _store.PutCharter(charter);
        _store.QueueDownlink(request.MoundId, envelope);

        // The colony's record of what it granted. Written when the charter is QUEUED rather than
        // when it is collected, because the authority exists from the moment it was issued — and a
        // mound that never comes back for it must still show in the console as chartered rather
        // than as though nobody had decided anything.
        mound.CharterId = charter.CharterId;
        mound.CharterExpiresAt = charter.ExpiresAt;
        mound.LeaseExpiresAt = now.Add(request.LeaseTtl).ToWire();
        mound.Quiesced = false;
        _store.UpsertMound(mound);

        Publish(MicromoundEvents.CharterIssued,
            $"Charter issued to Micromound '{request.MoundId}' at ceiling '{request.ActionCeiling}'.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = request.MoundId,
                ["charter_id"] = charter.CharterId,
                ["action_ceiling"] = charter.ActionCeiling,
                ["capabilities"] = charter.Capabilities.Count,
                ["routines"] = charter.Routines.Count,
                ["expires_at"] = charter.ExpiresAt,
                ["issued_by"] = issuedBy,
                ["mission_ref"] = charter.MissionRef,
            });

        return new CharterIssue(true, [], charter, envelope);
    }

    /// <summary>
    /// Renew the lease, which is what acknowledging a beat means and the only thing that does.
    /// PROTOCOL.md §5 — nothing on-device can extend a lease, so this is the single writer.
    ///
    /// Returns false when there is nothing to renew: no charter, one that has passed its hard
    /// expiry, or a mound that has QUIESCED. A lease renewed past its charter would be authority the
    /// charter never granted, and the distinction is the point — renewal is not resumption.
    ///
    /// v0.3.8.114 — THE QUIESCED CASE IS THE ONE THE TWO-WAY BEAT MADE REACHABLE, and getting it
    /// wrong once is what pinned down where the rule actually lives. The first version refused to
    /// renew whenever the lease TIMESTAMP had lapsed, reasoning from §5's "fresh authority must be
    /// issued to resume". The device does not work that way: `KernelAuthority.RenewLease` renews
    /// whenever there is a charter and the mound is not stopped and NOT QUIESCED — and quiescing is
    /// a separate act, driven by the device's own clock tick, which sets the flag that closes the
    /// door. A late beat from a mound that has not yet quiesced renews there.
    ///
    /// So a timestamp rule here would have been a SECOND implementation of one rule (defect class
    /// 5) that disagrees with the authority: the colony would hold a mound as lease-expired,
    /// refusing to dispatch, while the device sat perfectly willing to work. The mound is the
    /// authority on its own state, `quiesced` is the report that says it entered `safe_state`, and
    /// this reads that rather than re-deriving it from arithmetic.
    /// </summary>
    public bool RenewLease(MoundRecord mound, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mound);

        if (string.IsNullOrEmpty(mound.CharterId)) return false;

        // "A fresh charter is the only way out of quiesce" — the device's own words, and the only
        // place the colony must not renew.
        if (mound.Quiesced) return false;

        if (!ProtocolTime.TryParse(mound.CharterExpiresAt, out var charterExpiry) || charterExpiry <= now)
            return false;

        var charter = _store.GetCharter(mound.CharterId);
        var ttl = charter?.LeaseTtlSeconds ?? 0;
        if (ttl <= 0) return false;

        // Clamped to the charter's own expiry. A lease that outlived the document granting it is
        // the same failure as renewing past it, arrived at by arithmetic instead of by decision.
        var renewed = now.AddSeconds(ttl);
        if (renewed > charterExpiry) renewed = charterExpiry;

        mound.LeaseExpiresAt = renewed.ToWire();
        _store.UpsertMound(mound);
        return true;
    }

    /// <summary>
    /// Has this mound's lease lapsed, by the colony's own record of what it granted? Never read
    /// from anything the device reports about itself.
    ///
    /// THIS ANSWERS THE DISPATCH QUESTION, NOT THE RENEWAL ONE, and the two are deliberately
    /// different. <see cref="RenewLease"/> refuses only for a QUIESCED mound, mirroring the device
    /// exactly. This is stricter on purpose: issuing work against authority the colony cannot
    /// currently vouch for is the one direction where being wrong moves something physical, so
    /// ambiguity resolves downward and the remedy is a charter somebody issued. A reader finding
    /// the two rules and assuming one is a bug should read this paragraph as the answer.
    /// </summary>
    public static bool LeaseExpired(MoundRecord mound, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mound);

        // No charter is not an expired lease — it is no authority at all, which is a different
        // fact and reads differently to an operator. Absent authority resolves downward either way.
        if (string.IsNullOrEmpty(mound.LeaseExpiresAt)) return true;

        return !ProtocolTime.TryParse(mound.LeaseExpiresAt, out var expiry) || expiry <= now;
    }

    private CharterIssue Refuse(string moundId, params string[] reasons) => Refuse(moundId, (IReadOnlyList<string>)reasons);

    private CharterIssue Refuse(string moundId, IReadOnlyList<string> reasons)
    {
        Publish(MicromoundEvents.CharterRefused,
            $"Charter refused for Micromound '{moundId}': {string.Join("; ", reasons)}",
            new Dictionary<string, object?> { ["mound_id"] = moundId, ["reasons"] = reasons });

        return CharterIssue.Refused(reasons);
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }
}
