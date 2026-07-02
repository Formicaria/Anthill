using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 2 autonomy: the Strategist's fail-closed fallback path, and the objective-depth helper
/// its follow-up cap relies on. Runs fully offline — with no model router configured, the
/// Strategist must degrade to exactly the Phase 1 behaviour (charter verbatim, no follow-ups)
/// without ever calling out to a model. Its LLM-driven paths (goal synthesis, dedup rejection,
/// follow-up parsing) are exercised live only when a real router is available — not testable
/// here without network access, so they're left to manual/integration verification.
/// </summary>
public class StrategistTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public StrategistTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_strategist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void NoRouter_FallsBackToCharterVerbatim()
    {
        var strategist = new Strategist(router: null, _memory);
        var objective = new Objective { Title = "audit-deps", Charter = "Audit project dependencies weekly." };

        var result = strategist.GenerateGoal(objective);

        Assert.Equal("fallback", result.Source);
        Assert.Equal(objective.Charter, result.Goal);
        Assert.Empty(result.FollowUps);
    }

    [Fact]
    public void NoRouter_FallsBackToTitle_WhenCharterIsBlank()
    {
        var strategist = new Strategist(router: null, _memory);
        var objective = new Objective { Title = "nightly-scan", Charter = "   " };

        var result = strategist.GenerateGoal(objective);

        Assert.Equal("fallback", result.Source);
        Assert.Equal(objective.Title, result.Goal);
    }

    [Fact]
    public void OneShotObjective_UsesCharterVerbatim_EvenWithNoRouter()
    {
        // max_runs==1 is an explicit do-this-once task; the Strategist must not "diversify" it
        // (that drift once turned "create docs/x.md" into "train a model"). Charter is used as-is.
        var strategist = new Strategist(router: null, _memory);
        var oneShot = new Objective { Title = "make-file", Charter = "Create a new file docs/x.md.", MaxRuns = 1 };

        var result = strategist.GenerateGoal(oneShot);

        Assert.Equal("charter_verbatim", result.Source);
        Assert.Equal(oneShot.Charter, result.Goal);
        Assert.Empty(result.FollowUps);
        Assert.Contains("one-shot", (result.Notes ?? "").ToLowerInvariant());
    }

    [Fact]
    public void NoRouter_NeverThrowsAndNeverBlocks()
    {
        var strategist = new Strategist(router: null, _memory);
        var objective = new Objective { Title = "t", Charter = "c" };

        // Should return immediately — no attempted model call when there's no router at all.
        var result = strategist.GenerateGoal(objective);
        Assert.NotNull(result);
    }

    [Fact]
    public void ObjectiveDepth_RootObjectiveIsZero()
    {
        var root = new Objective { Title = "root", Charter = "root charter" };
        _memory.SaveObjective(root);

        Assert.Equal(0, _memory.ObjectiveDepth(root.Id));
    }

    [Fact]
    public void ObjectiveDepth_WalksTheParentChain()
    {
        var root = new Objective { Title = "root", Charter = "c" };
        _memory.SaveObjective(root);
        var child = new Objective { Title = "child", Charter = "c", ParentObjectiveId = root.Id };
        _memory.SaveObjective(child);
        var grandchild = new Objective { Title = "grandchild", Charter = "c", ParentObjectiveId = child.Id };
        _memory.SaveObjective(grandchild);

        Assert.Equal(0, _memory.ObjectiveDepth(root.Id));
        Assert.Equal(1, _memory.ObjectiveDepth(child.Id));
        Assert.Equal(2, _memory.ObjectiveDepth(grandchild.Id));
    }

    [Fact]
    public void ObjectiveDepth_HandlesDanglingParentGracefully()
    {
        // Points at a parent id that doesn't exist — must count the one real hop and stop,
        // never throw and never loop.
        var orphan = new Objective { Title = "orphan", Charter = "c", ParentObjectiveId = "does-not-exist" };
        _memory.SaveObjective(orphan);

        Assert.Equal(1, _memory.ObjectiveDepth(orphan.Id));
    }

    [Fact]
    public void ObjectiveDepth_UnknownObjectiveIsZero()
    {
        Assert.Equal(0, _memory.ObjectiveDepth("no-such-objective"));
    }
}
