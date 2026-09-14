using System.Text.Json.Serialization;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>Permissions this module introduces, tiered exactly like the infrastructure's three.</summary>
public static class MicromoundPermissions
{
    /// <summary>See the fleet, missions, and evidence. Everything M1 exposes.</summary>
    public const string Read = "read_micromound";

    /// <summary>Create mounds, mint enrollment tokens, retire devices.</summary>
    public const string Manage = "manage_micromound";

    /// <summary>
    /// Issue charters, dispatch physical missions, stop, resume. `.60` declared this with nothing
    /// using it "so the tiering is settled before anything can be tempted to skip it"; v0.3.8.114
    /// is when it started governing something.
    ///
    /// Configuration is deliberately NOT here — it is <see cref="Manage"/>. A manifest is the
    /// hardware map an operator authors and it grants nothing; it can only narrow what a charter
    /// may later spend.
    /// </summary>
    public const string Approve = "approve_micromound_actions";
}

/// <summary>Widget payload kinds this integration publishes through integration_state.</summary>
public static class MicromoundWidgetKinds
{
    public const string MoundFleet = "mound_fleet";
    public const string MissionStatus = "mission_status";
    public const string EvidenceFeed = "evidence_feed";

    public static readonly IReadOnlyList<string> All = [MoundFleet, MissionStatus, EvidenceFeed];
}

/// <summary>
/// A device the colony knows about. Note what is absent: no private key, ever — SAFETY.md
/// prohibits any endpoint or record that reads one back, and that absence is deliberate rather
/// than an oversight to fill in later.
///
/// v0.3.8.114 added the authority fields below. The charter id here is the colony's record of what
/// it GRANTED, not of what the mound accepted: the device validates a charter against its own
/// firmware and may refuse, and that refusal arrives as an uplink ack rather than being inferred.
/// </summary>
public sealed class MoundRecord
{
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>edge_queen | deterministic_controller — decides the protocol profile (§8).</summary>
    [JsonPropertyName("tier")] public string Tier { get; set; } = MoundTiers.EdgeQueen;
    /// <summary>Lowercase hex Ed25519 public key, bound at enrollment. Empty until enrolled.</summary>
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("hardware_profile")] public string HardwareProfile { get; set; } = "";
    /// <summary>Capabilities the device reported it physically has. Not a grant — a fact.</summary>
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
    [JsonPropertyName("enrolled_at")] public string EnrolledAt { get; set; } = "";
    [JsonPropertyName("last_seen")] public string LastSeen { get; set; } = "";
    [JsonPropertyName("last_seq")] public long LastSeq { get; set; } = -1;
    /// <summary>Digest of the last accepted envelope — the anchor the next backlog chains to.</summary>
    [JsonPropertyName("last_digest")] public string LastDigest { get; set; } = "";
    [JsonPropertyName("sync_interval_s")] public int SyncIntervalSeconds { get; set; } = 15;
    /// <summary>Per-mound stop, held in the record as MICROMOUND.md requires.</summary>
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }

    /// <summary>
    /// Protocol features the device advertised at enrollment. v0.3.9.4 — UPSTREAM.md §amendments.
    ///
    /// NOT A GRANT, A FACT, exactly like <see cref="Capabilities"/>. A mound advertising
    /// `postconditions` evaluates a step's `expect`; one that does not confirms on presence alone
    /// and reports `succeeded` either way. The colony sent this field nowhere and read it nowhere
    /// until now, which is how a postcondition could be authored, ignored, and reported as met.
    ///
    /// Empty means none — including for every mound enrolled before this landed, which is correct:
    /// an absent advertisement is not an advertisement.
    /// </summary>
    [JsonPropertyName("features")] public List<string> Features { get; set; } = [];

    /// <summary>
    /// The driver setting schemas the device shipped, kept as the device sent them — raw JSON text.
    ///
    /// TEXT, NOT A JsonElement, and the reason is a real bug rather than taste. A JsonElement's
    /// lifetime is its backing JsonDocument's, and ASP.NET disposes the request's document when the
    /// request completes — so a JsonElement parked on a long-lived MoundRecord is a use-after-
    /// dispose sitting in the fleet listing waiting for someone to read it. Caught by
    /// StorePersistenceTests, which refused to round-trip the nullable struct and was right to.
    ///
    /// Unmodelled on purpose. The colony does not interpret these yet — the hardware form
    /// UPSTREAM.md assigns upstream is unbuilt — and a model here would need editing every time a
    /// driver gains a field, which is the drift this whole exercise is about. Keeping the bytes
    /// means the form gets built later against what the device actually sent.
    /// </summary>
    [JsonPropertyName("driver_schemas_json")] public string DriverSchemasJson { get; set; } = "";

    // ---- Authority. v0.3.8.114 — the fields M1 had no command path to fill. -------------------

    /// <summary>The charter currently in force, or empty for none — which means observe only.</summary>
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";

    /// <summary>
    /// The charter's hard expiry. Distinct from the lease: a lease renews on every acknowledged
    /// beat, this does not renew at all, and when it passes the mound needs a NEW charter rather
    /// than another beat.
    /// </summary>
    [JsonPropertyName("charter_expires_at")] public string CharterExpiresAt { get; set; } = "";

    /// <summary>
    /// Absolute lease expiry, as the colony last extended it. PROTOCOL.md §5: acknowledging a sync
    /// beat is the ONLY renewal path, and nothing on the device can extend it — so this is the
    /// colony's own record of what it granted, not a report of what the mound believes.
    /// </summary>
    [JsonPropertyName("lease_expires_at")] public string LeaseExpiresAt { get; set; } = "";

    /// <summary>
    /// Reported by the mound after a lease lapse (PROTOCOL.md §5). Reconnection resumes nothing:
    /// a quiesced mound waits for fresh authority, and renewal is not resumption.
    /// </summary>
    [JsonPropertyName("quiesced")] public bool Quiesced { get; set; }

    /// <summary>
    /// What this mound will accept, and from whom — §17. Defaults to the enum's zero value,
    /// `ManualOnly`, so a record written before this field existed reads as the most conservative
    /// state rather than the most convenient one.
    /// </summary>
    [JsonPropertyName("autonomy_policy")] public AutonomyPolicy AutonomyPolicy { get; set; }

    /// <summary>The manifest this colony last AUTHORED for the mound. Not proof it accepted one.</summary>
    [JsonPropertyName("manifest_id")] public string ManifestId { get; set; } = "";

    /// <summary>
    /// When that manifest was issued. Named "revision" rather than "version" because it orders
    /// configurations without claiming any of them is running — the mound validates against its own
    /// drivers and may refuse, so what is in force is a fact only the sync path can report.
    /// </summary>
    [JsonPropertyName("configuration_revision")] public string ConfigurationRevision { get; set; } = "";

    // ---- Retirement. P-3 — a state, never a delete. -------------------------------------------
    //
    // Until these fields existed the colony had exactly one way to stop trusting a device: delete
    // it, and everything keyed to it, evidence included. A replaced device kept beating, kept being
    // acknowledged and kept renewing its own lease, because the sync path had no fact to gate on.
    // Now it has one. A retired mound is refused nothing it REPORTS — its evidence is the only
    // record of what a machine physically did, and refusing it loses safety information at the
    // moment it matters most — and granted nothing it ASKS FOR: no charter, no mission, no
    // configuration, no lease renewal, no re-mint of its id. Stop still reaches it, all three ways.

    /// <summary>
    /// When this mound was retired, as wire time, or empty while it is a live member of the fleet.
    /// The retirement fact. Everything else about a retired mound derives from this being set.
    /// </summary>
    [JsonPropertyName("retired_at")] public string RetiredAt { get; set; } = "";

    /// <summary>Why — one of <see cref="MoundRetirement"/>'s reasons. Empty while live.</summary>
    [JsonPropertyName("retirement_reason")] public string RetirementReason { get; set; } = "";

    /// <summary>
    /// The mound that took over this one's role, when the reason is a replacement. Empty otherwise.
    /// The successor is a NEW mound id with its own enrolment: a retired id is final, so that the
    /// evidence under it can never be confused with a different device's.
    /// </summary>
    [JsonPropertyName("replaced_by")] public string ReplacedBy { get; set; } = "";

    /// <summary>Derived, read-only, and on the wire as `retired` so a listing needs no date parsing.</summary>
    [JsonPropertyName("retired")] public bool IsRetired => !string.IsNullOrEmpty(RetiredAt);
}

/// <summary>The reasons a mound leaves the fleet, and the one rule about leaving it.</summary>
public static class MoundRetirement
{
    /// <summary>An operator removed it. The device is not told; its next beat is answered as retired.</summary>
    public const string Unlinked = "unlinked";
    /// <summary>Another mound took its role (entitlement model §7.3). `replaced_by` names the successor.</summary>
    public const string Replaced = "replaced";
    /// <summary>Its key is no longer trusted (key management §5.3). A revocation is a state, not a delete.</summary>
    public const string Revoked = "revoked";

    public static bool IsKnown(string reason) => reason is Unlinked or Replaced or Revoked;

    /// <summary>
    /// The one line every refusal a retired mound meets says, so an operator reading any of them
    /// learns the same thing: what happened, and that the evidence is still there.
    /// </summary>
    public static string Describe(MoundRecord mound) =>
        $"mound '{mound.MoundId}' was retired ({mound.RetirementReason}) at {mound.RetiredAt}"
        + (string.IsNullOrEmpty(mound.ReplacedBy) ? "" : $", replaced by '{mound.ReplacedBy}'")
        + "; a retired mound receives no new authority, and what it still reports is kept";
}

public static class MoundTiers
{
    public const string EdgeQueen = "edge_queen";
    public const string DeterministicController = "deterministic_controller";

    public static bool IsKnown(string tier) =>
        tier is EdgeQueen or DeterministicController;
}

/// <summary>
/// A one-time enrollment secret, minted by an operator. The token itself is stored write-only
/// (hashed here, and encrypted at rest by the field cipher when one is configured) — PROTOCOL.md
/// §3 burns it on use, and there is no self-service re-key.
/// </summary>
public sealed class EnrollmentToken
{
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("token_hash")] public string TokenHash { get; set; } = "";
    [JsonPropertyName("issued_at")] public string IssuedAt { get; set; } = "";
    [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
    [JsonPropertyName("burned_at")] public string BurnedAt { get; set; } = "";
    [JsonPropertyName("issued_by")] public string IssuedBy { get; set; } = "";

    public bool IsBurned => !string.IsNullOrEmpty(BurnedAt);
}

/// <summary>What one sync beat told the colony, and what the colony did about it.</summary>
public sealed class MoundBeat
{
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("received_at")] public string ReceivedAt { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "unknown";
    [JsonPropertyName("envelopes")] public int EnvelopeCount { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("refusals")] public List<string> Refusals { get; set; } = [];
}

/// <summary>
/// The outcome of accepting (or refusing) one uplink batch. Refusal is loud and itemised: the
/// colony's audit trail is worth nothing if "we dropped it" is all it records.
/// </summary>
public sealed record SyncOutcome(
    bool Accepted,
    IReadOnlyList<string> Refusals,
    long AcceptedThroughSeq,
    string AnchorDigest,
    bool StopInEffect)
{
    /// <summary>
    /// The signed envelopes that travel back in the HTTP response, in the order they should be
    /// handled. v0.3.8.114 — M1 had none, and named a KIND instead ("a stop order, or nothing"),
    /// which was honest about a colony that could not sign anything and is not what a controller
    /// does.
    ///
    /// EMPTY ON A REFUSAL, and that is the load-bearing part: an ack for a batch nobody accepted
    /// would tell the device to discard records the colony does not hold.
    /// </summary>
    public IReadOnlyList<Envelope> Downlink { get; init; } = [];

    /// <summary>
    /// The mound reported that its lease lapsed and it entered `safe_state` (PROTOCOL.md §5). Not a
    /// kind of offline — it is beating normally and holding no authority, so the remedy is a
    /// charter rather than a network cable.
    /// </summary>
    public bool Quiesced { get; init; }

    /// <summary>
    /// This batch had already been acknowledged and was answered with the same ack, processing
    /// nothing. Normal, not an error: the ack rides the sync RESPONSE, so a lost response means the
    /// device re-sends exactly this.
    /// </summary>
    public bool Duplicate { get; init; }

    /// <summary>
    /// P-3. The beat came from a retired identity: verified, its evidence kept and marked, its
    /// lease NOT renewed and its queue not drained. The device is not told in so many words — the
    /// ack's detail says "retired identity", and its lease lapsing is what tells it to stand down.
    /// </summary>
    public bool Retired { get; init; }

    public static SyncOutcome Refused(IReadOnlyList<string> refusals, string anchorDigest, bool stop) =>
        new(false, refusals, -1, anchorDigest, stop);
}
