using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Skills;
using Anthill.Core.Workers;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The four questions colony memory should answer. v3.8.19 — post-refactor stage 3.
///
/// Every test here asserts a NON-EMPTY, correctly-ordered result. That is deliberate and it is the
/// lesson from writing this file: the first draft of <c>WhatHasWorked</c> filtered on a
/// <c>signal_category</c> value that does not exist, so it returned an empty list forever. A test
/// that only checked "does not throw" would have passed. An empty result is the failure mode these
/// queries have, so it is the failure mode the tests are shaped around.
/// </summary>
public class ColonyRecallTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anthill-recall-{Guid.NewGuid():N}.db");
    private readonly SqliteMemory _memory;

    public ColonyRecallTests()
    {
        _memory = new SqliteMemory(_dbPath);
        // `tasks` carries FOREIGN KEY (mission_id) REFERENCES missions(id), so the mission has to
        // exist before any task does. The fixture's first draft saved tasks against a mission id
        // that was never persisted and SQLite refused it — correctly.
        _memory.SaveMission(new Mission { Id = "m1", Goal = "recall fixtures" });
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ---- "What has worked before?" ----------------------------------------

    /// <summary>
    /// A planner pattern that keeps succeeding surfaces; tool reliability does not. The signal
    /// categories exist precisely to keep "did the tool answer" out of "what strategy works", and a
    /// recall method that ignored them would re-admit telemetry into planning.
    /// </summary>
    [Fact]
    public void WhatHasWorked_ReturnsLearningSignalsAndExcludesTelemetry()
    {
        _memory.UpdatePheromoneTrail("pattern:split-then-verify", "planner_pattern", success: true, strengthDelta: 0.3);
        _memory.UpdatePheromoneTrail("tool:read_text_file", "tool", success: true, strengthDelta: 0.3);

        var worked = _memory.WhatHasWorked();

        Assert.NotEmpty(worked);
        Assert.Contains(worked, r => r["trail_key"]?.ToString() == "pattern:split-then-verify");
        Assert.DoesNotContain(worked, r => r["trail_key"]?.ToString() == "tool:read_text_file");
    }

    /// <summary>A trail that fails more than it succeeds is not something that worked.</summary>
    [Fact]
    public void WhatHasWorked_ExcludesFailureDominantTrails()
    {
        _memory.UpdatePheromoneTrail("pattern:doomed", "planner_pattern", success: false, strengthDelta: -0.1);
        _memory.UpdatePheromoneTrail("pattern:doomed", "planner_pattern", success: false, strengthDelta: -0.1);

        Assert.DoesNotContain(_memory.WhatHasWorked(), r => r["trail_key"]?.ToString() == "pattern:doomed");
    }

    [Fact]
    public void WhatHasWorked_OrdersByStrength()
    {
        _memory.UpdatePheromoneTrail("pattern:weak", "planner_pattern", success: true, strengthDelta: 0.05);
        _memory.UpdatePheromoneTrail("pattern:strong", "planner_pattern", success: true, strengthDelta: 0.45);

        var worked = _memory.WhatHasWorked();

        Assert.Equal("pattern:strong", worked[0]["trail_key"]?.ToString());
    }

    // ---- "What usually fails?" --------------------------------------------

    /// <summary>
    /// Failure classes by frequency, and — the useful half — how often the class survived a retry.
    /// A class that fails once and passes on attempt two is a flake; one that fails every attempt is
    /// a wall the colony should stop paying for. That distinction was already recorded and never read.
    /// </summary>
    [Fact]
    public void WhatUsuallyFails_RanksClassesAndCountsRepeatFailures()
    {
        RecordFailedAttempt("t1", "TransientProviderFailure");
        RecordFailedAttempt("t1", "TransientProviderFailure");   // attempt two on the same task
        RecordFailedAttempt("t2", "ValidationFailure");

        var failures = _memory.WhatUsuallyFails();

        Assert.NotEmpty(failures);
        var top = failures[0];
        Assert.Equal("TransientProviderFailure", top["failure_class"]?.ToString());
        Assert.Equal(2L, Convert.ToInt64(top["occurrences"]));
        Assert.Equal(1L, Convert.ToInt64(top["failed_again_on_retry"]));
    }

    [Fact]
    public void WhatUsuallyFails_IsEmptyOnAColonyThatHasNeverFailed() =>
        Assert.Empty(_memory.WhatUsuallyFails());

    // ---- "Who solved this previously?" ------------------------------------

    [Fact]
    public void WhoSolvedThis_FindsTheRoleWithTheRecord()
    {
        RecordCompletedTask("t10", "Refactor the parser module", "coder", success: true);
        RecordCompletedTask("t11", "Refactor the parser module again", "coder", success: true);
        RecordCompletedTask("t12", "Write the release notes", "scribe", success: true);

        var who = _memory.WhoSolvedThis("parser");

        Assert.NotEmpty(who);
        Assert.Equal("coder", who[0]["ant_name"]?.ToString());
        Assert.Equal(2L, Convert.ToInt64(who[0]["successes"]));
        Assert.DoesNotContain(who, r => r["ant_name"]?.ToString() == "scribe");
    }

    /// <summary>A failed attempt is not a solution, however much of it there is.</summary>
    [Fact]
    public void WhoSolvedThis_IgnoresFailures()
    {
        RecordCompletedTask("t20", "Fix the flaky migration", "medic", success: false);

        Assert.Empty(_memory.WhoSolvedThis("migration"));
    }

    /// <summary>
    /// A two-character topic would match most titles in the database. Refused rather than answered
    /// badly — a recall method that returns everything has told the caller nothing.
    /// </summary>
    [Fact]
    public void WhoSolvedThis_RefusesATopicTooShortToMeanAnything() =>
        Assert.Empty(_memory.WhoSolvedThis("ab"));

    // ---- "What knowledge already exists?" ---------------------------------

    /// <summary>
    /// Candidates are excluded. A candidate is a route observed once and never re-verified; listing
    /// it as knowledge is exactly how observation gets mistaken for proof.
    /// </summary>
    [Fact]
    public void WhatKnowledgeExists_ExcludesUnprovenCandidates()
    {
        _memory.SaveSkill(new Skill { Id = "skill-proven", Purpose = "run the build", Status = SkillStatus.Certified });
        _memory.SaveSkill(new Skill { Id = "skill-guess", Purpose = "maybe works", Status = SkillStatus.Candidate });

        var known = _memory.WhatKnowledgeExists();

        Assert.Contains(known, r => r["id"]?.ToString() == "skill-proven");
        Assert.DoesNotContain(known, r => r["id"]?.ToString() == "skill-guess");
    }

    // ---- fixtures ---------------------------------------------------------

    /// <summary>
    /// A real claim-then-finish cycle rather than a hand-written row, so the fixture exercises the
    /// same path production does. `number` comes out of the claim: a second claim on the same task
    /// IS attempt two, which is what makes the retry column meaningful.
    /// </summary>
    private void RecordFailedAttempt(string taskId, string failureClass)
    {
        _memory.SaveWorker(new WorkerRegistration
        {
            Id = "worker-1", Roles = new List<string> { "coder" }, Kind = "local", MaxConcurrent = 4,
        });
        var attempt = _memory.TryClaimTask(taskId, "m1", "worker-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(attempt);
        _memory.FinishAttempt(attempt!.Id, AttemptState.Failed, failureClass: failureClass,
                              failureReason: "for the test");
    }

    private void RecordCompletedTask(string taskId, string title, string ant, bool success)
    {
        // Domain.Task carries no MissionId — SaveTask takes it separately, which is why the mission
        // is the first argument. And the terminal success state is `Complete`, not `Completed`.
        _memory.SaveTask("m1", new Anthill.Core.Domain.Task
        {
            Id = taskId, Title = title, Description = title,
            AssignedAnt = ant, TaskType = "work", Status = TaskStatus.Complete,
        });
        _memory.SaveTaskResult("m1", taskId, ant, new AntExecutionResult
        {
            Success = success,
            StatusCode = success ? "succeeded" : "failed_permanent",
            Summary = title,
        });
    }
}
