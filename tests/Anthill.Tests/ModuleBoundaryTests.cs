using System.Reflection;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The rule the whole refactor rests on, enforced by the compiler's own metadata rather than by
/// discipline. v3.8.8.
///
/// Phases 0–4 moved 8,555 lines out of the core on one principle: arrows point toward
/// <c>Anthill.SDK</c>. Every phase verified that by hand with a grep, and every phase would have
/// passed a grep run five minutes before someone added a using statement. A boundary maintained by
/// review is a boundary that erodes at the first deadline — this repository's own history says so,
/// which is why <c>CallSiteAudit</c> exists.
///
/// These read ASSEMBLY REFERENCES, not source text. A project reference that is present but unused
/// still fails here, which is deliberate: it is the reference that permits the coupling, and it is
/// what a future edit would quietly take advantage of.
/// </summary>
public class ModuleBoundaryTests
{
    private const string ModulePrefix = "Anthill.Modules.";

    private static IReadOnlyList<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

    /// <summary>
    /// The core must not reference any module. This is the one that matters: it is what makes
    /// "the colony runs without AI" and "the colony runs without the homelab" true by construction
    /// rather than by testing each case.
    /// </summary>
    [Fact]
    public void TheCoreReferencesNoModule()
    {
        var offenders = ReferencesOf(typeof(Queen).Assembly)
            .Where(n => n.StartsWith(ModulePrefix, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Anthill.Core references a module, which inverts the dependency the Core/Modules split "
          + "exists to establish. Either the type belongs in Anthill.SDK, or the core is reaching "
          + "for capability it should be declaring a contract for: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A module may reference the SDK and nothing else of ours. Not the core, and not another
    /// module — modules contribute capability and observe events; one needing another is a design
    /// problem to solve deliberately, not to discover through a reference that already compiles.
    /// </summary>
    [Theory]
    [InlineData("Anthill.Modules.Reasoning")]
    [InlineData("Anthill.Modules.Homelab")]
    [InlineData("Anthill.Modules.Tools")]
    public void AModuleReferencesTheSdkAndNothingElseOfOurs(string moduleName)
    {
        var module = Assembly.Load(moduleName);

        var offenders = ReferencesOf(module)
            .Where(n => n.StartsWith("Anthill.", StringComparison.Ordinal))
            .Where(n => n != "Anthill.SDK")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{moduleName} references {string.Join(", ", offenders)}. A module that imports the core "
          + "is not a module; a module that imports another module is a dependency graph nobody "
          + "declared. If it needs a type from there, the type belongs in Anthill.SDK.");
    }

    /// <summary>
    /// The SDK is contracts and primitives. It must not reference the core, a module, a database
    /// driver or an HTTP client — because everything references the SDK, so anything it depends on
    /// is inherited by the entire colony, and the boundary stops meaning anything.
    /// </summary>
    [Fact]
    public void TheSdkDependsOnNothingOfOursAndNothingHeavy()
    {
        var sdk = Assembly.Load("Anthill.SDK");
        var refs = ReferencesOf(sdk);

        var ours = refs.Where(n => n.StartsWith("Anthill.", StringComparison.Ordinal)).ToList();
        Assert.True(ours.Count == 0,
            "Anthill.SDK references " + string.Join(", ", ours) + ". Contracts cannot depend on "
          + "their implementations.");

        // Named rather than a blanket allow-list: these are the two that would actually be reached
        // for, and both would be inherited by every module in the colony.
        foreach (var forbidden in new[] { "Microsoft.Data.Sqlite", "System.Net.Http" })
            Assert.False(refs.Contains(forbidden),
                $"Anthill.SDK references {forbidden}. Everything references the SDK, so this is "
              + "inherited colony-wide — a contracts project must not carry a database driver or an "
              + "HTTP stack.");
    }

    /// <summary>
    /// The composition root is ALLOWED to name modules — that is its job, and it is the only place
    /// in the process that does. Asserted positively so the boundary tests above cannot be
    /// satisfied by the trivial reading where nothing composes anything.
    /// </summary>
    [Fact]
    public void TheApiComposesEveryModule()
    {
        var refs = ReferencesOf(typeof(Anthill.Api.ApiHost).Assembly);

        Assert.Contains("Anthill.Modules.Reasoning", refs);
        Assert.Contains("Anthill.Modules.Homelab", refs);
        Assert.Contains("Anthill.Modules.Tools", refs);
    }

    // The CLI is a composition root too, and it must load AND drain the tools module. That is
    // asserted in CallSiteAuditTests rather than here: this file reads assembly metadata, and
    // Anthill.Tests does not reference Anthill.Cli, so there is no CLI assembly to read. The
    // property that would actually regress is the drain call, which is source anyway.
}
