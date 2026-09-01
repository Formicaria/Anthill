using System.Text.Json;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Memory;

/// <summary>
/// ADR-004's artifact and evidence stores, persisted. v3.8.19.
///
/// APPEND-ONLY BY CONSTRUCTION. There is no Update and no Delete here, and that is not an oversight:
/// a revision is a new artifact citing the old one in its sources. The store's whole value is
/// answering "what was this based on, at the time" — an in-place edit destroys exactly that.
///
/// SHIPPED WITH NO PRODUCER. Nothing writes to these tables yet; ants still pass prose through
/// <c>Task.Result</c>. That is deliberate, and it is the shape phase 0 of the refactor used: land the
/// contract and the persistence, prove they work, then move consumers in a release whose blast radius
/// is one thing. ADR-004 calls replacing the output path the largest behavioural change in V3.
///
/// Implemented EXPLICITLY, like <c>IPheromoneMemory</c> and <c>IEventLog</c> in
/// <c>SqliteMemory.SdkContracts.cs</c> — reachable only through the interface, so a core call site
/// cannot drift into using the module-facing shape by accident.
/// </summary>
public sealed partial class SqliteMemory : IArtifactStore, IEvidenceStore
{
    private static string JsonList(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> ParseList(object? json)
    {
        var text = json?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(text) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static Artifact ToArtifact(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Schema = row.GetValueOrDefault("schema")?.ToString() ?? "",
        SchemaVersion = (int)AsLong(row.GetValueOrDefault("schema_version")),
        ProducerRole = row.GetValueOrDefault("producer_role")?.ToString() ?? "",
        MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
        TaskId = row.GetValueOrDefault("task_id")?.ToString(),
        WorkspaceId = row.GetValueOrDefault("workspace_id")?.ToString(),
        SourceArtifactIds = ParseList(row.GetValueOrDefault("source_ids_json")),
        ContentHash = row.GetValueOrDefault("content_hash")?.ToString() ?? "",
        Visibility = Enum.TryParse<ArtifactVisibility>(row.GetValueOrDefault("visibility")?.ToString(), out var v)
            ? v
            // Unparseable visibility fails CLOSED. A row whose audience cannot be read is not one to
            // guess about — Secret is never rendered, so the failure is invisible content rather
            // than a leak.
            : ArtifactVisibility.Secret,
        Payload = row.GetValueOrDefault("payload")?.ToString() ?? "",
        CreatedAt = ParseUtc(row.GetValueOrDefault("created_at")),
        // Null for every artifact written before v0.3.8.57, and for any producer that could not
        // state its origin. FromJson returns null on unparseable text rather than throwing: an
        // artifact whose provenance cannot be read is still an artifact a worker may need.
        Provenance = ArtifactProvenance.FromJson(row.GetValueOrDefault("provenance_json")?.ToString()),
    };

    private static Evidence ToEvidence(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Kind = row.GetValueOrDefault("kind")?.ToString() ?? "",
        Deterministic = AsLong(row.GetValueOrDefault("deterministic")) != 0,
        Passed = AsLong(row.GetValueOrDefault("passed")) != 0,
        ArtifactIds = ParseList(row.GetValueOrDefault("artifact_ids_json")),
        Detail = row.GetValueOrDefault("detail")?.ToString() ?? "",
        MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
        TaskId = row.GetValueOrDefault("task_id")?.ToString(),
        CreatedAt = ParseUtc(row.GetValueOrDefault("created_at")),
        // v0.3.8.57 — which tree this check judged. NULL on a legacy row, which reads as "not about
        // a materialized revision" rather than as a match; see Evidence.IdentifiesARevision.
        RevisionId = row.GetValueOrDefault("revision_id")?.ToString(),
        PatchSetHash = row.GetValueOrDefault("patch_set_hash")?.ToString(),
        TreeHash = row.GetValueOrDefault("tree_hash")?.ToString(),
    };

    private static DateTime ParseUtc(object? value) =>
        DateTime.TryParse(value?.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d : AnthillTime.NowUtc();

    // ---- IArtifactStore ---------------------------------------------------

    string IArtifactStore.Put(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // v0.3.8.57 — the WRITE boundary. Until now any string could be stored under any schema
        // name, so the store could prove a payload had not changed and nothing about whether it
        // was ever the shape its label claimed.
        //
        // REPORTS, DOES NOT REFUSE. A producer with an off-shape payload has made a mistake worth
        // surfacing; dropping the row would trade a wrong artifact for a missing one, and a missing
        // one is the harder failure to notice — the consumer just proceeds with less. The read
        // boundary in ArtifactContext tells the worker what it is actually holding.
        var conformance = ArtifactSchemaCheck.Validate(artifact.Schema, artifact.Payload);
        if (!conformance.Conforms)
        {
            // The REPORT may never fail the WRITE. `events` carries a foreign key to missions(id)
            // and `artifacts` does not, so an artifact stored against a mission with no row — which
            // several call sites and tests legitimately do — made LogEvent throw and took the Put
            // down with it. A diagnostic that can break the operation it is describing is worse than
            // no diagnostic: it converts "this payload is the wrong shape" into "the artifact was
            // never stored", which is the harder failure and the wrong one.
            try
            {
                LogEvent(artifact.MissionId, "artifact_schema_violation",
                    $"{artifact.ProducerRole} stored an artifact that does not match its schema: {conformance.Reason}",
                    artifact.TaskId, artifact.ProducerRole,
                    new()
                    {
                        ["artifact_id"] = artifact.Id,
                        ["schema"] = artifact.Schema,
                        ["conformance"] = conformance.Status.ToString(),
                        ["reason"] = conformance.Reason,
                    });
            }
            catch (Exception error)
            {
                // Still SAID, on the channel that cannot fail. Swallowing it entirely would make the
                // check silent exactly when the store is unhealthy.
                Console.Error.WriteLine(
                    $"[artifact-schema] {artifact.Schema} from {artifact.ProducerRole}: {conformance.Reason} "
                  + $"(could not record the event: {error.Message})");
            }
        }

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO artifacts
                    (id, schema, schema_version, producer_role, mission_id, task_id, workspace_id,
                     source_ids_json, content_hash, visibility, payload, provenance_json, created_at)
                  VALUES (@id, @schema, @sv, @role, @mission, @task, @ws, @sources, @hash, @vis, @payload, @prov, @at)",
                ("@id", artifact.Id), ("@schema", artifact.Schema), ("@sv", artifact.SchemaVersion),
                ("@role", artifact.ProducerRole), ("@mission", artifact.MissionId),
                ("@task", artifact.TaskId), ("@ws", artifact.WorkspaceId),
                ("@sources", JsonList(artifact.SourceArtifactIds)), ("@hash", artifact.ContentHash),
                ("@vis", artifact.Visibility.ToString()), ("@payload", artifact.Payload),
                ("@prov", artifact.Provenance?.ToJson()),
                ("@at", artifact.CreatedAt.ToIso()));
        }
        return artifact.Id;
    }

    Artifact? IArtifactStore.Get(string artifactId)
    {
        var rows = Query("SELECT * FROM artifacts WHERE id = @id LIMIT 1", ("@id", artifactId ?? ""));
        return rows.Count == 0 ? null : ToArtifact(rows[0]);
    }

    IReadOnlyList<Artifact> IArtifactStore.ForMission(string missionId, int limit) =>
        Query("SELECT * FROM artifacts WHERE mission_id = @m ORDER BY created_at DESC LIMIT @l",
              ("@m", missionId ?? ""), ("@l", limit)).Select(ToArtifact).ToList();

    IReadOnlyList<Artifact> IArtifactStore.ForMission(string missionId, string schema, int limit) =>
        Query("SELECT * FROM artifacts WHERE mission_id = @m AND schema = @s ORDER BY created_at DESC LIMIT @l",
              ("@m", missionId ?? ""), ("@s", schema ?? ""), ("@l", limit)).Select(ToArtifact).ToList();

    IReadOnlyList<Artifact> IArtifactStore.SourcesOf(string artifactId)
    {
        var self = ((IArtifactStore)this).Get(artifactId);
        if (self is null || self.SourceArtifactIds.Count == 0) return Array.Empty<Artifact>();

        // Resolved one at a time rather than with an IN clause: the list is short by construction
        // (an artifact cites the inputs it actually used), and a parameterised IN of variable arity
        // is the kind of string-built SQL this file should not contain.
        return self.SourceArtifactIds
            .Select(id => ((IArtifactStore)this).Get(id))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
    }

    /// <summary>
    /// The reverse edge. A LIKE over the JSON source list rather than a join table — the id is a
    /// 36-character opaque token, so a substring match on <c>"art_..."</c> cannot collide with
    /// anything else in that column, and a second table would need to be kept consistent with the
    /// field that is already the truth.
    /// </summary>
    IReadOnlyList<Artifact> IArtifactStore.ConsumersOf(string artifactId) =>
        string.IsNullOrWhiteSpace(artifactId)
            ? Array.Empty<Artifact>()
            : Query("SELECT * FROM artifacts WHERE source_ids_json LIKE @needle ORDER BY created_at DESC",
                    ("@needle", $"%\"{artifactId}\"%")).Select(ToArtifact).ToList();

    // ---- the consumption ledger (v0.3.8.57) -------------------------------

    private static ArtifactConsumption ToConsumption(Dictionary<string, object?> row) => new()
    {
        ArtifactId = row.GetValueOrDefault("artifact_id")?.ToString() ?? "",
        ContentHash = row.GetValueOrDefault("content_hash")?.ToString() ?? "",
        Schema = row.GetValueOrDefault("schema")?.ToString() ?? "",
        MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
        ConsumerRole = row.GetValueOrDefault("consumer_role")?.ToString() ?? "",
        // Stored as '' rather than NULL so the composite primary key works — SQLite treats NULLs in a
        // key as distinct, which would defeat the whole idempotency. Read back as null, because "no
        // task" is what it means.
        ConsumerTaskId = row.GetValueOrDefault("consumer_task_id")?.ToString() is { Length: > 0 } t ? t : null,
        // v0.3.8.106. NULL for every legacy row and every same-mission read; `ReadBy` resolves it.
        ConsumerMissionId = row.GetValueOrDefault("consumer_mission_id")?.ToString() is { Length: > 0 } c ? c : null,
        ReadCount = (int)AsLong(row.GetValueOrDefault("read_count")),
        FirstReadAt = ParseUtc(row.GetValueOrDefault("first_read_at")),
        LastReadAt = ParseUtc(row.GetValueOrDefault("last_read_at")),
    };

    void IArtifactStore.RecordConsumption(ArtifactConsumption consumption)
    {
        ArgumentNullException.ThrowIfNull(consumption);
        if (string.IsNullOrWhiteSpace(consumption.ArtifactId) || string.IsNullOrWhiteSpace(consumption.ConsumerRole))
            return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO artifact_consumptions
                    (artifact_id, consumer_role, consumer_task_id, content_hash, schema, mission_id,
                     consumer_mission_id, read_count, first_read_at, last_read_at)
                  VALUES (@aid, @role, @task, @hash, @schema, @mission, @consumer, 1, @at, @at)
                  ON CONFLICT (artifact_id, consumer_role, consumer_task_id) DO UPDATE SET
                    read_count = read_count + 1,
                    last_read_at = @at,
                    -- v0.3.8.106: a re-read must not blank the consumer a prior read established.
                    -- COALESCE on the INCOMING value, so a task-less re-read of a cross-mission
                    -- artifact keeps the mission that was recorded reading it.
                    consumer_mission_id = COALESCE(@consumer, consumer_mission_id)",
                ("@aid", consumption.ArtifactId), ("@role", consumption.ConsumerRole),
                ("@task", consumption.ConsumerTaskId ?? ""), ("@hash", consumption.ContentHash),
                ("@schema", consumption.Schema), ("@mission", consumption.MissionId),
                ("@consumer", (object?)consumption.ConsumerMissionId),
                ("@at", AnthillTime.NowUtc().ToIso()));
        }
    }

    IReadOnlyList<ArtifactConsumption> IArtifactStore.ConsumptionsOf(string artifactId) =>
        string.IsNullOrWhiteSpace(artifactId)
            ? Array.Empty<ArtifactConsumption>()
            : Query("SELECT * FROM artifact_consumptions WHERE artifact_id = @a ORDER BY last_read_at DESC",
                    ("@a", artifactId)).Select(ToConsumption).ToList();

    /// <summary>
    /// Consumptions keyed on the PRODUCING mission — unchanged at v0.3.8.106, deliberately.
    ///
    /// `.98`'s assessment objective asks "did the verifier read what it graded", which is a
    /// question about one mission's own artifacts, and every row it has ever read is a same-mission
    /// row where the two mission columns agree. Changing this query to the consumer would have
    /// altered that grading for no reason the release could name. The new question gets a new
    /// method rather than a new meaning for an old one.
    /// </summary>
    IReadOnlyList<ArtifactConsumption> IArtifactStore.ConsumptionsForMission(string missionId, int limit) =>
        string.IsNullOrWhiteSpace(missionId)
            ? Array.Empty<ArtifactConsumption>()
            : Query("SELECT * FROM artifact_consumptions WHERE mission_id = @m ORDER BY last_read_at DESC LIMIT @l",
                    ("@m", missionId), ("@l", limit)).Select(ToConsumption).ToList();

    /// <summary>
    /// What a mission READ, including artifacts other missions produced. v0.3.8.106.
    ///
    /// Matches on the consumer column, falling back to the producing mission for the legacy and
    /// same-mission rows where the consumer was never written — so this answers "what did mission X
    /// consume" across the whole ledger's history rather than only across rows written since.
    /// </summary>
    public IReadOnlyList<ArtifactConsumption> ConsumptionsByMission(string missionId, int limit = 500) =>
        string.IsNullOrWhiteSpace(missionId)
            ? Array.Empty<ArtifactConsumption>()
            : Query(@"SELECT * FROM artifact_consumptions
                      WHERE COALESCE(NULLIF(consumer_mission_id, ''), mission_id) = @m
                      ORDER BY last_read_at DESC LIMIT @l",
                    ("@m", missionId), ("@l", limit)).Select(ToConsumption).ToList();

    // ---- IEvidenceStore ---------------------------------------------------

    string IEvidenceStore.Put(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO evidence
                    (id, kind, deterministic, passed, artifact_ids_json, detail, mission_id, task_id, created_at,
                     revision_id, patch_set_hash, tree_hash)
                  VALUES (@id, @kind, @det, @passed, @arts, @detail, @mission, @task, @at,
                          @rev, @psh, @tree)",
                ("@id", evidence.Id), ("@kind", evidence.Kind),
                ("@det", evidence.Deterministic ? 1 : 0), ("@passed", evidence.Passed ? 1 : 0),
                ("@arts", JsonList(evidence.ArtifactIds)), ("@detail", evidence.Detail),
                ("@mission", evidence.MissionId), ("@task", evidence.TaskId),
                ("@at", evidence.CreatedAt.ToIso()),
                // v0.3.8.57 — persisted, or the identity would exist only in memory and the whole
                // point (querying "does this evidence judge THIS revision") would be unreachable.
                ("@rev", (object?)evidence.RevisionId ?? DBNull.Value),
                ("@psh", (object?)evidence.PatchSetHash ?? DBNull.Value),
                ("@tree", (object?)evidence.TreeHash ?? DBNull.Value));
        }
        return evidence.Id;
    }

    IReadOnlyList<Evidence> IEvidenceStore.ForMission(string missionId, int limit) =>
        Query("SELECT * FROM evidence WHERE mission_id = @m ORDER BY created_at DESC LIMIT @l",
              ("@m", missionId ?? ""), ("@l", limit)).Select(ToEvidence).ToList();

    IReadOnlyList<Evidence> IEvidenceStore.ForArtifact(string artifactId) =>
        string.IsNullOrWhiteSpace(artifactId)
            ? Array.Empty<Evidence>()
            : Query("SELECT * FROM evidence WHERE artifact_ids_json LIKE @needle ORDER BY created_at DESC",
                    ("@needle", $"%\"{artifactId}\"%")).Select(ToEvidence).ToList();

    /// <summary>
    /// One question, one place. Every promotion path asks it, and the v2.26.0 rule is that only
    /// reproducible evidence may carry a mission to a verified outcome — so a model review, however
    /// confident, cannot satisfy this.
    /// </summary>
    bool IEvidenceStore.HasDeterministicPass(string missionId) =>
        AsLong(Scalar("SELECT COUNT(*) FROM evidence WHERE mission_id = @m AND deterministic = 1 AND passed = 1",
                      ("@m", missionId ?? ""))) > 0;
}
