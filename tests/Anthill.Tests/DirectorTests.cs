using Anthill.Api;
using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Defines a shared, non-parallel collection so the autonomy tests never race on global
/// runtime flags or the on-disk STOP sentinel.
/// </summary>
[CollectionDefinition("Autonomy")]
public class AutonomyCollection { }

/// <summary>
/// Phase 1 Colony Director loop. Runs fully offline (model routing disabled → planner/ant
/// fallbacks) so a real objective is pulled, a mission is launched through the job worker,
/// and the outcome is recorded — all without Ollama.
/// </summary>
[Collection("Autonomy")]
public class DirectorTests : IDisposable
{
    private readonly string _dir;
    private readonly Queen _queen;
    private readonly ApiJobRegistry _jobs;
    private readonly ColonyDirector _director;

    // Saved globals to restore after each test.
    private readonly bool _enableAutonomy;
    private readonly bool _enableRouting;
    private readonly bool _useOllama;
    private readonly int _poll;

    public DirectorTests()
    {
        AnthillRuntime.Initialize();
        _enableAutonomy = AnthillRuntime.EnableAutonomy;
        _enableRouting = AnthillRuntime.EnableModelRouting;
        _useOllama = AnthillRuntime.UseOllama;
        _poll = AnthillRuntime.AutonomyPollSeconds;

        // Offline + fast loop.
        AnthillRuntime.EnableAutonomy = true;
        AnthillRuntime.EnableModelRouting = false;
        AnthillRuntime.UseOllama = false;
        AnthillRuntime.AutonomyPollSeconds = 5;
        AnthillRuntime.AutonomyMaxMissionsPerHour = 100;
        AnthillRuntime.AutonomyMaxMissionsPerDay = 100;

        _dir = Path.Combine(Path.GetTempPath(), "anthill_director_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _queen = new Queen(new SqliteMemory(Path.Combine(_dir, "test.db")));
        _jobs = new ApiJobRegistry(_queen, 1);
        _director = new ColonyDirector(_queen, _jobs);
        AutonomyControl.Resume();
    }

    public void Dispose()
    {
        _director.Stop("test teardown");
        _director.Dispose();
        _jobs.Dispose();
        _queen.Dispose();
        AutonomyControl.Resume();
        AnthillRuntime.EnableAutonomy = _enableAutonomy;
        AnthillRuntime.EnableModelRouting = _enableRouting;
        AnthillRuntime.UseOllama = _useOllama;
        AnthillRuntime.AutonomyPollSeconds = _poll;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(150);
        }
        return condition();
    }

    [Fact]
    public void Start_RefusesWhenAutonomyDisabled()
    {
        AnthillRuntime.EnableAutonomy = false;
        Assert.False(_director.Start());
        Assert.False(_director.IsRunning);
        AnthillRuntime.EnableAutonomy = true;
    }

    [Fact]
    public void Director_RunsObjectiveAndRecordsOutcome()
    {
        var objective = new Objective
        {
            Title = "describe-anthill",
            Charter = "Summarize in one sentence what the ANTHILL framework does.",
            MaxRuns = 1,
        };
        _queen.Memory.SaveObjective(objective);

        Assert.True(_director.Start());
        Assert.True(_director.IsRunning);

        var completed = WaitUntil(() => _queen.Memory.GetObjective(objective.Id)?.RunCount == 1, 60000);
        _director.Stop("test done");

        Assert.True(completed, "Director did not complete the objective in time.");

        var updated = _queen.Memory.GetObjective(objective.Id)!;
        Assert.Equal(1, updated.RunCount);
        Assert.Equal(ObjectiveStatus.Done, updated.Status); // max_runs=1 reached

        var runs = _queen.Memory.ListAutonomyRuns(objective.Id);
        Assert.Single(runs);
        Assert.NotNull(runs[0].GetValueOrDefault("mission_id"));
        Assert.NotNull(runs[0].GetValueOrDefault("finished_at"));
    }

    [Fact]
    public void Director_RunsObjectivesConcurrently_AndRecordsBothOutcomes()
    {
        // Phase 3: two ready objectives, two slots, a healthy injected governor (so host load on
        // the test machine can never clamp the cap), and a 2-worker job pool.
        var savedConcurrency = AnthillRuntime.AutonomyConcurrency;
        AnthillRuntime.AutonomyConcurrency = 2;
        var jobs = new ApiJobRegistry(_queen, 2);
        var governor = new ResourceGovernor(() => 0.1, () => 0.9, () => null);
        var director = new ColonyDirector(_queen, jobs, governor);
        try
        {
            var first = new Objective { Title = "conc-a", Charter = "Summarize what ANTHILL is.", MaxRuns = 1, Priority = 2 };
            var second = new Objective { Title = "conc-b", Charter = "List the ant roles in the colony.", MaxRuns = 1, Priority = 1 };
            _queen.Memory.SaveObjective(first);
            _queen.Memory.SaveObjective(second);

            Assert.True(director.Start());

            var completed = WaitUntil(() =>
                _queen.Memory.GetObjective(first.Id)?.Status == ObjectiveStatus.Done &&
                _queen.Memory.GetObjective(second.Id)?.Status == ObjectiveStatus.Done, 120000);
            director.Stop("test done");

            Assert.True(completed, "Director did not complete both objectives in time.");
            Assert.Single(_queen.Memory.ListAutonomyRuns(first.Id));
            Assert.Single(_queen.Memory.ListAutonomyRuns(second.Id));

            // Every run must be recorded with its own mission id — no cross-talk between slots.
            var allRuns = _queen.Memory.ListAutonomyRuns();
            var missionIds = allRuns.Select(r => r.GetValueOrDefault("mission_id")?.ToString()).ToList();
            Assert.All(missionIds, id => Assert.False(string.IsNullOrEmpty(id)));
            Assert.Equal(missionIds.Count, missionIds.Distinct().Count());

            var status = director.StatusSnapshot();
            Assert.Equal(2, status["concurrency_configured"]);
            Assert.NotNull(status["governor_code"]);
        }
        finally
        {
            director.Stop("test teardown");
            director.Dispose();
            jobs.Dispose();
            AnthillRuntime.AutonomyConcurrency = savedConcurrency;
            AutonomyControl.Resume();
        }
    }

    [Fact]
    public void StatusSnapshot_ExposesConcurrencyAndGovernorFields()
    {
        var status = _director.StatusSnapshot();
        Assert.True(status.ContainsKey("concurrency_configured"));
        Assert.True(status.ContainsKey("concurrency_effective"));
        Assert.True(status.ContainsKey("governor_code"));
        Assert.True(status.ContainsKey("in_flight"));
        Assert.Empty((List<Dictionary<string, object?>>)status["in_flight"]!);
    }

    [Fact]
    public void Director_HaltsWhenKillSwitchEngagedWhileRunning()
    {
        // Empty backlog: the Director idles, so engaging the kill switch is the only thing
        // that ends the loop and no mission is ever launched.
        Assert.True(_director.Start());
        Assert.True(_director.IsRunning);

        AutonomyControl.Stop("engage while running");
        var stopped = WaitUntil(() => !_director.IsRunning, 12000);

        Assert.True(stopped, "Director did not halt after the kill switch was engaged.");
        Assert.Empty(_queen.Memory.ListAutonomyRuns());
        AutonomyControl.Resume();
    }
}
