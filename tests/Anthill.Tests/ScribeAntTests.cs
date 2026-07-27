using System.Text.Json;
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
///
/// v2.19.0: ScribeAnt returns a structured AntExecutionResult. These assertions previously
/// substring-matched the Compat() blob, including hand-written JSON fragments like
/// "\"DestinationRole\":\"verifier\"" that depended on the adapter's serialisation shape rather
/// than on routing behaviour. They now read handoffs, artifacts, warnings, and status directly.
/// </summary>
[Collection("specialist-gates")]
public class ScribeAntTests
{
    private static AntExecutionResult Run(string desc, string type, params (string ant, string title, string result)[] prior)
    {
        var t = new DomainTask { Title = "Write docs", Description = desc, AssignedAnt = "scribe", TaskType = type };
        var m = new Mission { Goal = "document the change", Tasks = { t } };
        foreach (var (ant, title, result) in prior)
            m.Tasks.Insert(0, new DomainTask { Title = title, AssignedAnt = ant, Result = result, Status = Anthill.Core.Domain.TaskStatus.Complete });
        return new ScribeAnt().Execute(t, m);
    }

    /// <summary>The recorded documentation — Queen stores Narrative ?? Summary.</summary>
    private static string Recorded(AntExecutionResult r) => r.Narrative ?? r.Summary;

    private static AntArtifact? Artifact(AntExecutionResult r, string kind) =>
        r.Artifacts.FirstOrDefault(a => a.Kind == kind);

    private static void AssertRoutesTo(AntExecutionResult r, string role) =>
        Assert.Contains(r.Handoffs, h => h.DestinationRole == role);

    [Fact]
    public void ProducesReleaseNotes_FromRealMissionResults()
    {
        var o = Run("summarize the verified change", "release_notes",
            ("coder", "Patch header", "patched src/Anthill.Api/ApiHost.cs header rendering"),
            ("verifier", "Verify", "PASS: header verified"));
        Assert.NotNull(Artifact(o, "release_notes"));
        // Evidence from the real result, on both the documentation and the structured evidence.
        Assert.Contains("src/Anthill.Api/ApiHost.cs", Recorded(o));
        Assert.Contains(o.Evidence, e => e.Kind == "file_path" && e.Value == "src/Anthill.Api/ApiHost.cs");
        AssertRoutesTo(o, "verifier");
    }

    [Fact]
    public void DocsPatchProposal_DocsTargets_Allowed_WithApprovalRequired()
    {
        var o = Run("update docs. target: docs/HOMELAB.md target: CHANGELOG.md", "docs_patch_proposal");
        var patch = Artifact(o, "docs_patch_set");
        Assert.NotNull(patch);
        // Parsed, not substring-matched: requires_approval must be true, not merely mentioned.
        var root = JsonDocument.Parse(patch!.Content).RootElement;
        Assert.True(root.GetProperty("requires_approval").GetBoolean());
        var targets = root.GetProperty("targets").EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Equal(new[] { "docs/HOMELAB.md", "CHANGELOG.md" }, targets);
    }

    [Fact]
    public void DocsPatchProposal_SourceCodeTarget_IsBlockedOutright()
    {
        var o = Run("update docs. target: src/Anthill.Core/Orchestration/Queen.cs", "docs_patch_proposal");
        Assert.Equal("blocked", o.StatusCode);
        Assert.False(o.Success);
        Assert.Contains("documentation-only restriction", o.Summary);
        Assert.Null(Artifact(o, "docs_patch_set")); // the whole proposal refused, not filtered
    }

    [Fact]
    public void DocsPatchProposal_NonMarkdownDocsFile_IsBlocked()
    {
        var o = Run("target: docs/script.ps1", "docs_patch_proposal");
        Assert.Equal("blocked", o.StatusCode);
        Assert.Null(Artifact(o, "docs_patch_set"));
    }

    [Fact]
    public void DocsPatchProposal_WithoutTargets_FailsValidation()
    {
        var o = Run("update some docs somewhere", "docs_patch_proposal");
        Assert.Equal("failed_permanent", o.StatusCode);
        Assert.Contains("requires explicit 'target:", o.Summary);
        // Validation failure is the ant's own defect-free refusal, not a transient condition:
        // a retry would produce the identical result, so it must not be retryable.
        Assert.NotNull(o.Failure);
        Assert.False(o.Failure!.Retryable);
    }

    [Fact]
    public void SecuritySensitiveDocs_RouteToSoldier()
    {
        var o = Run("document how the credential store rotation works", "operator_documentation");
        AssertRoutesTo(o, "soldier");
        Assert.Contains(o.Warnings, w => w.Contains("security-sensitive"));
        Assert.Equal("succeeded_with_warnings", o.StatusCode);
        // The operator record must carry the gate, not just the machine result.
        Assert.Contains("security-sensitive", Recorded(o));
    }

    [Fact]
    public void SecuritySensitiveHandoff_IsRequired_PlainDocsHandoffIsNot()
    {
        // The soldier route gates publication; the verifier route is ordinary follow-up.
        Assert.True(Assert.Single(Run("document the credential rotation", "operator_documentation").Handoffs).Required);
        Assert.False(Assert.Single(Run("document the button colour", "operator_documentation").Handoffs).Required);
    }

    [Fact]
    public void ForeignTaskType_IsBlockedByContract()
    {
        var o = Run("anything", "test_execution");
        Assert.Equal("blocked", o.StatusCode);
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
