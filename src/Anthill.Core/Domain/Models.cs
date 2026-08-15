using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Domain;

/// <summary>
/// A Task is a single tunnel segment in the mission path. The Queen assigns it to one
/// specialised ant; memory records the result. The scheduler mutates these in place,
/// so this is a mutable class (not a record) — faithful to the Pydantic model it replaces.
/// </summary>
public sealed class Task
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssignedAnt { get; set; } = "";
    public string? AssignedWorker { get; set; }
    public string TaskType { get; set; } = "general";
    public string? ParentTaskId { get; set; }
    public List<string> ParentTaskIds { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// The artifacts this task is AUTHORITATIVELY given, by id. v0.3.8.57.
    ///
    /// Empty means "no declared inputs", and the context compiler then falls back to the mission-wide
    /// block — which is what every task got before this field existed. That fallback is deliberate:
    /// most tasks have no unambiguous producer to point at, and narrowing them by guesswork would
    /// starve a worker of context it legitimately used.
    ///
    /// WHY IT IS NEEDED. `ArtifactContext.Compile` handed every task every artifact the mission had
    /// accumulated, ordered by a static schema priority. So a tester received the `ui_map` and a
    /// researcher received the `patch_set`: technically bounded, and still "whatever previous work
    /// seems relevant" rather than "the inputs this task was created to consume". When the runtime
    /// KNOWS the producer — a policy-inserted review exists precisely because a specific patch-set
    /// artifact was written — the task can say so, and the worker can be given that instead.
    ///
    /// Ids rather than content, for the reason `ArtifactContext` already gives: the id is the
    /// provenance and the excerpt is a convenience. Recording ids is what makes a task's inputs
    /// reconstructable on replay instead of reassembled from summaries.
    /// </summary>
    public List<string> InputArtifactIds { get; set; } = new();

    /// <summary>
    /// When false, a Failed/Skipped result on THIS task does not propagate a skip to tasks
    /// that depend on it — dependents still wait for it to reach a terminal state, then proceed.
    /// Used by spec-ingestion missions so one failed section never aborts synthesis. Critical
    /// (the default) keeps the original fail-fast dependency semantics.
    /// </summary>
    public bool Critical { get; set; } = true;
    /// <summary>v2.26.0: why this task was cancelled/timed out during the drain — persisted so a
    /// restored mission explains its interrupted tasks the same way the live run did.</summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// v2.22.0: the certified procedure this task was planned FROM, if any. Set only when the
    /// planner chose a route offered by SkillPlanningContext. It records provenance — which proven
    /// procedure was followed — so a verified mission outcome can be credited back to the skill
    /// that earned it. It grants nothing: a skill reference never widens what the task may do.
    /// </summary>
    public string? SkillId { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public string? Result { get; set; }
    public string? ResultSummary { get; set; }
    public int ResultChars { get; set; }
    public int EstimatedTokens { get; set; }
    /// <summary>v3.0.1: the ant produced this result via a DEGRADED (non-model) fallback because the
    /// routed model was unavailable — a structured signal (set from the ant's
    /// <c>succeeded_with_warnings</c> + <c>provider_failure</c> disclosure), never parsed from prose.
    /// Read by <see cref="Anthill.Core.Outcomes.MissionEvaluator"/> so an all-fallback mission cannot
    /// be scored as a verified success. Transient/in-memory: consumed by the single live evaluation.</summary>
    public bool GenerationDegraded { get; set; }

    /// <summary>
    /// v3.8.22: a DETERMINISTIC check said no. Null means nothing blocked; a non-null value is the
    /// reason, recorded for the operator.
    ///
    /// Set from two places, both reproducible and neither a model's opinion: a patch set whose
    /// <c>VerificationBundle</c> came back non-promotable, and a soldier finding marked Blocking.
    /// Before this existed both were LOGGED and neither was consequential — a patch that failed the
    /// build verifier and a patch the policy engine blocked could each still reach
    /// <c>completed_verified</c>, because the only thing reading either was an event row.
    ///
    /// A field rather than a prose scan, for the same reason <see cref="GenerationDegraded"/> is:
    /// the evaluator must stay a pure function of the mission, and a block inferred by parsing a
    /// result string is exactly the prose-derived control flow v3.2.0 removed. Transient/in-memory,
    /// consumed by the single live evaluation — identical lifetime to GenerationDegraded.
    /// </summary>
    public string? DeterministicBlock { get; set; }

    /// <summary>
    /// Structural repair §3/§4 — REVISION LINEAGE, transient like <see cref="GenerationDegraded"/>.
    /// <c>ProducedRevisionId</c> is stamped on the task whose patch set was materialized into a
    /// mission revision; <c>RanRevisionId</c> is stamped on a deterministic check task (tester/
    /// soldier) that executed INSIDE that revision's tree. MissionVerification requires the latest
    /// produced revision to have matching fresh check evidence — evidence from an earlier revision
    /// (or from the unpatched mission workspace, where RanRevisionId stays null) does not satisfy a
    /// later candidate. In-memory, consumed by the single live evaluation.
    /// </summary>
    public string? ProducedRevisionId { get; set; }
    public string? RanRevisionId { get; set; }

    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? SkippedAt { get; set; }
    public double? ElapsedSeconds { get; set; }

    // Scheduler lifecycle metadata (schema v7).
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public string? FailureReason { get; set; }
    public string? FailureType { get; set; }
    public string? SkippedReason { get; set; }
    public string? BlockedReason { get; set; }

    /// <summary>Deep clone for the locked mission snapshot ants receive (pydantic_deep_copy).</summary>
    public Task DeepCopy() => new()
    {
        Id = Id, Title = Title, Description = Description, AssignedAnt = AssignedAnt, AssignedWorker = AssignedWorker, TaskType = TaskType,
        ParentTaskId = ParentTaskId, ParentTaskIds = new List<string>(ParentTaskIds), DependsOn = new List<string>(DependsOn),
        Critical = Critical, Status = Status, Result = Result, ResultSummary = ResultSummary, ResultChars = ResultChars,
        EstimatedTokens = EstimatedTokens, CreatedAt = CreatedAt, StartedAt = StartedAt, FinishedAt = FinishedAt,
        CompletedAt = CompletedAt, FailedAt = FailedAt, SkippedAt = SkippedAt, ElapsedSeconds = ElapsedSeconds,
        AttemptCount = AttemptCount, MaxAttempts = MaxAttempts, FailureReason = FailureReason, FailureType = FailureType,
        SkippedReason = SkippedReason, BlockedReason = BlockedReason,
        ProducedRevisionId = ProducedRevisionId, RanRevisionId = RanRevisionId,
        // v0.3.8.57. The ant receives a DeepCopy, and ArtifactContext reads the inputs off the
        // copy -- omitting this line would leave the field set on the original and empty on the
        // only object the worker ever sees, which is a silent fall back to mission-wide context.
        InputArtifactIds = new List<string>(InputArtifactIds),
    };
}

/// <summary>A Mission is the user request as understood by the Queen: a task path that is executed, verified, scored, and saved.</summary>
public sealed class Mission
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Goal { get; set; } = "";
    public List<Task> Tasks { get; set; } = new();
    public MissionStatus Status { get; set; } = MissionStatus.Created;
    public string? UserResult { get; set; }
    public string? DebugResult { get; set; }
    public string? FinalResult { get; set; }
    public string? BestOutputTaskId { get; set; }
    public double? SuccessScore { get; set; }
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();

    public Mission DeepCopy() => new()
    {
        Id = Id, Goal = Goal, Tasks = Tasks.Select(t => t.DeepCopy()).ToList(), Status = Status,
        UserResult = UserResult, DebugResult = DebugResult, FinalResult = FinalResult,
        BestOutputTaskId = BestOutputTaskId, SuccessScore = SuccessScore, CreatedAt = CreatedAt,
    };
}

/// <summary>Events are the observable activity stream a live UI renders. The colony's visibility layer rides on these.</summary>
public sealed class Event
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MissionId { get; set; } = "";
    public string? TaskId { get; set; }
    public string? AntName { get; set; }
    public string EventType { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class AgentMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MissionId { get; set; } = "";
    public string? TaskId { get; set; }
    public string Sender { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentChars { get; set; }
    public int EstimatedTokens { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public string SchemaVersion { get; set; } = AnthillRuntime.AgentMessageVersion;
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class SearchResult
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Source { get; set; } = "web";
}

public sealed class SourceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MissionId { get; set; } = "";
    public string? TaskId { get; set; }
    public string? AntName { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Provider { get; set; } = AnthillRuntime.WebSearchProvider;
    public double RelevanceScore { get; set; }
    public double FreshnessScore { get; set; }
    public double AuthorityScore { get; set; }
    public double ConfidenceScore { get; set; }
    public string ConfidenceLabel { get; set; } = "unknown";
    public string QualityNotes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class PatchProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = "";
    public PatchChangeType ChangeType { get; set; } = PatchChangeType.Modify;
    public string Reason { get; set; } = "";
    public string Risk { get; set; } = "";
    public string? OldContent { get; set; }
    public string? NewContent { get; set; }

    /// <summary>
    /// What the target file hashed to when this patch was built. v0.3.8.37.
    ///
    /// AUTONOMY-10 Phase 1's largest gap: `old_content` matching proves the FRAGMENT is still there
    /// and says nothing about whether the rest of the file moved on underneath it. A patch produced
    /// from a stale read then applies silently into a file the coder never saw.
    ///
    /// Null means "the producer recorded no base", which is how every proposal written before this
    /// release looks. Those still apply — see `PatchApply.Compute` — because refusing them all would
    /// turn a safety improvement into an outage.
    /// </summary>
    public string? BaseHash { get; set; }

    /// <summary>
    /// Where a RENAME moves the file to, workspace-relative like <see cref="FilePath"/>. v0.3.8.52.
    ///
    /// Null for every other change type, and null for every proposal written before this release —
    /// which is why the column is additive and nullable rather than required. A rename that reaches
    /// the applier without one is refused there (<c>PatchApply.RefusedMissingDestination</c>) rather
    /// than defaulted to anything: there is no safe guess about where a file was meant to go.
    /// </summary>
    public string? DestinationPath { get; set; }

    public bool RequiresApproval { get; set; } = true;
    public PatchStatus Status { get; set; } = PatchStatus.Proposed;
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class PatchSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MissionId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<PatchProposal> Proposals { get; set; } = new();
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class ApprovalRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MissionId { get; set; } = "";
    public string? TaskId { get; set; }
    public ApprovalActionType ActionType { get; set; } = ApprovalActionType.PatchProposal;
    public string TargetId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string RequestedBy { get; set; } = "queen";
    public string? DecisionNote { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
    public DateTime? DecidedAt { get; set; }
}

public sealed class SelfTestCheck
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, object?> Details { get; set; } = new();
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
}

public sealed class SelfTestReport
{
    public string SchemaVersion { get; set; } = AnthillRuntime.SelfTestSchemaVersion;
    public string Version { get; set; } = AnthillRuntime.Version;
    public bool Ok { get; set; }
    public int ChecksPassed { get; set; }
    public int ChecksFailed { get; set; }
    public int ChecksWarning { get; set; }
    public List<SelfTestCheck> Checks { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = AnthillTime.NowUtc();
}
