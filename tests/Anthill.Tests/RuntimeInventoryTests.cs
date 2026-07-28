using Anthill.Core.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Anthill.Tests;

/// <summary>
/// v3.0.0 baseline lock — the inventory and the call-site audit.
///
/// V2 shipped seven well-tested subsystems that nothing called, each found by a person reading
/// carefully one release too late. These tests make that reading automatic: the inventory
/// enumerates what the runtime declares, and the audit fails the build when a declaration has no
/// production consumer and no written exemption.
/// </summary>
public class RuntimeInventoryTests
{
    private readonly ITestOutputHelper _output;
    public RuntimeInventoryTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static RuntimeInventoryReport Inventory() => RuntimeInventory.Build(RepoRoot());

    // ---- the inventory sees the runtime -------------------------------------------------------------

    /// <summary>The inventory must actually find things. An empty inventory would make the audit
    /// below vacuously green — the failure mode a call-site audit can least afford.</summary>
    [Fact]
    public void TheInventory_EnumeratesEveryDeclarationKind()
    {
        var report = Inventory();
        foreach (var kind in new[]
                 {
                     RuntimeInventory.Kinds_Role, RuntimeInventory.Kinds_FeatureGate,
                     RuntimeInventory.Kinds_Endpoint, RuntimeInventory.Kinds_Table,
                     RuntimeInventory.Kinds_BackgroundLoop,
                 })
            Assert.True(report.Entries.Any(e => e.Kind == kind), $"inventory found no '{kind}' declarations");

        // Sanity floors: this runtime is large. A collapse to a handful means the scanner broke.
        Assert.True(report.Entries.Count(e => e.Kind == RuntimeInventory.Kinds_Role) >= 12,
            "expected the full ant roster");
        Assert.True(report.Entries.Count(e => e.Kind == RuntimeInventory.Kinds_Endpoint) >= 40,
            "expected the full API surface");
        Assert.True(report.Entries.Count(e => e.Kind == RuntimeInventory.Kinds_Table) >= 20,
            "expected the full schema");
    }

    /// <summary>Comments are stripped before the search: a symbol named only in a doc comment is
    /// not a consumer. This is precisely how V2's dead code looked alive.</summary>
    [Fact]
    public void ADocCommentMention_IsNotACallSite()
    {
        var sources = new Dictionary<string, string>
        {
            ["src/Decl.cs"] = "public static class Thing { }",
            ["src/OnlyMentionsIt.cs"] = "// Thing is great, see Thing\n/* Thing again */\npublic class X { }",
            ["src/ActuallyUsesIt.cs"] = "public class Y { void M() { Thing.Go(); } }",
        }.ToDictionary(kv => kv.Key, kv => RuntimeInventory.StripComments(kv.Value));

        var sites = RuntimeInventory.FindCallSites(sources, "Thing", "src/Decl.cs");
        Assert.Equal(new[] { "src/ActuallyUsesIt.cs" }, sites);
    }

    /// <summary>Substring matches do not count: `MissionEvaluator` must not be credited as a
    /// consumer of `Mission`.</summary>
    [Fact]
    public void FindCallSites_MatchesWholeSymbolsOnly()
    {
        var sources = new Dictionary<string, string>
        {
            ["src/A.cs"] = "class A { void M() { MissionEvaluator.Evaluate(); } }",
            ["src/B.cs"] = "class B { void M() { Mission.Go(); } }",
        };
        Assert.Equal(new[] { "src/B.cs" }, RuntimeInventory.FindCallSites(sources, "Mission"));
        Assert.Equal(new[] { "src/A.cs" }, RuntimeInventory.FindCallSites(sources, "MissionEvaluator"));
    }

    // ---- the audit ----------------------------------------------------------------------------------

    /// <summary>
    /// THE v3.0.0 exit gate: zero declaration-without-call-site defects.
    ///
    /// A failure here means one of two things. Either a new declaration has no production consumer
    /// — wire it or exempt it with a written reason — or an exemption has gone stale because the
    /// thing it protects now has consumers. Both are real; neither should be silenced.
    /// </summary>
    [Fact]
    public void EveryDeclaration_HasAProductionCallSite_OrAWrittenExemption()
    {
        var result = CallSiteAudit.Run(Inventory());
        _output.WriteLine(result.Explain());
        Assert.True(result.Clean, result.Explain());
    }

    /// <summary>The audit must be able to FAIL. A gate that cannot fail is not a gate — the same
    /// stance the readiness evaluation takes about empty scoreboards.</summary>
    [Fact]
    public void TheAudit_ActuallyFails_OnAnUnexemptedOrphan()
    {
        var planted = new RuntimeInventoryReport("test", "now", new[]
        {
            new InventoryEntry(RuntimeInventory.Kinds_FeatureGate, "EnableSomethingNobodyReads",
                "planted orphan", Array.Empty<string>()),
        });

        var result = CallSiteAudit.Run(planted);
        Assert.False(result.Clean);
        Assert.Contains(result.NewOrphans, o => o.Name == "EnableSomethingNobodyReads");
        Assert.Contains("NO production consumer", result.Explain());
    }

    /// <summary>And it fails on a STALE exemption — an allowlist nobody prunes is how a real gap
    /// eventually hides inside it.</summary>
    [Fact]
    public void TheAudit_FailsOnAStaleExemption()
    {
        // Exemptions are injected: the real list is empty at v3.0.0, and a check that cannot be
        // shown to fire is not a check.
        var exemptions = new Dictionary<string, string>
        {
            ["feature_gate:EnableWasOrphanedOnceButNotAnymore"] = "exempt for a reason that has since expired",
        };
        var planted = new RuntimeInventoryReport("test", "now", new[]
        {
            new InventoryEntry(RuntimeInventory.Kinds_FeatureGate, "EnableWasOrphanedOnceButNotAnymore",
                "now has a consumer", new[] { "src/Somewhere.cs" }),
        });

        var result = CallSiteAudit.Run(planted, exemptions);
        Assert.False(result.Clean);
        Assert.Contains(result.StaleExemptions, o => o.Name == "EnableWasOrphanedOnceButNotAnymore");
        Assert.Contains("STALE", result.Explain());
    }

    /// <summary>An exemption naming something the runtime no longer declares is also stale — the
    /// declaration was deleted and the exemption outlived it.</summary>
    [Fact]
    public void TheAudit_FailsOnAnExemptionForSomethingNoLongerDeclared()
    {
        var exemptions = new Dictionary<string, string> { ["feature_gate:EnableLongDeleted"] = "gone" };
        var result = CallSiteAudit.Run(new RuntimeInventoryReport("test", "now",
            Array.Empty<InventoryEntry>()), exemptions);
        Assert.False(result.Clean);
        Assert.Contains("no longer declares it", result.Explain());
    }

    /// <summary>Endpoints and background loops are outermost surfaces — their own call site by
    /// construction — and must never be audited as orphans.</summary>
    [Fact]
    public void OutermostSurfaces_AreNotAudited()
    {
        var planted = new RuntimeInventoryReport("test", "now", new[]
        {
            new InventoryEntry(RuntimeInventory.Kinds_Endpoint, "GET /whatever", "d", Array.Empty<string>()),
            new InventoryEntry(RuntimeInventory.Kinds_BackgroundLoop, "some-loop", "d", Array.Empty<string>()),
        });
        Assert.True(CallSiteAudit.Run(planted).Clean);
    }

    /// <summary>
    /// Every exemption states a reason. "Because the audit was red" is not a reason, and a blank
    /// one would let an entry be added without thought.
    ///
    /// The list is empty at v3.0.0 — the baseline lock found one orphan in 300 declarations and
    /// deleted it rather than exempting it. This test guards the list as it grows.
    /// </summary>
    [Fact]
    public void EveryExemption_StatesAReason()
    {
        Assert.All(CallSiteAudit.ExpectedOrphans, kv =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"exemption '{kv.Key}' has no stated reason");
            Assert.True(kv.Value.Length >= 20, $"exemption '{kv.Key}' reason is too thin: '{kv.Value}'");
        });
    }
}
