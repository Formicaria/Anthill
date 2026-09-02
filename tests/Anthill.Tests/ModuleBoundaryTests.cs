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
    /// <remarks>
    /// v0.3.8.112 — THE LIST IS NO LONGER HAND-MAINTAINED. R0's "module auto-discovery" item, and
    /// the paragraph this replaces is the strongest argument for it, so it is kept verbatim:
    ///
    ///   "This list is hand-maintained, which is the one soft spot in an otherwise mechanical check:
    ///   a module absent from it is not exempt, it is simply never looked at, and that reads exactly
    ///   like passing. Adding a module to `src/Anthill.Modules/` means adding a line here and a
    ///   project reference in this test project's csproj, or the boundary silently stops applying to
    ///   it. Micromound was added in the M1 phase and found this gap."
    ///
    /// The modules are now discovered from the DIRECTORY, and the boundary is checked against every
    /// one found. A module that this test project does not reference cannot be `Assembly.Load`ed,
    /// and that is reported as a FAILURE naming the missing reference rather than skipped — which
    /// is the whole difference between a check that grew a hole and one that says it has.
    ///
    /// DISCOVERY STOPS AT THE TEST BOUNDARY, deliberately. The production composition roots keep
    /// their explicit `LoadAll` calls: a colony that reflected over whatever assemblies happened to
    /// be on disk and loaded them as modules would be a strictly worse security posture than one
    /// that names what it composes, and this project's whole doctrine is that the composition root
    /// is the place things are decided. What was defective was never the explicitness — it was a
    /// GUARD that could silently stop covering a module. That is what is fixed.
    ///
    /// Note what "ours" means mechanically: the filter is the <c>Anthill.</c> prefix. Micromound
    /// carries <c>Micromound.Protocol</c> and <c>Micromound.Crypto</c> — the MICROMOUND wire
    /// contract and its Ed25519 implementation, from the sibling repository — and those pass,
    /// because they are not ANTHILL assemblies. That is the intended reading rather than a hole:
    /// the rule exists to stop a module reaching into the core or into a sibling module, and a
    /// shared wire contract is neither. Duplicating it on this side would be the actual violation,
    /// of MICROMOUND.md's "reuses, never duplicates".
    /// </remarks>
    /// <summary>
    /// Every module in <c>src/Anthill.Modules/</c>, by assembly name, discovered from the directory.
    ///
    /// MICROMOUND IS THE ONE CONDITIONAL MODULE, and it is filtered by the SAME constant that
    /// decides whether it is compiled — not by name. Its project builds only under `MICROMOUND`, so
    /// outside that build there is no assembly to load and demanding one would fail every default
    /// build for a module that correctly does not exist there. Filtering on the constant keeps the
    /// two facts in agreement: when it IS built, it is discovered and checked like any other.
    ///
    /// Every other unloadable module is a FAILURE, not a skip. That asymmetry is the whole point —
    /// "not compiled in this configuration" is a decision the build records, while "this project
    /// forgot to reference it" is the silent hole the hand-maintained list had.
    /// </summary>
    public static IEnumerable<object[]> DiscoveredModules()
    {
        var modules = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Modules");
        foreach (var project in Directory.GetFiles(modules, "*.csproj", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(project);
#if !MICROMOUND
            if (string.Equals(name, "Anthill.Modules.Micromound", StringComparison.Ordinal)) continue;
#endif
            yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(DiscoveredModules))]
    public void AModuleReferencesTheSdkAndNothingElseOfOurs(string moduleName)
    {
        Assembly module;
        try { module = Assembly.Load(moduleName); }
        catch (Exception load)
        {
            // NOT A SKIP. A module this project cannot load is a module the boundary is not being
            // applied to, which is the exact condition the old hand-maintained list produced
            // silently. Micromound outside a `MICROMOUND` build is the one legitimate case, and it
            // is named in the message rather than swallowed by a conditional.
            Assert.Fail(
                $"'{moduleName}' exists under src/Anthill.Modules and this test project cannot load "
              + $"it ({load.GetType().Name}: {load.Message}). Add a ProjectReference to "
              + "tests/Anthill.Tests/Anthill.Tests.csproj. The module boundary is currently "
              + "UNCHECKED for it, and an unchecked boundary must not read as a passing one — which "
              + "is exactly what the hand-maintained list this replaced used to do.");
            return;
        }

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
    /// AND DISCOVERY FINDS THE MODULES THAT EXIST. A `[MemberData]` source returning nothing turns
    /// the theory above into zero test cases — which xunit reports as a pass, and which is precisely
    /// the "never looked at, and that reads exactly like passing" failure the hand-maintained list
    /// had. The auto-discovery is only an improvement if it cannot fail the same way.
    /// </summary>
    [Fact]
    public void ModuleDiscovery_FindsEveryModuleOnDisk()
    {
        var found = DiscoveredModules().Select(row => (string)row[0]).ToList();

        Assert.True(found.Count >= 3,
            $"module discovery found {found.Count} module(s) under src/Anthill.Modules. The boundary "
          + "theory ranges over this list, so a short one means the check has quietly stopped "
          + "applying — which is the defect auto-discovery replaced.");

        foreach (var expected in new[] { "Anthill.Modules.Reasoning", "Anthill.Modules.Homelab", "Anthill.Modules.Tools" })
            Assert.Contains(expected, found);
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

        // Anthill.Modules.Micromound is deliberately NOT asserted yet. The module exists and its
        // boundary is checked above, but the Api does not compose it — MICROMOUND M1 is read-only
        // and its endpoints, persistence and registration land in the composition root as a
        // separate step. Add the line here when that step ships; asserting it early would make
        // this test fail for a reason that is not a boundary violation.
    }

    // The CLI is a composition root too, and it must load AND drain the tools module. That is
    // asserted in CallSiteAuditTests rather than here: this file reads assembly metadata, and
    // Anthill.Tests does not reference Anthill.Cli, so there is no CLI assembly to read. The
    // property that would actually regress is the drain call, which is source anyway.
}
