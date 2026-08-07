using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.SDK.Events;
using Anthill.SDK.Memory;
using Anthill.SDK.Modules;
using Anthill.SDK.Reasoning;
using Anthill.SDK.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anthill.Core.Modules;

/// <summary>
/// Loads modules into a colony. v3.8.6.
///
/// The composition root builds one of these, hands it the modules the build ships, and calls
/// <see cref="Load"/>. That is the whole lifecycle — there is no unload, no reload and no ordering
/// declaration, because modules contribute capability and observe events; they do not depend on
/// each other. If two modules ever DO need ordering, that is a design problem to solve then rather
/// than a dependency graph to build now against no requirement.
///
/// Before this existed the API registered a reasoning factory directly with
/// <see cref="ReasoningProviders"/> and <see cref="IAnthillModule"/> was declared, implemented once,
/// and never invoked by anything.
/// </summary>
public sealed class ModuleHost
{
    private readonly List<IAnthillModule> _loaded = new();
    private readonly ModuleContext _context;

    public ModuleHost(SqliteMemory memory, IEventBus events, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(events);
        _context = new ModuleContext(memory, events, loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <summary>The modules loaded into this colony, in load order. For diagnostics and reporting.</summary>
    public IReadOnlyList<IAnthillModule> Loaded => _loaded;

    /// <summary>
    /// Tools contributed by modules, awaiting the registry. v3.8.10.
    ///
    /// Buffered rather than registered directly, because of an ordering the composition root cannot
    /// avoid: modules must load BEFORE the Queen (a Queen built first would report model fitness
    /// against a colony with no providers), and the tool registry is built BY the Queen. So a module
    /// registering a tool has nowhere to put it yet.
    ///
    /// The composition root drains this into <c>Queen.Tools</c> once she exists. Making the module
    /// wait for a registry it cannot see would have meant a lazier, more surprising contract than
    /// simply handing the tool over and letting the root place it.
    /// </summary>
    public IReadOnlyList<ITool> ContributedTools => _context.Tools;

    /// <summary>
    /// Load a module: hand it a context and let it contribute.
    ///
    /// A module that throws during registration takes the colony down, and that is intended. Unlike
    /// a failure at CALL time — where a missing provider must degrade to a typed refusal so a
    /// mission can still report — a module that cannot even register is a misconfigured build, and
    /// starting anyway would produce a colony that silently lacks a capability the operator
    /// installed it to have.
    /// </summary>
    public void Load(IAnthillModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Register(_context);
        _loaded.Add(module);
    }

    public void LoadAll(params IAnthillModule[] modules)
    {
        foreach (var module in modules) Load(module);
    }

    /// <summary>
    /// The only surface a module gets onto the colony.
    ///
    /// Note what is NOT reachable from here: the Queen, the scheduler, the task queue, the tool
    /// registry, and <see cref="SqliteMemory"/> itself. The memory arrives as
    /// <see cref="IPheromoneMemory"/> and <see cref="IEventLog"/> — two narrow views of a class with
    /// 177 public methods — so a module can teach the colony what worked and record what it did,
    /// and cannot read another module's provider credentials or enqueue work.
    /// </summary>
    private sealed class ModuleContext : IModuleContext
    {
        private readonly SqliteMemory _memory;

        public ModuleContext(SqliteMemory memory, IEventBus events, ILoggerFactory loggerFactory)
        {
            _memory = memory;
            Events = events;
            LoggerFactory = loggerFactory;
        }

        public IEventBus Events { get; }

        public IPheromoneMemory Pheromones => _memory;

        public IEventLog EventLog => _memory;

        /// <summary>
        /// Empty until modules are configurable from <c>config.json</c>. Deliberately shipped empty
        /// rather than omitted: the property is what makes per-module scoping the default when
        /// configuration does arrive, instead of a global settings bag every module can read.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Configuration { get; } = new Dictionary<string, object?>();

        public ILoggerFactory LoggerFactory { get; }

        public void RegisterReasoningProvider(IReasoningProviderFactory factory) =>
            ReasoningProviders.Register(factory);

        public void RegisterCapabilityProbe(IModelCapabilityProbe probe) =>
            ReasoningProviders.RegisterProbe(probe);

        internal List<ITool> Tools { get; } = new();

        /// <summary>
        /// A duplicate name throws rather than overwriting. <c>ToolRegistry.Register</c> assigns by
        /// key and last-write-wins, which is right for the core replacing its own built-ins — but
        /// two MODULES both claiming "shell" is a build misconfiguration, and silently running one
        /// of them is not a failure anyone notices until the wrong one executes.
        /// </summary>
        public void RegisterTool(ITool tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (Tools.Any(t => string.Equals(t.Name, tool.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Two modules both registered a tool named '{tool.Name}'. Tool names are the "
                  + "colony's dispatch keys and must be unique across modules.");
            Tools.Add(tool);
        }
    }
}
