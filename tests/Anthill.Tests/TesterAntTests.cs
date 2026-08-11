using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D-2 validation gate (spec §15 TESTERANT): only allowlisted checks run, unknown/disabled
/// checks refuse before any process starts, reports carry deterministic evidence (exit codes),
/// failure hands to medic and success to verifier, arbitrary shell stays structurally denied,
/// and the role is executable only behind its gates.
/// </summary>
[Collection("specialist-gates")]
public class TesterAntTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_tester_" + Guid.NewGuid().ToString("N"));
    private SqliteMemory? _mem;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private TesterAnt Harness()
    {
        Directory.CreateDirectory(_dir);
        _mem = new SqliteMemory(Path.Combine(_dir, "t.db"));
        var tools = new ToolRegistry(_mem);
        tools.Register(new RunAllowlistedCheckTool(_dir));
        return new TesterAnt(tools);
    }

    private (DomainTask, Mission) CheckTask(string desc, string type = "test_execution")
    {
        var t = new DomainTask { Title = "Run checks", Description = desc, AssignedAnt = "tester", TaskType = type };
        var m = new Mission { Goal = "check things", Tasks = { t } };
        _mem!.SaveMission(m);
        return (t, m);
    }

    // ---- Check catalog boundaries --------------------------------------------------------------

    [Fact]
    public void UnknownCheck_RefusedBeforeAnyProcessStarts()
    {
        var tool = new RunAllowlistedCheckTool(_dir);
        var r = tool.Run(new Dictionary<string, object?> { ["check_id"] = "rm -rf /" });
        Assert.False(r.Success);
        Assert.Contains("not in the allowlisted catalog", r.Error);
    }

    [Fact]
    public void DisabledCheck_Refused()
    {
        CheckCatalog.Register(new CheckDefinition("disabled_probe", "dotnet", "--version", 30, Enabled: false, "off"));
        var r = new RunAllowlistedCheckTool(_dir).Run(new Dictionary<string, object?> { ["check_id"] = "disabled_probe" });
        Assert.False(r.Success);
        Assert.Contains("disabled", r.Error);
    }

    [Fact]
    public void AllowlistedCheck_RunsAndReportsExitCodeEvidence()
    {
        var r = new RunAllowlistedCheckTool(Path.GetTempPath()).Run(new Dictionary<string, object?> { ["check_id"] = "dotnet_version" });
        Assert.True(r.Success, r.Error);
        Assert.Contains("exit_code=0", r.Output);
        Assert.Contains("check_id=dotnet_version", r.Output);
    }

    // ---- Which tree was judged -----------------------------------------------------------------
    //
    // v0.3.8.41. RunAllowlistedCheckTool resolves its working directory from whatever workspace is
    // ambient, and a mission has TWO at different moments: the mission workspace, which is the
    // source as the coder left it, and the disposable tree VerifyPatchSet materialises a patch set
    // into. Only the second contains the proposal.
    //
    // A tester ant runs as its own task in the DAG, after VerifyPatchSet has returned and disposed
    // its scope — so it resolves to the FIRST, and "3 checks passed" was recorded as though it said
    // something about the patch. It did not; it was a true statement about a tree with no patch in
    // it, which is the same shape as v3.8.22's build verdicts.
    //
    // These pin the distinction into the record. They do not claim the tester runs on the patched
    // tree — it does not, and a test asserting otherwise would be describing a fix that is not here.

    [Fact]
    public void AReportFromTheMissionWorkspace_SaysTheTreeIsUnpatched()
    {
        var tester = Harness();
        var (task, mission) = CheckTask("dotnet_version");

        using var _ = Anthill.Core.Workspaces.MissionWorkspaceScope.Enter(new Anthill.Core.Workspaces.MissionWorkspace
        {
            Id = "ws-1", MissionId = mission.Id, Root = _dir,
            State = Anthill.Core.Workspaces.WorkspaceState.Active,
            // No MaterializedPatchSetId: this is the coder's tree, not a patched one.
        });

        var result = tester.Execute(task, mission);

        Assert.Contains("UNPATCHED", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.Evidence, e => e.Kind == "workspace");
    }

    [Fact]
    public void AReportFromAMaterializedPatchTree_NamesThePatchSet()
    {
        var tester = Harness();
        var (task, mission) = CheckTask("dotnet_version");

        using var _ = Anthill.Core.Workspaces.MissionWorkspaceScope.Enter(new Anthill.Core.Workspaces.MissionWorkspace
        {
            Id = "verify-ps-7", MissionId = mission.Id, Root = _dir,
            State = Anthill.Core.Workspaces.WorkspaceState.Active,
            MaterializedPatchSetId = "ps-7",
        });

        var result = tester.Execute(task, mission);

        Assert.Contains("ps-7", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("UNPATCHED", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pass/fail counts are parsed out of the report lines, so the line naming the tree must not
    /// be counted as a check. Worth pinning rather than assuming: the counting is substring-based,
    /// and a tree description is free text that will change.
    /// </summary>
    [Fact]
    public void TheTreeLine_IsNotCountedAsACheck()
    {
        var tester = Harness();
        var (task, mission) = CheckTask("dotnet_version");

        var result = tester.Execute(task, mission);

        Assert.Contains("1 check(s)", result.Summary, StringComparison.Ordinal);
        Assert.Single(result.Evidence, e => e.Kind == "check");
    }

    // ---- TesterAnt behavior --------------------------------------------------------------------

    // v2.19.0: these three asserted against the JSON that Compat() embedded in the returned
    // string. TesterAnt now returns a real AntExecutionResult, so they assert on structure
    // instead of substring-matching serialised prose. The behaviour proven is unchanged and the
    // assertions are stronger — a handoff is now checked by field, not by hoping the right text
    // appears somewhere in the output.

    [Fact]
    public void PassingCheck_ProducesReportEvidence_AndVerifierHandoff()
    {
        var ant = Harness();
        var (t, m) = CheckTask("run dotnet_version only");
        var result = ant.Execute(t, m);

        Assert.Equal("succeeded", result.StatusCode);
        Assert.True(result.Success);
        Assert.Contains(result.Artifacts, a => a.Kind == "test_report" && a.Content.Contains("dotnet_version: PASS"));
        Assert.Contains(result.Evidence, e => e.Kind == "check" && e.Value == "dotnet_version");
        Assert.Contains(result.Handoffs, h => h.DestinationRole == "verifier");
    }

    [Fact]
    public void FailingCheck_HandsOffToMedic_NeverInventsSuccess()
    {
        CheckCatalog.Register(new CheckDefinition("always_fails", "dotnet", "not-a-real-verb", 60, true, "fails on purpose"));
        var ant = Harness();
        var (t, m) = CheckTask("run always_fails");
        var result = ant.Execute(t, m);

        Assert.False(result.Success);
        Assert.Equal("failed_retryable", result.StatusCode);
        Assert.Contains(result.Artifacts, a => a.Content.Contains("always_fails: FAIL"));

        var medic = Assert.Single(result.Handoffs, h => h.DestinationRole == "medic");
        Assert.True(medic.Required, "a failure diagnosis handoff is required, not advisory");

        // v2.19.0: and it can no longer be recorded as a completed task.
        Assert.False(Anthill.Core.Outcomes.TaskOutcomeMapper.IsCompleting(result));
    }

    [Fact]
    public void ForeignTaskType_IsBlockedByContract()
    {
        var ant = Harness();
        var (t, m) = CheckTask("whatever", type: "ui_mapping");
        var result = ant.Execute(t, m);

        Assert.Equal("blocked", result.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(Anthill.SDK.Contracts.FailureClass.AuthorizationFailure, result.Failure!.Class);
    }

    // ---- Boundaries + gates --------------------------------------------------------------------

    [Fact]
    public void Tester_CannotShellWritePatch_StructuralDenial()
    {
        Assert.False(ToolAuthorization.Evaluate("tester", "shell_command").Allowed);
        Assert.False(ToolAuthorization.Evaluate("tester", "write_text_file").Allowed);
        Assert.False(ToolAuthorization.Evaluate("tester", "apply_patch").Allowed);
        Assert.True(ToolAuthorization.Evaluate("tester", "run_allowlisted_check").Allowed);
    }

    [Fact]
    public void GatesControlExecutability()
    {
        Assert.DoesNotContain("tester", AntRegistry.ExecutableRoleIds);
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableTesterAnt = true;
            Assert.Contains("tester", AntRegistry.ExecutableRoleIds);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableTesterAnt = false;
        }
    }
}
