using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D-3 validation gate (spec §15 SOLDIERANT): blocked paths, secret-like content, permission
/// expansion, and scope mismatch are detected deterministically; the verdict is computed with no
/// model in the path (so nothing generated can override it); gates control executability.
/// </summary>
[Collection("specialist-gates")]
public class SoldierAntTests
{
    private static (DomainTask, Mission) Review(string desc, string type = "security_review")
    {
        var t = new DomainTask { Title = "Review", Description = desc, AssignedAnt = "soldier", TaskType = type };
        return (t, new Mission { Goal = "review", Tasks = { t } });
    }

    private static string Run(string desc, string type = "security_review")
    {
        var (t, m) = Review(desc, type);
        return new SoldierAnt().Run(t, m);
    }

    // ---- Deterministic detections --------------------------------------------------------------

    [Fact]
    public void BlockedPath_CiWorkflow_IsBlocking()
    {
        var o = Run("patch modifies .github/workflows/ci.yml to add a step");
        Assert.Contains("blocked_path_ci", o);
        Assert.Contains("BLOCKING", o);
    }

    [Fact]
    public void SecretLikeContent_IsBlockingCritical()
    {
        var o = Run("adds config line password = 'hunter2secret' to settings");
        Assert.Contains("secret_material", o);
        Assert.Contains("BLOCKING", o);
        Assert.Contains("critical", o);
    }

    [Fact]
    public void PermissionExpansion_ApplyPatchGrant_IsBlocking()
    {
        var o = Run("change sets ApplyPatches = true for the coder role");
        Assert.Contains("permission_expansion", o);
        Assert.Contains("BLOCKING", o);
    }

    [Fact]
    public void PythonOutsideArchive_IsBlocking()
    {
        var o = Run("adds helper script tools/helper.py to the repo");
        Assert.Contains("python_outside_archive", o);
    }

    [Fact]
    public void ScopeMismatch_PathOutsideApprovedScope_IsBlocking()
    {
        var o = Run("approved_scope: docs/\nchanges docs/HOMELAB.md and src/Anthill.Core/Queen.cs");
        Assert.Contains("scope_mismatch", o);
        Assert.Contains("BLOCKING", o);
    }

    [Fact]
    public void AuthTouch_IsAdvisoryNotBlocking()
    {
        var o = Run("refactors RequireAuth call ordering in one endpoint");
        Assert.Contains("auth_change", o);
        Assert.DoesNotContain("deterministic block", o); // advisory → review passes
        Assert.Contains("\"DestinationRole\":\"verifier\"", o.Replace(" ", ""));
    }

    [Fact]
    public void CleanDocsChange_PassesWithVerifierHandoff()
    {
        var o = Run("approved_scope: docs/\nupdates docs/HOMELAB.md wording only");
        Assert.Contains("Security review passed", o);
        Assert.Contains("\"DestinationRole\":\"verifier\"", o.Replace(" ", ""));
    }

    [Fact]
    public void BlockingFindings_RouteToBuilderForOperatorExplanation()
    {
        var o = Run("patch modifies deploy/lxc/setup.sh");
        Assert.Contains("blocked_path_deploy", o);
        Assert.Contains("\"DestinationRole\":\"builder\"", o.Replace(" ", ""));
    }

    // ---- Determinism = model cannot override ---------------------------------------------------

    [Fact]
    public void Verdict_IsDeterministic_SameInputSameFindings()
    {
        var a = PolicyScan.Scan("password = 'hunter2secret'");
        var b = PolicyScan.Scan("password = 'hunter2secret'");
        Assert.Equal(a.Select(f => f.RuleId), b.Select(f => f.RuleId));
        Assert.All(a, f => Assert.True(f.Blocking)); // and no code path accepts model text into Scan
    }

    [Fact]
    public void ForeignTaskType_IsBlockedByContract()
    {
        var o = Run("anything", type: "test_execution");
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
    }

    // ---- Gates ---------------------------------------------------------------------------------

    [Fact]
    public void GatesControlExecutability()
    {
        Assert.DoesNotContain("soldier", AntRegistry.ExecutableRoleIds);
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableSoldierAnt = true;
            Assert.Contains("soldier", AntRegistry.ExecutableRoleIds);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableSoldierAnt = false;
        }
    }
}
