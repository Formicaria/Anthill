namespace Anthill.SDK.Memory;

/// <summary>One pheromone trail, as the colony reads it back.</summary>
/// <param name="TrailKey">Identity of the path being reinforced.</param>
/// <param name="TrailType">What kind of path — routing preference, procedural learning,
/// reliability signal. Determines whether the trail is allowed to steer planning at all.</param>
/// <param name="Strength">Clamped to [0,1].</param>
public sealed record PheromoneTrail(
    string TrailKey,
    string TrailType,
    double Strength,
    int SuccessCount,
    int FailureCount,
    DateTime LastUpdated)
{
    public int NetCount => SuccessCount - FailureCount;
    public int TotalCount => SuccessCount + FailureCount;

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Retained-for-reporting under the learning-reset contract. Never pruned.</summary>
    public bool Legacy { get; init; }
}

/// <summary>
/// Store, retrieve, weight and decay — the pheromone memory contract.
///
/// This is a core responsibility and stays in the core. It is declared here in the SDK for one
/// reason: modules will want to reinforce trails (a homelab action that worked, a provider that
/// answered reliably) and read them, and the alternative is handing modules the concrete
/// <c>SqliteMemory</c> — a class with 177 public methods spanning jobs, users, workspaces, shadow
/// runs and credentials. Passing that across the module boundary would make every module able to
/// reach anything, and the boundary would be decorative.
///
/// The method shapes are taken from the six pheromone methods on <c>SqliteMemory</c> as they exist
/// today, so implementing this is a delegation rather than a rewrite.
/// </summary>
public interface IPheromoneMemory
{
    /// <summary>
    /// Lay down or strengthen a trail. <paramref name="strengthDelta"/> may be negative; the
    /// implementation clamps the result to [0,1].
    /// </summary>
    void Reinforce(string trailKey, string trailType, bool success, double strengthDelta,
        IReadOnlyDictionary<string, object?>? metadata = null);

    /// <summary>
    /// The trails allowed to influence planning — strongest first.
    ///
    /// Note this is NOT simply the top of <see cref="ListAll"/>. Only learning-bearing categories
    /// qualify; operational telemetry and heuristic source-quality signals are recorded but never
    /// steer strategy. A module reading trails to make a decision wants this one.
    /// </summary>
    IReadOnlyList<PheromoneTrail> Top(int limit = 10);

    /// <summary>
    /// Every trail, strongest first — including categories excluded from planning. For display and
    /// diagnosis, not for decisions.
    /// </summary>
    IReadOnlyList<PheromoneTrail> ListAll(int limit = 300);

    /// <summary>
    /// Drop trails that have proven unusable: too weak, or failure-dominant and not strongly
    /// reinforced. Returns the number removed. Legacy trails are never removed.
    /// </summary>
    int Prune(double minStrength = 0.15, bool dropFailureDominant = true);
}
