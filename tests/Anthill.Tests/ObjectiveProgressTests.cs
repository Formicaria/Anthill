using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.22.0 Phase C3: an objective's terminal state reflects what it ACHIEVED, not how many times
/// it ran.
///
/// The defect: `RecordObjectiveRunOutcome` moves an objective to Done the moment
/// `RunCount >= MaxRuns`. An objective that failed every attempt therefore ended in exactly the
/// same state as one that succeeded on its first — "Done" meant the budget ran out, not that the
/// goal was met, and every report reading that status turned failure into achievement.
/// </summary>
public class ObjectiveProgressTests
{
    private static Dictionary<string, object?> Run(string missionStatus, string? finishedAt = null) => new()
    {
        ["mission_status"] = missionStatus,
        ["finished_at"] = finishedAt,
    };

    private static Objective Exhausted() =>
        new() { Id = "o1", Title = "t", Charter = "c", Status = ObjectiveStatus.Done, RunCount = 5, MaxRuns = 5 };

    // ---- reading the evidence -------------------------------------------------------------------

    [Fact]
    public void AnObjectiveThatNeverSucceeded_HasNotAchievedAnything()
    {
        var progress = ObjectiveProgress.Assess(new[]
        {
            Run(MissionOutcome.Partial), Run(MissionOutcome.FailedPermanent), Run(MissionOutcome.CompletedUnverified),
        });

        Assert.Equal(3, progress.Runs);
        Assert.Equal(0, progress.VerifiedSuccesses);
        Assert.False(progress.Achieved);
        Assert.Contains("never achieved", ObjectiveProgress.Explain(progress));
    }

    [Fact]
    public void OneVerifiedRunIsEnough_EvenIfLaterRunsFailed()
    {
        // Succeeding then later failing does not un-achieve the goal.
        var progress = ObjectiveProgress.Assess(new[]
        {
            Run(MissionOutcome.CompletedVerified, "2026-01-01T00:00:00Z"),
            Run(MissionOutcome.Partial),
            Run(MissionOutcome.FailedPermanent),
        });

        Assert.True(progress.Achieved);
        Assert.Equal(1, progress.VerifiedSuccesses);
    }

    /// <summary>
    /// `completed_unverified` is the exact case v2.19.0 exists to distinguish: the mission finished
    /// but nothing proved the work. It must not count as an achievement here either, or the
    /// objective layer would apply a weaker standard than mission grading.
    /// </summary>
    [Fact]
    public void CompletedButUnverified_IsNotAnAchievement()
    {
        var progress = ObjectiveProgress.Assess(new[] { Run(MissionOutcome.CompletedUnverified) });
        Assert.False(progress.Achieved);
    }

    /// <summary>
    /// Runs recorded before v2.19.0 hold raw statuses like "complete". They cannot be confirmed as
    /// verified, so they fail closed — the same stance the v2.20.0 learning reset took.
    /// </summary>
    [Fact]
    public void PreVocabularyRuns_FailClosed()
    {
        var progress = ObjectiveProgress.Assess(new[] { Run("complete"), Run("partial"), Run("") });
        Assert.Equal(3, progress.Runs);
        Assert.False(progress.Achieved);
    }

    [Fact]
    public void TheLastVerifiedTimestampIsTheLatestOne_NotTheLastRowSeen()
    {
        var progress = ObjectiveProgress.Assess(new[]
        {
            Run(MissionOutcome.CompletedVerified, "2026-03-01T00:00:00Z"),
            Run(MissionOutcome.CompletedVerified, "2026-01-01T00:00:00Z"),
        });
        Assert.Equal("2026-03-01T00:00:00Z", progress.LastVerifiedAt);
    }

    [Fact]
    public void NoRuns_IsHandled()
    {
        foreach (var empty in new[] { ObjectiveProgress.Assess(null), ObjectiveProgress.Assess(Array.Empty<Dictionary<string, object?>>()) })
        {
            Assert.Equal(0, empty.Runs);
            Assert.False(empty.Achieved);
            Assert.Equal("never ran", ObjectiveProgress.Explain(empty));
        }
    }

    // ---- the end reason it drives ---------------------------------------------------------------

    /// <summary>The distinction the old code could not draw.</summary>
    [Fact]
    public void BudgetExhaustionWithoutSuccess_IsNotCompletion()
    {
        var never = ObjectiveProgress.Assess(new[] { Run(MissionOutcome.Partial), Run(MissionOutcome.Partial) });
        Assert.Equal(ObjectiveEndReason.ExhaustedWithoutSuccess, ObjectiveProgress.BudgetEndReason(never));

        var achieved = ObjectiveProgress.Assess(new[] { Run(MissionOutcome.CompletedVerified) });
        Assert.Equal(ObjectiveEndReason.CompletedSuccessfully, ObjectiveProgress.BudgetEndReason(achieved));
    }

    [Fact]
    public void TheNewEndReasonHasAnOperatorFacingLabel()
    {
        var label = ObjectiveEndReason.Label(ObjectiveEndReason.ExhaustedWithoutSuccess);
        Assert.Contains("Exhausted", label);
        Assert.NotEqual(ObjectiveEndReason.ExhaustedWithoutSuccess, label);   // a label, not the raw code
    }

    /// <summary>
    /// The behavioural change end to end: an objective whose budget ran out having never verified
    /// anything is now reported as exhausted, where before it was "Completed".
    /// </summary>
    [Fact]
    public void AnExhaustedObjective_IsNoLongerReportedAsCompleted()
    {
        var neverSucceeded = ObjectiveProgress.Assess(new[] { Run(MissionOutcome.Partial), Run(MissionOutcome.FailedPermanent) });

        var decision = ObjectiveLifecycle.EvaluateCompletion(
            Exhausted(), success: false, followUpsCreated: 0, alreadyDone: true, progress: neverSucceeded);

        Assert.NotNull(decision);
        Assert.Equal(ObjectiveEndReason.ExhaustedWithoutSuccess, decision!.EndReason);
        Assert.Contains("none verified", decision.Detail);
    }

    /// <summary>
    /// And the converse, which the old rule also got wrong: an objective that achieved its goal
    /// early but whose FINAL run failed is still a completion, because achievement is not undone
    /// by a later failure.
    /// </summary>
    [Fact]
    public void SucceededEarlyThenFailedLate_IsStillACompletion()
    {
        var achievedEarly = ObjectiveProgress.Assess(new[]
        {
            Run(MissionOutcome.CompletedVerified, "2026-01-01T00:00:00Z"),
            Run(MissionOutcome.FailedPermanent),
        });

        var decision = ObjectiveLifecycle.EvaluateCompletion(
            Exhausted(), success: false, followUpsCreated: 0, alreadyDone: true, progress: achievedEarly);

        Assert.Equal(ObjectiveEndReason.CompletedSuccessfully, decision!.EndReason);
    }

    /// <summary>Callers with no run history fall back to the final run — no worse than before.</summary>
    [Fact]
    public void WithoutProgress_TheFinalRunOutcomeIsUsed()
    {
        Assert.Equal(ObjectiveEndReason.CompletedSuccessfully,
            ObjectiveLifecycle.EvaluateCompletion(Exhausted(), true, 0, alreadyDone: true)!.EndReason);
        Assert.Equal(ObjectiveEndReason.ExhaustedWithoutSuccess,
            ObjectiveLifecycle.EvaluateCompletion(Exhausted(), false, 0, alreadyDone: true)!.EndReason);
    }

    // ---- the call site ---------------------------------------------------------------------------

    [Fact]
    public void TheDirectorAssessesProgress_FromRealRunHistory()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ColonyDirector.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
        Assert.Contains("ObjectiveProgress.Assess(_queen.Memory.ListAutonomyRuns(objective.Id", code);
        Assert.Contains("alreadyDone, progress)", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
