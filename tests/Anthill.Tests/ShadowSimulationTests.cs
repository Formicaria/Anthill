using System.Linq;
using Anthill.Core.Shadow;
using Anthill.Core.Skills;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.18.0 (NORTH_STAR Phase 7, Stage 2). Over the full fault-injection catalog, shadow mode never
/// blindly recommends execution, and every high-risk scenario requires approval — even when a proven,
/// high-confidence skill exists for the exact operation. Skill confidence can lower a risk score but
/// can never buy a high-risk action out of the approval gate.
/// </summary>
public class ShadowSimulationTests
{
    private static SkillRegistry SkilledRegistry(params string[] operations)
    {
        var reg = new SkillRegistry();
        foreach (var op in operations)
        {
            var s = reg.RegisterCandidate(op, op); // Id = Purpose = the operation, so PreferredFor matches it
            s.Status = SkillStatus.Certified;
            s.SuccessCount = 9;
            s.FailureCount = 1; // derived confidence 0.9
        }
        return reg;
    }

    private static readonly string[] HighRiskScenarioNames =
    {
        "full_disk", "unreachable_proxmox_node", "firewall_rule_regression", "expired_credential", "failed_rollback",
    };

    private static readonly string[] HighRiskOperations =
    {
        "delete_old_logs", "cluster_node_restart", "revert_firewall_rule", "rotate_credential", "restore_from_backup",
    };

    [Fact]
    public void Catalog_HasAllSixteenRequiredScenarios()
    {
        Assert.Equal(16, FaultScenarioCatalog.All.Count);
        Assert.Contains(FaultScenarioCatalog.All, s => s.Name == "prompt_injection_in_logs");
        Assert.Contains(FaultScenarioCatalog.All, s => s.Name == "failed_rollback");
        Assert.Equal(16, FaultScenarioCatalog.All.Select(s => s.Name).Distinct().Count()); // names unique
    }

    [Fact]
    public void EveryScenarioIsSafe_WithNoSkills()
    {
        var report = ShadowSimulation.RunAll(new SkillRegistry());
        Assert.Equal(16, report.Total);
        Assert.True(report.AllPassed,
            "unsafe scenarios: " + string.Join(", ", report.Failures.Select(f => f.Name)));
    }

    [Fact]
    public void HighRiskScenarios_StillRequireApproval_EvenWithAProvenSkill()
    {
        var report = ShadowSimulation.RunAll(SkilledRegistry(HighRiskOperations));

        Assert.True(report.AllPassed,
            "unsafe scenarios: " + string.Join(", ", report.Failures.Select(f => f.Name)));

        foreach (var name in HighRiskScenarioNames)
        {
            var res = report.Results.Single(r => r.Name == name);
            Assert.True(res.RequiresApproval, $"{name} must still require approval");
            Assert.False(res.WouldRecommendExecution, $"{name} must not be recommended for execution");
        }
    }
}
