using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md acceptance gates 8 and 9 — the two roles that write the RECORD cannot write a flattering
/// one. v0.3.8.57.
///
/// These two are the last roles to touch a mission, and both produce output that outlives it: the
/// scribe writes what a person reads, the archivist writes what the colony learns. A wrong verdict
/// from either is not caught downstream, because there is no downstream — which is exactly why the
/// acceptance list names them.
///
/// GATE 9 was already met and is pinned here rather than built. `RunArchivistAfterFinalization` runs
/// outside the task graph, after the canonical evaluation is computed AND persisted, and claims a
/// ledger entry so a restarted finalization cannot archive twice.
///
/// GATE 8 had a real hole. The scribe's contract supports a `verified_change_summary` task type —
/// a document whose OUTPUT ASSERTS a verification — and nothing checked that one had occurred. A
/// mission whose verifier never ran could produce a summary telling the operator its change was
/// verified, and that document is the most confident and least grounded thing the colony makes.
/// </summary>
public class ScribeArchivistOrderingTests
{
    private static Mission MissionWith(params Task[] tasks)
    {
        var mission = new Mission { Id = "m1", Goal = "write it up" };
        mission.Tasks.AddRange(tasks);
        return mission;
    }

    /// <summary>
    /// A verifier task carrying REAL verdict text.
    ///
    /// `MissionVerification` parses the verdict with `VerificationVerdict`, whose vocabulary is the
    /// three exact phrases `VerifierAnt` emits. My first fixture wrote "verified: pass", which parses
    /// to Unknown — and Unknown is not a pass, correctly. A fixture that invents its own vocabulary
    /// tests the fixture; this one uses the phrase production actually produces.
    /// </summary>
    private static Task Verifier(TaskStatus status, string? result) => new()
    {
        Id = "v1", Title = "Verify", Description = "verify", AssignedAnt = "verifier",
        TaskType = "verification", Status = status, Result = result,
    };

    private static Task Summary() => new()
    {
        Id = "s1", Title = "Summarise the verified change", Description = "write it up",
        AssignedAnt = "scribe", TaskType = "verified_change_summary",
    };

    // -------------------------------------------------------------------------------------------
    // Gate 8 — the scribe cannot certify what nobody verified
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AVerifiedChangeSummary_IsRefusedWhenNothingVerifiedAnything()
    {
        var result = new ScribeAnt(null).Execute(Summary(), MissionWith(Summary()));

        Assert.Equal("blocked", result.StatusCode);
        Assert.Contains("was verified, and it was not", result.Summary + result.Narrative);
    }

    /// <summary>
    /// A verifier that ran and FAILED is not verification. This is the case that would slip through
    /// an "is there a verifier task" check — the role was present, which is not the same claim.
    /// </summary>
    [Fact]
    public void AVerifiedChangeSummary_IsRefusedWhenTheVerifierFailed()
    {
        var mission = MissionWith(Summary(), Verifier(TaskStatus.Failed, "Verdict: Verification Failed"));

        Assert.Equal("blocked", new ScribeAnt(null).Execute(Summary(), mission).StatusCode);
    }

    /// <summary>
    /// And when verification genuinely passed, the scribe writes it. A gate that refused here would
    /// make the task type unusable, which is a different way of being wrong.
    /// </summary>
    [Fact]
    public void AVerifiedChangeSummary_ProceedsWhenVerificationActuallyPassed()
    {
        var mission = MissionWith(Summary(), Verifier(TaskStatus.Complete, "Verdict: Verification Passed"));

        Assert.NotEqual("blocked", new ScribeAnt(null).Execute(Summary(), mission).StatusCode);
    }

    /// <summary>
    /// ONLY that task type. A scribe writing release notes or a docs proposal mid-mission is doing
    /// legitimate work and asserts nothing about verification; blocking it would be the gate
    /// widening past the sentence it exists to enforce.
    /// </summary>
    [Theory]
    [InlineData("release_notes")]
    [InlineData("operator_documentation")]
    [InlineData("incident_summary")]
    public void OtherScribeWork_IsNotBlockedByTheVerificationRule(string taskType)
    {
        var task = new Task
        {
            Id = "s1", Title = "Write it", Description = "docs", AssignedAnt = "scribe", TaskType = taskType,
        };

        Assert.NotEqual("blocked", new ScribeAnt(null).Execute(task, MissionWith(task)).StatusCode);
    }

    // -------------------------------------------------------------------------------------------
    // Gate 8 — the archivist cannot reinforce unverified work
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Positive procedural memory comes ONLY from `completed_verified`. The rule is stated in the
    /// archivist's own summary and asserted here against the code, because a learning loop that
    /// reinforces unverified work is how a colony gets confidently worse over time — and it does so
    /// silently, since nothing downstream re-checks a promoted lesson.
    /// </summary>
    [Fact]
    public void TheArchivist_ReinforcesPositivelyOnlyForVerifiedOutcomes()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

        Assert.Contains("completed_verified", source);
        // Nothing the archivist writes may promote itself: certification is the evaluation
        // pipeline's job, and archival asserting it would be the same claim from a weaker source.
        Assert.Contains("auto_promote: false", source);
    }

    // -------------------------------------------------------------------------------------------
    // Gate 9 — the archivist runs only after the canonical evaluation persists
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Order, and idempotency across restart. Both are properties of the same few lines, so they are
    /// asserted together: the archivist runs after `SaveMissionEvaluation`, and it claims a ledger
    /// entry so a finalization replayed after a crash cannot archive the same mission twice.
    ///
    /// The duplicate matters more than it looks. Memory candidates feed skill-candidate registration,
    /// whose promotion threshold requires repeat evidence ACROSS missions — so one mission finalised
    /// twice would satisfy a bar designed to need two.
    /// </summary>
    [Fact]
    public void TheArchivist_RunsAfterThePersistedEvaluationAndOnlyOnce()
    {
        var queen = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        var persisted = queen.IndexOf("SaveMissionEvaluation(evaluation)", StringComparison.Ordinal);
        var archivist = queen.IndexOf("RunArchivistAfterFinalization(mission, evaluation)", StringComparison.Ordinal);

        Assert.True(persisted >= 0, "the canonical evaluation is no longer persisted in FinalizeMission");
        Assert.True(archivist >= 0, "the archivist no longer runs at finalization");
        Assert.True(persisted < archivist,
            "the archivist now runs BEFORE the canonical evaluation is persisted, so it would read a "
          + "mission whose outcome is not yet decided — which is exactly what a planner-scheduled "
          + "archivist would have done, and why the contract forbids that.");

        Assert.Contains("TryClaimArchivist", queen);
    }

    /// <summary>
    /// And a disabled archivist is SAID OUT LOUD. "No lessons were extracted" and "the archivist is
    /// switched off" are different facts, and a silent skip makes them look identical.
    /// </summary>
    [Fact]
    public void ADisabledArchivist_IsReportedRatherThanSilentlySkipped()
    {
        var queen = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        Assert.Contains("archivist_skipped", queen);
    }

    // -------------------------------------------------------------------------------------------
    // The plan
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void TheAcceptanceGates_AreRecordedAsClosed(int number)
    {
        var gate = SourceText.PlanAcceptanceGate(number);

        Assert.Contains("✅", gate);
        Assert.Contains("v0.3.8.57", gate);
    }
}
