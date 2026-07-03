using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.16 — objective lifecycle hardening + planner constraint enforcement. Verifies that
/// one-shot/verification objectives end cleanly (rather than looping until retirement), that true
/// autonomy loops are still retired, and that verification-only missions never plan coder patch
/// tasks. All offline and deterministic; the one runtime knob touched is saved/restored.
/// </summary>
public class MissionConstraintsTests
{
    [Theory]
    [InlineData("Verify the config is correct, verification only", true)]
    [InlineData("Audit the auth module — do not modify files", true)]
    [InlineData("Inspect the repo read-only and report", true)]
    [InlineData("Please do not create patches, just check for issues", true)]
    [InlineData("Add a docs/notes.md file describing the API", false)]
    [InlineData("Refactor the parser and fix the bug", false)]
    public void Parse_DetectsNoPatchIntent(string goal, bool blocks)
    {
        Assert.Equal(blocks, MissionConstraints.Parse(goal).BlocksPatches);
    }

    [Theory]
    [InlineData("Run this once: summarize the changelog", true)]
    [InlineData("one-shot: generate the report", true)]
    [InlineData("Keep improving test coverage over time", false)]
    public void Parse_DetectsOneShot(string goal, bool oneShot)
    {
        Assert.Equal(oneShot, MissionConstraints.Parse(goal).OneShot);
    }

    [Fact]
    public void Parse_EmptyIsNone()
    {
        var c = MissionConstraints.Parse("");
        Assert.False(c.BlocksPatches);
        Assert.False(c.OneShot);
    }
}

public class PlannerConstraintTests
{
    private static List<Task> Plan(params (string ant, string type)[] tasks) =>
        tasks.Select(t => new Task { Title = t.ant, AssignedAnt = t.ant, TaskType = t.type }).ToList();

    [Fact]
    public void EnforceConstraints_StripsCoderPatchTasks_OnVerificationOnly()
    {
        var tasks = Plan(("researcher", "research"), ("coder", "patch_proposal"), ("verifier", "verification"));
        var constraints = MissionConstraints.Parse("verify the module, verification only, do not modify files");
        var result = Planner.EnforceConstraints(tasks, "verify the module, verification only", constraints);

        Assert.DoesNotContain(result, t => t.AssignedAnt == "coder");
        Assert.DoesNotContain(result, t => t.TaskType == "patch_proposal");
        Assert.Contains(result, t => t.AssignedAnt == "verifier");
    }

    [Fact]
    public void EnforceConstraints_AddsReadOnlyFileInspection_WhenFilesMentioned()
    {
        // Only a coder task existed; stripping it must still leave a way to inspect the files.
        var tasks = Plan(("coder", "patch_proposal"));
        var constraints = MissionConstraints.Parse("read-only inspection of the src/Foo.cs file");
        var result = Planner.EnforceConstraints(tasks, "read-only inspection of the src/Foo.cs file", constraints);

        Assert.Contains(result, t => t.AssignedAnt == "file");
        Assert.Contains(result, t => t.AssignedAnt == "verifier");
        Assert.DoesNotContain(result, t => t.AssignedAnt == "coder");
    }

    [Fact]
    public void EnforceConstraints_DropsDependenciesOnRemovedTasks()
    {
        var research = new Task { Title = "r", AssignedAnt = "researcher", TaskType = "research" };
        var coder = new Task { Title = "c", AssignedAnt = "coder", TaskType = "patch_proposal" };
        var verify = new Task { Title = "v", AssignedAnt = "verifier", TaskType = "verification",
            DependsOn = new List<string> { coder.Id } };
        var constraints = MissionConstraints.Parse("verification only");
        var result = Planner.EnforceConstraints(new List<Task> { research, coder, verify }, "verification only", constraints);

        var v = Assert.Single(result, t => t.AssignedAnt == "verifier");
        Assert.DoesNotContain(coder.Id, v.DependsOn);
    }

    [Fact]
    public void EnforceConstraints_LeavesNormalCodeMissionUnchanged()
    {
        var tasks = Plan(("researcher", "research"), ("coder", "patch_proposal"), ("verifier", "verification"));
        var constraints = MissionConstraints.Parse("add a new feature and modify the parser");
        var result = Planner.EnforceConstraints(tasks, "add a new feature and modify the parser", constraints);

        Assert.Contains(result, t => t.AssignedAnt == "coder");
        Assert.Equal(3, result.Count);
    }
}

public class ObjectiveLifecycleTests : IDisposable
{
    private readonly bool _completion;

    public ObjectiveLifecycleTests()
    {
        AnthillRuntime.Initialize();
        _completion = AnthillRuntime.AutonomyOneShotCompletion;
        AnthillRuntime.AutonomyOneShotCompletion = true;
    }

    public void Dispose() => AnthillRuntime.AutonomyOneShotCompletion = _completion;

    [Fact]
    public void SuccessfulOneShotByWording_CompletesCleanly()
    {
        var o = new Objective { Title = "Report", Charter = "Run this once: produce the status report", MaxRuns = 0 };
        var d = ObjectiveLifecycle.EvaluateCompletion(o, success: true, followUpsCreated: 0, alreadyDone: false);
        Assert.NotNull(d);
        Assert.Equal(ObjectiveStatus.Done, d!.Status);
        Assert.Equal(ObjectiveEndReason.CompletedSuccessfully, d.EndReason);
    }

    [Fact]
    public void SuccessfulVerificationOnly_StopsNoFollowup()
    {
        var o = new Objective { Title = "Audit", Charter = "Verification only: confirm the build is green", MaxRuns = 0 };
        var d = ObjectiveLifecycle.EvaluateCompletion(o, success: true, followUpsCreated: 0, alreadyDone: false);
        Assert.NotNull(d);
        Assert.Equal(ObjectiveStatus.Done, d!.Status);
        Assert.Equal(ObjectiveEndReason.StoppedNoFollowupRequired, d.EndReason);
    }

    [Fact]
    public void VerificationObjective_WithNewFollowup_KeepsRunning()
    {
        var o = new Objective { Title = "Audit", Charter = "Verification only: confirm the build is green", MaxRuns = 0 };
        // Genuinely new work was discovered → do not complete.
        Assert.Null(ObjectiveLifecycle.EvaluateCompletion(o, success: true, followUpsCreated: 1, alreadyDone: false));
    }

    [Fact]
    public void BroadStandingObjective_KeepsRunning()
    {
        var o = new Objective { Title = "Improve", Charter = "Continuously improve test coverage", MaxRuns = 0 };
        Assert.Null(ObjectiveLifecycle.EvaluateCompletion(o, success: true, followUpsCreated: 0, alreadyDone: false));
    }

    [Fact]
    public void RunBudgetExhausted_CompletesSuccessfully()
    {
        var o = new Objective { Title = "Once", Charter = "do the thing", MaxRuns = 1, RunCount = 1 };
        var d = ObjectiveLifecycle.EvaluateCompletion(o, success: true, followUpsCreated: 0, alreadyDone: true);
        Assert.NotNull(d);
        Assert.Equal(ObjectiveEndReason.CompletedSuccessfully, d!.EndReason);
    }

    [Fact]
    public void FailedRun_DoesNotCompleteCleanly()
    {
        var o = new Objective { Title = "Once", Charter = "Run this once: do the thing", MaxRuns = 0 };
        Assert.Null(ObjectiveLifecycle.EvaluateCompletion(o, success: false, followUpsCreated: 0, alreadyDone: false));
    }
}

public class RiskLevelTests
{
    [Theory]
    [InlineData("low risk, adds a comment", "low")]
    [InlineData("HIGH — touches auth", "high")]
    [InlineData("moderate change to config", "medium")]
    [InlineData("", "unknown")]
    [InlineData("critical: rewrites the parser", "high")]
    public void Normalize_MapsProseToLevel(string raw, string expected)
    {
        Assert.Equal(expected, RiskLevel.Normalize(raw));
    }

    [Fact]
    public void Normalize_HighWinsOverLow()
    {
        Assert.Equal("high", RiskLevel.Normalize("low chance but high impact"));
    }
}
