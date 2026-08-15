using System.Text.Json.Serialization;

namespace Anthill.Modules.Micromound;

/// <summary>Permissions this module introduces, tiered exactly like the homelab's three.</summary>
public static class MicromoundPermissions
{
    /// <summary>See the fleet, missions, and evidence. Everything M1 exposes.</summary>
    public const string Read = "read_micromound";

    /// <summary>Create mounds, mint enrollment tokens, retire devices.</summary>
    public const string Manage = "manage_micromound";

    /// <summary>
    /// Issue charters, stop, resume. Not used in M1 — the command path does not exist yet — but
    /// declared here so the tiering is settled before anything can be tempted to skip it.
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
/// A device the colony knows about. Note what is absent: no private key, ever (SAFETY.md
/// prohibits any endpoint or record that reads one back), and no charter — M1 has no command
/// path, so the colony can see mounds and cannot direct them.
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

/// <summary>What one sync beat told the colony. Read-only telemetry: no command travels back.</summary>
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
    public static SyncOutcome Refused(IReadOnlyList<string> refusals, string anchorDigest, bool stop) =>
        new(false, refusals, -1, anchorDigest, stop);
}
