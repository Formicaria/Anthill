using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.22.0 Phase D: specialist activation as one deliberate dial instead of six independent
/// booleans. The tier is a CEILING — per-role rollout flags still apply on top — so raising it can
/// never switch a role on by itself, and every existing gate stays exactly as binding.
/// </summary>
[Collection("specialist-gates")]
public class ActivationTierTests
{
    private static T WithTier<T>(ActivationTier tier, Func<T> body)
    {
        var previous = AnthillRuntime.ActivationTier;
        try { AnthillRuntime.ActivationTier = tier; return body(); }
        finally { AnthillRuntime.ActivationTier = previous; }
    }

    private static T WithGates<T>(ActivationTier tier, string role, Func<T> body) => WithTier(tier, () =>
    {
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            SetRoleFlag(role, true);
            return body();
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            SetRoleFlag(role, false);
        }
    });

    private static void SetRoleFlag(string role, bool value)
    {
        switch (role)
        {
            case "tester": AnthillRuntime.EnableTesterAnt = value; break;
            case "medic": AnthillRuntime.EnableMedicAnt = value; break;
            case "soldier": AnthillRuntime.EnableSoldierAnt = value; break;
            case "scribe": AnthillRuntime.EnableScribeAnt = value; break;
            case "archivist": AnthillRuntime.EnableArchivistAnt = value; break;
            case "ui_cartographer": AnthillRuntime.EnableUiCartographerAnt = value; break;
        }
    }

    // ---- parsing narrows, never widens -----------------------------------------------------------

    [Theory]
    [InlineData("core", ActivationTier.Core)]
    [InlineData("adaptive", ActivationTier.Adaptive)]
    [InlineData("full", ActivationTier.Full)]
    [InlineData("FULL", ActivationTier.Full)]
    [InlineData("  adaptive  ", ActivationTier.Adaptive)]
    public void RecognisedNamesParse(string name, ActivationTier expected) =>
        Assert.Equal(expected, ActivationTiers.Parse(name));

    /// <summary>A typo in a config file must narrow what can run, never widen it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("everything")]
    [InlineData("addaptive")]
    public void UnrecognisedNamesFailClosedToCore(string? name) =>
        Assert.Equal(ActivationTier.Core, ActivationTiers.Parse(name));

    // ---- the ceiling ------------------------------------------------------------------------------

    [Fact]
    public void CoreAdmitsNoSpecialist()
    {
        foreach (var role in new[] { "tester", "medic", "soldier", "scribe", "archivist", "ui_cartographer" })
            Assert.False(ActivationTiers.Admits(ActivationTier.Core, role));
    }

    /// <summary>
    /// The adaptive set is detect/diagnose plus read-only mapping. Roles that issue security
    /// verdicts, write operator documentation, or write durable memory each deserve their own
    /// decision and are excluded.
    /// </summary>
    [Fact]
    public void AdaptiveAdmitsOnlyTheLoopSupportRoles()
    {
        foreach (var included in new[] { "tester", "medic", "ui_cartographer" })
            Assert.True(ActivationTiers.Admits(ActivationTier.Adaptive, included), $"{included} should be adaptive");

        foreach (var excluded in new[] { "soldier", "scribe", "archivist" })
            Assert.False(ActivationTiers.Admits(ActivationTier.Adaptive, excluded), $"{excluded} must not be adaptive");
    }

    [Fact]
    public void FullAdmitsEverySpecialist()
    {
        foreach (var role in new[] { "tester", "medic", "soldier", "scribe", "archivist", "ui_cartographer" })
            Assert.True(ActivationTiers.Admits(ActivationTier.Full, role));
    }

    // ---- the tier never switches anything ON --------------------------------------------------------

    /// <summary>
    /// The safety property. Raising the tier to Full with every per-role flag OFF must leave the
    /// colony exactly as it was — otherwise the tier would be a second, weaker way to enable a
    /// role, defeating the rollout gates.
    /// </summary>
    [Fact]
    public void RaisingTheTierAlone_EnablesNothing()
    {
        WithTier(ActivationTier.Full, () =>
        {
            foreach (var role in new[] { "tester", "medic", "soldier", "scribe", "archivist", "ui_cartographer" })
                Assert.False(AntExecutorCatalog.SpecialistGateOpen(role), $"{role} opened without its own flag");
            return 0;
        });
    }

    [Fact]
    public void ARoleWithItsFlagSet_StillNeedsTheTierToAdmitIt()
    {
        Assert.False(WithGates(ActivationTier.Core, "tester", () => AntExecutorCatalog.SpecialistGateOpen("tester")));
        Assert.True(WithGates(ActivationTier.Adaptive, "tester", () => AntExecutorCatalog.SpecialistGateOpen("tester")));
    }

    /// <summary>Narrowing is what the tier is FOR: it can turn a flagged role off.</summary>
    [Fact]
    public void NarrowingTheTier_TurnsAFlaggedRoleOff()
    {
        Assert.True(WithGates(ActivationTier.Full, "soldier", () => AntExecutorCatalog.SpecialistGateOpen("soldier")));
        Assert.False(WithGates(ActivationTier.Adaptive, "soldier", () => AntExecutorCatalog.SpecialistGateOpen("soldier")));
    }

    [Fact]
    public void TheMasterSwitchStillGovernsEverything()
    {
        WithTier(ActivationTier.Full, () =>
        {
            try
            {
                AnthillRuntime.EnableTesterAnt = true;   // flag on, tier full, master OFF
                Assert.False(AntExecutorCatalog.SpecialistGateOpen("tester"));
            }
            finally { AnthillRuntime.EnableTesterAnt = false; }
            return 0;
        });
    }

    // ---- upgrade safety ------------------------------------------------------------------------------

    /// <summary>
    /// The default is Full — "defer entirely to the per-role flags", i.e. exactly the behaviour
    /// before the tier existed. Defaulting to Core would have silently stopped specialists in every
    /// deployment that had already enabled them, on upgrade, with nothing announcing it. Safety
    /// comes from the per-role flags, which remain off by default.
    /// </summary>
    [Fact]
    public void TheDefaultPreservesPreTierBehaviour()
    {
        Assert.Equal(ActivationTier.Full, ActivationTiers.Parse(new AnthillConfig().ActivationTier));

        // ...and that default still leaves the colony fully closed, because the flags are off.
        WithTier(ActivationTiers.Parse(new AnthillConfig().ActivationTier), () =>
        {
            Assert.DoesNotContain("tester", AntRegistry.ExecutableRoleIds);
            Assert.DoesNotContain("soldier", AntRegistry.ExecutableRoleIds);
            return 0;
        });
    }

    [Fact]
    public void EveryTierHasAnOperatorFacingExplanation()
    {
        foreach (var tier in new[] { ActivationTier.Core, ActivationTier.Adaptive, ActivationTier.Full })
        {
            Assert.False(string.IsNullOrWhiteSpace(ActivationTiers.Explain(tier)));
            Assert.Equal(tier, ActivationTiers.Parse(ActivationTiers.Name(tier)));   // round-trips
        }
    }

    // ---- the call site --------------------------------------------------------------------------------

    [Fact]
    public void TheGateConsultsTheTier()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Agents", "AntExecutorCatalog.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
        Assert.Contains("ActivationTiers.Admits(AnthillRuntime.ActivationTier, roleId)", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
