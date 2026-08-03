using Anthill.Core.Domain;   // ToolResult
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.4.0 (ADR-006) — the registry projected into model-facing tool specs.
///
/// The rule under test is that a role is offered only the tools it may actually run. Offering
/// everything and letting authorization refuse at execution would be easier and wrong three ways:
/// it burns a model turn on a call that was never permitted, it teaches the model mid-conversation
/// that its tools fail at random (models respond by retrying or inventing workarounds), and it
/// discloses the existence and argument shape of privileged tools to a role that must not have
/// them — a disclosure bug even when the call itself is correctly denied.
/// </summary>
public class ToolSchemaProjectionTests
{
    /// <summary>
    /// A tool that declares a schema. Note it does NOT define ParametersJson when none is supplied:
    /// the first version wrote `_schema ?? ((ITool)this).ParametersJson`, which recursed forever and
    /// took the whole test host down with a stack overflow. Casting to the interface does not reach
    /// the DEFAULT member when the class implements that member — it dispatches straight back to
    /// the class. The default is only reachable from a type that stays silent about it, which is
    /// what <see cref="ToollessFake"/> below exists to prove.
    /// </summary>
    private sealed class FakeTool : ITool
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        private readonly string _schema;
        public FakeTool(string schema) => _schema = schema;
        public string ParametersJson => _schema;
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) => new(Name, true, "", null);
    }

    /// <summary>A tool that says nothing about its arguments, and so inherits the default schema.</summary>
    private sealed class ToollessFake : ITool
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) => new(Name, true, "", null);
    }

    private static ToolRegistry RegistryWith(params ITool[] tools)
    {
        var registry = new ToolRegistry(new SqliteMemory(":memory:"));
        foreach (var t in tools) registry.Register(t);
        return registry;
    }

    [Fact]
    public void ATool_CarriesItsOwnNameDescriptionAndSchema()
    {
        var spec = ToolSchemaProjection.ToSpec(new FakeTool(
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
        { Name = "read_text_file", Description = "reads a file" });

        Assert.Equal("read_text_file", spec.Name);
        Assert.Equal("reads a file", spec.Description);
        Assert.Contains("\"path\"", spec.ParametersJson);
    }

    /// <summary>
    /// A tool that has not declared arguments gets the empty-object schema rather than nothing.
    /// "Callable with no arguments" fails visibly at the tool; a missing schema makes the provider
    /// reject the request.
    /// </summary>
    [Fact]
    public void AToolWithoutADeclaredSchema_GetsAValidEmptyObjectSchema()
    {
        var spec = ToolSchemaProjection.ToSpec(new ToollessFake { Name = "system_info", Description = "host facts" });

        using var doc = System.Text.Json.JsonDocument.Parse(spec.ParametersJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// One tool with a broken schema must not disable the others. Providers reject the WHOLE
    /// request when any tool schema is malformed, so a single bad declaration would silently strip
    /// every tool from that call — the failure would look like a model that decided not to use any.
    /// </summary>
    [Fact]
    public void AMalformedSchema_DegradesThatToolOnly_RatherThanBreakingTheRequest()
    {
        var spec = ToolSchemaProjection.ToSpec(
            new FakeTool("{ this is not json") { Name = "broken", Description = "d" });

        using var doc = System.Text.Json.JsonDocument.Parse(spec.ParametersJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    /// <summary>A schema that is valid JSON but not an OBJECT is equally unusable as a parameter set.</summary>
    [Fact]
    public void ANonObjectSchema_IsReplacedToo()
    {
        var spec = ToolSchemaProjection.ToSpec(new FakeTool("[1,2,3]") { Name = "odd", Description = "d" });
        using var doc = System.Text.Json.JsonDocument.Parse(spec.ParametersJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    // ---- the load-bearing rule -------------------------------------------------------------------

    /// <summary>
    /// The specialists carry contracts with explicit tool allowlists, so they are the sharpest test:
    /// the tester's contract permits run_allowlisted_check and forbids apply_patch, and the
    /// projection must reflect exactly that.
    /// </summary>
    [Fact]
    public void ARoleIsOfferedOnlyTheToolsItsContractPermits()
    {
        var registry = RegistryWith(
            new ToollessFake { Name = "run_allowlisted_check", Description = "runs a check" },
            new ToollessFake { Name = "apply_patch", Description = "applies a patch" },
            new ToollessFake { Name = "write_file", Description = "writes a file" });

        var offered = ToolSchemaProjection.For(registry, "tester").Select(s => s.Name).ToList();

        Assert.Contains("run_allowlisted_check", offered);
        Assert.DoesNotContain("apply_patch", offered);   // forbidden by the tester contract
        Assert.DoesNotContain("write_file", offered);
    }

    /// <summary>
    /// Ordering is deterministic. A tool list that reshuffles between calls changes the prompt for
    /// no reason — defeating provider prompt caching and making two identical runs incomparable.
    /// </summary>
    [Fact]
    public void TheOfferedSet_IsDeterministicallyOrdered()
    {
        var registry = RegistryWith(
            new ToollessFake { Name = "zebra", Description = "z" },
            new ToollessFake { Name = "alpha", Description = "a" },
            new ToollessFake { Name = "middle", Description = "m" });

        var first = ToolSchemaProjection.For(registry, "researcher").Select(s => s.Name).ToList();
        var second = ToolSchemaProjection.For(registry, "researcher").Select(s => s.Name).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(n => n, StringComparer.Ordinal).ToList(), first);
    }

    [Fact]
    public void ANullRegistry_OffersNothing_RatherThanThrowing()
    {
        Assert.Empty(ToolSchemaProjection.For(null!, "researcher"));
    }
}
