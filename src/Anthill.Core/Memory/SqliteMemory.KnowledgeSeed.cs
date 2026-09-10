using Anthill.Core.Common;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Memory;

/// <summary>
/// WHAT HAS ALREADY BEEN SEEDED FROM A KNOWLEDGE BASE. v0.3.8.154.
///
/// The operator asked for this in one sentence: "I should be able to click on one knowledge base and
/// have it then run through the anthill automated missions to build its memory and pheromones."
/// Doing that twice must not do it twice, and a crash in the middle must neither drop a document nor
/// seed it again — which is a durable watermark, and this is it.
///
/// THE COLUMN NAMES ARE THE PRODUCER'S, NOT MINE, AND THAT IS THE WHOLE DESIGN.
/// `FORAGER_SHARED_CONTRACT.md` §5 specifies a change-feed envelope — `event_id`, `sequence`,
/// `revision_id`, `producer_instance_id`, `producer_generation`, `origin_kind`, `causation_id`,
/// `logical_content_hash`, `publication_status` — and Phase 0 decision 3 commits both sides to
/// adopting that vocabulary on the CONSUMER side now, so that when the feed (P2) and the publication
/// ledger (P8) arrive, producer events land in an already-shaped table instead of forcing a
/// migration that reinterprets old rows. Every column below is one of those names.
///
/// AND EVERY ROW SAYS IT IS SYNTHESIZED. There is no feed yet: these rows are made by POLLING
/// `ListSourcesAsync`, which the contract names as the interim substitute and requires be labelled
/// as such. `origin_kind` carries that label, so the day a real producer event is recorded here
/// nothing has to guess which rows were invented by the consumer.
///
/// WHAT MAKES A ROW UNIQUE, and it is five parts rather than one:
///
///     producer_instance_id   WHICH FORAGER. Two engines can hold the same project id.
///     producer_generation    WHICH HISTORY. §5 — a restored or cloned store gets a new generation,
///                            and every watermark from before it is about a different past. Without
///                            this, a restore silently means "already seeded" for documents that no
///                            longer exist.
///     project_ref            WHICH KNOWLEDGE BASE, in the producer's own id.
///     source_id              WHICH DOCUMENT, producer-assigned.
///     logical_content_hash   WHICH VERSION of it. A re-published document has a new hash and is
///                            seeded again, which is the point: the colony should learn the new one.
///
/// `logical_content_hash` is NULLABLE and a null is UNRECORDED, never "unchanged" — FORAGER does not
/// always carry a content hash, and treating its absence as a match would silently refuse to ever
/// re-seed that document. A null hash keys on the document alone and says so.
/// </summary>
public sealed partial class SqliteMemory
{
    private bool _knowledgeSeedTablesReady;
    private readonly object _knowledgeSeedInit = new();

    /// <summary>
    /// Lazy DDL, in the shape <c>SqliteMemory.Jobs.cs</c> established: a slice that most colonies
    /// never touch does not belong in the startup transaction every colony pays for.
    /// </summary>
    private void EnsureKnowledgeSeedTables()
    {
        if (_knowledgeSeedTablesReady) return;
        lock (_knowledgeSeedInit)
        {
            if (_knowledgeSeedTablesReady) return;
            using var conn = Connect();
            NonQuery(conn, null, @"
                CREATE TABLE IF NOT EXISTS knowledge_seed_receipts (
                    event_id TEXT PRIMARY KEY,
                    sequence INTEGER,
                    revision_id TEXT,
                    producer_instance_id TEXT NOT NULL DEFAULT '',
                    producer_generation TEXT NOT NULL DEFAULT '',
                    origin_kind TEXT NOT NULL,
                    causation_id TEXT,
                    logical_content_hash TEXT,
                    publication_status TEXT,
                    project_ref TEXT NOT NULL,
                    anthill_project_id TEXT,
                    source_id TEXT NOT NULL,
                    source_name TEXT NOT NULL DEFAULT '',
                    action_key TEXT NOT NULL,
                    job_id TEXT,
                    mission_id TEXT,
                    state TEXT NOT NULL DEFAULT 'received',
                    detail TEXT,
                    received_at TEXT NOT NULL,
                    submitted_at TEXT);");

            // THE UNIQUENESS IS THE IDEMPOTENCY, enforced by the database rather than by a check the
            // caller might skip — the same argument `mission_jobs`' partial unique index makes.
            NonQuery(conn, null, @"
                CREATE UNIQUE INDEX IF NOT EXISTS ix_knowledge_seed_action
                    ON knowledge_seed_receipts(action_key);");
            NonQuery(conn, null, @"
                CREATE INDEX IF NOT EXISTS ix_knowledge_seed_project
                    ON knowledge_seed_receipts(project_ref, source_id);");

            _knowledgeSeedTablesReady = true;
        }
    }

    /// <summary>
    /// One recorded intent to seed one document version. Typed, because the untyped-store ratchet
    /// exists and because a caller reading `row["state"]` is a caller that can misspell it.
    /// </summary>
    public sealed record KnowledgeSeedReceipt
    {
        public required string EventId { get; init; }
        public required string ActionKey { get; init; }
        public required string ProjectRef { get; init; }
        public string? AnthillProjectId { get; init; }
        public required string SourceId { get; init; }
        public string SourceName { get; init; } = "";
        public string? LogicalContentHash { get; init; }
        public string ProducerInstanceId { get; init; } = "";
        public string ProducerGeneration { get; init; } = "";
        public string OriginKind { get; init; } = SynthesizedByPolling;
        public string? RevisionId { get; init; }
        public string? PublicationStatus { get; init; }
        public string? JobId { get; init; }
        public string? MissionId { get; init; }
        public string State { get; init; } = "received";
        public string? Detail { get; init; }
        public DateTime ReceivedAt { get; init; } = AnthillTime.NowUtc();
        public DateTime? SubmittedAt { get; init; }
    }

    /// <summary>
    /// The `origin_kind` every row written by polling carries. Named here, once, because the whole
    /// value of the label is that a later reader can tell a synthesized row from a producer event —
    /// and two spellings of it would defeat that completely.
    /// </summary>
    public const string SynthesizedByPolling = "synthesized:polling";

    /// <summary>
    /// THE STABLE KEY, DERIVED EXACTLY AS THE CONTRACT PRESCRIBES. Part I §7:
    ///
    ///   "derive a stable key from the source instance, mapped project, knowledge revision, action
    ///    type and relevant automation-policy version."
    ///
    /// Every one of those five is here. The "knowledge revision" is the content hash where FORAGER
    /// gives one and the source id alone where it does not — labelled `norev` rather than silently
    /// producing a key that looks revision-bound and is not.
    ///
    /// The policy version is the LAST component and it is deliberate: when the seeding behaviour
    /// changes in a way that should re-run over knowledge already seeded, bumping it is how that is
    /// said. Without it the only way to re-seed would be deleting rows, which loses the record of
    /// what happened the first time.
    /// </summary>
    public static string SeedActionKey(string instanceId, string generation, string projectRef,
        string sourceId, string? contentHash, string actionType, string policyVersion) =>
        string.Join(':', new[]
        {
            "kseed",
            Slug(instanceId), Slug(generation), Slug(projectRef), Slug(sourceId),
            string.IsNullOrWhiteSpace(contentHash) ? "norev" : Slug(contentHash!),
            Slug(actionType), Slug(policyVersion),
        });

    /// <summary>
    /// `ApiJobRegistry` rejects an idempotency key outside `[A-Za-z0-9-_:.]` or over 200 characters,
    /// and this key is handed straight to it — so the reduction happens HERE, where the key is made,
    /// rather than being discovered as a refusal at submission time.
    /// </summary>
    private static string Slug(string value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var slug = new string(chars);
        return slug.Length <= 40 ? slug : slug[..40];
    }

    /// <summary>
    /// Record the intent to seed, and say whether this is the first time.
    ///
    /// RECEIPT BEFORE WORK, which is §7's rule and not a preference: "Anthill durably records an
    /// incoming event before acknowledging receipt… Persist submission intent and use the existing
    /// mission queue's idempotency support." A crash after this row and before the queue leaves a
    /// `received` row with no job, which a later pass can see and finish — the one outcome that is
    /// neither a dropped document nor a duplicate mission.
    ///
    /// Returns false when the key is already present, and that is the ordinary case rather than an
    /// error: it is what makes clicking twice do nothing.
    /// </summary>
    public bool TryRecordSeedIntent(KnowledgeSeedReceipt receipt)
    {
        EnsureKnowledgeSeedTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            try
            {
                NonQuery(conn, null, @"
                    INSERT INTO knowledge_seed_receipts
                        (event_id, sequence, revision_id, producer_instance_id, producer_generation,
                         origin_kind, causation_id, logical_content_hash, publication_status,
                         project_ref, anthill_project_id, source_id, source_name, action_key,
                         job_id, mission_id, state, detail, received_at, submitted_at)
                    VALUES
                        (@event_id, NULL, @revision_id, @instance, @generation,
                         @origin, NULL, @hash, @publication,
                         @project_ref, @anthill_project, @source_id, @source_name, @action_key,
                         @job_id, NULL, @state, @detail, @received_at, NULL);",
                    ("@event_id", receipt.EventId),
                    ("@revision_id", (object?)receipt.RevisionId ?? DBNull.Value),
                    ("@instance", receipt.ProducerInstanceId),
                    ("@generation", receipt.ProducerGeneration),
                    ("@origin", receipt.OriginKind),
                    ("@hash", (object?)receipt.LogicalContentHash ?? DBNull.Value),
                    ("@publication", (object?)receipt.PublicationStatus ?? DBNull.Value),
                    ("@project_ref", receipt.ProjectRef),
                    ("@anthill_project", (object?)receipt.AnthillProjectId ?? DBNull.Value),
                    ("@source_id", receipt.SourceId),
                    ("@source_name", receipt.SourceName),
                    ("@action_key", receipt.ActionKey),
                    ("@job_id", (object?)receipt.JobId ?? DBNull.Value),
                    ("@state", receipt.State),
                    ("@detail", (object?)receipt.Detail ?? DBNull.Value),
                    ("@received_at", receipt.ReceivedAt.ToIso()));
                return true;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                // The unique index refused it: this document version already has a receipt. Not an
                // error — it is the guarantee working, and the caller counts it as "already seeded".
                return false;
            }
        }
    }

    /// <summary>Attach the queued job to a receipt, once the mission queue has accepted it.</summary>
    public void MarkSeedSubmitted(string actionKey, string jobId)
    {
        EnsureKnowledgeSeedTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, @"
                UPDATE knowledge_seed_receipts
                   SET job_id = @job_id, state = 'submitted', submitted_at = @at
                 WHERE action_key = @action_key;",
                ("@job_id", jobId), ("@at", AnthillTime.NowUtc().ToIso()), ("@action_key", actionKey));
        }
    }

    /// <summary>
    /// Receipts that were recorded and never reached the queue — the crash window §7 names. Read by
    /// the next pass so an interrupted seed finishes rather than sitting as a row nobody revisits.
    /// </summary>
    public IReadOnlyList<KnowledgeSeedReceipt> UnsubmittedSeedIntents(string projectRef, int limit = 100) =>
        SeedQuery(@"
            SELECT * FROM knowledge_seed_receipts
             WHERE project_ref = @p AND state = 'received'
             ORDER BY received_at LIMIT @n;",
            ("@p", projectRef), ("@n", limit));

    /// <summary>What this knowledge base has already had seeded, newest first.</summary>
    public IReadOnlyList<KnowledgeSeedReceipt> SeedReceiptsFor(string projectRef, int limit = 200) =>
        SeedQuery(@"
            SELECT * FROM knowledge_seed_receipts
             WHERE project_ref = @p
             ORDER BY received_at DESC LIMIT @n;",
            ("@p", projectRef), ("@n", limit));

    /// <summary>How many documents of this base have a receipt. The console's "already seeded" count.</summary>
    public int SeededSourceCount(string projectRef)
    {
        EnsureKnowledgeSeedTables();
        var value = Scalar("SELECT COUNT(DISTINCT source_id) FROM knowledge_seed_receipts WHERE project_ref = @p;",
            ("@p", projectRef));
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private IReadOnlyList<KnowledgeSeedReceipt> SeedQuery(string sql, params (string Name, object? Value)[] args)
    {
        EnsureKnowledgeSeedTables();
        return Query(sql, args).Select(SeedReceiptFrom).ToList();
    }

    /// <summary>One row, one object, `RowValues` throughout — the typed-slice shape since `.113`.</summary>
    private static KnowledgeSeedReceipt SeedReceiptFrom(Dictionary<string, object?> row) => new()
    {
        EventId = RowValues.Text(row, "event_id"),
        ActionKey = RowValues.Text(row, "action_key"),
        ProjectRef = RowValues.Text(row, "project_ref"),
        AnthillProjectId = RowValues.TextOrNull(row, "anthill_project_id"),
        SourceId = RowValues.Text(row, "source_id"),
        SourceName = RowValues.Text(row, "source_name"),
        LogicalContentHash = RowValues.TextOrNull(row, "logical_content_hash"),
        ProducerInstanceId = RowValues.Text(row, "producer_instance_id"),
        ProducerGeneration = RowValues.Text(row, "producer_generation"),
        OriginKind = RowValues.Text(row, "origin_kind", SynthesizedByPolling),
        RevisionId = RowValues.TextOrNull(row, "revision_id"),
        PublicationStatus = RowValues.TextOrNull(row, "publication_status"),
        JobId = RowValues.TextOrNull(row, "job_id"),
        MissionId = RowValues.TextOrNull(row, "mission_id"),
        State = RowValues.Text(row, "state", "received"),
        Detail = RowValues.TextOrNull(row, "detail"),
        ReceivedAt = RowValues.TimestampOrNow(row, "received_at"),
        SubmittedAt = RowValues.Timestamp(row, "submitted_at"),
    };
}
