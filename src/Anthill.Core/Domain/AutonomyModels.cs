using Anthill.Core.Common;

namespace Anthill.Core.Domain;

/// <summary>
/// A standing objective in the autonomous Director's backlog. The Director works the
/// highest-priority ready objective each cycle, turning its charter into concrete missions.
/// Phase 0 only defines and persists objectives; the loop that consumes them arrives in Phase 1.
/// </summary>
public sealed class Objective
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    /// <summary>The standing goal text the Strategist expands into concrete missions.</summary>
    public string Charter { get; set; } = "";
    /// <summary>Higher runs first. Ties broken by created_at (oldest first).</summary>
    public int Priority { get; set; } = 0;
    public ObjectiveStatus Status { get; set; } = ObjectiveStatus.Pending;
    /// <summary>0 = unlimited. Otherwise the Director retires the objective after this many missions.</summary>
    public int MaxRuns { get; set; } = 0;
    public int RunCount { get; set; }
    /// <summary>Consecutive autonomous failures; the circuit breaker pauses the objective when this trips.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Set when this objective was enqueued as a follow-up discovered by another objective's work.</summary>
    public string? ParentObjectiveId { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = AnthillTime.NowUtc();
    public DateTime? LastRunAt { get; set; }
}

/// <summary>
/// One audit record per autonomous mission the Director launches: which objective drove it,
/// the goal the Strategist generated, the resulting mission id, and the outcome. Fully
/// replayable so a human can reconstruct exactly what the colony did unattended.
/// </summary>
public sealed class AutonomyRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ObjectiveId { get; set; } = "";
    public string? MissionId { get; set; }
    public string GeneratedGoal { get; set; } = "";
    public string MissionStatus { get; set; } = "";
    public double? SuccessScore { get; set; }
    public int FollowUpsCreated { get; set; }
    public string? Notes { get; set; }
    public DateTime StartedAt { get; set; } = AnthillTime.NowUtc();
    public DateTime? FinishedAt { get; set; }
}
