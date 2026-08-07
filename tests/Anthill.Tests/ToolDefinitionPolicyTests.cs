using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.15 — pins the seam phase 5c step 3 created, and the one hazard it introduced.
///
/// <c>ToolDefinition</c> now lives in <c>Anthill.SDK.Tools</c>, which cannot see
/// <c>ToolInventory</c> or <c>ToolAuthorization</c>. Two of its checks are load-bearing — a
/// definition may not shadow a built-in, and may not claim a structurally forbidden name — so those
/// tables reach it through <c>IToolDefinitionPolicy</c>, installed by the same module initializer
/// that has carried the SSRF and patch-path readers since v3.8.12.
///
/// The hazard is the one v3.8.12 named and this release repeats deliberately: the SDK carries a
/// MIRROR of the core's tables for the unconfigured case, and a mirror can drift. Drift here is not
/// cosmetic — a name in the inventory and missing from the mirror is a name a definition could
/// shadow in any process that never loaded the core. So the mirror is asserted equal, rather than
/// trusted to be maintained.
/// </summary>
public class ToolDefinitionPolicyTests
{
    private static ToolDefinition Http(string name) => new()
    {
        Name = name,
        Description = "fetches a widget",
        Kind = ToolKind.Http,
    };

    /// <summary>
    /// The module initializer alone — no colony, no host, no <c>Configure</c> call in this test.
    /// The same property <c>SafetyPolicyTests</c> pins for the other two readers.
    /// </summary>
    [Fact]
    public void CoreInstallsTheLivePolicyWithoutAnyCompositionRoot() =>
        Assert.Same(ToolDefinitionPolicy.Live, SafetyPolicy.ToolDefinitions);

    /// <summary>
    /// READ THROUGH, not copied. Same instance, so a table edited after startup is the table
    /// enforced — the property a snapshot would silently break while every test still passed.
    /// </summary>
    [Fact]
    public void TheLivePolicyReadsTheCoresOwnTablesRatherThanCopyingThem()
    {
        Assert.Same(ToolInventory.Implemented, ToolDefinitionPolicy.Live.ReservedToolNames);
        Assert.Same(ToolAuthorization.MissionAgentForbidden, ToolDefinitionPolicy.Live.ForbiddenToolNames);
    }

    /// <summary>
    /// The drift guard. Adding a tool to <see cref="ToolInventory.Implemented"/> and forgetting the
    /// SDK's mirror fails HERE, at the cost of one line, rather than in a process that validates a
    /// definition without having loaded the core.
    /// </summary>
    [Fact]
    public void TheSdkFallbackMirrorsTheCoresTablesExactly()
    {
        var fallback = new SafetyPolicy.BuiltInToolDefinitionPolicy();

        Assert.Equal(
            ToolInventory.Implemented.OrderBy(x => x, StringComparer.Ordinal),
            fallback.ReservedToolNames.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(
            ToolAuthorization.MissionAgentForbidden.OrderBy(x => x, StringComparer.Ordinal),
            fallback.ForbiddenToolNames.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(
            ToolDefinitionPolicy.Live.BuildableKinds.OrderBy(x => x),
            fallback.BuildableKinds.OrderBy(x => x));
    }

    /// <summary>
    /// An unconfigured process must never be MORE permissive than a configured one. Stated as its
    /// own assertion rather than left implied by the equality above, because this is the property
    /// that actually matters and equality is merely how it is currently achieved.
    /// </summary>
    [Fact]
    public void TheSdkFallbackIsNeverMorePermissiveThanTheCore()
    {
        var fallback = new SafetyPolicy.BuiltInToolDefinitionPolicy();

        Assert.Empty(ToolInventory.Implemented.Except(fallback.ReservedToolNames, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(ToolAuthorization.MissionAgentForbidden.Except(fallback.ForbiddenToolNames, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The buildable set is DERIVED from the executors the registrar actually constructs, so a kind
    /// cannot be declared buildable while nothing can build it — or built while every definition
    /// naming it is told it is not.
    /// </summary>
    [Fact]
    public void BuildableKindsMatchTheExecutorsTheRegistrarShips()
    {
        Assert.Equal(UserToolRegistrar.BuildableKinds, ToolDefinitionPolicy.Live.BuildableKinds);
        Assert.Contains(ToolKind.Http, ToolDefinitionPolicy.Live.BuildableKinds);
        Assert.DoesNotContain(ToolKind.Mcp, ToolDefinitionPolicy.Live.BuildableKinds);
    }

    /// <summary>
    /// The optional argument works, and is what a test uses instead of reaching for
    /// <c>SafetyPolicy.Reset</c>. A definition validated against a policy reserving nothing is
    /// accepted — which proves the check is genuinely coming from the policy rather than from a
    /// table the record still knows about.
    /// </summary>
    [Fact]
    public void AnExplicitPolicyOverridesTheInstalledOne()
    {
        Assert.NotEmpty(Http("apply_patch").Validate());
        Assert.Empty(Http("apply_patch").Validate(new PermissivePolicy()));
    }

    private sealed class PermissivePolicy : IToolDefinitionPolicy
    {
        public IReadOnlySet<string> ReservedToolNames { get; } = new HashSet<string>();
        public IReadOnlySet<string> ForbiddenToolNames { get; } = new HashSet<string>();
        public IReadOnlySet<ToolKind> BuildableKinds { get; } = new HashSet<ToolKind> { ToolKind.Http };
    }
}
