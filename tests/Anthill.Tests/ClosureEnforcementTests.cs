using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// A MISSION MAY NOT CLOSE COMPLETE WHEN ITS OWN VERIFICATION SAID NO. v0.3.8.140.
///
/// `docs/PLAN.md` §2e has carried this since `.118`. `mission.Status` is computed from task terminal
/// states alone; `VerificationStatus` is computed from the evidence. They never met, so a mission
/// whose verifier returned "Verification Failed" was reported to the operator as COMPLETE with
/// `verification: failed` printed beside it — one record disagreeing with itself, and the line
/// people read first winning.
///
/// `.122` TRIED TO JOIN THEM AND REVERTED, and its own note is the reason this release could
/// succeed: "`Verification.Failed` does not mean 'a check said no' … `failed` spans 'the check said
/// no' and 'nothing could satisfy the check'. Demoting on it reclassified a legitimately complete
/// mission." `VerifierAnt` downgrades a model-authored pass to `Unknown` when no deterministic
/// evidence backs it, so a perfectly good scripted mission graded `failed` and `.122`'s demotion
/// broke it. `ScriptedProviderTests` caught that in one run.
///
/// SO THIS RELEASE SPLIT THE WORD RATHER THAN THE JOIN, and needed nothing new to do it.
/// `VerificationVerdict.Parse` has separated `Failed` and `NeedsImprovement` from `Unknown` since
/// `v2.19.0`; only the mission-level status flattened them. Now `failed` means a verdict-bearing
/// task said no, `inconclusive` means nothing could say anything, and only the first demotes.
///
/// IT DID NOT NEED THE PER-TASK EXECUTION RECORD either, which `docs/PLAN.md` §2e named as the
/// prerequisite for eleven releases. `.139` built that row and every remaining gate still wants it;
/// this was never blocked on a missing fact, only on one word doing two jobs.
///
/// The plan's instruction to the next attempt was literal — "Do not begin the next attempt by making
/// the status line read `VerificationStatus`" — and it is followed: the status line reads the one
/// value that means something said no.
/// </summary>
public class ClosureEnforcementTests : IDisposable
{
    private readonly bool _objVerify;

    public ClosureEnforcementTests()
    {
        AnthillRuntime.Initialize();
        _objVerify = AnthillRuntime.EnableObjectiveVerification;
    }

    public void Dispose() => AnthillRuntime.EnableObjectiveVerification = _objVerify;

    private static DomainTask Work() => new()
    {
        Title = "Work", AssignedAnt = "researcher", TaskType = "research",
        Status = TaskStatus.Complete, Result = "done",
    };

    /// <summary>
    /// A verifier task carrying the REAL verdict vocabulary — `VerificationVerdict.Phrases`. An
    /// invented phrasing parses as `Unknown` and fails closed, which is correct and would make
    /// several of these tests assert the wrong thing for the right reason.
    /// </summary>
    private static DomainTask Verifier(string prose) => new()
    {
        Title = "Verify", AssignedAnt = "verifier", TaskType = "verification",
        Status = TaskStatus.Complete, Result = prose,
    };

    private static Mission MissionWith(params DomainTask[] tasks)
    {
        var m = new Mission { Goal = "research a topic", Status = MissionStatus.Complete };
        m.Tasks.AddRange(tasks);
        return m;
    }

    private static MissionEvaluation Evaluate(Mission mission) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: false,
            evidence: Array.Empty<Evidence>(), specification: null,
            consumptions: Array.Empty<ArtifactConsumption>(), artifacts: Array.Empty<Artifact>());

    // ---- the enforcement -----------------------------------------------------------------------

    /// <summary>
    /// THE NAMED TEST. Every task succeeded, the mission's own verifier said no, and the mission
    /// does not close complete.
    ///
    /// PARTIAL AND NOT FAILED: the work happened. What did not happen is verification, and calling
    /// it failed would say the mission broke — the same distinction `CloseAttempt` draws between an
    /// abandoned attempt and a failed one.
    /// </summary>
    [Fact]
    public void AMissionWhoseVerifierSaidNo_DoesNotCloseComplete()
    {
        var evaluation = Evaluate(MissionWith(Work(),
            Verifier("Verification Failed\nReasoning: required work missing.")));

        Assert.Equal(MissionEvaluation.Verification.Failed, evaluation.VerificationStatus);
        Assert.Equal(MissionStatus.Partial.Value(), evaluation.StructuralStatus);
        Assert.Equal(MissionOutcome.Partial, evaluation.OutcomeCode);
        Assert.False(evaluation.IsPositive);
    }

    /// <summary>
    /// AND THE DEMOTION NAMES ITSELF. "structural=partial" on a mission whose every task succeeded
    /// names no cause, and a demotion an operator cannot locate is one they cannot answer.
    /// </summary>
    [Fact]
    public void TheRefusal_SaysWhichLayerSaidNo()
    {
        var evaluation = Evaluate(MissionWith(Work(),
            Verifier("Verification Failed\nReasoning: no.")));

        Assert.Contains("Closure refused", evaluation.Explanation, StringComparison.Ordinal);
    }

    /// <summary>NEEDS IMPROVEMENT IS ALSO A NO. The verifier looked and declined to pass it.</summary>
    [Fact]
    public void NeedsImprovement_IsAlsoAVerdictOfNo()
    {
        var evaluation = Evaluate(MissionWith(Work(),
            Verifier("Needs Improvement\nReasoning: thin.")));

        Assert.Equal(MissionEvaluation.Verification.Failed, evaluation.VerificationStatus);
        Assert.Equal(MissionOutcome.Partial, evaluation.OutcomeCode);
    }

    // ---- what `.122` got wrong, and must stay right ---------------------------------------------

    /// <summary>
    /// THE REGRESSION `.122` SHIPPED AND REVERTED. `Unknown` is not the check saying no — it is
    /// nothing being able to say anything — and demoting on it is what reclassified a legitimately
    /// complete mission last time.
    ///
    /// Both shapes `Parse` reads as `Unknown` are covered: output with NO verdict in it, and output
    /// with two. The second is not theoretical — the verifier prompt lists all three options on one
    /// line, so a model that echoes that line would otherwise be read as whichever verdict the
    /// parser checked first, a coin flip deciding whether a mission closes.
    ///
    /// It is `inconclusive`: not a pass, never a pass, and it demotes nothing.
    /// </summary>
    [Theory]
    [InlineData("The mission ran and I have no opinion to offer about it.")]
    [InlineData("Verdict: Verification Passed / Needs Improvement / Verification Failed")]
    public void AVerdictNothingCouldEstablish_IsInconclusive_AndDemotesNothing(string prose)
    {
        var evaluation = Evaluate(MissionWith(Work(), Verifier(prose)));

        Assert.Equal(MissionEvaluation.Verification.Inconclusive, evaluation.VerificationStatus);
        Assert.Equal(MissionStatus.Complete.Value(), evaluation.StructuralStatus);
        Assert.Equal(MissionOutcome.CompletedUnverified, evaluation.OutcomeCode);

        // AND IT IS STILL NOT A PASS. `inconclusive` is exactly as unverified as `failed`; the only
        // thing it does not do is accuse the mission of having failed.
        Assert.False(evaluation.IsPositive);
    }

    /// <summary>
    /// A MISSION WITH NO VERIFIER AT ALL IS UNTOUCHED. `not_run` is its own status and always was —
    /// absence of verification is not verification, and it is also not a verdict of no.
    /// </summary>
    [Fact]
    public void AMissionWithNoVerifier_IsUnaffected()
    {
        var evaluation = Evaluate(MissionWith(Work()));

        Assert.Equal(MissionEvaluation.Verification.NotRun, evaluation.VerificationStatus);
        Assert.Equal(MissionStatus.Complete.Value(), evaluation.StructuralStatus);
        Assert.Equal(MissionOutcome.CompletedUnverified, evaluation.OutcomeCode);
    }

    /// <summary>
    /// AND THE LINE CAN ONLY EVER REDUCE. A mission that was already Partial or Failed is not
    /// promoted by anything here — the enforcement reads `Complete` and nothing else, which is what
    /// makes it safe to add to a grading path six releases of work already depend on.
    /// </summary>
    [Theory]
    [InlineData(MissionStatus.Partial, MissionOutcome.Partial)]
    [InlineData(MissionStatus.Failed, MissionOutcome.FailedPermanent)]
    public void AnAlreadyDemotedMission_IsNotPromoted(MissionStatus status, string expected)
    {
        var mission = MissionWith(Work(), Verifier("Verification Failed\nReasoning: no."));
        mission.Status = status;

        Assert.Equal(expected, Evaluate(mission).OutcomeCode);
    }

    // ---- the road not taken ---------------------------------------------------------------------

    /// <summary>
    /// THE OTHER FIX THIS RELEASE TRIED, AND WHY IT IS NOT HERE. Pinned as a test because the
    /// property it asserts is the reason, and a reason that lives only in a comment gets re-tried.
    ///
    /// `VerifierAnt` DECIDES a verdict (`v3.8.27`) and records it; this gate re-parses the model's
    /// prose instead. That reads exactly like defect #5 — two implementations of one rule, with the
    /// authoritative one losing — so the gate was pointed at the ant's ruling. Twenty integration
    /// tests across five mission classes failed at once, and they were right.
    ///
    /// The ant's verdict answers a PROMOTION question: is there DETERMINISTIC evidence behind this.
    /// `EvidenceVerdict.For` returns `Unknown` when a mission holds only non-deterministic rows —
    /// and an audit, an external action and a system action have none BY DESIGN, because their
    /// authority is `observe` or their work is an approved operation rather than a check. So the
    /// ruling is `Unknown` for entire classes of legitimately verified mission.
    ///
    /// This asserts that shape directly: a mission with real, passing, NON-deterministic evidence
    /// and a verifier that plainly passed it must still verify. Pointing closure at the promotion
    /// verdict breaks exactly this, which is what it cost to find out.
    /// </summary>
    [Fact]
    public void AClassWithNoDeterministicEvidence_CanStillVerify()
    {
        var evaluation = Evaluate(MissionWith(Work(),
            Verifier("Verification Passed\nReasoning: the operation record is complete.")));

        Assert.Equal(MissionEvaluation.Verification.Passed, evaluation.VerificationStatus);
        Assert.Equal(MissionOutcome.CompletedVerified, evaluation.OutcomeCode);
    }

    /// <summary>
    /// AND THE TWO RESOLVERS AGREE, WHICH IS THE ONLY THING `VerdictOf` EXISTS FOR. `IsSatisfied`
    /// and `SomethingSaidNo` must never read the verifier differently — a mission that is
    /// `inconclusive` while the gate refused it for a stated "no", or the reverse, is one record
    /// disagreeing with itself, which is the whole defect this release closes.
    /// </summary>
    [Theory]
    [InlineData("Verification Passed\nReasoning: ok.", false)]
    [InlineData("Verification Failed\nReasoning: no.", true)]
    [InlineData("Needs Improvement\nReasoning: thin.", true)]
    [InlineData("no verdict here at all", false)]
    public void SomethingSaidNo_ReadsTheSameVerdictTheGateDid(string prose, bool saidNo)
    {
        var tasks = new List<DomainTask> { Work(), Verifier(prose) };

        Assert.Equal(saidNo, MissionVerification.SomethingSaidNo(tasks));
        Assert.Equal(VerificationVerdict.Parse(prose), MissionVerification.VerdictOf(tasks[1]));
    }
}
