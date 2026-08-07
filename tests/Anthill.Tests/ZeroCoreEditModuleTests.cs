using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Tools;
using Anthill.SDK.Contracts;
using Anthill.SDK.Events;
using Anthill.SDK.Modules;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The refactor's last success criterion, measured instead of asserted: "a new integration is added
/// as a module with zero Core edits." v3.8.18.
///
/// It sat "partial" through the whole refactor because every module so far was an EXTRACTION —
/// reasoning, homelab, tools all came OUT of the core, so none of them tested whether something new
/// can go IN. An external review called that out as the difference between implementation complete
/// and acceptance complete, and it was right.
///
/// This is the fixture. <see cref="SampleModule"/> below implements <c>IAnthillModule</c> using SDK
/// types only, contributes a tool whose name the core has never heard of, and is loaded through the
/// ordinary <c>ModuleHost</c>. Nothing in <c>Anthill.Core</c> was edited to make it work.
///
/// The answer is not a clean pass, and the value is in exactly where it stops.
/// </summary>
public class ZeroCoreEditModuleTests
{
    private const string NovelTool = "sample_module_probe";

    /// <summary>A tool the core's tables have never seen. SDK types only.</summary>
    private sealed class ProbeTool : ITool
    {
        public string Name => NovelTool;
        public string Description => "A sample module's tool, unknown to the core's inventory.";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, true, "probe ran");
    }

    private sealed class SampleModule : IAnthillModule
    {
        public string Name => "sample";
        public string Version => "1.0.0";
        public void Register(IModuleContext context) => context.RegisterTool(new ProbeTool());
    }

    private static (SqliteMemory memory, ToolRegistry registry) Colony()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"anthill-zeroedit-{Guid.NewGuid():N}.db");
        var memory = new SqliteMemory(dbPath);
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new SampleModule());

        var registry = new ToolRegistry(memory);
        foreach (var tool in host.ContributedTools) registry.Register(tool);
        return (memory, registry);
    }

    /// <summary>
    /// PASSES. A module written against the SDK alone registers a tool the core has never heard of,
    /// through the published contract, with no core change. The extension seam is real.
    /// </summary>
    [Fact]
    public void ANewModule_ContributesAnUnknownTool_WithNoCoreEdit()
    {
        var (memory, registry) = Colony();
        using (memory)
        {
            Assert.Contains(NovelTool, registry.Names);
            Assert.DoesNotContain(NovelTool, ToolInventory.Implemented);
        }
    }

    /// <summary>
    /// PASSES. It is offered to models like any other tool — the schema projection reads the
    /// registry, not the inventory — and it runs on the system-internal dispatch path.
    /// </summary>
    [Fact]
    public void TheUnknownTool_IsOfferedAndRunnable_OnTheSystemPath()
    {
        var (memory, registry) = Colony();
        using (memory)
        {
            Assert.Contains(registry.Tools, t => t.Name == NovelTool);

            var result = registry.RunTool(NovelTool);   // no ant name = system-internal
            Assert.True(result.Success);
            Assert.Equal("probe ran", result.Output);
        }
    }

    /// <summary>
    /// AND HERE IS THE LIMIT, pinned deliberately rather than left to be discovered.
    ///
    /// Every mission agent is refused. <c>ToolAuthorization</c>'s tables — role allowlists and the
    /// specialist execution contracts — are closed lists compiled into <c>Anthill.Core</c>, so a name
    /// that did not exist at compile time is denied by all of them. A module can therefore add a
    /// capability the colony can hold and offer, but NOT one any ant may dispatch, without a core
    /// edit.
    ///
    /// So the criterion is genuinely partial, and this test is what makes that statement checkable
    /// rather than a hedge. Lifting it is a design decision the refactor did not take: either
    /// authorization grows a module-supplied grant (the mechanism operator-defined tools already
    /// have, via <c>UserToolGrants</c>), or role allowlists move out of compiled tables. Both are
    /// changes to how permission works, which is coordination, which is core — so neither is a
    /// module's to make, and the boundary is arguably behaving correctly by refusing.
    /// </summary>
    [Theory]
    [InlineData("researcher")]
    [InlineData("file")]
    [InlineData("web")]
    [InlineData("coder")]
    public void ButNoMissionAgentMayDispatchIt_WithoutACoreEdit(string role)
    {
        var (memory, registry) = Colony();
        using (memory)
        {
            var result = registry.RunTool(NovelTool, antName: role);

            Assert.False(result.Success);
            Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
        }
    }

    /// <summary>
    /// The control plane can, which is what makes the capability reachable at all today: a module
    /// tool is usable by the queen/director pipeline and by system-internal callers, and that is the
    /// honest scope of "zero Core edits" as things stand.
    /// </summary>
    [Fact]
    public void TheControlPlaneMayDispatchIt()
    {
        var (memory, registry) = Colony();
        using (memory)
        {
            Assert.True(registry.RunTool(NovelTool, antName: "queen").Success);
        }
    }
}
