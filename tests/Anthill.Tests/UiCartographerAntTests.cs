using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D canary 1 validation gate (spec §15): UICartographerAnt reads UI files through the
/// enforced dispatch path, produces a structured map with evidence and a coder handoff, cannot
/// write or shell, and is planner-visible ONLY while both rollout gates are open.
/// Gate-flipping tests share a collection so they can never race other gate-sensitive tests.
/// </summary>
[Collection("specialist-gates")]
public class UiCartographerAntTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_uic_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory? _mem;

    private (UiCartographerAnt Ant, string Ws) Harness()
    {
        Directory.CreateDirectory(_dir);
        var ws = Path.Combine(_dir, "ws"); Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "index.html"),
            "<div class=\"page\" id=\"page-overview\"></div><div class=\"page\" id=\"page-colony\"></div>" +
            "<style>.x{}</style><script>function loadColony(){ api('/colony/registry'); } function showPage(x){}</script>");
        _mem = new SqliteMemory(Path.Combine(_dir, "t.db"));
        var tools = new ToolRegistry(_mem);
        var guard = new WorkspacePathGuard(ws);
        tools.Register(new DirectoryListTool(guard));
        tools.Register(new ReadTextFileTool(guard));
        tools.Register(new WriteTextFileTool(guard)); // present but must be unreachable for this role
        return (new UiCartographerAnt(tools), ws);
    }

    private static (DomainTask, Mission) UiTask()
    {
        var t = new DomainTask { Title = "Map the UI", Description = "map it", AssignedAnt = "ui_cartographer", TaskType = "ui_mapping" };
        var m = new Mission { Goal = "map the ui", Tasks = { t } };
        return (t, m);
    }

    [Fact]
    public void ProducesStructuredUiMap_WithRoutesFunctionsApisAndEvidence()
    {
        var (ant, _) = Harness();
        var (t, m) = UiTask();
        _mem!.SaveMission(m); // tool audit events FK onto the mission row, exactly like the real runtime
        var output = ant.Run(t, m);
        Assert.Contains("UI_MAP_JSON:", output);
        Assert.Contains("page-overview", output.Replace("\"", "")); // likely_modification_points
        Assert.Contains("overview", output);
        Assert.Contains("colony", output);
        Assert.Contains("loadColony", output);
        Assert.Contains("/colony/registry", output);
        Assert.Contains("index.html", output); // files examined = evidence
    }

    [Fact]
    public void CannotWrite_DispatchDeniesEvenIfHandlerTried()
    {
        var (ant, ws) = Harness();
        // The handler never calls write tools; prove the BOUNDARY holds even if it did.
        var denied = ToolAuthorization.Evaluate("ui_cartographer", "write_text_file");
        Assert.False(denied.Allowed);
        Assert.False(ToolAuthorization.Evaluate("ui_cartographer", "shell_command").Allowed);
        Assert.False(ToolAuthorization.Evaluate("ui_cartographer", "apply_patch").Allowed);
        _ = ant; _ = ws;
    }

    [Fact]
    public void GatesClosed_RoleIsNotExecutable_AndTasksAreRejected()
    {
        Assert.DoesNotContain("ui_cartographer", AntRegistry.ExecutableRoleIds);
        var (t, _) = UiTask();
        var v = AntRegistry.ValidateTask(t, MissionConstraints.Parse("map the ui"));
        Assert.False(v.Allowed);
        Assert.Contains("visible-only", v.Reason);
    }

    [Fact]
    public void GatesOpen_RoleBecomesExecutable_AndTasksValidate()
    {
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableUiCartographerAnt = true;
            Assert.Contains("ui_cartographer", AntRegistry.ExecutableRoleIds);
            var (t, _) = UiTask();
            var v = AntRegistry.ValidateTask(t, MissionConstraints.Parse("map the ui"));
            Assert.True(v.Allowed, v.Reason);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableUiCartographerAnt = false;
        }
    }

    [Fact]
    public void MasterGateAlone_IsNotEnough()
    {
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true; // per-role gate still closed
            Assert.DoesNotContain("ui_cartographer", AntRegistry.ExecutableRoleIds);
        }
        finally { AnthillRuntime.EnableSpecialistAntExecution = false; }
    }
}
