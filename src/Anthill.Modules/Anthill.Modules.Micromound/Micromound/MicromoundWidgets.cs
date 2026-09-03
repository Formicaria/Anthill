using System.Text.Json;
using System.Text.Json.Serialization;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// Typed widget payloads — the Integrations tab renders Micromound through the existing widget
/// runtime, without special-casing it. Everything here is secret-free by construction: there is
/// no field on any of these types that could carry a key, a token, or a credential id, which is a
/// stronger guarantee than remembering to strip them.
/// </summary>
public static class MicromoundWidgets
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string BuildFleet(IReadOnlyList<MoundRecord> mounds, MicromoundOptions options,
        DateTimeOffset now)
    {
        var globalStop = MicromoundStop.IsEngaged(options);

        var payload = new FleetPayload
        {
            GlobalStop = globalStop,
            Total = mounds.Count,
            Items = [.. mounds.Select(m => new FleetItem
            {
                MoundId = m.MoundId,
                Name = m.Name,
                Tier = m.Tier,
                Status = StatusOf(m, options, now, globalStop),
                LastSeen = m.LastSeen,
                LastSeq = m.LastSeq,
                Capabilities = m.Capabilities.Count,
                Enrolled = !string.IsNullOrEmpty(m.PublicKey)
            })]
        };

        payload.Online = payload.Items.Count(i => i.Status == "online");
        payload.Offline = payload.Items.Count(i => i.Status == "offline");
        payload.Stopped = payload.Items.Count(i => i.Status == "stopped");
        payload.Unenrolled = payload.Items.Count(i => i.Status == "unenrolled");

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// THE COMMAND PATH, AS IT ACTUALLY STANDS. v0.3.8.114.
    ///
    /// This used to answer `phase: M1, command_path: false` with a note saying charters and
    /// missions arrive later. They arrived, and a widget still reporting otherwise is defect class
    /// 3 — a declaration disagreeing with the runtime — in the one surface an operator reads to
    /// find out what the colony can do.
    ///
    /// What it shows per mound is AUTHORITY, not activity: chartered or not, the lease, the
    /// autonomy policy, and what is queued. That is the set of facts that decides whether asking
    /// for physical work would succeed, which is the question somebody opening this widget has.
    /// Each mission carries the colony's evidence-derived verdict, never the device's claim alone.
    /// </summary>
    public static string BuildMissionStatus(IMoundStore store, IReadOnlyList<MoundRecord> mounds,
        MicromoundEvidence evidence, DateTimeOffset now, int perMound = 5)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mounds);
        ArgumentNullException.ThrowIfNull(evidence);

        var items = new List<MissionStatusItem>();

        foreach (var mound in mounds)
            foreach (var mission in store.MissionsForMound(mound.MoundId, perMound))
            {
                var summary = evidence.SummarizeMission(mound.MoundId, mission.MissionId);
                var report = store.GetMissionReport(mound.MoundId, mission.MissionId);

                items.Add(new MissionStatusItem
                {
                    MoundId = mound.MoundId,
                    Name = mound.Name,
                    MissionId = mission.MissionId,
                    CharterId = mission.CharterId,
                    Worker = mission.Worker,
                    ExpiresAt = mission.ExpiresAt,
                    // The device's word and the colony's proof, side by side. A widget that showed
                    // only one of them would be hiding whichever it dropped.
                    DeviceState = report?.State ?? "",
                    ColonyVerified = summary.AllVerified,
                    Actions = summary.Actions,
                    VerifiedActions = summary.Verified,
                    Detail = summary.Detail,
                });
            }

        return JsonSerializer.Serialize(new MissionStatusPayload
        {
            CommandPath = true,
            Note = "Charters, configuration and missions are issued from ANTHILL and collected on "
                 + "the mound's next beat. Every mission's state here is what the colony can PROVE, "
                 + "never what the device claimed.",
            Mounds = mounds.Count,
            Chartered = mounds.Count(m => !string.IsNullOrEmpty(m.CharterId)),
            LeaseHeld = mounds.Count(m => !MicromoundCharters.LeaseExpired(m, now)),
            AwaitingCollection = mounds.Sum(m => store.PendingDownlinkCount(m.MoundId)),
            Items = [.. items.OrderByDescending(i => i.ExpiresAt, StringComparer.Ordinal)],
        }, Options);
    }

    public static string BuildEvidenceFeed(IMoundStore store, IReadOnlyList<MoundRecord> mounds, int perMound)
    {
        var items = new List<EvidenceFeedItem>();

        foreach (var mound in mounds)
        {
            foreach (var beat in store.RecentBeats(mound.MoundId, perMound))
            {
                items.Add(new EvidenceFeedItem
                {
                    MoundId = mound.MoundId,
                    Name = mound.Name,
                    ReceivedAt = beat.ReceivedAt,
                    State = beat.State,
                    Seq = beat.Seq,
                    Envelopes = beat.EnvelopeCount,
                    Accepted = beat.Accepted,
                    // Refusals are shown, not hidden. A feed that only lists what was believed is
                    // not an audit trail.
                    Refusals = [.. beat.Refusals.Take(5)]
                });
            }
        }

        return JsonSerializer.Serialize(new EvidenceFeedPayload
        {
            Items = [.. items.OrderByDescending(i => i.ReceivedAt, StringComparer.Ordinal)]
        }, Options);
    }

    /// <summary>
    /// Offline is a normal state, not an incident (PROTOCOL.md §1) — so a mound that has missed
    /// its beats is reported as offline and nothing else happens.
    ///
    /// THE ONE PLACE THIS IS DECIDED. `MicromoundResolver` reads it rather than re-deriving
    /// "reachable" from `LastSeen` itself; two answers to "is this mound there" would eventually
    /// disagree, and the fleet widget and the resolver disagreeing means a console showing a mound
    /// as online while the colony refuses to route work to it.
    ///
    /// v0.3.8.114 — QUIESCED joins the vocabulary, and it is NOT a kind of offline. A quiesced
    /// mound is beating normally and holds no authority: its lease lapsed, it entered `safe_state`,
    /// and PROTOCOL.md §5 has it waiting for a fresh charter rather than for a fresh connection.
    /// Reporting it as offline would tell an operator to check the network when the answer is to
    /// issue authority.
    /// </summary>
    /// <summary>
    /// PUBLIC SINCE v0.3.8.115, so the console does not have to have an opinion.
    ///
    /// This verdict reads `SyncIntervalSeconds` and the configured `MoundOfflineAfterMissedBeats`
    /// grace. A browser has neither, so a client that wanted to show "online" had exactly two
    /// options: recompute the rule from fields it can see — a second implementation that disagrees
    /// the moment the grace is reconfigured — or say nothing. Colony Live chose to say nothing at
    /// `.115`, correctly and unhelpfully. Widening this is the smaller change: one rule, computed
    /// where its inputs live, carried on the wire.
    /// </summary>
    public static string StatusOf(MoundRecord mound, MicromoundOptions options, DateTimeOffset now,
        bool globalStop)
    {
        if (globalStop || mound.Stopped) return "stopped";
        if (string.IsNullOrEmpty(mound.PublicKey)) return "unenrolled";
        if (!ProtocolTime.TryParse(mound.LastSeen, out var seen)) return "offline";

        var grace = Math.Max(mound.SyncIntervalSeconds, 1) * Math.Max(options.MoundOfflineAfterMissedBeats, 1);
        if (now - seen > TimeSpan.FromSeconds(grace)) return "offline";

        return mound.Quiesced ? "quiesced" : "online";
    }

    public sealed class FleetPayload
    {
        [JsonPropertyName("global_stop")] public bool GlobalStop { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("online")] public int Online { get; set; }
        [JsonPropertyName("offline")] public int Offline { get; set; }
        [JsonPropertyName("stopped")] public int Stopped { get; set; }
        [JsonPropertyName("unenrolled")] public int Unenrolled { get; set; }
        [JsonPropertyName("items")] public List<FleetItem> Items { get; set; } = [];
    }

    public sealed class FleetItem
    {
        [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("tier")] public string Tier { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("last_seen")] public string LastSeen { get; set; } = "";
        [JsonPropertyName("last_seq")] public long LastSeq { get; set; }
        [JsonPropertyName("capabilities")] public int Capabilities { get; set; }
        [JsonPropertyName("enrolled")] public bool Enrolled { get; set; }
    }

    public sealed class MissionStatusPayload
    {
        [JsonPropertyName("command_path")] public bool CommandPath { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("mounds")] public int Mounds { get; set; }
        [JsonPropertyName("chartered")] public int Chartered { get; set; }
        [JsonPropertyName("lease_held")] public int LeaseHeld { get; set; }
        /// <summary>Signed envelopes queued for mounds that have not beaten since.</summary>
        [JsonPropertyName("awaiting_collection")] public int AwaitingCollection { get; set; }
        [JsonPropertyName("items")] public List<MissionStatusItem> Items { get; set; } = [];
    }

    public sealed class MissionStatusItem
    {
        [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
        [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
        [JsonPropertyName("worker")] public string Worker { get; set; } = "";
        [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
        /// <summary>What the mound reported, or empty when it has not reported yet.</summary>
        [JsonPropertyName("device_state")] public string DeviceState { get; set; } = "";
        /// <summary>What the colony can prove — every action verified, or not.</summary>
        [JsonPropertyName("colony_verified")] public bool ColonyVerified { get; set; }
        [JsonPropertyName("actions")] public int Actions { get; set; }
        [JsonPropertyName("verified_actions")] public int VerifiedActions { get; set; }
        [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    }

    public sealed class EvidenceFeedPayload
    {
        [JsonPropertyName("items")] public List<EvidenceFeedItem> Items { get; set; } = [];
    }

    public sealed class EvidenceFeedItem
    {
        [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("received_at")] public string ReceivedAt { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("seq")] public long Seq { get; set; }
        [JsonPropertyName("envelopes")] public int Envelopes { get; set; }
        [JsonPropertyName("accepted")] public bool Accepted { get; set; }
        [JsonPropertyName("refusals")] public List<string> Refusals { get; set; } = [];
    }
}
