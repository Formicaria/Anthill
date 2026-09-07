using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Workers;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT THE MISSION DID SURVIVES THE PROCESS THAT DID IT. v0.3.8.139.
///
/// `docs/PLAN.md` §2e has said since `.118` that items 3 through 8 — authoritative execution
/// records, artifact and evidence handoff, verification that reads execution rather than a
/// narrative, closure ENFORCEMENT, unsourced-claim rejection — "all consume the same missing row",
/// and that `.122` did not add it. Eleven releases of work were queued behind one fact.
///
/// THE ROW WAS NOT MISSING. `task_attempts` has been live and load-bearing since `v3.8.0`, because
/// the atomic claim runs on it. What it carried was WHO was executing and HOW IT ENDED, and nothing
/// at all about what the attempt DID. So the plan's "add a record" was, read against the tree, "give
/// the record you already write the facts it drops" — and a second table beside it would have been
/// two records of one thing, which is defect #5 on this repository's own list.
///
/// AND THE FACTS WERE NEVER MISSING EITHER, WHICH IS THE PART WORTH STATING. Every one of them is
/// computed, correct, and sitting on `Domain.Task` — where four are marked TRANSIENT in their own
/// doc comments, meaning the object holds them and the `tasks` row does not. A restart therefore
/// forgot why a worker was chosen, which deliverable a task served, what capability was required of
/// it, and WHICH TREE a check actually ran in. Every closure question after this release is asked of
/// those four, and until now each was answerable only for as long as the process lived.
///
/// SO THE TEST IS A RESTART, and it has to be. "Transient" is exactly what is being fixed, and a
/// test that read the facts back through the same live objects would assert nothing at all — it
/// would pass identically against the code this release replaces. The store is closed and reopened
/// on the same file, which is the cheapest honest simulation of the thing that used to lose them.
/// </summary>
public class ExecutionRecordTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public ExecutionRecordTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-execrec-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "memory.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Task Executed() => new()
    {
        Id = "t1",
        Title = "Reproduce the reported symptom",
        AssignedAnt = "tester",
        TaskType = "validation_check",
        WorkerBasis = WorkerDecisionBasis.Specification,
        DeliverableIds = new List<string> { "d1", "d2" },
        RequiredCapability = "execute_diagnostic_checks",
        GenerationDegraded = true,
        ProducedRevisionId = "rev-produced",
        RanRevisionId = "rev-ran",
    };

    // ---- the record ----------------------------------------------------------------------------

    /// <summary>
    /// THE NAMED TEST. Close an attempt, drop the process, and ask the store what happened.
    ///
    /// Every assertion here is a question some later gate has to ask: which tree did this check
    /// judge, was this the worker the capability chose or one a keyword picked, which of the
    /// operator's requests was this task serving, was the generation that produced it degraded. All
    /// of them returned "gone" across a restart before this release.
    /// </summary>
    [Fact]
    public void TheExecutionFacts_SurviveTheProcessThatRecordedThem()
    {
        string attemptId;
        using (var memory = new SqliteMemory(_db))
        {
            attemptId = memory.TryClaimTask("t1", "m1", "worker-1", TimeSpan.FromMinutes(5))!.Id;
            memory.FinishAttempt(attemptId, AttemptState.Succeeded,
                record: AttemptExecutionRecord.From(Executed()));
        }

        using var reopened = new SqliteMemory(_db);
        var attempt = Assert.Single(reopened.LoadMissionAttempts("m1"));

        Assert.Equal(AttemptState.Succeeded, attempt.State);
        Assert.Equal("tester", attempt.AssignedAnt);
        Assert.Equal("validation_check", attempt.TaskType);
        Assert.Equal(nameof(WorkerDecisionBasis.Specification), attempt.WorkerBasis);
        Assert.Equal(new[] { "d1", "d2" }, attempt.DeliverableIds);
        Assert.Equal("execute_diagnostic_checks", attempt.RequiredCapability);
        Assert.True(attempt.GenerationDegraded);
        Assert.Equal("rev-produced", attempt.ProducedRevisionId);
        Assert.Equal("rev-ran", attempt.RanRevisionId);
    }

    /// <summary>
    /// AN UNDECIDED WORKER BASIS IS NULL, NOT THE STRING "Unset".
    ///
    /// The column's rule is that null means NOT RECORDED and never "no", and `Unset` is the one
    /// value that would break it by writing a decision nobody made as though it were one. It is
    /// written as the enum's NAME rather than its ordinal for the reason this project's status codes
    /// are text: an ordinal is correct until somebody inserts a member in the middle, and then every
    /// historical row silently means something else.
    /// </summary>
    [Fact]
    public void AnUndecidedWorkerBasis_IsNotRecordedAsADecision()
    {
        using var memory = new SqliteMemory(_db);
        var attemptId = memory.TryClaimTask("t1", "m1", "worker-1", TimeSpan.FromMinutes(5))!.Id;

        memory.FinishAttempt(attemptId, AttemptState.Succeeded,
            record: AttemptExecutionRecord.From(new Task { Id = "t1", AssignedAnt = "builder" }));

        Assert.Null(Assert.Single(memory.LoadMissionAttempts("m1")).WorkerBasis);
    }

    /// <summary>
    /// AND "THE PLAN DECLARED NO DELIVERABLE" IS A RECORDED FACT, distinguishable from a legacy row
    /// that predates the column. An empty list round-trips as an empty list — if it came back the
    /// same as never-written, a closure gate could not tell a task the plan deliberately attached to
    /// nothing from one that ran before this release.
    /// </summary>
    [Fact]
    public void AnEmptyDeliverableList_IsRecordedRatherThanAbsent()
    {
        using var memory = new SqliteMemory(_db);
        var attemptId = memory.TryClaimTask("t1", "m1", "worker-1", TimeSpan.FromMinutes(5))!.Id;

        memory.FinishAttempt(attemptId, AttemptState.Succeeded,
            record: AttemptExecutionRecord.From(new Task { Id = "t1", AssignedAnt = "builder" }));

        Assert.Empty(Assert.Single(memory.LoadMissionAttempts("m1")).DeliverableIds);
    }

    // ---- what it must not do -------------------------------------------------------------------

    /// <summary>
    /// A CALLER WITH NOTHING TO SAY ERASES NOTHING.
    ///
    /// `FinishAttempt` has three call sites that predate the record and pass none — two of them
    /// release a claim the scheduler declined, and neither is holding an executed task. Null must
    /// therefore mean "this caller had nothing to record", never "there was nothing to record", or
    /// adding an argument to one method would have silently blanked facts another wrote. That is the
    /// `INSERT OR REPLACE` shape `Queen`'s own finalization carries two paragraphs of warning about,
    /// and the reason every column here is written through COALESCE.
    /// </summary>
    [Fact]
    public void AFinishWithNoRecord_LeavesWhatWasAlreadyThere()
    {
        using var memory = new SqliteMemory(_db);
        var attemptId = memory.TryClaimTask("t1", "m1", "worker-1", TimeSpan.FromMinutes(5))!.Id;

        memory.FinishAttempt(attemptId, AttemptState.Succeeded,
            record: AttemptExecutionRecord.From(Executed()));
        // A second close cannot happen in production — the UPDATE is guarded on state='Running' —
        // so this asserts the COALESCE directly rather than through a path that cannot reach it.
        memory.FinishAttempt(attemptId, AttemptState.Failed, failureReason: "later, and emptier");

        Assert.Equal("rev-ran", Assert.Single(memory.LoadMissionAttempts("m1")).RanRevisionId);
    }

    /// <summary>
    /// AND A LEGACY ROW READS AS UNRECORDED, NEVER AS A NEGATIVE.
    ///
    /// Every attempt written before this release migrates in place with no values, and a closure
    /// gate that read those nulls as "this check ran in no revision" or "this generation was fine"
    /// would refuse every mission the colony has already run — inventing history to satisfy a guard,
    /// which is the direction `evidence.revision_id` and `patch_sets.base_fingerprint` both refused.
    /// </summary>
    [Fact]
    public void AnAttemptWithNoRecord_ReadsAsUnrecorded()
    {
        using var memory = new SqliteMemory(_db);
        var attemptId = memory.TryClaimTask("t1", "m1", "worker-1", TimeSpan.FromMinutes(5))!.Id;
        memory.FinishAttempt(attemptId, AttemptState.Succeeded);

        var attempt = Assert.Single(memory.LoadMissionAttempts("m1"));

        Assert.Null(attempt.RanRevisionId);
        Assert.Null(attempt.WorkerBasis);
        Assert.Null(attempt.RequiredCapability);
        Assert.False(attempt.GenerationDegraded);
    }

    /// <summary>
    /// THE FACTS COME FROM THE TASK, IN ONE PLACE. `AttemptExecutionRecord.From` is the only
    /// constructor, so "which facts belong in the execution record" is answered once — the property
    /// this whole slice exists to establish, applied to itself. A call site assembling the record
    /// field by field could omit one and nothing would notice.
    /// </summary>
    [Fact]
    public void TheRecord_IsBuiltFromTheTaskAndNotAssembledByHand()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        Assert.Contains("AttemptExecutionRecord.From(task)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AttemptExecutionRecord", source, StringComparison.Ordinal);
    }
}
