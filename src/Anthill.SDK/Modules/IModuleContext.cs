using Anthill.SDK.Events;
using Anthill.SDK.Memory;
using Anthill.SDK.Reasoning;
using Microsoft.Extensions.Logging;

namespace Anthill.SDK.Modules;

/// <summary>
/// Everything a module is allowed to touch, and nothing else.
///
/// This type is the module boundary made concrete, so it is worth being explicit about what is
/// deliberately absent. There is no <c>SqliteMemory</c> here, no <c>Queen</c>, no
/// <c>TaskScheduler</c>. A module cannot enqueue a task, cannot alter a mission plan, and cannot
/// reach the database except through the narrow store interfaces above. That is the whole design:
/// colony intelligence emerges from scheduling in the core, and a module that could schedule would
/// be a second, competing source of it.
///
/// The pressure to widen this will be real and will always arrive with a plausible reason. The
/// question to ask is whether the capability belongs to coordination — if it does, it is a core
/// change, not a new property here.
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// The colony spine. A module publishes what it did and subscribes to what it cares about;
    /// this is the ONLY way a module talks to the core at runtime.
    /// </summary>
    IEventBus Events { get; }

    /// <summary>
    /// Read and reinforce trails, so a module's successes and failures teach the colony. Modules
    /// are frequently the best-placed judges of whether something worked.
    /// </summary>
    IPheromoneMemory Pheromones { get; }

    /// <summary>Durable event history, for modules that need to catch up rather than only listen.</summary>
    IEventLog EventLog { get; }

    /// <summary>
    /// The module's own configuration section, already scoped to <see cref="IAnthillModule.Name"/>.
    /// Scoped rather than global so one module cannot read another's credentials.
    /// </summary>
    IReadOnlyDictionary<string, object?> Configuration { get; }

    ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Contribute reasoning. v3.8.6.
    ///
    /// A module cannot register itself: the registry lives in <c>Anthill.Core</c>, which a module
    /// may not reference. Before this existed, <c>Anthill.Api</c> reached past the module system and
    /// poked the core registry with a factory it had constructed itself — which worked, and left
    /// <see cref="IAnthillModule"/> declared and never once invoked. A contract nothing calls is not
    /// a boundary, it is a comment.
    ///
    /// Typed rather than a generic <c>RegisterService&lt;T&gt;</c>, deliberately. A generic
    /// registration surface is a service locator: it makes the set of things a module can contribute
    /// unbounded and unreadable, and the core would have to search it by type at the point of use.
    /// Reasoning is a capability the core explicitly recognises and explicitly works without, so it
    /// gets a named method and its absence stays meaningful.
    /// </summary>
    void RegisterReasoningProvider(IReasoningProviderFactory factory);

    /// <summary>
    /// Contribute capability discovery — what a provider's models can actually do, as opposed to
    /// what their names suggest. Optional: with none registered the core falls back to its declared
    /// name table.
    /// </summary>
    void RegisterCapabilityProbe(IModelCapabilityProbe probe);

    // Tool registration is NOT here yet, and its absence is deliberate.
    //
    // Modules will need to offer tools, but `ITool` currently lives in Anthill.Core and does not
    // move to the SDK until Phase 5. The two ways to have it now are both worse than waiting:
    // declaring `RegisterTool(string, object)` gives up the type system at precisely the seam
    // that exists to enforce types, and declaring a parallel SDK tool interface creates a
    // duplicate of a contract that is already correct — the thing this refactor is meant to
    // remove, introduced by the refactor itself.
    //
    // Phase 5 moves `ITool` and `IToolKindExecutor` here and adds `RegisterTool(ITool)` then.
}
