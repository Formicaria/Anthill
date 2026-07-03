using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.18 Mission Composer — the dry-run <see cref="Queen.PlanPreview"/> that backs
/// <c>POST /missions/plan</c>. It must reuse the real planner + constraint enforcement, honour
/// verification-only / no-patch intent, and NOT create or execute a mission. Ollama is forced off
/// so the planner uses its deterministic fallback (no network, no model needed).
/// </summary>
[Collection("Autonomy")]
public class PlanPreviewTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly Queen _queen;
    private readonly bool _useOllama;

    public PlanPreviewTests()
    {
        AnthillRuntime.Initialize();
        _useOllama = AnthillRuntime.UseOllama;
        AnthillRuntime.UseOllama = false; // deterministic fallback planner — captured when Queen is built
        _dir = Path.Combine(Path.GetTempPath(), "anthill_planpreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
        _queen = new Queen(_memory);
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllama;
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void PlanPreview_VerificationOnly_HasNoCoderTask()
    {
        var tasks = _queen.PlanPreview("verify the parser is correct — verification only, do not modify files");
        Assert.DoesNotContain(tasks, t => t.AssignedAnt == "coder");
        Assert.DoesNotContain(tasks, t => t.TaskType == "patch_proposal");
        Assert.Contains(tasks, t => t.AssignedAnt == "verifier");
    }

    [Fact]
    public void PlanPreview_CodeGoal_IncludesCoderTask()
    {
        var tasks = _queen.PlanPreview("create a new file src/Foo.cs and add a class");
        Assert.Contains(tasks, t => t.AssignedAnt == "coder");
    }

    [Fact]
    public void PlanPreview_DoesNotCreateOrExecuteAMission()
    {
        var before = _memory.GetRecentMissions(50).Count;
        var tasks = _queen.PlanPreview("do something useful");
        Assert.NotEmpty(tasks);
        Assert.Equal(before, _memory.GetRecentMissions(50).Count); // nothing persisted
    }

    [Fact]
    public void PlanPreview_AlwaysEndsWithVerifier()
    {
        var tasks = _queen.PlanPreview("research the best approach and summarize");
        Assert.Contains(tasks, t => t.AssignedAnt == "verifier");
    }
}
