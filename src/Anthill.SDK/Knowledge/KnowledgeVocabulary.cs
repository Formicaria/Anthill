namespace Anthill.SDK.Knowledge;

// The shared vocabulary of the knowledge boundary: what a statement's support level means, what
// state it is in, how far it may travel, and what the tools that fetch it are called.
//
// In the SDK because both sides need it and neither may reference the other. The core's
// authorization tables key on the tool NAMES; the knowledge module REGISTERS by them. Two
// spellings of one boundary eventually disagree about where the boundary is — the same argument
// SystemActionToolNames and ExternalActionToolNames already make, for the same reason.
//
// Everything here mirrors a distinction FORAGER already draws and persists. That is deliberate and
// it is the whole design: ANTHILL MAPS these values, it never invents or upgrades one. A retrieval
// layer that can promote an unverified claim to a fact is a retrieval layer that can lie.

/// <summary>
/// How well-supported a statement is, as FORAGER classified it at extraction time.
///
/// ORDERED BY STRENGTH, and the order is load-bearing: context assembly sorts on it so the model
/// meets the best-supported material first, and a caller may filter with a minimum. Do not reorder.
/// </summary>
public enum KnowledgeSupport
{
    /// <summary>
    /// Unknown or unmappable. NOT a default-to-safe value — it is the honest answer when FORAGER
    /// sent a support level this build does not recognise, which happens when FORAGER is newer than
    /// ANTHILL. Rendered as UNKNOWN and never treated as a fact.
    /// </summary>
    Unknown = 0,

    /// <summary>The source states it directly. The excerpt says the thing.</summary>
    DirectFact,

    /// <summary>Inferred, but the inference is carried by the evidence.</summary>
    SupportedInference,

    /// <summary>Inferred with a gap the evidence does not close.</summary>
    UncertainInference,

    /// <summary>
    /// Asserted somewhere, supported by nothing. Hedged and reported speech lands here.
    /// FORAGER's own consumption rules say not to act on one of these without checking the source;
    /// the assembled context repeats that instruction rather than assuming the model knows it.
    /// </summary>
    UnverifiedClaim,
}

/// <summary>
/// Where a statement sits in its own history. FORAGER never deletes and never silently collapses,
/// so these states are the mechanism by which "what did we believe in March" stays answerable.
///
/// The integration's job is to carry them across intact. Flattening <c>Superseded</c> into absence,
/// or <c>Disputed</c> into <c>Active</c>, would destroy temporal reasoning at the boundary — which
/// is exactly the failure the canonical store was built to prevent.
/// </summary>
public enum KnowledgeStatus
{
    Unknown = 0,

    /// <summary>Current and uncontested.</summary>
    Active,

    /// <summary>Replaced by a later statement, which <c>SupersededBy</c> names. Still true of its time.</summary>
    Superseded,

    /// <summary>In an open conflict. Something else asserts otherwise and nobody has decided.</summary>
    Disputed,

    /// <summary>Its evidence no longer resolves — the source was removed, or the excerpt cannot be located.</summary>
    Unresolved,

    /// <summary>Old enough that FORAGER flagged it. Not wrong; unrefreshed.</summary>
    Stale,

    /// <summary>Retired by a reviewer. Retained for audit, excluded from retrieval by default.</summary>
    Archived,
}

/// <summary>
/// The confidentiality band FORAGER assigns, and the one thing in this file that is a HARD RULE
/// rather than a description.
///
/// FORAGER already separates these into different files in its export and states the rule in its
/// package manifest: tenant material must not be merged into shared or global memory. ANTHILL
/// honours it at every layer — retrieval, context assembly, memory write-back and caching.
/// </summary>
public enum KnowledgeConfidentiality
{
    /// <summary>
    /// Unknown provenance band. Treated as <see cref="Tenant"/> everywhere a decision is required.
    /// Failing closed is the only safe reading: the cost of over-restricting a general lesson is an
    /// operator re-running an export; the cost of under-restricting a customer fact is a leak.
    /// </summary>
    Unknown = 0,

    /// <summary>Customer- and project-identifying. Mission or project scope ONLY. Never global, never shared.</summary>
    Tenant,

    /// <summary>Generalized operational learning naming no customer. Safe for shared operational memory.</summary>
    General,
}

/// <summary>
/// How far a query may reach. Resolution is most-specific-wins:
/// <c>Mission -&gt; Project -&gt; Workspace -&gt; Global</c>.
///
/// There is deliberately NO "all projects" member. Cross-project retrieval is not a scope this
/// build can express, so it cannot be reached by a bug, a misconfiguration, or a model that asks
/// nicely. If it is ever wanted it needs its own permission and its own audit lane — see
/// docs/FORAGER_INTEGRATION.md §12.
/// </summary>
public enum KnowledgeScopeKind
{
    /// <summary>No scope could be resolved. Not a wildcard — a refusal. Nothing is queryable.</summary>
    None = 0,

    /// <summary>Knowledge the operator marked as colony-wide. General-band material only.</summary>
    Global,

    /// <summary>Everything reachable from one workspace.</summary>
    Workspace,

    /// <summary>One project's knowledge base. The common case.</summary>
    Project,

    /// <summary>One mission's view of a project, narrowed further by the mission's own scope.</summary>
    Mission,
}

/// <summary>
/// A resolved knowledge scope: the answer to "which knowledge may THIS caller see".
///
/// Resolved ONCE, at intake, and passed explicitly — the same discipline <c>MissionContext</c>
/// follows and for the same reason. An ambient scope is a scope that widens when nobody is looking.
///
/// <see cref="ProjectRef"/> is FORAGER's project id. It is required for every real query: the
/// FORAGER endpoints that matter are all project-rooted, so a provider physically cannot construct
/// an unscoped search. That is the containment, and it is structural rather than checked.
/// </summary>
public sealed record KnowledgeScope
{
    public required KnowledgeScopeKind Kind { get; init; }

    /// <summary>The FORAGER project id this scope resolves to. Null only when <see cref="Kind"/> is None.</summary>
    public string? ProjectRef { get; init; }

    /// <summary>The ANTHILL project id, when the scope came from one. For audit correlation.</summary>
    public string? AnthillProjectId { get; init; }

    public string? WorkspaceId { get; init; }
    public string? MissionId { get; init; }

    /// <summary>
    /// Whether material in <paramref name="band"/> may be retrieved into this scope.
    ///
    /// THE RULE LIVES HERE, as a method, and not as a comparison at each call site. It was written
    /// once as "band &lt;= scope.Maximum" and that was WRONG in a way worth recording: the enum is
    /// ordered by declaration, not by sensitivity — <c>Tenant</c> is 1 and <c>General</c> is 2 — so
    /// the comparison silently admitted tenant material into a global scope, which is the exact leak
    /// the band exists to prevent. Any ordering-based check on this enum is a bug waiting for a
    /// reader who assumes the numbers mean something.
    ///
    /// The rule, stated plainly:
    ///   - General material is safe everywhere. It names no customer.
    ///   - Tenant material — and anything unrecognised, which maps to Tenant — requires a scope
    ///     narrower than global. Project and mission qualify; global never does.
    /// </summary>
    public bool Allows(KnowledgeConfidentiality band) =>
        band == KnowledgeConfidentiality.General || Kind != KnowledgeScopeKind.Global;

    /// <summary>Whether this scope can be queried at all. <c>None</c>, or a missing project, cannot.</summary>
    public bool IsQueryable => Kind != KnowledgeScopeKind.None && !string.IsNullOrWhiteSpace(ProjectRef);

    /// <summary>The refusal. Nothing is retrievable and the caller is told why rather than getting an empty result.</summary>
    public static readonly KnowledgeScope Unresolved = new() { Kind = KnowledgeScopeKind.None };

    public static KnowledgeScope ForProject(string foragerProjectId, string? anthillProjectId = null) =>
        new()
        {
            Kind = KnowledgeScopeKind.Project,
            ProjectRef = foragerProjectId,
            AnthillProjectId = anthillProjectId,
        };

    public static KnowledgeScope ForMission(string foragerProjectId, string missionId, string? anthillProjectId = null) =>
        new()
        {
            Kind = KnowledgeScopeKind.Mission,
            ProjectRef = foragerProjectId,
            MissionId = missionId,
            AnthillProjectId = anthillProjectId,
        };

    /// <summary>A stable cache discriminator. Two different scopes must never share a cache entry.</summary>
    public string CacheKey => $"{Kind}:{ProjectRef ?? "-"}:{WorkspaceId ?? "-"}:{MissionId ?? "-"}";

    public override string ToString() => IsQueryable
        ? $"{Kind.ToString().ToLowerInvariant()} {ProjectRef}"
        : "unresolved";
}

/// <summary>
/// The knowledge tool names, spelled once.
///
/// Read-only names and mutating names are separated into two classes rather than distinguished by a
/// prefix convention, because the core's authorization tables and the console's permission gate
/// both have to answer "is this one of the dangerous ones" and a convention is not something a
/// compiler can check. <see cref="Mutating"/> is the enumerated register; a name that is not in it
/// is read-only by construction.
/// </summary>
public static class KnowledgeToolNames
{
    /// <summary>Ranked candidates for a query. Read-only.</summary>
    public const string Search = "knowledge_search";

    /// <summary>Assemble evidence-backed context for a query. Read-only, and the main retrieval path.</summary>
    public const string Retrieve = "knowledge_retrieve";

    /// <summary>One knowledge item by id, with its evidence and conflicts. Read-only.</summary>
    public const string Get = "knowledge_get";

    /// <summary>Why the colony believes an item — its evidence links. Read-only.</summary>
    public const string Evidence = "knowledge_evidence";

    /// <summary>An entity, its aliases and what mentions it. Read-only.</summary>
    public const string Entity = "knowledge_entity";

    /// <summary>
    /// Propose a review action on an item. Writes a PROPOSAL into ANTHILL's approval pipeline; it
    /// does not touch FORAGER. Applying it is an operator act, which is the whole point of Rule 8.
    /// </summary>
    public const string Review = "knowledge_review";

    /// <summary>
    /// Every name in this vocabulary. Used by the tool inventory guard so a tool that exists but is
    /// unregistered — or registered but not declared — fails the build rather than the mission.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Search, Retrieve, Get, Evidence, Entity, Review };

    /// <summary>
    /// The names that are NOT read-only. Enumerated, not derived from a prefix, so adding a
    /// mutating tool without deciding it is mutating is not possible.
    /// </summary>
    public static readonly IReadOnlySet<string> Mutating =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Review };

    public static bool IsReadOnly(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !Mutating.Contains(name);
}

/// <summary>
/// The permission names the console's routes gate on. Two, not one: reading organizational
/// knowledge and administering it are different acts with different blast radii, and a build that
/// cannot tell them apart cannot give an analyst read access without also giving them the ability
/// to resolve a conflict.
/// </summary>
public static class KnowledgePermissions
{
    public const string Read = "read_knowledge";
    public const string Manage = "manage_knowledge";
}
