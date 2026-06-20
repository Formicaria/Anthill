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
    public string TaskType { get; set; } = "general";
    public string? ParentTaskId { get; set; }
    public List<string> ParentTaskIds { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public string? Result { get; set; }
    public string? ResultSummary { get; set; }
    public int ResultChars { get; set; }
    public int EstimatedTokens { get; set; }

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
        Id = Id, Title = Title, Description = Description, AssignedAnt = AssignedAnt, TaskType = TaskType,
        ParentTaskId = ParentTaskId, ParentTaskIds = new List<string>(ParentTaskIds), DependsOn = new List<string>(DependsOn),
        Status = Status, Result = Result, ResultSummary = ResultSummary, ResultChars = ResultChars,
        EstimatedTokens = EstimatedTokens, CreatedAt = CreatedAt, StartedAt = StartedAt, FinishedAt = FinishedAt,
        CompletedAt = CompletedAt, FailedAt = FailedAt, SkippedAt = SkippedAt, ElapsedSeconds = ElapsedSeconds,
        AttemptCount = AttemptCount, MaxAttempts = MaxAttempts, FailureReason = FailureReason, FailureType = FailureType,
        SkippedReason = SkippedReason, BlockedReason = BlockedReason,
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

public sealed class ToolResult
{
    public string ToolName { get; set; } = "";
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }

    public ToolResult() { }
    public ToolResult(string toolName, bool success, string output, string? error = null)
    {
        ToolName = toolName; Success = success; Output = output; Error = error;
    }
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
