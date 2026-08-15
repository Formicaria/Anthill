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
    /// M1 has no missions to report, and says so rather than rendering an empty table that looks
    /// like a fleet with nothing to do. The widget exists now so the Integrations tab has a stable
    /// shape to render before M2 fills it.
    /// </summary>
    public static string BuildMissionStatus(IReadOnlyList<MoundRecord> mounds) =>
        JsonSerializer.Serialize(new MissionStatusPayload
        {
            Phase = "M1",
            CommandPath = false,
            Note = "Read-only integration: ANTHILL can see mounds, not direct them. " +
                   "Charters and missions arrive in M2.",
            Mounds = mounds.Count
        }, Options);

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
    /// </summary>
    internal static string StatusOf(MoundRecord mound, MicromoundOptions options, DateTimeOffset now,
        bool globalStop)
    {
        if (globalStop || mound.Stopped) return "stopped";
        if (string.IsNullOrEmpty(mound.PublicKey)) return "unenrolled";
        if (!ProtocolTime.TryParse(mound.LastSeen, out var seen)) return "offline";

        var grace = Math.Max(mound.SyncIntervalSeconds, 1) * Math.Max(options.MoundOfflineAfterMissedBeats, 1);
        return now - seen <= TimeSpan.FromSeconds(grace) ? "online" : "offline";
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
        [JsonPropertyName("phase")] public string Phase { get; set; } = "";
        [JsonPropertyName("command_path")] public bool CommandPath { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("mounds")] public int Mounds { get; set; }
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
