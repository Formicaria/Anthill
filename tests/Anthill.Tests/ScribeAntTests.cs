using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D-4 validation gate (spec §15 SCRIBEANT): produces docs artifacts from real mission
/// results; docs patch proposals are restricted to documentation paths (source-code targets are
/// BLOCKED, not warned); proposals always carry requires_approval and no apply permission exists;
/// security-sensitive docs route to the soldier; gates control executability.
/// </summary>
[Collection("specialist-gates")]
public class ScribeAntTests
{
    private static string Run(string desc, string type, params (string ant, string title, string result)[] prior)
    {
        var t = new DomainTask { Title = "Write docs", Description = desc, AssignedAnt = "scribe", TaskType = type };
        var m = new Mission { Goal = "document the change", Tasks = { t } };
        foreach (var (ant, title, result) in prior)
            m.Tasks.Insert(0, new DomainTask { Title = title, AssignedAnt = ant, Result = result, Status = Anthill.Core.Domain.TaskStatus.Complete });
        return new ScribeAnt().Run(t, m);
    }

    [Fact]
    public void ProducesReleaseNotes_FromRealMissionResults()
    {
        var o = Run("summarize the verified change", "release_notes",
            ("coder", "Patch header", "patched src/Anthill.Api/ApiHost.cs header rendering"),
            ("verifier", "Verify", "PASS: header verified"));
        Assert.Contains("release_notes", o);
        Assert.Contains("src/Anthill.Api/ApiHost.cs", o); // evidence from the real result
        Assert.Contains("\"DestinationRole\":\"verifier\"", o.Replace(" ", ""));
    }

    [Fact]
    public void DocsPatchProposal_DocsTargets_Allowed_WithApprovalRequired()
    {
        var o = Run("update docs. target: docs/HOMELAB.md target: CHANGELOG.md", "docs_patch_proposal");
        Assert.Contains("docs_patch_set", o);
        Assert.Contains("requires_approval", o);
    }

    [Fact]
    public void DocsPatchProposal_SourceCodeTarget_IsBlockedOutright()
    {
        var o = Run("update docs. target: src/Anthill.Core/Orchestration/Queen.cs", "docs_patch_proposal");
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
        Assert.Contains("documentation-only restriction", o);
        Assert.DoesNotContain("docs_patch_set", o); // the whole proposal refused, not filtered
    }

    [Fact]
    public void DocsPatchProposal_NonMarkdownDocsFile_IsBlocked()
    {
        var o = Run("target: docs/script.ps1", "docs_patch_proposal");
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
    }

    [Fact]
    public void DocsPatchProposal_WithoutTargets_FailsValidation()
    {
        var o = Run("update some docs somewhere", "docs_patch_proposal");
        Assert.Contains("failed_permanent", o);
        Assert.Contains("requires explicit 'target:", o);
    }

    [Fact]
    public void SecuritySensitiveDocs_RouteToSoldier()
    {
        var o = Run("document how the credential store rotation works", "operator_documentation");
        Assert.Contains("\"DestinationRole\":\"soldier\"", o.Replace(" ", ""));
        Assert.Contains("security-sensitive", o);
    }

    [Fact]
    public void ForeignTaskType_IsBlockedByContract()
    {
        var o = Run("anything", "test_execution");
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
    }

    [Fact]
    public void Scribe_HasNoApplyPath_Anywhere()
    {
        Assert.False(Anthill.Core.Tools.ToolAuthorization.Evaluate("scribe", "apply_patch").Allowed);
        Assert.False(Anthill.Core.Tools.ToolAuthorization.Evaluate("scribe", "shell_command").Allowed);
        Assert.False(AntExecutionCatalog.ContractFor("scribe")!.AllowedTools.Contains("apply_patch"));
    }

    [Fact]
    public void GatesControlExecutability()
    {
        Assert.DoesNotContain("scribe", AntRegistry.ExecutableRoleIds);
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableScribeAnt = true;
            Assert.Contains("scribe", AntRegistry.ExecutableRoleIds);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableScribeAnt = false;
        }
    }
}
