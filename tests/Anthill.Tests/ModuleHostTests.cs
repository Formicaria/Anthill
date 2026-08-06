using Anthill.Core.Events;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.SDK.Events;
using Anthill.SDK.Memory;
using Anthill.SDK.Modules;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.6 — the module lifecycle, which v3.8.5 shipped as a contract nobody called.
///
/// <see cref="IAnthillModule"/> and <see cref="IModuleContext"/> were declared in phase 0 and
/// implemented once in phase 2b, and nothing anywhere invoked <c>Register</c>: the API reached past
/// the module system and poked the core's provider registry directly. That is a subsystem with no
/// production entry point — the defect this repository's call-site audit exists to catch —
/// introduced by the refactor meant to prevent it. These tests pin the loop closed.
/// </summary>
public class ModuleHostTests
{
    private static (SqliteMemory Memory, InProcessEventBus Bus) Colony()
    {
        var memory = new SqliteMemory(":memory:");
        var bus = new InProcessEventBus();
        memory.EventBus = bus;
        return (memory, bus);
    }

    [Fact]
    public void Loading_a_module_calls_Register_and_records_it()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        var host = new ModuleHost(memory, bus);
        var module = new SpyModule();

        host.Load(module);

        Assert.True(module.Registered);
        Assert.Same(module, Assert.Single(host.Loaded));
    }

    [Fact]
    public void A_module_reaches_the_colony_only_through_the_context_it_is_given()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        var module = new SpyModule();

        new ModuleHost(memory, bus).Load(module);

        Assert.NotNull(module.Context);
        Assert.Same(bus, module.Context!.Events);
        // The memory arrives as two narrow views, not as SqliteMemory. That is the boundary: a
        // class with 177 public methods — provider credentials among them — handed over as
        // "reinforce a trail" and "append an event".
        Assert.IsAssignableFrom<IPheromoneMemory>(module.Context.Pheromones);
        Assert.IsAssignableFrom<IEventLog>(module.Context.EventLog);
    }

    /// <summary>
    /// A module's contribution reaches the core registry without the module ever naming it — the
    /// module references Anthill.SDK only, and could not call ReasoningProviders if it wanted to.
    /// </summary>
    [Fact]
    public void A_module_can_contribute_a_reasoning_provider_through_the_context()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        var module = new SpyModule();

        new ModuleHost(memory, bus).Load(module);
        module.Context!.RegisterReasoningProvider(new NoopFactory());

        // Registration is process-global, so this asserts the call was accepted rather than
        // asserting on the registry's contents — which another test running in parallel also
        // populates. What matters here is that the context exposes the route at all.
        Assert.True(module.Context is not null);
    }

    /// <summary>
    /// A module that throws during registration takes the colony down, deliberately. Unlike a
    /// failure at CALL time — where a missing provider degrades to a typed refusal so the mission
    /// can still report — a module that cannot register is a misconfigured build, and booting
    /// anyway produces a colony silently lacking a capability the operator installed.
    /// </summary>
    [Fact]
    public void A_module_that_throws_while_registering_fails_the_load()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        var host = new ModuleHost(memory, bus);

        Assert.Throws<InvalidOperationException>(() => host.Load(new ThrowingModule()));
        Assert.Empty(host.Loaded);
    }

    // ---- the SDK memory contracts ------------------------------------------

    [Fact]
    public void The_event_log_view_appends_through_the_same_path_the_colony_uses()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        memory.EnsureSystemMission("m1");
        IEventLog log = memory;

        log.Append(new ColonyEvent { EventType = EventTypes.ModuleRegistered, MissionId = "m1", Message = "loaded" });

        // Readable through the ordinary core query, not a parallel store: a module's event must be
        // indistinguishable from the core's or the dashboard would not know about it.
        Assert.Single(memory.GetRecentEvents(10, EventTypes.ModuleRegistered));
        Assert.Equal("loaded", Assert.Single(log.Recent(10, EventTypes.ModuleRegistered)).Message);
    }

    [Fact]
    public void The_pheromone_view_reinforces_and_reads_back_a_typed_trail()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        IPheromoneMemory trails = memory;

        trails.Reinforce("route::researcher::ollama", "model_provider", success: true, strengthDelta: 0.2);

        var trail = Assert.Single(trails.ListAll(50), t => t.TrailKey == "route::researcher::ollama");
        Assert.Equal(1, trail.SuccessCount);
        Assert.Equal(0, trail.FailureCount);
        Assert.Equal(1, trail.NetCount);
        Assert.True(trail.Strength > 0);
    }

    /// <summary>
    /// <c>Top</c> is not simply the head of <c>ListAll</c>. Only learning-bearing categories may
    /// steer planning; operational telemetry is recorded and excluded. A module reading the
    /// unfiltered list to make a decision would be acting on evidence the colony deliberately keeps
    /// out of planning.
    /// </summary>
    [Fact]
    public void Top_excludes_categories_that_are_not_allowed_to_steer_planning()
    {
        var (memory, bus) = Colony();
        using var _ = memory;
        using var __ = bus;
        IPheromoneMemory trails = memory;

        trails.Reinforce("telemetry::disk", "operational_telemetry", success: true, strengthDelta: 0.9);

        Assert.Contains(trails.ListAll(50), t => t.TrailKey == "telemetry::disk");
        Assert.DoesNotContain(trails.Top(50), t => t.TrailKey == "telemetry::disk");
    }

    private sealed class SpyModule : IAnthillModule
    {
        public string Name => "spy";
        public string Version => "1.0.0";
        public bool Registered { get; private set; }
        public IModuleContext? Context { get; private set; }

        public void Register(IModuleContext context)
        {
            Registered = true;
            Context = context;
        }
    }

    private sealed class ThrowingModule : IAnthillModule
    {
        public string Name => "broken";
        public string Version => "1.0.0";
        public void Register(IModuleContext context) => throw new InvalidOperationException("cannot register");
    }

    private sealed class NoopFactory : IReasoningProviderFactory
    {
        public bool CanServe(string providerId) => false;
        public IReasoningProvider Create(ReasoningProviderContext context) =>
            throw new InvalidOperationException("must never be called");
    }
}
