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

    /// <remarks>
    /// v0.3.8.41 — restores the PREVIOUS gate state rather than setting everything to false.
    ///
    /// Restoring to false was indistinguishable from correct while false was also the shipped
    /// default. Now that the default roster is `full`, a helper that resets to false is a way for one
    /// test to switch the colony off for every test that runs after it in the same process — a flake
    /// that would appear as an unrelated failure somewhere else, ordered by the runner.
    /// </remarks>
    private static T WithGates<T>(ActivationTier tier, string role, Func<T> body) => WithTier(tier, () =>
    {
        var previous = RosterGates.Capture();
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            SetRoleFlag(role, true);
            return body();
        }
        finally { RosterGates.Restore(previous); }
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
    /// <remarks>
    /// v0.3.8.41 — the per-role flags are now forced OFF rather than assumed off. `WithTier` only
    /// ever saved and restored the tier, which was sufficient while `core` was the shipped default
    /// and the flags were false in every process. With `full` as the default they are true, so this
    /// test was reading "the tier is Full and the flags are on" and reporting it as the tier having
    /// opened a gate by itself. The property is unchanged; only the setup was leaning on a default.
    /// </remarks>
    [Fact]
    public void RaisingTheTierAlone_EnablesNothing() => RosterGates.WithAll(false, () =>
        WithTier(ActivationTier.Full, () =>
        {
            foreach (var role in new[] { "tester", "medic", "soldier", "scribe", "archivist", "ui_cartographer" })
                Assert.False(AntExecutorCatalog.SpecialistGateOpen(role), $"{role} opened without its own flag");
            return 0;
        }));

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

    /// <remarks>
    /// v0.3.8.41 — the master switch is now forced OFF by the test rather than assumed off.
    ///
    /// It passed before by accident of ordering: some neighbouring test's `finally` had set
    /// `EnableSpecialistAntExecution = false` first, and while `core` was the default that reset was
    /// invisible. With `full` as the default this assertion is only true if the test makes it true,
    /// which is what a test asserting "the master switch governs everything" should have been doing
    /// all along.
    /// </remarks>
    [Fact]
    public void TheMasterSwitchStillGovernsEverything() =>
        RosterGates.With(() => WithTier(ActivationTier.Full, () =>
        {
            // flag on, tier full, master OFF — the gate must still be shut.
            Assert.False(AntExecutorCatalog.SpecialistGateOpen("tester"));
            return 0;
        }), specialists: false, tester: true);

    // ---- upgrade safety ------------------------------------------------------------------------------

    /// <summary>
    /// The default tier is Full — "defer entirely to the per-role flags", i.e. exactly the behaviour
    /// before the tier existed. Defaulting to Core would have silently stopped specialists in every
    /// deployment that had already enabled them, on upgrade, with nothing announcing it.
    ///
    /// v0.3.8.41 — the second half of this test used to add "and that default still leaves the
    /// colony fully closed, because the flags are off". That sentence has stopped being true and its
    /// replacement is more precise about what the tier is: the tier is not what opens a role, and it
    /// never was. The ROSTER PROFILE opens roles; the tier can only narrow. So the assertion below is
    /// made with the flags explicitly closed, which is the condition it was always really about, and
    /// the new fact — that the shipped profile is what turns them on — is asserted beside it rather
    /// than left implied.
    /// </summary>
    [Fact]
    public void TheDefaultTierDefersToTheFlags_AndTheProfileIsWhatSetsThem()
    {
        var defaultTier = ActivationTiers.Parse(new AnthillConfig().ActivationTier);
        Assert.Equal(ActivationTier.Full, defaultTier);

        // With the flags closed, the default tier opens nothing. This is the tier's whole contract.
        RosterGates.WithAll(false, () => WithTier(defaultTier, () =>
        {
            Assert.DoesNotContain("tester", AntRegistry.ExecutableRoleIds);
            Assert.DoesNotContain("soldier", AntRegistry.ExecutableRoleIds);
            return 0;
        }));

        // And the shipped ROSTER PROFILE is what sets those flags — a different dial, deliberately.
        var shipped = RosterProfiles.Resolve(new AnthillConfig().RosterProfile, disabledRoles: null,
            new RosterActivation(false, ActivationTier.Core, false, false, false, false, false, false, false, false));

        Assert.True(shipped.Tester);
        Assert.True(shipped.Soldier);
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
