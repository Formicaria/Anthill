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

    // v2.19.0: SoldierAnt returns a structured AntExecutionResult. These assertions previously
    // substring-matched the JSON Compat() embedded in the returned string.
    private static AntExecutionResult Run(string desc, string type = "security_review")
    {
        var (t, m) = Review(desc, type);
        return new SoldierAnt().Execute(t, m);
    }

    /// <summary>The recorded review text — Queen stores Narrative ?? Summary.</summary>
    private static string Recorded(AntExecutionResult r) => r.Narrative ?? r.Summary;

    private static void AssertRoutesTo(AntExecutionResult r, string role) =>
        Assert.Contains(r.Handoffs, h => h.DestinationRole == role);

    // ---- Deterministic detections --------------------------------------------------------------

    [Fact]
    public void BlockedPath_CiWorkflow_IsBlocking()
    {
        var o = Run("patch modifies .github/workflows/ci.yml to add a step");
        Assert.Contains("blocked_path_ci", Recorded(o));
        Assert.Contains("BLOCKING", Recorded(o));
    }

    [Fact]
    public void SecretLikeContent_IsBlockingCritical()
    {
        var o = Run("adds config line password = 'hunter2secret' to settings");
        Assert.Contains("secret_material", Recorded(o));
        Assert.Contains("BLOCKING", Recorded(o));
        Assert.Contains("critical", Recorded(o));
    }

    [Fact]
    public void PermissionExpansion_ApplyPatchGrant_IsBlocking()
    {
        var o = Run("change sets ApplyPatches = true for the coder role");
        Assert.Contains("permission_expansion", Recorded(o));
        Assert.Contains("BLOCKING", Recorded(o));
    }

    [Fact]
    public void PythonOutsideArchive_IsBlocking()
    {
        var o = Run("adds helper script tools/helper.py to the repo");
        Assert.Contains("python_outside_archive", Recorded(o));
    }

    [Fact]
    public void ScopeMismatch_PathOutsideApprovedScope_IsBlocking()
    {
        var o = Run("approved_scope: docs/\nchanges docs/HOMELAB.md and src/Anthill.Core/Queen.cs");
        Assert.Contains("scope_mismatch", Recorded(o));
        Assert.Contains("BLOCKING", Recorded(o));
    }

    [Fact]
    public void AuthTouch_IsAdvisoryNotBlocking()
    {
        var o = Run("refactors RequireAuth call ordering in one endpoint");
        Assert.Contains("auth_change", Recorded(o));
        // "advisory, not blocking" used to be asserted by the absence of the phrase "deterministic
        // block" from the Compat output, which mixed Summary and payload. Recorded() is the review
        // text alone, where that phrase never appears — so the assertion would be vacuous. These
        // check the property it was standing in for.
        Assert.Equal("succeeded", o.StatusCode);
        Assert.Empty(o.Warnings);
        Assert.Contains("blocked: False", Recorded(o));
        AssertRoutesTo(o, "verifier");
    }

    [Fact]
    public void CleanDocsChange_PassesWithVerifierHandoff()
    {
        var o = Run("approved_scope: docs/\nupdates docs/HOMELAB.md wording only");
        Assert.Contains("Security review passed", o.Summary);
        AssertRoutesTo(o, "verifier");
    }

    [Fact]
    public void BlockingFindings_RouteToBuilderForOperatorExplanation()
    {
        var o = Run("patch modifies deploy/lxc/setup.sh");
        Assert.Contains("blocked_path_deploy", Recorded(o));
        AssertRoutesTo(o, "builder");
        // A blocking finding surfaces as a warning, which stage 6 reads when deciding
        // completed_verified — the review itself still succeeded.
        Assert.NotEmpty(o.Warnings);
        Assert.Equal("succeeded_with_warnings", o.StatusCode);
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
        Assert.Equal("blocked", o.StatusCode);
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
