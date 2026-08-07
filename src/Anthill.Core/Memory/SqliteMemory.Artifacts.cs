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
    };

    private static DateTime ParseUtc(object? value) =>
        DateTime.TryParse(value?.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d : AnthillTime.NowUtc();

    // ---- IArtifactStore ---------------------------------------------------

    string IArtifactStore.Put(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO artifacts
                    (id, schema, schema_version, producer_role, mission_id, task_id, workspace_id,
                     source_ids_json, content_hash, visibility, payload, created_at)
                  VALUES (@id, @schema, @sv, @role, @mission, @task, @ws, @sources, @hash, @vis, @payload, @at)",
                ("@id", artifact.Id), ("@schema", artifact.Schema), ("@sv", artifact.SchemaVersion),
                ("@role", artifact.ProducerRole), ("@mission", artifact.MissionId),
                ("@task", artifact.TaskId), ("@ws", artifact.WorkspaceId),
                ("@sources", JsonList(artifact.SourceArtifactIds)), ("@hash", artifact.ContentHash),
                ("@vis", artifact.Visibility.ToString()), ("@payload", artifact.Payload),
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

    // ---- IEvidenceStore ---------------------------------------------------

    string IEvidenceStore.Put(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO evidence
                    (id, kind, deterministic, passed, artifact_ids_json, detail, mission_id, task_id, created_at)
                  VALUES (@id, @kind, @det, @passed, @arts, @detail, @mission, @task, @at)",
                ("@id", evidence.Id), ("@kind", evidence.Kind),
                ("@det", evidence.Deterministic ? 1 : 0), ("@passed", evidence.Passed ? 1 : 0),
                ("@arts", JsonList(evidence.ArtifactIds)), ("@detail", evidence.Detail),
                ("@mission", evidence.MissionId), ("@task", evidence.TaskId),
                ("@at", evidence.CreatedAt.ToIso()));
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
