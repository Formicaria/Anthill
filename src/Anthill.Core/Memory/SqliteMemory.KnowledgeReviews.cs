using Anthill.Core.Common;

namespace Anthill.Core.Memory;

/// <summary>
/// KNOWLEDGE REVIEW PROPOSALS, AS A RECORD SOMEBODY CAN DECIDE. v0.3.8.155.
///
/// `.122` shipped the proposal and said plainly what it was leaving: "This is not the approval
/// pipeline — a typed proposal KIND is a core surface and deserves its own release — but a durable
/// record with every field an operator needs is the difference between 'not built yet' and
/// 'silently discarded'." This is that release.
///
/// WHAT WAS ACTUALLY WRONG, and it was worse than a missing screen. The proposal reached the event
/// log and stopped: an event is a thing that HAPPENED, and a proposal is a thing that is WAITING.
/// Nothing could list what was outstanding, nothing could answer one, and no role held the tool that
/// raises them — so `knowledge_review` was registered, described, argued for, and reachable by
/// nobody. Three layers of a feature with no first layer.
///
/// ANTHILL DECIDES NOTHING ABOUT THE KNOWLEDGE ITSELF, and this table is careful not to imply
/// otherwise. §1 of the shared contract gives FORAGER the classification, the conflicts and the
/// ranking; a review here is an ANTHILL-side record that an operator agreed with an agent's
/// objection. Accepting one does not change a knowledge base and this build cannot make it — see
/// `accepted` below, and P13 in the contract, which is the producer surface that would.
/// </summary>
public sealed partial class SqliteMemory
{
    private bool _knowledgeReviewTablesReady;
    private readonly object _knowledgeReviewInit = new();

    private void EnsureKnowledgeReviewTables()
    {
        if (_knowledgeReviewTablesReady) return;
        lock (_knowledgeReviewInit)
        {
            if (_knowledgeReviewTablesReady) return;
            using var conn = Connect();
            NonQuery(conn, null, @"
                CREATE TABLE IF NOT EXISTS knowledge_reviews (
                    id TEXT PRIMARY KEY,
                    knowledge_id TEXT NOT NULL,
                    project_ref TEXT NOT NULL DEFAULT '',
                    anthill_project_id TEXT,
                    action TEXT NOT NULL,
                    rationale TEXT NOT NULL,
                    mission_id TEXT,
                    proposed_by TEXT NOT NULL DEFAULT 'researcher',
                    status TEXT NOT NULL DEFAULT 'pending',
                    decided_by TEXT,
                    decision_note TEXT,
                    proposed_at TEXT NOT NULL,
                    decided_at TEXT);");

            NonQuery(conn, null, @"
                CREATE INDEX IF NOT EXISTS ix_knowledge_reviews_status
                    ON knowledge_reviews(status, proposed_at);");

            _knowledgeReviewTablesReady = true;
        }
    }

    /// <summary>
    /// One proposal and what became of it.
    ///
    /// `Status` is `pending`, `accepted` or `declined`, and there is deliberately no `applied`.
    /// Accepting records that the OPERATOR agreed; applying it to the knowledge base needs a producer
    /// endpoint that does not exist (P13). A status this build can never reach would be a promise in
    /// an enum, which is the shape of claim this repository refuses everywhere else.
    /// </summary>
    public sealed record KnowledgeReview
    {
        public required string Id { get; init; }
        public required string KnowledgeId { get; init; }
        public string ProjectRef { get; init; } = "";
        public string? AnthillProjectId { get; init; }
        public required string Action { get; init; }
        public required string Rationale { get; init; }
        public string? MissionId { get; init; }
        public string ProposedBy { get; init; } = "researcher";
        public string Status { get; init; } = "pending";
        public string? DecidedBy { get; init; }
        public string? DecisionNote { get; init; }
        public DateTime ProposedAt { get; init; } = AnthillTime.NowUtc();
        public DateTime? DecidedAt { get; init; }
    }

    public void SaveKnowledgeReview(KnowledgeReview review)
    {
        EnsureKnowledgeReviewTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, @"
                INSERT OR REPLACE INTO knowledge_reviews
                    (id, knowledge_id, project_ref, anthill_project_id, action, rationale, mission_id,
                     proposed_by, status, decided_by, decision_note, proposed_at, decided_at)
                VALUES
                    (@id, @knowledge_id, @project_ref, @anthill_project, @action, @rationale, @mission_id,
                     @proposed_by, @status, @decided_by, @note, @proposed_at, @decided_at);",
                ("@id", review.Id),
                ("@knowledge_id", review.KnowledgeId),
                ("@project_ref", review.ProjectRef),
                ("@anthill_project", (object?)review.AnthillProjectId ?? DBNull.Value),
                ("@action", review.Action),
                ("@rationale", review.Rationale),
                ("@mission_id", (object?)review.MissionId ?? DBNull.Value),
                ("@proposed_by", review.ProposedBy),
                ("@status", review.Status),
                ("@decided_by", (object?)review.DecidedBy ?? DBNull.Value),
                ("@note", (object?)review.DecisionNote ?? DBNull.Value),
                ("@proposed_at", review.ProposedAt.ToIso()),
                ("@decided_at", (object?)review.DecidedAt?.ToIso() ?? DBNull.Value));
        }
    }

    /// <summary>
    /// Answer a proposal. Returns the decided review, or null when the id is unknown or it has
    /// already been decided — a second decision on one proposal is not an update, it is two people
    /// disagreeing about a record that already says what happened.
    /// </summary>
    public KnowledgeReview? DecideKnowledgeReview(string id, bool accept, string decidedBy, string? note)
    {
        var review = KnowledgeReviewById(id);
        if (review is null || !string.Equals(review.Status, "pending", StringComparison.Ordinal)) return null;

        var decided = review with
        {
            Status = accept ? "accepted" : "declined",
            DecidedBy = decidedBy,
            DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
            DecidedAt = AnthillTime.NowUtc(),
        };
        SaveKnowledgeReview(decided);
        return decided;
    }

    public KnowledgeReview? KnowledgeReviewById(string id)
    {
        EnsureKnowledgeReviewTables();
        return Query("SELECT * FROM knowledge_reviews WHERE id = @id;", ("@id", id))
            .Select(KnowledgeReviewFrom).FirstOrDefault();
    }

    /// <summary>Proposals, newest first. `status` null means every status.</summary>
    public IReadOnlyList<KnowledgeReview> KnowledgeReviews(string? status = null, int limit = 100)
    {
        EnsureKnowledgeReviewTables();
        var rows = status is null
            ? Query("SELECT * FROM knowledge_reviews ORDER BY proposed_at DESC LIMIT @n;", ("@n", limit))
            : Query("SELECT * FROM knowledge_reviews WHERE status = @s ORDER BY proposed_at DESC LIMIT @n;",
                    ("@s", status), ("@n", limit));
        return rows.Select(KnowledgeReviewFrom).ToList();
    }

    private static KnowledgeReview KnowledgeReviewFrom(Dictionary<string, object?> row) => new()
    {
        Id = RowValues.Text(row, "id"),
        KnowledgeId = RowValues.Text(row, "knowledge_id"),
        ProjectRef = RowValues.Text(row, "project_ref"),
        AnthillProjectId = RowValues.TextOrNull(row, "anthill_project_id"),
        Action = RowValues.Text(row, "action"),
        Rationale = RowValues.Text(row, "rationale"),
        MissionId = RowValues.TextOrNull(row, "mission_id"),
        ProposedBy = RowValues.Text(row, "proposed_by", "researcher"),
        Status = RowValues.Text(row, "status", "pending"),
        DecidedBy = RowValues.TextOrNull(row, "decided_by"),
        DecisionNote = RowValues.TextOrNull(row, "decision_note"),
        ProposedAt = RowValues.TimestampOrNow(row, "proposed_at"),
        DecidedAt = RowValues.Timestamp(row, "decided_at"),
    };
}
