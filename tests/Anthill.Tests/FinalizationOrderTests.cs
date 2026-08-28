using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The order of finalization, and the fact that it happens once. v0.3.8.41.
///
/// TWO DEFECTS, ONE SHAPE. Both are producer/consumer orderings where the consumer ran first and
/// nothing failed:
///
/// <list type="bullet">
/// <item><b>Learning consumed the archivist's output before the archivist ran.</b>
///   <c>LearningRecorder.RegisterProceduralRoutes</c> queries the mission's <c>memory_candidate</c>
///   events; it was called from <c>FinalizeMission</c>, and the archivist runs after that returns.
///   The query has therefore returned an empty list on every mission this project has ever run.
///   v2.26.0 moved route registration to finalization to fix an earlier version of the same bug and
///   landed one step short, because the producer had no trigger yet; v3.8.26 gave the archivist a
///   trigger and put it AFTER the consumer.</item>
/// <item><b>Finalization could run twice.</b> Pheromone strength and skill observations are
///   cumulative, so a recovery pass over an already-finalised mission does not produce a slightly
///   stale answer — it produces a permanently doubled one, and afterwards nothing distinguishes
///   "succeeded twice" from "counted twice".</item>
/// </list>
///
/// The ledger tests below are behavioural against a real store. The ordering tests read the source,
/// because the property is about the sequence of two calls in one method and a behavioural test
/// would need a fake for each of the three services — at which point it would be asserting the order
/// of the fakes.
/// </summary>
public class FinalizationOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_final_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static MissionEvaluation Evaluation(string missionId, string outcome = MissionOutcome.CompletedVerified) =>
        new(missionId, outcome, "complete",
            MissionEvaluation.Verification.Passed, MissionEvaluation.Deliverable.Satisfied,
            null, "v2", AnthillTime.NowUtc().ToIso(), "evaluated");

    private static string QueenSource() =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.cs")));

    private static string ExecutionSource() =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

    // -------------------------------------------------------------------------------------------
    // Idempotence
    // -------------------------------------------------------------------------------------------

    /// <summary>The first claim wins and every later one is refused.</summary>
    [Fact]
    public void LearningIsClaimedExactlyOnce_PerEvaluation()
    {
        using var memory = Memory();
        var mission = new Mission { Goal = "learn once" };
        memory.SaveMission(mission);
        var evaluation = Evaluation(mission.Id);

        Assert.True(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation));
        Assert.False(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation));
        Assert.False(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation));
    }

    /// <summary>The archivist has its own claim: one step being replayed must not excuse the other.</summary>
    [Fact]
    public void TheArchivistAndLearning_AreClaimedSeparately()
    {
        using var memory = Memory();
        var mission = new Mission { Goal = "two steps" };
        memory.SaveMission(mission);
        var evaluation = Evaluation(mission.Id);

        Assert.True(MissionFinalizationLedger.TryClaimArchivist(memory, mission.Id, evaluation));
        Assert.True(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation));
        Assert.False(MissionFinalizationLedger.TryClaimArchivist(memory, mission.Id, evaluation));
    }

    /// <summary>
    /// The claim survives the process. This is the state a restart actually produces: the row and
    /// its events exist, no live object remembers anything, and the second finalization must still
    /// be refused. An in-memory flag would pass every test above and fail exactly here.
    /// </summary>
    [Fact]
    public void TheClaimIsDurable_AcrossAReopenedStore()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "durable.db");
        string missionId;
        MissionEvaluation evaluation;

        using (var memory = new SqliteMemory(path))
        {
            var mission = new Mission { Goal = "survives restart" };
            memory.SaveMission(mission);
            missionId = mission.Id;
            evaluation = Evaluation(missionId);
            Assert.True(MissionFinalizationLedger.TryClaimLearning(memory, missionId, evaluation));
        }

        using (var reopened = new SqliteMemory(path))
        {
            Assert.False(MissionFinalizationLedger.TryClaimLearning(reopened, missionId, evaluation));
        }
    }

    /// <summary>
    /// A genuinely DIFFERENT evaluation is a different fact and may be learned.
    ///
    /// Without this the ledger would be a mission-level lock, and a mission re-evaluated to a
    /// different canonical outcome — which is what a recovery that finds new evidence produces —
    /// could never record what it actually concluded.
    /// </summary>
    [Fact]
    public void ADifferentEvaluation_MayBeLearned()
    {
        using var memory = Memory();
        var mission = new Mission { Goal = "re-evaluated" };
        memory.SaveMission(mission);

        Assert.True(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id,
            Evaluation(mission.Id, MissionOutcome.CompletedUnverified)));
        Assert.True(MissionFinalizationLedger.TryClaimLearning(memory, mission.Id,
            Evaluation(mission.Id, MissionOutcome.CompletedVerified)));
    }

    /// <summary>
    /// A refused claim is RECORDED. "Learning was skipped because it already happened" and "learning
    /// was never wired" look identical from a mission's event stream unless the skip says so, and
    /// this repository's whole defect record is variations on that sentence.
    /// </summary>
    [Fact]
    public void ARefusedClaim_LeavesEvidenceThatItWasRefused()
    {
        using var memory = Memory();
        var mission = new Mission { Goal = "explain the skip" };
        memory.SaveMission(mission);
        var evaluation = Evaluation(mission.Id);

        MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation);
        MissionFinalizationLedger.TryClaimLearning(memory, mission.Id, evaluation);

        Assert.NotEmpty(memory.GetRecentEvents(20,
            MissionFinalizationLedger.LearningEventType + "_skipped", mission.Id));
    }

    // -------------------------------------------------------------------------------------------
    // Ordering
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The archivist runs BEFORE learning, in <c>RunMission</c>.
    ///
    /// Asserted by position because that is exactly what the property is. Both calls are in one
    /// method, and the bug was that they were in the wrong order — nothing about either call's
    /// arguments or return value differs between the correct and the broken arrangement.
    /// </summary>
    [Fact]
    public void TheArchivistRunsBeforeLearning()
    {
        var source = QueenSource();

        var archivist = source.IndexOf("RunArchivistAfterFinalization(mission, evaluation)", StringComparison.Ordinal);
        var learning = source.IndexOf("_learning.Record(mission, context, evaluation)", StringComparison.Ordinal);

        Assert.True(archivist >= 0, "RunArchivistAfterFinalization is no longer called from Queen.cs");
        Assert.True(learning >= 0, "_learning.Record is no longer called from Queen.cs");
        Assert.True(archivist < learning,
            "Learning runs before the archivist. LearningRecorder.RegisterProceduralRoutes reads the "
          + "memory_candidate events the archivist writes, so in this order that query is guaranteed "
          + "to return nothing — which is what it did on every mission before v0.3.8.41.");
    }

    /// <summary>
    /// And learning is NOT called from <c>FinalizeMission</c> any more, which is where it used to be.
    ///
    /// The ordering test above passes as soon as ONE correctly-placed call exists. A leftover call in
    /// the old position would learn twice per mission — once too early with no candidates, once with
    /// them — and the doubling is exactly what the ledger was built to prevent, so it must not be
    /// possible to reintroduce it from inside the Queen either.
    /// </summary>
    [Fact]
    public void LearningIsCalledExactlyOnce_FromOnePlace()
    {
        var source = QueenSource();
        var calls = System.Text.RegularExpressions.Regex.Matches(source, @"_learning\.Record\s*\(").Count;

        Assert.True(calls == 1,
            $"Queen.cs calls _learning.Record {calls} times; exactly one call site is correct, after "
          + "the archivist and after the canonical evaluation is persisted.");
    }

    /// <summary>
    /// The score is persisted by the NARROW update, not by <c>SaveMission</c>.
    ///
    /// <c>SaveMission</c> is an INSERT OR REPLACE that does not carry the evaluation columns. Learning
    /// now runs after <c>SaveMissionEvaluation</c>, so a wide write in that position would erase the
    /// canonical evaluation moments after it was written — the same defect the ordering comment in
    /// this file has warned about since v2.26.0, arriving from the other direction.
    /// </summary>
    [Fact]
    public void ThePheromoneScore_IsPersistedWithoutErasingTheEvaluation()
    {
        var source = QueenSource();

        var evaluationSaved = source.IndexOf("Memory.SaveMissionEvaluation(evaluation)", StringComparison.Ordinal);
        var scoreSaved = source.IndexOf("Memory.SaveMissionScore(", StringComparison.Ordinal);

        Assert.True(scoreSaved > evaluationSaved && evaluationSaved >= 0,
            "the pheromone score must be persisted after the evaluation, by the narrow update");

        var afterEvaluation = source[evaluationSaved..];
        Assert.DoesNotContain("Memory.SaveMission(mission)", afterEvaluation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A narrow update really is narrow. If it ever grows a second column this test should be the
    /// thing that asks why.
    /// </summary>
    [Fact]
    public void SaveMissionScore_WritesOnlyTheScore()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.Evaluation.cs")));

        var start = source.IndexOf("public void SaveMissionScore", StringComparison.Ordinal);
        Assert.True(start >= 0, "SaveMissionScore is no longer recognisable");
        var body = source[start..Math.Min(source.Length, start + 600)];

        Assert.Contains("UPDATE missions SET success_score=@score WHERE id=@id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", body, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Verification waits for its evidence
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The verifier is bound to the evidence tasks, from the same place that inserts them.
    ///
    /// `AutoWireDependencies` wires a planned verifier to "everything before it" — meaning everything
    /// the PLANNER produced. Tester and soldier do not exist at planning time; they are inserted when
    /// a patch set appears. So the verifier's dependency set was fixed before its two most important
    /// inputs existed, and it could return a verdict about checks that had not run. Nothing failed
    /// when it did, because a verifier produces a verdict either way.
    /// </summary>
    [Fact]
    public void InsertingTheReviewRoles_AlsoBindsTheVerifierToThem()
    {
        var source = ExecutionSource();

        // v0.3.8.97: the method returns bool — whether reviews are now pending — so the bypass
        // lane can sequence itself after them.
        var insert = source.IndexOf("private bool InsertPolicyReviewTasks", StringComparison.Ordinal);
        Assert.True(insert >= 0, "InsertPolicyReviewTasks is no longer recognisable");

        // Scoped to THE METHOD, not to a character budget. The first cut of this bounded the search
        // at 4,000 characters, which is a proxy for "inside the method" and a bad one: `CodeOnly`
        // blanks comments but PRESERVES their length, so explaining a change inside this method
        // pushed the binding past the window and failed a test about something else entirely. The
        // property being checked is that the call sits in this method — so that is what is checked.
        var rest = source[insert..];
        var end = NextMemberDeclaration(rest);
        var body = rest[..end];

        Assert.Contains("EnsureVerificationWaitsFor(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE near-miss this release nearly shipped, pinned so it cannot come back.
    ///
    /// <c>MissionVerification.IsVerificationTask</c> answers "is this a verification STEP", and its
    /// role set is {verifier, tester, soldier}. Using it to look up "does a verifier already exist"
    /// finds the TESTER task inserted three lines earlier, concludes a verifier is present, and wires
    /// the tester to depend on the soldier. No verdict would ever be scheduled, and nothing would
    /// report a problem — a check answering an adjacent question and passing, which is the defect
    /// class this repository has found twelve times.
    /// </summary>
    [Fact]
    public void TheVerifierLookup_AsksForTheVerifierRole_NotForAnyVerificationStep()
    {
        var source = ExecutionSource();

        var start = source.IndexOf("private void EnsureVerificationWaitsFor", StringComparison.Ordinal);
        Assert.True(start >= 0, "EnsureVerificationWaitsFor is no longer recognisable");
        var end = source.IndexOf("private void EnsureVerificationAfterDeliverable", StringComparison.Ordinal);
        Assert.True(end > start, "EnsureVerificationAfterDeliverable no longer follows it");

        var body = source[start..end];

        Assert.DoesNotContain("IsVerificationTask", body, StringComparison.Ordinal);
        Assert.Contains("\"verifier\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verifier that cannot be inserted blocks verification rather than being skipped quietly.
    /// Fail closed is the whole doctrine; a silent skip would let an unverifiable mission read as a
    /// verified one.
    /// </summary>
    [Fact]
    public void ARefusedVerification_SetsADeterministicBlock()
    {
        var source = ExecutionSource();
        var start = source.IndexOf("private void EnsureVerificationWaitsFor", StringComparison.Ordinal);
        var end = source.IndexOf("private void EnsureVerificationAfterDeliverable", StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("DeterministicBlock", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the member that starts at index 0 of <paramref name="fromMemberStart"/> ends — the next
    /// member declaration at class indentation, or the end of the file.
    ///
    /// Crude on purpose. A real parser would be more precise and would also be a second thing that
    /// can be wrong; this only has to be right about where the NEXT `private`/`public`/`internal`
    /// member begins, and every member in these files is declared at four-space indentation.
    /// </summary>
    private static int NextMemberDeclaration(string fromMemberStart)
    {
        var best = fromMemberStart.Length;
        foreach (var keyword in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    protected " })
        {
            // Skip index 0's own declaration by searching from one character in.
            var at = fromMemberStart.IndexOf(keyword, 1, StringComparison.Ordinal);
            if (at >= 0 && at < best) best = at;
        }
        return best;
    }

}
