using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Missions;

namespace Anthill.Core.Orchestration;

/// <summary>
/// The ceilings a mission may not exceed. Ceilings only — consumption is tracked in execution
/// state, deliberately not here. "Just update the budget on the context" is how a bound stops
/// bounding (ADR-002 §3).
/// </summary>
/// <param name="MaxElapsedSeconds">Whole-mission wall-clock budget.</param>
/// <param name="MaxTaskSeconds">Per-task wall-clock budget.</param>
/// <param name="MaxDeltaPlans">Adaptive delta-plan generations the mission may spend.</param>
/// <param name="MaxRepairCycles">Adaptive repair cycles the mission may spend.</param>
/// <param name="MaxWebSearches">Web searches the mission may spend.</param>
/// <param name="MaxSources">Source records the mission may retain.</param>
public sealed record MissionBudgets(
    int MaxElapsedSeconds,
    int MaxTaskSeconds,
    int MaxDeltaPlans,
    int MaxRepairCycles,
    int MaxWebSearches,
    int MaxSources);

/// <summary>
/// v3.1.0 (ADR-002) — the immutable per-mission governing facts, resolved ONCE at intake.
///
/// Before this, a mission's own boundaries were re-derived wherever they were needed:
/// <c>MissionConstraints.Parse(mission.Goal)</c> at eight separate sites, the deadline as a
/// duration compared against a start time in two loops, capability grants read from mutable
/// statics at the moment of use rather than at admission. Each re-derivation is an opportunity to
/// disagree, and v2.26.0 demonstrated that they eventually do.
///
/// Three rules make this type worth its signature churn:
///
/// <list type="bullet">
/// <item><b>Resolved once.</b> Constraints are parsed at intake and never again; every later reader
/// consumes <see cref="Constraints"/>.</item>
/// <item><b>Immutable.</b> A mission's boundaries cannot widen mid-flight. The adaptive controller
/// may narrow what a mission attempts, never what it is permitted.</item>
/// <item><b>Explicit.</b> Passed as a parameter — never ambient, never a static, never
/// thread-local. An AsyncLocal context would be a smaller diff and would reproduce the exact
/// defect being removed. <c>ModelCallScope</c> remains ambient because it is the cancellation
/// mechanism, not state.</item>
/// </list>
///
/// The deadline is an absolute UTC instant rather than a duration, so a resumed mission compares
/// against the same wall-clock boundary the original run did instead of restarting its own clock.
/// </summary>
public sealed record MissionContext
{
    public required string MissionId { get; init; }

    /// <summary>Spans this mission's model calls, tool calls, events and artifacts. Distinct from
    /// <see cref="MissionId"/> so a future resumed or re-run mission can correlate to its
    /// predecessor without pretending to be it.</summary>
    public required string CorrelationId { get; init; }

    public required string Goal { get; init; }

    /// <summary>Parsed exactly once, at intake.</summary>
    public required MissionConstraints Constraints { get; init; }

    /// <summary>
    /// What the operator asked for, resolved exactly once, at intake. v0.3.8.98.
    ///
    /// Beside <see cref="Constraints"/> and for the identical reason ADR-002 gives for it: a fact
    /// about the mission that every layer needs must be resolved once and carried, or each layer
    /// re-derives it and they disagree. Constraints were re-parsed at eight sites before ADR-002;
    /// the operator's REQUEST was re-interpreted at three — the planner read it to choose roles,
    /// `ObjectiveVerification` re-read it to guess a deliverable, and `ResultAssembler` never read
    /// it at all.
    ///
    /// An unclassified request carries <see cref="MissionSpecification.General"/>, which constrains
    /// nothing and leaves every existing behaviour exactly as it was.
    /// </summary>
    public MissionSpecification Specification { get; init; } = MissionSpecification.General("");

    /// <summary>What this run may do. Resolved at admission, not at each point of use.</summary>
    public required RuntimeProfile Profile { get; init; }

    /// <summary>Absolute UTC instant the mission must stop by.</summary>
    public required DateTime Deadline { get; init; }

    public required MissionBudgets Budgets { get; init; }

    public required string EnvironmentFingerprint { get; init; }

    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// The disposable workspace this mission owns. Null until v3.3.0 ships
    /// <c>MissionWorkspaceManager</c>; declared here because ADR-002 fixes the shape of the context
    /// now, and a later phase filling in a field is cheaper than a later phase changing one.
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Convenience passthrough — the configuration snapshot behind the profile.</summary>
    public RuntimeOptions Options => Profile.Options;

    /// <summary>True when <paramref name="now"/> is at or past the absolute deadline.</summary>
    public bool IsPastDeadline(DateTime now) => now >= Deadline;

    /// <summary>Time left before the deadline; never negative.</summary>
    public TimeSpan Remaining(DateTime now) =>
        Deadline > now ? Deadline - now : TimeSpan.Zero;

    /// <summary>
    /// Build the context for a mission at intake. <paramref name="startedAt"/> is the mission's own
    /// start instant, so the deadline is anchored to when the mission actually began rather than to
    /// when this object happened to be constructed.
    /// </summary>
    public static MissionContext Create(Mission mission, RuntimeProfile profile, DateTime startedAt)
    {
        var options = profile.Options;
        return new MissionContext
        {
            MissionId = mission.Id,
            CorrelationId = mission.Id,
            Goal = mission.Goal,
            Constraints = MissionConstraints.Parse(mission.Goal),
            // Resolved here, once, for the same reason the constraints are — see the field.
            Specification = Missions.MissionIntake.Resolve(mission.Goal),
            Profile = profile,
            Deadline = startedAt.AddSeconds(options.MaxMissionSeconds),
            Budgets = new MissionBudgets(
                MaxElapsedSeconds: options.MaxMissionSeconds,
                MaxTaskSeconds: options.MaxTaskSeconds,
                MaxDeltaPlans: AdaptiveBudget.MaxReplans,
                MaxRepairCycles: AdaptiveBudget.MaxRepairCycles,
                MaxWebSearches: options.MaxWebSearchesPerMission,
                MaxSources: options.MaxSourcesPerMission),
            EnvironmentFingerprint = options.EnvironmentFingerprint,
            CreatedAt = startedAt,
        };
    }

    /// <summary>
    /// Intake convenience for callers outside the mission engine (tests, tooling, the plan
    /// preview): capture the live runtime and resolve a context in one step.
    ///
    /// Production mission execution does NOT use this — the Queen resolves its profile explicitly
    /// so the capture point is visible at the call site rather than hidden in a helper.
    /// </summary>
    public static MissionContext ForMission(Mission mission, IEnumerable<string>? registeredTools = null) =>
        Create(mission,
            RuntimeProfile.Resolve(RuntimeOptions.Capture(), registeredTools ?? Array.Empty<string>()),
            AnthillTime.NowUtc());

    /// <summary>Operator-visible projection for events and the API. Secret-free.</summary>
    public Dictionary<string, object?> Snapshot() => new()
    {
        ["mission_id"] = MissionId,
        ["correlation_id"] = CorrelationId,
        ["created_at"] = CreatedAt.ToIso(),
        ["deadline"] = Deadline.ToIso(),
        ["environment"] = EnvironmentFingerprint,
        ["workspace_id"] = WorkspaceId,
        ["specification"] = Specification.Snapshot(),
        ["constraints"] = new Dictionary<string, object?>
        {
            ["no_patches"] = Constraints.NoPatches,
            ["verification_only"] = Constraints.VerificationOnly,
            ["read_only"] = Constraints.ReadOnly,
            ["one_shot"] = Constraints.OneShot,
            ["blocks_patches"] = Constraints.BlocksPatches,
        },
        ["budgets"] = new Dictionary<string, object?>
        {
            ["max_elapsed_seconds"] = Budgets.MaxElapsedSeconds,
            ["max_task_seconds"] = Budgets.MaxTaskSeconds,
            ["max_delta_plans"] = Budgets.MaxDeltaPlans,
            ["max_repair_cycles"] = Budgets.MaxRepairCycles,
            ["max_web_searches"] = Budgets.MaxWebSearches,
            ["max_sources"] = Budgets.MaxSources,
        },
        ["profile"] = Profile.Snapshot(),
    };
}
