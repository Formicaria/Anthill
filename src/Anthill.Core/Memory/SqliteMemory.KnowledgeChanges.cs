using Anthill.Core.Common;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Memory;

/// <summary>
/// WHAT CHANGED IN A KNOWLEDGE BASE SINCE THE COLONY LAST READ IT. v0.3.9.3 (A4).
///
/// WHY THIS IS A TABLE AND NOT A FEED. The shared contract's P2 asks the producer for a change feed
/// and records it as absent; §5's interim decision is polling reconciliation with a
/// consumer-constructed identity, LABELLED as synthesized. So a change here is not something FORAGER
/// told us — it is something this colony noticed by comparing what a document's content hash is now
/// against the hash it recorded when it studied that document. `origin_kind` on the seed receipt has
/// said `synthesized:polling` since v0.3.8.154 for the same reason.
///
/// THE WATERMARK IS DERIVED, NOT STORED. "What has changed since?" is answered by the newest row
/// here, which means there is no second fact that can disagree with the rows — the failure a
/// separate cursor column invites the first time a pass writes one and not the other.
///
/// A CHANGE IS A FINDING, NOT AN INSTRUCTION. Recording one queues nothing: under `suggest` the
/// operator decides, under `on` the same pass queues it, and under `off` the pass does not run at
/// all. That split is the whole of A4's policy question, and it lives in one config key rather than
/// in a second switch that could contradict the first.
/// </summary>
public sealed partial class SqliteMemory
{
    private readonly object _knowledgeChangeInit = new();
    private bool _knowledgeChangeTablesReady;

    private void EnsureKnowledgeChangeTables()
    {
        if (_knowledgeChangeTablesReady) return;
        lock (_knowledgeChangeInit)
        {
            if (_knowledgeChangeTablesReady) return;
            using var conn = Connect();
            NonQuery(conn, null, @"
                CREATE TABLE IF NOT EXISTS knowledge_changes (
                    id TEXT PRIMARY KEY,
                    project_ref TEXT NOT NULL,
                    anthill_project_id TEXT,
                    source_id TEXT NOT NULL,
                    source_name TEXT NOT NULL DEFAULT '',
                    change_kind TEXT NOT NULL,
                    previous_hash TEXT,
                    current_hash TEXT,
                    action_key TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'open',
                    mission_id TEXT,
                    note TEXT,
                    detected_at TEXT NOT NULL,
                    decided_at TEXT
                );

                -- ONE ROW PER (source, version). The action key already encodes the instance, the
                -- generation, the project, the source and its content hash, so a second pass over an
                -- unchanged document collides here and is skipped rather than piling up a finding a
                -- day about a document nobody touched.
                CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledge_changes_action
                    ON knowledge_changes(action_key);

                CREATE INDEX IF NOT EXISTS ix_knowledge_changes_open
                    ON knowledge_changes(project_ref, status, detected_at);");

            _knowledgeChangeTablesReady = true;
        }
    }

    /// <summary>One thing that changed, and what became of it.</summary>
    public sealed record KnowledgeChange
    {
        public required string Id { get; init; }
        public required string ProjectRef { get; init; }
        public string? AnthillProjectId { get; init; }
        public required string SourceId { get; init; }
        public string SourceName { get; init; } = "";

        /// <summary>`new` — never studied — or `changed` — studied at a different content hash.</summary>
        public required string Kind { get; init; }

        /// <summary>The hash the colony studied. Null for a document it has never seen.</summary>
        public string? PreviousHash { get; init; }

        /// <summary>What the document's content hash is NOW. Null when the producer did not say —
        /// unrecorded, never "unchanged", the same rule the seed receipt keeps.</summary>
        public string? CurrentHash { get; init; }

        /// <summary>The seeding action key this change would queue under, so a finding and the
        /// mission it becomes share one identity rather than two that must be kept in step.</summary>
        public required string ActionKey { get; init; }

        /// <summary>`open`, `queued`, or `dismissed`.</summary>
        public string Status { get; init; } = "open";

        public string? MissionId { get; init; }
        public string? Note { get; init; }
        public DateTime DetectedAt { get; init; } = AnthillTime.NowUtc();
        public DateTime? DecidedAt { get; init; }
    }

    /// <summary>
    /// Record a finding. Returns false when this exact change is already recorded — which is the
    /// ordinary case on every pass after the first, and not a failure.
    /// </summary>
    public bool TryRecordKnowledgeChange(KnowledgeChange change)
    {
        EnsureKnowledgeChangeTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            try
            {
                NonQuery(conn, null, @"
                INSERT INTO knowledge_changes
                    (id, project_ref, anthill_project_id, source_id, source_name, change_kind,
                     previous_hash, current_hash, action_key, status, mission_id, note,
                     detected_at, decided_at)
                VALUES
                    (@id, @project, @anthill, @source, @name, @kind, @prev, @cur, @key, @status,
                     @mission, @note, @detected, @decided);",
                ("@id", change.Id),
                ("@project", change.ProjectRef),
                ("@anthill", (object?)change.AnthillProjectId ?? DBNull.Value),
                ("@source", change.SourceId),
                ("@name", change.SourceName),
                ("@kind", change.Kind),
                ("@prev", (object?)change.PreviousHash ?? DBNull.Value),
                ("@cur", (object?)change.CurrentHash ?? DBNull.Value),
                ("@key", change.ActionKey),
                ("@status", change.Status),
                ("@mission", (object?)change.MissionId ?? DBNull.Value),
                ("@note", (object?)change.Note ?? DBNull.Value),
                ("@detected", change.DetectedAt.ToIso()),
                ("@decided", (object?)change.DecidedAt?.ToIso() ?? DBNull.Value));
                return true;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                // The unique index refused it: this document is already recorded AT THIS VERSION.
                // Not an error — it is the guarantee working, and it is what stops a daily pass from
                // filing the same finding about a document nobody has touched.
                return false;
            }
        }
    }

    /// <summary>The mission a finding became. Recorded by the pass that queued it, or by an operator
    /// pressing the button — the same row either way, because it is the same event.</summary>
    public void MarkKnowledgeChangeQueued(string actionKey, string missionId)
    {
        EnsureKnowledgeChangeTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, @"
                UPDATE knowledge_changes
                   SET status = 'queued', mission_id = @mission, decided_at = @at
                 WHERE action_key = @key AND status <> 'queued';",
                ("@mission", missionId), ("@at", AnthillTime.NowUtc().ToIso()), ("@key", actionKey));
        }
    }

    /// <summary>An operator saying this change needs no mission. Kept, not deleted: a dismissed
    /// finding is the record that someone looked and decided, and deleting it would make the next
    /// pass rediscover it forever.</summary>
    public KnowledgeChange? DismissKnowledgeChange(string id, string? note)
    {
        var change = KnowledgeChangeById(id);
        if (change is null || !string.Equals(change.Status, "open", StringComparison.Ordinal)) return null;

        var dismissed = change with
        {
            Status = "dismissed",
            Note = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
            DecidedAt = AnthillTime.NowUtc(),
        };

        EnsureKnowledgeChangeTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE knowledge_changes SET status = 'dismissed', note = @note, decided_at = @at WHERE id = @id;",
                ("@note", (object?)dismissed.Note ?? DBNull.Value),
                ("@at", dismissed.DecidedAt!.Value.ToIso()), ("@id", id));
        }
        return dismissed;
    }

    public KnowledgeChange? KnowledgeChangeById(string id)
    {
        EnsureKnowledgeChangeTables();
        return Query("SELECT * FROM knowledge_changes WHERE id = @id;", ("@id", id))
            .Select(KnowledgeChangeFrom).FirstOrDefault();
    }

    /// <summary>Findings, newest first. Null status means every status.</summary>
    public IReadOnlyList<KnowledgeChange> KnowledgeChanges(string? status = "open", int limit = 200)
    {
        EnsureKnowledgeChangeTables();
        var rows = status is null
            ? Query("SELECT * FROM knowledge_changes ORDER BY detected_at DESC LIMIT @n;", ("@n", limit))
            : Query("SELECT * FROM knowledge_changes WHERE status = @s ORDER BY detected_at DESC LIMIT @n;",
                    ("@s", status), ("@n", limit));
        return rows.Select(KnowledgeChangeFrom).ToList();
    }

    /// <summary>
    /// THE WATERMARK: when this colony last SAW a change in that knowledge base, derived from the
    /// findings rather than stored beside them. Null means nothing has ever been noticed there.
    /// </summary>
    public DateTime? KnowledgeChangeWatermark(string projectRef)
    {
        EnsureKnowledgeChangeTables();
        return Query("SELECT MAX(detected_at) AS at FROM knowledge_changes WHERE project_ref = @p;",
                ("@p", projectRef))
            .Select(r => RowValues.Timestamp(r, "at")).FirstOrDefault();
    }

    /// <summary>
    /// The content hash this colony last STUDIED for each source in a knowledge base.
    ///
    /// The comparison A4 rests on, and it is a read of the seeding receipts rather than a new record:
    /// `.154` has stored `logical_content_hash` per studied source since it shipped, so "what has
    /// changed" is already answerable and was simply never asked.
    /// </summary>
    public IReadOnlyDictionary<string, string> StudiedContentHashes(string projectRef)
    {
        EnsureKnowledgeSeedTables();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        // Newest first, and the first one per source wins: a document studied twice has two receipts
        // and the LATEST is what the colony currently believes about it.
        foreach (var row in Query(
            @"SELECT source_id, logical_content_hash FROM knowledge_seed_receipts
              WHERE project_ref = @p AND logical_content_hash IS NOT NULL
              ORDER BY received_at DESC;", ("@p", projectRef)))
        {
            var source = RowValues.Text(row, "source_id");
            var hash = RowValues.Text(row, "logical_content_hash");
            if (source.Length == 0 || hash.Length == 0) continue;
            if (!hashes.ContainsKey(source)) hashes[source] = hash;
        }
        return hashes;
    }

    private static KnowledgeChange KnowledgeChangeFrom(Dictionary<string, object?> row) => new()
    {
        Id = RowValues.Text(row, "id"),
        ProjectRef = RowValues.Text(row, "project_ref"),
        AnthillProjectId = RowValues.TextOrNull(row, "anthill_project_id"),
        SourceId = RowValues.Text(row, "source_id"),
        SourceName = RowValues.Text(row, "source_name"),
        Kind = RowValues.Text(row, "change_kind", "changed"),
        PreviousHash = RowValues.TextOrNull(row, "previous_hash"),
        CurrentHash = RowValues.TextOrNull(row, "current_hash"),
        ActionKey = RowValues.Text(row, "action_key"),
        Status = RowValues.Text(row, "status", "open"),
        MissionId = RowValues.TextOrNull(row, "mission_id"),
        Note = RowValues.TextOrNull(row, "note"),
        DetectedAt = RowValues.TimestampOrNow(row, "detected_at"),
        DecidedAt = RowValues.Timestamp(row, "decided_at"),
    };
}
