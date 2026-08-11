using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Execution framework Stage C validation gate: startup validation classifies every role, gated
/// specialists stay unavailable with explicit reasons, and missing handlers are loud and fail closed.
///
/// v0.3.8.41 — every test that classifies a SIX-HANDLER colony now forces the specialist gates shut
/// first, and that is not cosmetic. `AntRegistry.ExecutableRoleIds` adds any specialist whose gate is
/// open, and `AntExecutorCatalog.Initialize` reports an executable role with no handler as a startup
/// PROBLEM. So with the full roster ambient, initialising against six handlers produces six problems
/// and `Assert.Empty(problems)` fails.
///
/// It passed before this release for a reason worth naming: a neighbouring test's `finally` had set
/// the gates to false, and while `core` was the shipped default that reset was invisible. The tests
/// in this file were ordering-dependent and nobody could see it, because the ordering happened to
/// produce the state they wanted.
/// </summary>
[Collection("specialist-gates")]
public class AntExecutorCatalogTests
{
    private static readonly string[] CurrentSix = { "researcher", "web", "file", "coder", "builder", "verifier" };

    /// <summary>A colony with the six core handlers and NO specialist gate open — the state these
    /// classification tests are about, made explicit rather than inherited from test ordering.</summary>
    private static List<string> InitWithCurrentHandlers() =>
        RosterGates.WithAll(false, () => AntExecutorCatalog.Initialize(CurrentSix));

    private static readonly string[] SixSpecialists =
        { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" };

    /// <summary>
    /// The master switch closes every specialist gate, whatever the individual flags say.
    ///
    /// v0.3.8.41 — this used to read the statics and assert they were `false`, which was a test of
    /// the SHIPPED DEFAULT wearing a gate test's name. That was fine while the default was `core`
    /// and misleading the moment it became `full`: the assertion would then pass or fail depending
    /// on whether an earlier test in the process had loaded configuration. The property actually
    /// worth holding is the one below — the master switch is absolute — and it is now asserted
    /// against a state this test sets rather than one it hopes for.
    /// </summary>
    [Fact]
    public void TheMasterSwitch_ClosesEverySpecialistGate() =>
        RosterGates.With(() =>
        {
            foreach (var role in SixSpecialists)
            {
                Assert.False(AntExecutorCatalog.SpecialistGateOpen(role));
                Assert.Equal(RoleGateStatus.ClosedByMasterSwitch, AntExecutorCatalog.GateStatusOf(role));
            }
        },
        specialists: false, tier: ActivationTier.Full,
        tester: true, soldier: true, medic: true, archivist: true, uiCartographer: true, scribe: true);

    /// <summary>The shipped default, asserted where it is actually decidable: the config object.</summary>
    [Fact]
    public void TheShippedConfig_EnablesTheFullRoster()
    {
        var shipped = new AnthillConfig();

        var roster = RosterProfiles.Resolve(shipped.RosterProfile, shipped.DisabledRoles,
            new RosterActivation(false, ActivationTier.Core, false, false, false, false, false, false, false, false));

        Assert.True(roster.SpecialistExecution);
        Assert.True(roster.Tester && roster.Soldier && roster.Medic
                    && roster.Archivist && roster.UiCartographer && roster.Scribe);
        Assert.True(roster.HandoffIngestion && roster.AdaptiveMissionControl);
    }

    [Fact]
    public void CurrentSix_AreAvailableAndPlannerEligible_NoProblems()
    {
        var problems = InitWithCurrentHandlers();
        Assert.Empty(problems);
        foreach (var role in CurrentSix)
        {
            var a = AntExecutorCatalog.Snapshot[role];
            Assert.True(a.RuntimeAvailable);
            Assert.True(a.PlannerEligible);
            Assert.Equal("", a.UnavailabilityReason);
        }
    }

    [Fact]
    public void Specialists_AreUnavailable_WithMissingHandlerReason()
    {
        InitWithCurrentHandlers();
        foreach (var role in SixSpecialists)
        {
            var a = AntExecutorCatalog.Snapshot[role];
            Assert.False(a.RuntimeAvailable);
            Assert.False(a.PlannerEligible);
            Assert.Equal("missing runtime handler", a.UnavailabilityReason);
            Assert.False(a.Implemented); // implemented-but-disabled must differ from unimplemented
        }
    }

    [Fact]
    public void SpecialistWithHandler_ButGateClosed_IsImplementedYetDisabled()
    {
        RosterGates.With(() =>
        {
            AntExecutorCatalog.Initialize(CurrentSix.Append("ui_cartographer").ToList());
            var a = AntExecutorCatalog.Snapshot["ui_cartographer"];
            Assert.True(a.Implemented);                    // handler exists
            Assert.False(a.RuntimeAvailable);              // but the gate is shut
            Assert.Equal("disabled by configuration", a.UnavailabilityReason);
        }, specialists: true, tier: ActivationTier.Full, uiCartographer: false);

        InitWithCurrentHandlers();   // restore for other tests, gates shut
    }

    /// <summary>
    /// The counterpart, and the one the full-roster default makes load-bearing: with its gate open
    /// and a handler present, a specialist is genuinely available. Without this the file only ever
    /// proved roles can be switched OFF.
    /// </summary>
    [Fact]
    public void SpecialistWithHandler_AndGateOpen_IsAvailableAndPlannerEligible()
    {
        RosterGates.With(() =>
        {
            AntExecutorCatalog.Initialize(CurrentSix.Append("ui_cartographer").ToList());
            var a = AntExecutorCatalog.Snapshot["ui_cartographer"];
            Assert.True(a.Implemented);
            Assert.True(a.RuntimeAvailable);
            Assert.True(a.PlannerEligible);
            Assert.Equal("", a.UnavailabilityReason);
        }, specialists: true, tier: ActivationTier.Full, uiCartographer: true);

        InitWithCurrentHandlers();
    }

    [Fact]
    public void ControlPlaneAndDeterministicRoles_AreNeverSchedulable()
    {
        InitWithCurrentHandlers();
        foreach (var role in new[] { "queen", "director", "planner", "constraint" })
        {
            var a = AntExecutorCatalog.Snapshot[role];
            Assert.False(a.PlannerEligible);
            Assert.Equal("control-plane component", a.UnavailabilityReason);
        }
        foreach (var role in new[] { "inventory", "health", "proxmox", "quartermaster" })
        {
            var a = AntExecutorCatalog.Snapshot[role];
            Assert.False(a.PlannerEligible);
            Assert.Equal("deterministic service", a.UnavailabilityReason);
        }
    }

    [Fact]
    public void MissingHandlerOnExecutableRole_IsLoud_AndFailClosed()
    {
        var problems = RosterGates.WithAll(false,
            () => AntExecutorCatalog.Initialize(CurrentSix.Where(r => r != "coder").ToList()));
        Assert.Contains(problems, p => p.Contains("'coder'") && p.Contains("NO runtime handler"));
        var a = AntExecutorCatalog.Snapshot["coder"];
        Assert.False(a.RuntimeAvailable);
        Assert.False(a.PlannerEligible);
        Assert.Equal("missing runtime handler", a.UnavailabilityReason);
        InitWithCurrentHandlers();   // restore
    }
}
