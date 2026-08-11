using Anthill.Core.Agents;
using Anthill.Core.Configuration;
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

    /// <summary>
    /// The role matrix and the registry agree about the six GATED roles — in both gate states.
    ///
    /// v0.3.8.41 — this asserted "docs say gated-off; the registry must agree (gates default off ⇒
    /// not executable)", which was a claim about the shipped default rather than about the gate. The
    /// default is now `full`, the doc's Default column says so, and the check that actually keeps the
    /// document honest is that these six are GATED: shut the gates and they leave the executable set,
    /// open them and they join it. A role in the matrix that ignored its gate would pass the old
    /// version of this test and fail this one.
    /// </summary>
    [Fact]
    public void ExecutionDoc_MatrixAgreesWithRegistry_OnGatedSpecialists()
    {
        var doc = Doc("docs/ANT_EXECUTION.md");
        var gated = new[] { "ui_cartographer", "tester", "soldier", "scribe", "medic", "archivist" };

        foreach (var role in gated)
        {
            Assert.Contains(role, doc);
            Assert.NotNull(AntExecutionCatalog.ContractFor(role)); // implemented ⇒ contract exists
        }

        RosterGates.WithAll(false, () =>
        {
            foreach (var role in gated) Assert.DoesNotContain(role, AntRegistry.ExecutableRoleIds);
            return 0;
        });

        RosterGates.WithAll(true, () =>
        {
            foreach (var role in gated) Assert.Contains(role, AntRegistry.ExecutableRoleIds);
            return 0;
        });
    }

    /// <summary>
    /// And the matrix's Default column is not stale. It said <b>off</b> for six releases and the
    /// shipped profile now enables all six — a table that quietly disagrees with the configuration is
    /// exactly the drift this file exists to catch, and it is the kind a reader trusts.
    /// </summary>
    [Fact]
    public void ExecutionDoc_DefaultColumn_MatchesTheShippedRosterProfile()
    {
        var doc = Doc("docs/ANT_EXECUTION.md");
        var shipped = RosterProfiles.Resolve(new AnthillConfig().RosterProfile, disabledRoles: null,
            new RosterActivation(false, ActivationTier.Core, false, false, false, false, false, false, false, false));

        // The shipped profile turns them on, so the matrix must not still be advertising them as off.
        Assert.True(shipped.Tester && shipped.Soldier && shipped.Medic
                    && shipped.Archivist && shipped.UiCartographer && shipped.Scribe,
            "the shipped roster profile no longer enables all six gated roles — update this test and "
            + "the Default column in docs/ANT_EXECUTION.md together");

        foreach (var role in new[] { "ui_cartographer", "tester", "soldier", "scribe", "medic", "archivist" })
        {
            var row = doc.Split('\n').FirstOrDefault(l => l.StartsWith($"| {role} |", StringComparison.Ordinal));
            Assert.True(row is not null, $"docs/ANT_EXECUTION.md has no role-matrix row for {role}");
            Assert.DoesNotContain("**off**", row!, StringComparison.Ordinal);
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
