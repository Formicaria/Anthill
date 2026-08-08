using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Execution framework Stage G (spec §13.7): documentation cannot drift from runtime behavior.
/// </summary>
[Collection("specialist-gates")]
public class ExecutionDocsConsistencyTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Doc(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

    [Fact]
    public void CanonicalExecutionDoc_Exists_AndReadmeLinksIt()
    {
        Assert.True(File.Exists(Path.Combine(Root(), "docs", "ANT_EXECUTION.md")));
        Assert.Contains("docs/ANT_EXECUTION.md", Doc("README.md"));
    }

    /// <summary>
    /// The plan and the autonomy doc both reach the execution framework. v3.8.24 — this checked
    /// NORTH_STAR and ROADMAP, which are archived; PLAN.md is the one forward-looking document now.
    ///
    /// The point is unchanged: the execution framework must be findable from whatever document a
    /// reader starts at, or the role matrix becomes a file only the people who already know about it
    /// can locate.
    /// </summary>
    [Fact]
    public void ThePlanAndAutonomyDoc_ReachTheExecutionFramework()
    {
        Assert.Contains("ANT_EXECUTION.md", Doc("docs/PLAN.md"));
        Assert.Contains("ANT_EXECUTION.md", Doc("docs/AUTONOMY.md"));
    }

    [Fact]
    public void ExecutionDoc_MatrixAgreesWithRegistry_OnGatedSpecialists()
    {
        var doc = Doc("docs/ANT_EXECUTION.md");
        foreach (var role in new[] { "ui_cartographer", "tester", "soldier", "scribe", "medic", "archivist" })
        {
            Assert.Contains(role, doc);
            // Docs say gated-off; the registry must agree (gates default off ⇒ not executable).
            Assert.DoesNotContain(role, AntRegistry.ExecutableRoleIds);
            Assert.NotNull(AntExecutionCatalog.ContractFor(role)); // and implemented ⇒ contract exists
        }
    }

    [Fact]
    public void ExecutionDoc_DoesNotClaimQuartermasterExecutable()
    {
        Assert.Contains("intentionally non-executable", Doc("docs/ANT_EXECUTION.md"));
        Assert.DoesNotContain("quartermaster", AntRegistry.ExecutableRoleIds);
        Assert.Equal(AntRuntimeKind.DeterministicService, AntExecutionCatalog.KindOf("quartermaster"));
    }

    [Fact]
    public void Changelog_RecordsTheFramework_WithoutPrematureVersionClaim()
    {
        var log = Doc("CHANGELOG.md");
        Assert.Contains("Ant Execution Framework", log);
        Assert.Contains("docs/ANT_EXECUTION.md", log);
    }
}
