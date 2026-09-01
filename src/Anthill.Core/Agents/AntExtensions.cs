namespace Anthill.Core.Agents;

/// <summary>
/// ONE ROLE, DECLARED ONCE, REACHING EVERY TABLE THAT DECIDES WHETHER IT RUNS. v0.3.8.108.
///
/// WHAT THIS CLOSES, and it is the claim the capability tables have been making implicitly since the
/// roster was written. "Extensible" was never true: adding a role meant editing FOUR statics in the
/// core, one of them inside the Queen's constructor —
///
/// <list type="bullet">
/// <item><see cref="AntRegistry"/>'s <c>BuildRoles()</c> list — who exists;</item>
/// <item><see cref="AntExecutionCatalog"/>'s <c>Kinds</c> — what KIND of thing it is;</item>
/// <item><see cref="AntExecutionCatalog"/>'s <c>Contracts</c> — what it may do;</item>
/// <item>and <c>Queen._ants</c>, a dictionary literal — what actually executes it.</item>
/// </list>
///
/// The last one is the sharp end. `.108`'s exit gate asks for an ant that registers "with no change
/// to Queen, planner, scheduler or assembler", and a role could not exist at all without changing
/// the Queen. Every other layer was already generic over roles — the planner reads the registry, the
/// scheduler reads the task graph, the assembler reads the ledger — so the orchestration was never
/// the obstacle. One dictionary literal was.
///
/// THIS IS A DECLARATION SURFACE, NOT A PLUGIN LOADER. A contribution is four facts the core already
/// requires of every built-in role, supplied together so they cannot be supplied apart — which is
/// the actual defect, since a role declared in three tables and missing from the fourth is a role
/// that exists, plans, dispatches and then fails at execution with "no ant found".
///
/// IT DOES NOT CROSS THE MODULE BOUNDARY YET, and that is named rather than implied.
/// <c>IModuleContext</c> can contribute reasoning providers, capability probes and tools, and not
/// ants — because <see cref="BaseAnt"/> and <see cref="AntExecutionResult"/> live in
/// <c>Anthill.Core</c>, which a module may not reference. That is precisely the position
/// <c>RegisterTool</c> was in before v3.8.10, and its own remarks say what was done about it: the
/// type moved to the SDK first, and the method followed. The same move for the ant contract is a
/// release of its own; this one makes the CORE composable, which is what the gate asks for and what
/// that move would need underneath it anyway.
///
/// EMPTY IN PRODUCTION. Nothing contributes here on a shipped colony, so the roster is exactly the
/// twenty-five built-in roles and every count in the documentation stays true. A contribution is
/// additive and scoped by the contributor's own lifetime.
/// </summary>
public static class AntExtensions
{
    /// <summary>
    /// A role and everything the core needs to run it, supplied together.
    /// </summary>
    /// <param name="Role">Who it is — the registry entry.</param>
    /// <param name="Kind">What kind of thing it is, for the execution catalog.</param>
    /// <param name="Contract">What it may do — task types, tools, model calls.</param>
    /// <param name="Executor">
    /// What actually runs it. A FACTORY rather than an instance: the built-in ants are constructed
    /// with the Queen's memory, tools and router, so a contributed one has to be built at the same
    /// moment with the same collaborators rather than handed over pre-wired against something else.
    /// </param>
    public sealed record Contribution(
        AntRoleDefinition Role,
        AntRuntimeKind Kind,
        AntExecutionContract Contract,
        Func<AntExecutionDependencies, BaseAnt> Executor);

    private static readonly object Lock = new();
    private static readonly List<Contribution> Contributions = new();

    /// <summary>
    /// Bumped on every change. The static tables cache their composed view and rebuild when this
    /// moves — so the common path is a cached read and a contribution is never missed by a table
    /// that had already computed itself.
    /// </summary>
    public static int Version { get; private set; }

    public static IReadOnlyList<Contribution> All
    {
        get { lock (Lock) return Contributions.ToList(); }
    }

    /// <summary>
    /// Declare a role. Refuses a duplicate rather than overwriting: two contributors quietly both
    /// claiming `verifier` is not a conflict anyone notices until the wrong one runs — the rule
    /// <c>IModuleContext.RegisterTool</c> states for tools, applied to the thing that executes them.
    /// </summary>
    public static void Declare(Contribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var id = contribution.Role.RoleId;

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("a contributed role must have a RoleId.", nameof(contribution));

        if (!string.Equals(id, contribution.Contract.RoleId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"the contributed role is '{id}' and its execution contract is for "
              + $"'{contribution.Contract.RoleId}'. A role whose contract names something else would "
              + "be authorized against a different role's permissions.", nameof(contribution));

        lock (Lock)
        {
            if (BuiltInRoleIds.Contains(id))
                throw new InvalidOperationException(
                    $"'{id}' is a built-in role and cannot be contributed over. Shadowing one would "
                  + "make the colony's behaviour depend on load order.");

            if (Contributions.Any(c => string.Equals(c.Role.RoleId, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"role '{id}' has already been contributed.");

            Contributions.Add(contribution);
            Version++;
        }
    }

    /// <summary>
    /// Remove every contribution. A TEST SEAM, and named as one: a contributed role that outlived
    /// the thing that contributed it would leak into unrelated tests as a twenty-sixth role, and the
    /// count assertions would start failing somewhere with no relationship to the cause.
    /// </summary>
    public static void Reset()
    {
        lock (Lock)
        {
            if (Contributions.Count == 0) return;
            Contributions.Clear();
            Version++;
        }
    }

    /// <summary>
    /// The built-in role ids, captured once from the registry's own list. Read through a lazy so
    /// this type can be referenced from <see cref="AntRegistry"/>'s static initialisation without
    /// the two deadlocking on each other's type initialiser.
    /// </summary>
    private static IReadOnlySet<string> BuiltInRoleIds => BuiltIn.Value;

    private static readonly Lazy<IReadOnlySet<string>> BuiltIn = new(() =>
        AntRegistry.BuiltInRoles.Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// What the Queen hands a contributed ant at construction. v0.3.8.108.
///
/// The same collaborators the built-in ants take, named rather than passed positionally, so adding
/// one later does not silently reorder somebody's factory. A contributed ant is built at the moment
/// the built-ins are, with the same memory, tools and router — anything else would be an ant
/// operating on a different colony than the one dispatching to it.
/// </summary>
/// <param name="Memory">The colony store. Also the artifact and evidence store, by interface.</param>
/// <param name="Tools">The dispatch chokepoint — authorization, the authority ceiling, evidence.</param>
/// <param name="Router">Model routing, or null on a colony with no reasoning provider.</param>
public sealed record AntExecutionDependencies(
    Memory.SqliteMemory Memory,
    Tools.ToolRegistry Tools,
    Models.ModelRouter? Router);
