using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 4 learning loop: success-EMA maintenance, the bounded read-time priority bias, its
/// effect on multi-slot selection, and stale/looping retirement decisions. All offline and
/// deterministic; runtime knobs are saved/restored around every test.
/// </summary>
[Collection("Autonomy")]
public class LearningTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly bool _learning;
    private readonly int _biasMax;
    private readonly double _alpha;
    private readonly int _retireMinRuns;
    private readonly double _retireThreshold;
    private readonly int _loopWindow;
    private readonly int _aging;

    public LearningTests()
    {
        AnthillRuntime.Initialize();
        _learning = AnthillRuntime.AutonomyLearningEnabled;
        _biasMax = AnthillRuntime.AutonomyPriorityBiasMax;
        _alpha = AnthillRuntime.AutonomyScoreEmaAlpha;
        _retireMinRuns = AnthillRuntime.AutonomyRetireMinRuns;
        _retireThreshold = AnthillRuntime.AutonomyRetireScoreThreshold;
        _loopWindow = AnthillRuntime.AutonomyLoopWindow;
        _aging = AnthillRuntime.AutonomyAgingMinutes;

        AnthillRuntime.AutonomyLearningEnabled = true;
        AnthillRuntime.AutonomyPriorityBiasMax = 2;
        AnthillRuntime.AutonomyScoreEmaAlpha = 0.3;
        AnthillRuntime.AutonomyRetireMinRuns = 5;
        AnthillRuntime.AutonomyRetireScoreThreshold = 0.25;
        AnthillRuntime.AutonomyLoopWindow = 4;
        AnthillRuntime.AutonomyAgingMinutes = 0; // isolate the learning bias from aging

        _dir = Path.Combine(Path.GetTempPath(), "anthill_learning_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        AnthillRuntime.AutonomyLearningEnabled = _learning;
        AnthillRuntime.AutonomyPriorityBiasMax = _biasMax;
        AnthillRuntime.AutonomyScoreEmaAlpha = _alpha;
        AnthillRuntime.AutonomyRetireMinRuns = _retireMinRuns;
        AnthillRuntime.AutonomyRetireScoreThreshold = _retireThreshold;
        AnthillRuntime.AutonomyLoopWindow = _loopWindow;
        AnthillRuntime.AutonomyAgingMinutes = _aging;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Objective Active(string title, int priority, int runCount = 0, double? ema = null) => new()
    {
        Title = title, Charter = $"charter for {title}", Priority = priority,
        Status = ObjectiveStatus.Active, RunCount = runCount, SuccessEma = ema,
    };

    // ---- EMA -----------------------------------------------------------------

    [Fact]
    public void UpdateEma_FirstRunSeedsDirectly_ThenSmooths()
    {
        Assert.Equal(0.8, ObjectiveLearning.UpdateEma(null, 0.8), 10);
        // 0.3 * 0.0 + 0.7 * 0.8 = 0.56
        Assert.Equal(0.56, ObjectiveLearning.UpdateEma(0.8, 0.0), 10);
    }

    [Fact]
    public void UpdateEma_NullScoreCountsAsZero()
    {
        Assert.Equal(0.0, ObjectiveLearning.UpdateEma(null, null), 10);
        Assert.Equal(0.7, ObjectiveLearning.UpdateEma(1.0, null), 10); // 0.3*0 + 0.7*1
    }

    [Fact]
    public void RecordObjectiveRunOutcome_PersistsEma()
    {
        var o = Active("persists-ema", 1);
        _memory.SaveObjective(o);
        _memory.RecordObjectiveRunOutcome(o.Id, success: true, successScore: 0.9);

        var loaded = _memory.GetObjective(o.Id)!;
        Assert.NotNull(loaded.SuccessEma);
        Assert.Equal(0.9, loaded.SuccessEma!.Value, 10);
    }

    // ---- priority bias ---------------------------------------------------------

    [Fact]
    public void PriorityBias_LinearAndBounded()
    {
        Assert.Equal(2, ObjectiveLearning.PriorityBias(Active("hi", 0, ema: 1.0)));
        Assert.Equal(0, ObjectiveLearning.PriorityBias(Active("mid", 0, ema: 0.5)));
        Assert.Equal(-2, ObjectiveLearning.PriorityBias(Active("lo", 0, ema: 0.0)));
        Assert.Equal(0, ObjectiveLearning.PriorityBias(Active("new", 0, ema: null))); // unbiased until first run
    }

    [Fact]
    public void PriorityBias_ZeroWhenLearningDisabled()
    {
        AnthillRuntime.AutonomyLearningEnabled = false;
        Assert.Equal(0, ObjectiveLearning.PriorityBias(Active("hi", 0, ema: 1.0)));
        AnthillRuntime.AutonomyLearningEnabled = true;
    }

    [Fact]
    public void NextReadyObjectives_HighEmaOutranksEqualStoredPriority()
    {
        var winner = Active("winner", 5, runCount: 3, ema: 1.0);   // effective 5 + 2
        var loser = Active("loser", 6, runCount: 3, ema: 0.0);     // effective 6 - 2
        _memory.SaveObjective(winner);
        _memory.SaveObjective(loser);

        Assert.Equal(winner.Id, _memory.NextReadyObjective()!.Id);

        // Learning off → pure stored priority wins again.
        AnthillRuntime.AutonomyLearningEnabled = false;
        Assert.Equal(loser.Id, _memory.NextReadyObjective()!.Id);
        AnthillRuntime.AutonomyLearningEnabled = true;
    }

    // ---- retirement --------------------------------------------------------------

    [Fact]
    public void EvaluateRetirement_StaleAfterEnoughLowScoringRuns()
    {
        var stale = Active("stale", 5, runCount: 5, ema: 0.1);
        var decision = ObjectiveLearning.EvaluateRetirement(stale, new List<string>());
        Assert.NotNull(decision);
        Assert.Equal("stale_low_success", decision!.Code);
    }

    [Fact]
    public void EvaluateRetirement_NotStaleBeforeMinRuns_OrAboveThreshold()
    {
        Assert.Null(ObjectiveLearning.EvaluateRetirement(Active("young", 5, runCount: 4, ema: 0.1), new List<string>()));
        Assert.Null(ObjectiveLearning.EvaluateRetirement(Active("healthy", 5, runCount: 20, ema: 0.6), new List<string>()));
    }

    [Fact]
    public void EvaluateRetirement_LoopingWhenRecentGoalsAreNearIdentical()
    {
        var looping = Active("looping", 5, runCount: 4, ema: 0.6); // healthy EMA — loop check must catch it
        var sameGoal = "Audit the dependency manifest and propose safe upgrades for the colony runtime";
        var goals = Enumerable.Repeat(sameGoal, 4).ToList();
        var decision = ObjectiveLearning.EvaluateRetirement(looping, goals);
        Assert.NotNull(decision);
        Assert.Equal("looping_goals", decision!.Code);
    }

    [Fact]
    public void EvaluateRetirement_DistinctGoalsAreNotALoop()
    {
        var o = Active("varied", 5, runCount: 6, ema: 0.6);
        var goals = new List<string>
        {
            "Audit the dependency manifest and propose safe upgrades",
            "Profile SQLite query latency across the pheromone tables",
            "Write integration coverage for the approval workflow endpoints",
            "Summarize open source records into a research digest",
        };
        Assert.Null(ObjectiveLearning.EvaluateRetirement(o, goals));
    }

    [Fact]
    public void EvaluateRetirement_DisabledOrNonActive_NeverRetires()
    {
        var stale = Active("stale", 5, runCount: 10, ema: 0.0);

        AnthillRuntime.AutonomyLearningEnabled = false;
        Assert.Null(ObjectiveLearning.EvaluateRetirement(stale, new List<string>()));
        AnthillRuntime.AutonomyLearningEnabled = true;

        stale.Status = ObjectiveStatus.Paused; // breaker already handled it
        Assert.Null(ObjectiveLearning.EvaluateRetirement(stale, new List<string>()));
    }
}
