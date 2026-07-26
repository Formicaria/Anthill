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

    // ---- TesterAnt behavior --------------------------------------------------------------------

    [Fact]
    public void PassingCheck_ProducesReportEvidence_AndVerifierHandoff()
    {
        var ant = Harness();
        var (t, m) = CheckTask("run dotnet_version only");
        var output = ant.Run(t, m);
        Assert.Contains("dotnet_version: PASS", output);
        Assert.Contains("test_report", output);
        Assert.Contains("\"DestinationRole\":\"verifier\"", output.Replace(" ", ""));
    }

    [Fact]
    public void FailingCheck_HandsOffToMedic_NeverInventsSuccess()
    {
        CheckCatalog.Register(new CheckDefinition("always_fails", "dotnet", "not-a-real-verb", 60, true, "fails on purpose"));
        var ant = Harness();
        var (t, m) = CheckTask("run always_fails");
        var output = ant.Run(t, m);
        Assert.Contains("always_fails: FAIL", output);
        Assert.Contains("\"status\":\"failed_retryable\"", output.Replace(" ", ""));
        Assert.Contains("\"DestinationRole\":\"medic\"", output.Replace(" ", ""));
    }

    [Fact]
    public void ForeignTaskType_IsBlockedByContract()
    {
        var ant = Harness();
        var (t, m) = CheckTask("whatever", type: "ui_mapping");
        var output = ant.Run(t, m);
        Assert.Contains("\"status\":\"blocked\"", output.Replace(" ", ""));
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
