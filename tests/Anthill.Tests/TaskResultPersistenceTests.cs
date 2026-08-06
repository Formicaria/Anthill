using Anthill.Core.Agents;
using Anthill.SDK.Contracts;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.2.0 (phase) — exit gate: every task result is reconstructable WITHOUT parsing narrative text.
///
/// Until now an AntExecutionResult decided a task's status and was then discarded except for its
/// narrative. Artifacts, evidence, handoffs, warnings, metrics and the failure class ended at the
/// persistence boundary, so an operator asking why a task failed, or any later replay, had to read
/// the prose back and infer — the exact thing this phase removes everywhere else.
///
/// These tests reconstruct a result from storage and assert it carries the structure, with the
/// narrative deliberately absent: the record must stand on its own.
/// </summary>
public class TaskResultPersistenceTests
{
    /// <summary>
    /// A memory with mission "m1" already saved. task_results has a foreign key to missions(id) and
    /// foreign_keys is ON, so a result for a mission that was never persisted is correctly refused
    /// — the first version of this fixture skipped the mission and the write was rejected, which is
    /// the schema working rather than a bug in it.
    /// </summary>
    private static SqliteMemory NewMemory()
    {
        var memory = new SqliteMemory(Path.Combine(Path.GetTempPath(), $"anthill-taskresults-{Guid.NewGuid():N}.db"));
        memory.SaveMission(new Mission { Id = "m1", Goal = "goal" });
        return memory;
    }

    private static AntExecutionResult RichResult() =>
        AntExecutionResult.Succeeded("did the thing", "a long narrative the operator reads") with
        {
            Artifacts = new List<AntArtifact> { new("patch", "proposal", "diff --git a b") },
            Evidence = new List<AntEvidence> { new("verification_verdict", "passed", "all checks green") },
            Warnings = new List<string> { "degraded: provider slow" },
            Metrics = new AntMetrics { ModelCalls = 2, ToolCalls = 3, OutputChars = 41, RetryCount = 1 },
        };

    [Fact]
    public void AStructuredResult_SurvivesTheRoundTrip_WithoutItsNarrative()
    {
        var memory = NewMemory();
        memory.SaveTaskResult("m1", "t1", "coder", RichResult());

        var loaded = memory.LoadTaskResult("t1");
        Assert.NotNull(loaded);
        Assert.True(loaded!.Success);
        Assert.Equal("succeeded", loaded.StatusCode);
        Assert.Equal("did the thing", loaded.Summary);

        // the structure, not the prose
        Assert.Equal("patch", Assert.Single(loaded.Artifacts).Kind);
        Assert.Equal("passed", Assert.Single(loaded.Evidence).Value);
        Assert.Equal("degraded: provider slow", Assert.Single(loaded.Warnings));
        Assert.Equal(2, loaded.Metrics.ModelCalls);
        Assert.Equal(3, loaded.Metrics.ToolCalls);
        Assert.Equal(1, loaded.Metrics.RetryCount);
    }

    /// <summary>
    /// A failure must be reconstructable AS a failure — class and retryability, not a sentence
    /// containing the word "failed". Retry decisions are made from this.
    /// </summary>
    [Fact]
    public void AFailure_ReconstructsItsClassAndRetryability()
    {
        var memory = NewMemory();
        memory.SaveTaskResult("m1", "t2", "coder",
            AntExecutionResult.Failed(FailureClass.TransientProviderFailure, "provider unreachable"));

        var loaded = memory.LoadTaskResult("t2")!;
        Assert.False(loaded.Success);
        Assert.Equal("failed_retryable", loaded.StatusCode);
        Assert.NotNull(loaded.Failure);
        Assert.Equal(FailureClass.TransientProviderFailure, loaded.Failure!.Class);
        Assert.True(loaded.Failure.Retryable);
    }

    /// <summary>
    /// A recorded result is listed for its mission, and it is recorded for roles that carry a
    /// versioned contract and roles that do not.
    ///
    /// The first version of this test asserted that "coder" has a contract version to record. It
    /// does not: AntExecutionCatalog holds contracts for the six SPECIALISTS only, so
    /// ContractFor("coder") is null and every core-ant result stores contract_version = NULL. That
    /// is the open required-work item "apply versioned AntExecutionContract records to all active
    /// and standby mission agents, not only specialists" — recorded here rather than papered over,
    /// because a test asserting the version exists would have to be deleted to close that gap
    /// honestly, and one asserting it does not would entrench it.
    /// </summary>
    [Fact]
    public void ResultsAreListedForTheirMission_ForContractedAndUncontractedRoles()
    {
        var memory = NewMemory();
        memory.SaveTaskResult("m1", "t3", "coder", RichResult());     // no contract today
        memory.SaveTaskResult("m1", "t3b", "tester", RichResult());   // has one

        Assert.Null(AntExecutionCatalog.ContractFor("coder"));
        Assert.NotNull(AntExecutionCatalog.ContractFor("tester"));

        var listed = memory.LoadMissionTaskResults("m1");
        Assert.Contains(listed, r => r.TaskId == "t3");
        Assert.Contains(listed, r => r.TaskId == "t3b");
    }

    /// <summary>Re-running a task overwrites its record rather than accumulating duplicates.</summary>
    [Fact]
    public void ARetriedTask_OverwritesItsRecord()
    {
        var memory = NewMemory();
        memory.SaveTaskResult("m1", "t4", "builder",
            AntExecutionResult.Failed(FailureClass.TransientProviderFailure, "first attempt"));
        memory.SaveTaskResult("m1", "t4", "builder", AntExecutionResult.Succeeded("second attempt"));

        var all = memory.LoadMissionTaskResults("m1").Where(r => r.TaskId == "t4").ToList();
        Assert.Single(all);
        Assert.True(all[0].Result.Success);
        Assert.Equal("second attempt", all[0].Result.Summary);
    }

    /// <summary>
    /// A task with no recorded result reads as null — NOT as an empty success. Callers must treat
    /// "not recorded" as unknown rather than re-deriving an answer from the task's prose, which is
    /// the habit this table exists to break.
    /// </summary>
    [Fact]
    public void AnUnrecordedTask_IsNull_NotAnEmptySuccess()
    {
        var memory = NewMemory();
        Assert.Null(memory.LoadTaskResult("never-ran"));
        Assert.Empty(memory.LoadMissionTaskResults("no-such-mission"));
    }

    /// <summary>A corrupt JSON column degrades to empty rather than failing the whole load.</summary>
    [Fact]
    public void ACorruptCollectionColumn_DegradesToEmpty_RatherThanThrowing()
    {
        Assert.Empty(Json.TryParseList<AntArtifact>("{not json"));
        Assert.Null(Json.TryParseTyped<AntMetrics>("{not json"));
    }
}
