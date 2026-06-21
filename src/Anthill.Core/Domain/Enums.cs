namespace Anthill.Core.Domain;

/// <summary>
/// Lifecycle and domain enums. Each carries an explicit lowercase wire string via
/// <see cref="EnumExtensions"/> so persisted/serialised values are byte-identical to
/// the Python <c>str, Enum</c> members the database and API contracts already use.
/// </summary>
public enum TaskStatus
{
    Pending,
    Ready,
    Blocked,
    Running,
    Complete,
    Failed,
    Skipped,
    // Reserved for a future explicit cancellation pathway; not emitted by scheduling flows.
    Cancelled,
}

public enum MissionStatus { Created, Running, Complete, Partial, Failed }

public enum PatchChangeType { Add, Modify, Delete, Rename }

public enum PatchStatus { Proposed, Approved, Rejected, Applied, Failed }

public enum ApprovalStatus { Pending, Approved, Rejected, Expired, Consumed }

public enum ApprovalActionType { PatchProposal, FileWrite, ShellCommand, ToolUse }

/// <summary>Lifecycle of an autonomous objective in the Director backlog (Phase 0+).</summary>
public enum ObjectiveStatus { Pending, Active, Paused, Done, Failed }

public static class EnumExtensions
{
    public static string Value(this TaskStatus s) => s switch
    {
        TaskStatus.Pending => "pending", TaskStatus.Ready => "ready", TaskStatus.Blocked => "blocked",
        TaskStatus.Running => "running", TaskStatus.Complete => "complete", TaskStatus.Failed => "failed",
        TaskStatus.Skipped => "skipped", TaskStatus.Cancelled => "cancelled", _ => "pending",
    };

    public static string Value(this MissionStatus s) => s switch
    {
        MissionStatus.Created => "created", MissionStatus.Running => "running", MissionStatus.Complete => "complete",
        MissionStatus.Partial => "partial", MissionStatus.Failed => "failed", _ => "created",
    };

    public static string Value(this PatchChangeType s) => s switch
    {
        PatchChangeType.Add => "add", PatchChangeType.Modify => "modify",
        PatchChangeType.Delete => "delete", PatchChangeType.Rename => "rename", _ => "modify",
    };

    public static string Value(this PatchStatus s) => s switch
    {
        PatchStatus.Proposed => "proposed", PatchStatus.Approved => "approved", PatchStatus.Rejected => "rejected",
        PatchStatus.Applied => "applied", PatchStatus.Failed => "failed", _ => "proposed",
    };

    public static string Value(this ApprovalStatus s) => s switch
    {
        ApprovalStatus.Pending => "pending", ApprovalStatus.Approved => "approved", ApprovalStatus.Rejected => "rejected",
        ApprovalStatus.Expired => "expired", ApprovalStatus.Consumed => "consumed", _ => "pending",
    };

    public static string Value(this ApprovalActionType s) => s switch
    {
        ApprovalActionType.PatchProposal => "patch_proposal", ApprovalActionType.FileWrite => "file_write",
        ApprovalActionType.ShellCommand => "shell_command", ApprovalActionType.ToolUse => "tool_use", _ => "tool_use",
    };

    public static string Value(this ObjectiveStatus s) => s switch
    {
        ObjectiveStatus.Pending => "pending", ObjectiveStatus.Active => "active", ObjectiveStatus.Paused => "paused",
        ObjectiveStatus.Done => "done", ObjectiveStatus.Failed => "failed", _ => "pending",
    };

    public static ObjectiveStatus ParseObjectiveStatus(string value) => value switch
    {
        "pending" => ObjectiveStatus.Pending, "active" => ObjectiveStatus.Active, "paused" => ObjectiveStatus.Paused,
        "done" => ObjectiveStatus.Done, "failed" => ObjectiveStatus.Failed, _ => ObjectiveStatus.Pending,
    };

    public static TaskStatus ParseTaskStatus(string value) => value switch
    {
        "pending" => TaskStatus.Pending, "ready" => TaskStatus.Ready, "blocked" => TaskStatus.Blocked,
        "running" => TaskStatus.Running, "complete" => TaskStatus.Complete, "failed" => TaskStatus.Failed,
        "skipped" => TaskStatus.Skipped, "cancelled" => TaskStatus.Cancelled, _ => TaskStatus.Pending,
    };

    public static PatchChangeType ParsePatchChangeType(string value) => value switch
    {
        "add" => PatchChangeType.Add, "modify" => PatchChangeType.Modify,
        "delete" => PatchChangeType.Delete, "rename" => PatchChangeType.Rename, _ => PatchChangeType.Modify,
    };
}
