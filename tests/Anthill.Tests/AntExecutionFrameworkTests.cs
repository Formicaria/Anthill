using Anthill.Core.Agents;
using Anthill.Core.Contracts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Ant Execution Framework — Stage A validation gate. Classification and contracts exist WITHOUT
/// activating anything: the executable set must be unchanged, control-plane and deterministic
/// roles must be planner-ineligible, scaffolds fail closed, and no contract ever permits
/// apply_patch. (Spec §15 registry tests, Stage A subset.)
/// </summary>
[Collection("specialist-gates")]
public class AntExecutionFrameworkTests
{
    private static readonly string[] Specialists =
        { "tester", "soldier", "medic", "archivist", "ui_cartographer", "scribe" };

    // ---- Nothing was activated by Stage A ------------------------------------------------------

    /// <remarks>
    /// v0.3.8.41 — the gates are forced shut rather than assumed shut.
    ///
    /// The property is "declaring a contract does not activate a role", and it was being tested
    /// against whatever the process happened to have configured. That was reliable while `core` was
    /// the shipped default and is not now: with `full`, `ExecutableRoleIds` legitimately contains
    /// twelve. The test was measuring the default; it now measures the rule.
    /// </remarks>
    [Fact]
    public void StageA_ActivatesNothing_ExecutableSetIsStillTheOriginalSix() =>
        RosterGates.WithAll(false, () =>
        {
            Assert.Equal(6, AntRegistry.ExecutableRoleIds.Count);
            foreach (var ant in new[] { "researcher", "web", "file", "coder", "builder", "verifier" })
                Assert.Contains(ant, AntRegistry.ExecutableRoleIds);
            foreach (var s in Specialists)
                Assert.DoesNotContain(s, AntRegistry.ExecutableRoleIds);
            return 0;
        });

    /// <summary>
    /// And with the gates OPEN the set is twelve — the shipped default since v0.3.8.41.
    ///
    /// The counterpart matters as much as the rule above. Without it this file only ever proved the
    /// colony can be switched off, which is the half that was never in doubt.
    /// </summary>
    [Fact]
    public void WithTheGatesOpen_TheExecutableSetIsAllTwelve() =>
        RosterGates.WithAll(true, () =>
        {
            Assert.Equal(12, AntRegistry.ExecutableRoleIds.Count);
            foreach (var s in Specialists)
                Assert.Contains(s, AntRegistry.ExecutableRoleIds);
            return 0;
        });

    // ---- Classification (spec §4.1) -------------------------------------------------------------

    [Theory]
    [InlineData("queen")] [InlineData("director")] [InlineData("planner")] [InlineData("constraint")]
    public void ControlPlaneRoles_AreClassified_AndNeverPlannerEligible(string role)
    {
        Assert.Equal(AntRuntimeKind.ControlPlane, AntExecutionCatalog.KindOf(role));
        Assert.False(AntExecutionCatalog.PlannerEligible(role));
    }

    [Theory]
    [InlineData("inventory")] [InlineData("network_scout")] [InlineData("health")] [InlineData("proxmox")]
    [InlineData("storage")] [InlineData("backup")] [InlineData("security_scout")] [InlineData("change_archivist")]
    [InlineData("quartermaster")]
    public void DeterministicServices_StayDeterministic_AndPlannerIneligible(string role)
    {
        Assert.Equal(AntRuntimeKind.DeterministicService, AntExecutionCatalog.KindOf(role));
        Assert.False(AntExecutionCatalog.PlannerEligible(role));
    }

    [Fact]
    public void UnknownRole_IsVisualScaffold_FailClosed()
    {
        Assert.Equal(AntRuntimeKind.VisualScaffold, AntExecutionCatalog.KindOf("future_mystery_ant"));
        Assert.False(AntExecutionCatalog.PlannerEligible("future_mystery_ant"));
    }

    /// <summary>
    /// Classification is independent of activation, in BOTH directions.
    ///
    /// v0.3.8.41 — renamed from `SpecialistMissionAgents_ClassifiedButNotYetEligible`, because "not
    /// yet" stopped being true: under the shipped profile these six are eligible. The property the
    /// test was always really about survives and is now stated as a pair — a role's KIND does not
    /// move when its gate does, and eligibility tracks the gate exactly.
    /// </summary>
    [Fact]
    public void SpecialistMissionAgents_AreClassifiedOnceAndEligibleOnlyWhenGated_Open()
    {
        RosterGates.WithAll(false, () =>
        {
            foreach (var s in Specialists)
            {
                Assert.Equal(AntRuntimeKind.MissionAgent, AntExecutionCatalog.KindOf(s));
                Assert.False(AntExecutionCatalog.PlannerEligible(s));   // implemented ≠ activated
            }
            return 0;
        });

        RosterGates.WithAll(true, () =>
        {
            foreach (var s in Specialists)
            {
                Assert.Equal(AntRuntimeKind.MissionAgent, AntExecutionCatalog.KindOf(s));
                Assert.True(AntExecutionCatalog.PlannerEligible(s));
            }
            return 0;
        });
    }

    [Fact]
    public void CurrentExecutableSix_ArePlannerEligibleMissionAgents()
    {
        foreach (var ant in AntRegistry.ExecutableRoleIds)
        {
            Assert.Equal(AntRuntimeKind.MissionAgent, AntExecutionCatalog.KindOf(ant));
            Assert.True(AntExecutionCatalog.PlannerEligible(ant));
        }
    }

    // ---- Contracts (spec §4.2) ------------------------------------------------------------------

    [Fact]
    public void EverySpecialist_HasVersionedContract_WithTaskTypesAndHandoffs()
    {
        foreach (var s in Specialists)
        {
            var c = AntExecutionCatalog.ContractFor(s);
            Assert.NotNull(c);
            Assert.False(string.IsNullOrWhiteSpace(c!.Version));
            Assert.NotEmpty(c.SupportedTaskTypes);
            Assert.NotEmpty(c.RequiredCapabilities);
            foreach (var target in c.AllowedHandoffRoles)
                Assert.NotEqual(AntRuntimeKind.VisualScaffold, AntExecutionCatalog.KindOf(target)); // handoffs go to real roles
        }
    }

    [Fact]
    public void NoContract_EverPermitsApplyPatch_OrArbitraryShell()
    {
        foreach (var c in AntExecutionCatalog.Contracts.Values)
        {
            Assert.DoesNotContain("apply_patch", c.AllowedTools);
            Assert.Contains("apply_patch", c.ForbiddenTools);
            Assert.DoesNotContain("shell", c.AllowedTools);
        }
    }

    [Fact]
    public void ReadOnlyRoles_DeclareNoSideEffects()
    {
        foreach (var role in new[] { "tester", "soldier", "medic", "ui_cartographer" })
            Assert.False(AntExecutionCatalog.ContractFor(role)!.AllowsSideEffects);
    }

    [Fact]
    public void OnlyScribe_MayProposePatches_AndTesterMayNotCallModels()
    {
        Assert.True(AntExecutionCatalog.ContractFor("scribe")!.ProducesPatchProposals);
        foreach (var s in Specialists.Where(x => x != "scribe"))
            Assert.False(AntExecutionCatalog.ContractFor(s)!.ProducesPatchProposals);
        Assert.False(AntExecutionCatalog.ContractFor("tester")!.AllowsModelCalls); // deterministic evidence only
    }

    [Fact]
    public void ContractTaskTypeCheck_RejectsForeignTaskTypes()
    {
        var tester = AntExecutionCatalog.ContractFor("tester")!;
        Assert.True(tester.SupportsTaskType("test_execution"));
        Assert.False(tester.SupportsTaskType("ui_mapping"));
        Assert.False(tester.SupportsTaskType(""));
    }

    // ---- Structured results (spec §4.3) ---------------------------------------------------------

    [Fact]
    public void StructuredResults_CarryTypedStatusAndFailure()
    {
        var ok = AntExecutionResult.Succeeded("all checks passed");
        Assert.True(ok.Success);
        Assert.Equal("succeeded", ok.StatusCode);

        var blocked = AntExecutionResult.Blocked("missing capability repo.read");
        Assert.False(blocked.Success);
        Assert.Equal("blocked", blocked.StatusCode);
        Assert.Equal(FailureClass.AuthorizationFailure, blocked.Failure!.Class);
        Assert.False(blocked.Failure.Retryable);

        var transient = AntExecutionResult.Failed(FailureClass.Timeout, "check timed out");
        Assert.Equal("failed_retryable", transient.StatusCode);
        var perm = AntExecutionResult.Failed(FailureClass.ValidationFailure, "bad input");
        Assert.Equal("failed_permanent", perm.StatusCode);
    }
}
