using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// Persistence for the mound registry, kept behind an interface so M1's logic is provable without
/// a database — the same move the homelab made with its mock-provider harness, and the reason its
/// 240 tests run network-free.
/// </summary>
public interface IMoundStore
{
    IReadOnlyList<MoundRecord> ListMounds();
    MoundRecord? GetMound(string moundId);
    void UpsertMound(MoundRecord mound);
    bool RemoveMound(string moundId);

    void PutEnrollmentToken(EnrollmentToken token);
    EnrollmentToken? GetEnrollmentToken(string moundId);

    /// <summary>
    /// Every enrolment token the colony holds, burned ones included, for the enrolment path to
    /// match a presented token against. v0.3.8.114. Bounded by fleet size — one row per mound.
    ///
    /// THE DEVICE DOES NOT SAY WHICH MOUND IT IS. `HttpEnrollmentClient` POSTs a token, a public
    /// key, a hardware profile and a tier — and no `mound_id`, because the mound id is a fact the
    /// OPERATOR established when they minted the token and not a claim the device gets to make.
    /// So the token is the lookup key, and this is what makes that lookup possible.
    ///
    /// Burned tokens are included so a re-presented one can be refused as "already used" rather
    /// than as unknown: the operator is standing next to the hardware, and the two call for
    /// different next moves.
    ///
    /// It returns the tokens rather than doing the matching, deliberately: the hash may be
    /// encrypted at rest with a non-deterministic cipher, so a `WHERE token_hash = ?` cannot work,
    /// and the constant-time comparison belongs in the one place that already owns it rather than
    /// once per store.
    /// </summary>
    IReadOnlyList<EnrollmentToken> AllEnrollmentTokens();

    void RecordBeat(MoundBeat beat);
    IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit);

    /// <summary>
    /// The colony's own Ed25519 signing seed, or null when it has never signed anything.
    ///
    /// WRITE-MOSTLY BY DESIGN. `SAFETY.md` prohibits any endpoint or envelope that reads a private
    /// key back, and while that rule is written about a DEVICE key the reasoning does not change
    /// for ours. This pair exists so <see cref="MicromoundIdentity"/> can mint an identity once and
    /// find it again after a restart; nothing above that layer calls either, and no route exposes
    /// them. The seed is encrypted at rest by the field cipher when one is configured, exactly as
    /// the enrollment token hash is.
    /// </summary>
    byte[]? GetControllerSeed();

    void PutControllerSeed(byte[] seed);

    // ---- Authority and downlink. v0.3.8.114 ---------------------------------------------------

    /// <summary>The charter currently on file for a mound, by charter id.</summary>
    Charter? GetCharter(string charterId);

    void PutCharter(Charter charter);

    /// <summary>The manifest this colony authored for a mound, by manifest id.</summary>
    MoundManifest? GetManifest(string manifestId);

    void PutManifest(MoundManifest manifest);

    /// <summary>A dispatched physical mission, by mission id. What the report will be matched to.</summary>
    Mission? GetMission(string missionId);

    void PutMission(Mission mission);

    /// <summary>Missions dispatched to one mound, most recent first. What the console renders.</summary>
    IReadOnlyList<Mission> MissionsForMound(string moundId, int limit);

    /// <summary>
    /// What the mound said became of a mission — PROTOCOL.md §9's `mission_report`. Idempotent by
    /// `(mound_id, mission_id)`: a backlog re-sends until acknowledged, and the report is a final
    /// statement rather than an accumulating log.
    ///
    /// Kept BESIDE the evidence-derived verdict, never instead of it, for the same reason an
    /// action record is: the device's claim and what the colony can prove are different facts, and
    /// the disagreement is the one worth showing an operator.
    /// </summary>
    void PutMissionReport(string moundId, MissionReport report);

    /// <summary>The mound's own report for one mission, or null when none has arrived.</summary>
    MissionReport? GetMissionReport(string moundId, string missionId);

    // ---- Physical evidence. v0.3.8.114 --------------------------------------------------------

    /// <summary>
    /// Store one evidence item. Idempotent by id: PROTOCOL.md §2 deduplicates by sequence and a
    /// backlog re-sends until acknowledged, so the same item legitimately arrives more than once.
    /// </summary>
    void PutEvidence(string moundId, EvidenceItem item);

    /// <summary>Everything this mound has ever proved. The gate reads by id across all of it.</summary>
    IReadOnlyList<EvidenceItem> EvidenceFor(string moundId);

    /// <summary>
    /// Store an action record with the colony's own verdict beside it — never over it. The device's
    /// report is kept exactly as sent, because the DISAGREEMENT is the interesting part.
    /// </summary>
    void PutAction(string moundId, ActionRecord record, string colonyOutcome, string reason);

    /// <summary>Every action one mission produced, with the colony's verdict on each.</summary>
    IReadOnlyList<IngestedAction> ActionsForMission(string moundId, string missionId);

    /// <summary>
    /// Queue a signed envelope for a mound to collect on its next beat.
    ///
    /// THE COLONY NEVER DIALS A MOUND (PROTOCOL.md §1, and UPSTREAM.md's "never require an inbound
    /// path"), so every downlink waits here until the device asks. That is not a limitation being
    /// worked around — it is what lets a mound sit behind NAT on somebody's home network and still
    /// be governed.
    /// </summary>
    void QueueDownlink(string moundId, Envelope envelope);

    /// <summary>
    /// Take everything queued for a mound, in order, removing it. Called when a beat is
    /// ACKNOWLEDGED, never before: an envelope handed to a device that then failed to receive it
    /// is an envelope nobody has, and the mound would wait forever for authority the colony
    /// believes it sent.
    /// </summary>
    IReadOnlyList<Envelope> DrainDownlink(string moundId);

    /// <summary>What is waiting, without taking it — for the console, and for tests.</summary>
    int PendingDownlinkCount(string moundId);

    /// <summary>
    /// Throw the queue away unsent. v0.3.8.114, and it exists for exactly one caller: a stop.
    ///
    /// PROTOCOL.md §7 is unambiguous that "clearing a stop restores nothing — the mound returns to
    /// observe-only and waits for a fresh charter; the authority in force before the stop is not
    /// reinstated." A charter that was queued before the stop and delivered after the resume would
    /// reinstate exactly that authority, by arithmetic rather than by anyone deciding to. So the
    /// queue is discarded at the beat where the stop takes effect, and the way back is a new
    /// charter that somebody issued knowing the stop had happened.
    /// </summary>
    void DiscardDownlink(string moundId);

    /// <summary>Widget payloads, keyed the same way integration_state keys them.</summary>
    void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt);
    (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind);
}

/// <summary>
/// The reference implementation. Deterministic, network-free, and the one every test uses; the
/// SQLite store lands with the Api wiring, against these same semantics.
/// </summary>
public sealed class InMemoryMoundStore : IMoundStore
{
    private readonly Dictionary<string, MoundRecord> _mounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnrollmentToken> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MoundBeat>> _beats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Payload, string UpdatedAt)> _widgets =
        new(StringComparer.Ordinal);
    private byte[]? _controllerSeed;
    private readonly Dictionary<string, Charter> _charters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MoundManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mission> _missions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Mound, string Mission), MissionReport> _reports = [];
    private readonly Dictionary<string, Dictionary<string, EvidenceItem>> _evidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, IngestedAction>> _actions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Envelope>> _downlink = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyList<MoundRecord> ListMounds()
    {
        lock (_gate) return _mounds.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    public MoundRecord? GetMound(string moundId)
    {
        lock (_gate) return _mounds.GetValueOrDefault(moundId);
    }

    public void UpsertMound(MoundRecord mound)
    {
        ArgumentNullException.ThrowIfNull(mound);
        lock (_gate) _mounds[mound.MoundId] = mound;
    }

    public bool RemoveMound(string moundId)
    {
        lock (_gate)
        {
            _tokens.Remove(moundId);
            _beats.Remove(moundId);
            _downlink.Remove(moundId);
            _evidence.Remove(moundId);
            _actions.Remove(moundId);

            // v0.3.8.114 — and the ones keyed by their OWN id rather than by the mound's, which is
            // why they were missed the first time. A charter, manifest, mission or report that
            // outlived the device it was written for is authority and proof addressed to an id an
            // operator has deliberately freed, waiting for whatever claims it next.
            foreach (var key in _charters.Where(c => c.Value.MoundId == moundId).Select(c => c.Key).ToList())
                _charters.Remove(key);
            foreach (var key in _manifests.Where(m => m.Value.MoundId == moundId).Select(m => m.Key).ToList())
                _manifests.Remove(key);
            foreach (var key in _missions.Where(m => m.Value.MoundId == moundId).Select(m => m.Key).ToList())
                _missions.Remove(key);
            foreach (var key in _reports.Keys.Where(k => k.Mound == moundId).ToList())
                _reports.Remove(key);

            return _mounds.Remove(moundId);
        }
    }

    public void PutEnrollmentToken(EnrollmentToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate) _tokens[token.MoundId] = token;
    }

    public EnrollmentToken? GetEnrollmentToken(string moundId)
    {
        lock (_gate) return _tokens.GetValueOrDefault(moundId);
    }

    public IReadOnlyList<EnrollmentToken> AllEnrollmentTokens()
    {
        lock (_gate) return [.. _tokens.Values];
    }

    public void RecordBeat(MoundBeat beat)
    {
        ArgumentNullException.ThrowIfNull(beat);
        lock (_gate)
        {
            if (!_beats.TryGetValue(beat.MoundId, out var list))
            {
                list = [];
                _beats[beat.MoundId] = list;
            }

            list.Add(beat);

            // Ring buffer, sized like a device's own: history is useful, unbounded history is a leak.
            if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
        }
    }

    public IReadOnlyList<MoundBeat> RecentBeats(string moundId, int limit)
    {
        lock (_gate)
        {
            if (!_beats.TryGetValue(moundId, out var list)) return [];
            return list.AsEnumerable().Reverse().Take(Math.Max(limit, 0)).ToList();
        }
    }

    public byte[]? GetControllerSeed()
    {
        // A copy, so a caller cannot zero or mutate the stored seed through the reference it got.
        lock (_gate) return _controllerSeed is null ? null : (byte[])_controllerSeed.Clone();
    }

    public void PutControllerSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_gate) _controllerSeed = (byte[])seed.Clone();
    }

    public Charter? GetCharter(string charterId)
    {
        lock (_gate) return _charters.GetValueOrDefault(charterId);
    }

    public void PutCharter(Charter charter)
    {
        ArgumentNullException.ThrowIfNull(charter);
        lock (_gate) _charters[charter.CharterId] = charter;
    }

    public MoundManifest? GetManifest(string manifestId)
    {
        lock (_gate) return _manifests.GetValueOrDefault(manifestId);
    }

    public void PutManifest(MoundManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_gate) _manifests[manifest.ManifestId] = manifest;
    }

    public Mission? GetMission(string missionId)
    {
        lock (_gate) return _missions.GetValueOrDefault(missionId);
    }

    public void PutMission(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        lock (_gate) _missions[mission.MissionId] = mission;
    }

    public IReadOnlyList<Mission> MissionsForMound(string moundId, int limit)
    {
        lock (_gate)
            return [.. _missions.Values
                .Where(m => string.Equals(m.MoundId, moundId, StringComparison.Ordinal))
                .OrderByDescending(m => m.ExpiresAt, StringComparer.Ordinal)
                .Take(limit)];
    }

    public void PutMissionReport(string moundId, MissionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate) _reports[(moundId, report.MissionId)] = report;
    }

    public MissionReport? GetMissionReport(string moundId, string missionId)
    {
        lock (_gate) return _reports.GetValueOrDefault((moundId, missionId));
    }

    

    public void PutEvidence(string moundId, EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (!_evidence.TryGetValue(moundId, out var byId))
            {
                byId = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);
                _evidence[moundId] = byId;
            }

            byId[item.EvidenceId] = item;
        }
    }

    public IReadOnlyList<EvidenceItem> EvidenceFor(string moundId)
    {
        lock (_gate)
            return _evidence.TryGetValue(moundId, out var byId) ? byId.Values.ToList() : [];
    }

    public void PutAction(string moundId, ActionRecord record, string colonyOutcome, string reason)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (!_actions.TryGetValue(moundId, out var byId))
            {
                byId = new Dictionary<string, IngestedAction>(StringComparer.Ordinal);
                _actions[moundId] = byId;
            }

            byId[record.ActionId] = new IngestedAction(record, colonyOutcome, reason);
        }
    }

    public IReadOnlyList<IngestedAction> ActionsForMission(string moundId, string missionId)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(moundId, out var byId)) return [];
            return byId.Values
                .Where(a => string.Equals(a.Record.MissionId, missionId, StringComparison.Ordinal))
                .ToList();
        }
    }

    public void QueueDownlink(string moundId, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            if (!_downlink.TryGetValue(moundId, out var queue))
            {
                queue = [];
                _downlink[moundId] = queue;
            }

            queue.Add(envelope);
        }
    }

    public IReadOnlyList<Envelope> DrainDownlink(string moundId)
    {
        lock (_gate)
        {
            if (!_downlink.TryGetValue(moundId, out var queue) || queue.Count == 0) return [];
            var taken = queue.ToList();
            queue.Clear();
            return taken;
        }
    }

    public int PendingDownlinkCount(string moundId)
    {
        lock (_gate) return _downlink.TryGetValue(moundId, out var queue) ? queue.Count : 0;
    }

    public void DiscardDownlink(string moundId)
    {
        lock (_gate) _downlink.Remove(moundId);
    }

    public void PutWidgetPayload(string widgetKind, string payloadJson, string updatedAt)
    {
        lock (_gate) _widgets[widgetKind] = (payloadJson, updatedAt);
    }

    public (string PayloadJson, string UpdatedAt)? GetWidgetPayload(string widgetKind)
    {
        lock (_gate) return _widgets.TryGetValue(widgetKind, out var found) ? found : null;
    }
}
