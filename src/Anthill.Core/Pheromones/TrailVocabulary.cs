namespace Anthill.Core.Pheromones;

/// <summary>
/// What a trail IS, as a closed vocabulary rather than a free string. Stage E, v3.8.29.
///
/// <c>trail_type</c> has been an arbitrary string since the pheromone layer was written. Twelve
/// distinct values are in use — `ant`, `worker`, `task_type`, `planner_pattern`, `worker_pattern`,
/// `task_pattern`, `capability`, `tool`, `source_domain`, `external_research_tool`,
/// `procedural_route`, `skill` — and nothing declares them, so nothing can check them. A typo
/// creates a new trail category silently, and a reader has no way to know whether `tool` and
/// `external_research_tool` are the same kind of claim.
///
/// Naming them is the prerequisite for the thing the plan actually wants: trails keyed by what they
/// are ABOUT, so a worker's reputation can be distinguished from a tool's reliability and from a
/// route's history. Those are three different questions and they have been sharing a column.
/// </summary>
public static class TrailKind
{
    // ---- what a ROLE or WORKER did ----------------------------------------------------------
    /// <summary>A role's own track record. Subject: the role.</summary>
    public const string Ant = "ant";
    /// <summary>A specific worker's track record. Subject: the worker.</summary>
    public const string Worker = "worker";

    // ---- what a SHAPE OF WORK tends to do ---------------------------------------------------
    public const string TaskType = "task_type";
    public const string PlannerPattern = "planner_pattern";
    public const string WorkerPattern = "worker_pattern";
    public const string TaskPattern = "task_pattern";
    public const string ProceduralRoute = "procedural_route";
    public const string Skill = "skill";

    // ---- what the ENVIRONMENT provides -------------------------------------------------------
    /// <summary>A capability the colony has, not a judgment about anyone using it.</summary>
    public const string Capability = "capability";
    public const string Tool = "tool";
    public const string ExternalResearchTool = "external_research_tool";
    public const string SourceDomain = "source_domain";

    /// <summary>Every kind this build recognises.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Ant, Worker, TaskType, PlannerPattern, WorkerPattern, TaskPattern,
            ProceduralRoute, Skill, Capability, Tool, ExternalResearchTool, SourceDomain,
        };

    /// <summary>
    /// The kinds whose subject is a WORKER OR ROLE — the only ones that constitute reputation.
    ///
    /// The distinction this vocabulary exists to make. A failing tool and a failing worker are
    /// different facts with different remedies, and a `worker_pattern` trail is about a SEQUENCE
    /// rather than about any one worker in it, so it does not belong here either.
    /// </summary>
    public static readonly IReadOnlySet<string> Reputation =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Ant, Worker };

    /// <summary>
    /// The kinds whose subject is the ENVIRONMENT. A failure here says nothing about whoever was
    /// holding the task — which is the rule <see cref="LearningAttribution"/> enforces on the other
    /// side, and this is the same boundary expressed as data.
    /// </summary>
    public static readonly IReadOnlySet<string> Environmental =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Capability, Tool, ExternalResearchTool, SourceDomain };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    public static bool IsReputation(string? kind) => kind is not null && Reputation.Contains(kind);
}

/// <summary>
/// A worker's or role's standing, derived from its trails. Stage E, v3.8.29.
///
/// The plan has said "the workers table has six columns and none is a score" since it was written.
/// This does not add a seventh column, deliberately: a stored score is a second source of truth that
/// drifts from the trails it was computed from, and the trails are already durable, already decayed
/// toward neutral, and already attributed correctly as of v3.8.26.
///
/// It is DERIVED instead — computed on read from the trail that already exists. There is exactly one
/// place the answer lives, which is the property every other "one canonical X" decision in this
/// codebase has been protecting.
/// </summary>
public sealed record Reputation(
    string Subject,
    double Strength,
    int Successes,
    int Failures,
    bool Established)
{
    /// <summary>Observations behind the score. A reputation is only as good as its sample.</summary>
    public int Observations => Successes + Failures;

    /// <summary>
    /// Neutral is 0.5 — the value decay pulls trails toward. A subject with no history is neutral,
    /// NOT bad: "we have never seen this role work" and "this role works badly" are different facts,
    /// and conflating them is how a newly-enabled specialist would be routed away from forever.
    /// </summary>
    public static Reputation Unknown(string subject) => new(subject, 0.5, 0, 0, Established: false);

    /// <summary>
    /// Compute from a trail row's strength and counts.
    /// </summary>
    /// <param name="minObservations">Below this the reputation is reported but NOT established, and
    /// a caller must not route on it. Matches the floor planning already applies — one mission is an
    /// anecdote, and a trail written once sits at whatever that run produced.</param>
    public static Reputation From(string subject, double strength, int successes, int failures,
        int minObservations = 3) =>
        new(subject, strength, successes, failures, Established: successes + failures >= minObservations);
}
