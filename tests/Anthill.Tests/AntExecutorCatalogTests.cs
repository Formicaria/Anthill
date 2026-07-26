using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Execution framework Stage C validation gate: startup validation classifies every role, gated
/// specialists stay unavailable with explicit reasons, missing handlers are loud and fail closed,
/// and all rollout gates default OFF.
/// </summary>
[Collection("specialist-gates")]
public class AntExecutorCatalogTests
{
    private static readonly string[] CurrentSix = { "researcher", "web", "file", "coder", "builder", "verifier" };

    private static List<string> InitWithCurrentHandlers() => AntExecutorCatalog.Initialize(CurrentSix);

    [Fact]
    public void AllRolloutGates_DefaultOff()
    {
        Assert.False(AnthillRuntime.EnableSpecialistAntExecution);
        foreach (var role in new[] { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" })
            Assert.False(AntExecutorCatalog.SpecialistGateOpen(role));
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
        foreach (var role in new[] { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" })
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
        AntExecutorCatalog.Initialize(CurrentSix.Append("ui_cartographer").ToList());
        var a = AntExecutorCatalog.Snapshot["ui_cartographer"];
        Assert.True(a.Implemented);                    // handler exists
        Assert.False(a.RuntimeAvailable);              // but the canary is not open
        Assert.Equal("disabled by configuration", a.UnavailabilityReason);
        AntExecutorCatalog.Initialize(CurrentSix.ToList()); // restore for other tests
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
        var problems = AntExecutorCatalog.Initialize(CurrentSix.Where(r => r != "coder").ToList());
        Assert.Contains(problems, p => p.Contains("'coder'") && p.Contains("NO runtime handler"));
        var a = AntExecutorCatalog.Snapshot["coder"];
        Assert.False(a.RuntimeAvailable);
        Assert.False(a.PlannerEligible);
        Assert.Equal("missing runtime handler", a.UnavailabilityReason);
        AntExecutorCatalog.Initialize(CurrentSix.ToList()); // restore
    }
}
