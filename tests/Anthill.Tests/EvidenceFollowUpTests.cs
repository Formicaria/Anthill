using Anthill.Core.Autonomy;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.24.0 Phase C6: follow-ups derived from what verification FOUND, not what a model proposed.
///
/// The verifier has always reported "Missing Steps:" — a concrete list of what the mission did not
/// do — and nothing read it. Follow-ups came instead from the Strategist's free-form opinion about
/// future work, generated on the strength of a success.
///
/// The ADR's vocabulary table demands three properties, and these hold them: a follow-up is not a
/// retry, it is evidence-derived rather than model-derived, and the chain is depth-limited.
/// </summary>
public class EvidenceFollowUpTests
{
    private const string VerifierWithGaps =
        "Verification Passed\nReasoning: the change is present.\n" +
        "Missing Steps:\n- add a regression test for the new branch\n- update the operator docs\n" +
        "Risk Notes: none.";

    private const string VerifierClean =
        "Verification Passed\nReasoning: complete.\nMissing Steps: None identified by static verification.\n" +
        "Risk Notes: none.";

    private static Dictionary<string, object?> VerifierRow(string result, string status = "complete") => new()
    {
        ["id"] = "task-v", ["assigned_ant"] = "verifier", ["status"] = status, ["result"] = result,
    };

    private static Objective Parent(int depth = 0) => new()
    {
        Id = "obj-parent", Title = "ship the widget", Charter = "make the widget work",
        Priority = 5, MaxRuns = 5,
        Metadata = depth > 0 ? new Dictionary<string, object?> { ["follow_up_depth"] = depth } : new(),
    };

    // ---- reading the evidence -------------------------------------------------------------------

    [Fact]
    public void MissingStepsAreReadFromTheVerifiersOwnOutput()
    {
        var steps = EvidenceFollowUps.MissingSteps(VerifierWithGaps);
        Assert.Equal(2, steps.Count);
        Assert.Contains("add a regression test for the new branch", steps);
        Assert.Contains("update the operator docs", steps);
    }

    /// <summary>"None identified" is the verifier saying nothing is missing — never a follow-up.</summary>
    [Fact]
    public void ACleanVerificationProducesNoSteps() =>
        Assert.Empty(EvidenceFollowUps.MissingSteps(VerifierClean));

    [Fact]
    public void TheBlockEndsAtTheNextSection_NotAtTheEndOfTheText()
    {
        var steps = EvidenceFollowUps.MissingSteps(VerifierWithGaps);
        Assert.DoesNotContain(steps, s => s.Contains("Risk Notes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The bug this parser actually had. `StaticVerify` writes the clean case INLINE —
    /// "Missing Steps: None identified by static verification." — so the findings block is empty
    /// and the next line is "Risk Notes: ...". Stopping at "a line containing a colon" was not
    /// enough: with no steps collected yet, that rule did not fire, and the parser produced a
    /// follow-up objective titled "Risk Notes: none" — work invented from a section header, on a
    /// verification that found nothing wrong.
    /// </summary>
    [Fact]
    public void AnInlineCleanResult_DoesNotLeakTheFollowingSectionAsAFinding()
    {
        const string inlineClean =
            "Verification Passed\nReasoning: complete.\n" +
            "Missing Steps: None identified by static verification.\nRisk Notes: none.";

        var steps = EvidenceFollowUps.MissingSteps(inlineClean);
        Assert.Empty(steps);
        Assert.DoesNotContain(steps, s => s.StartsWith("Risk Notes", StringComparison.OrdinalIgnoreCase));

        // And therefore no objective is created from a clean verification.
        Assert.Empty(EvidenceFollowUps.From(
            new[] { VerifierRow(inlineClean) }, "m1", Parent(), MissionOutcome.CompletedVerified));
    }

    /// <summary>Fed the real static verifier text, so a wording change fails loudly.</summary>
    [Fact]
    public void TheRealStaticVerifierCleanOutput_ProducesNoFollowUps()
    {
        const string real =
            "Verification Passed\nReasoning: Mission has completed task output and at least one builder/coder result.\n" +
            "Missing Steps: None identified by static verification.\n" +
            "Risk Notes: Static verification does not evaluate factual content.";

        Assert.Empty(EvidenceFollowUps.MissingSteps(real));
    }

    [Fact]
    public void AbsentOrMalformedOutputProducesNothing()
    {
        Assert.Empty(EvidenceFollowUps.MissingSteps(null));
        Assert.Empty(EvidenceFollowUps.MissingSteps(""));
        Assert.Empty(EvidenceFollowUps.MissingSteps("Verification Passed\nReasoning: fine."));
    }

    // ---- only verified missions, and only completed verifiers ---------------------------------------

    /// <summary>
    /// An unverified mission's "missing steps" describe work that may not be missing at all — the
    /// thing that was supposed to check is what failed.
    /// </summary>
    [Theory]
    [InlineData(MissionOutcome.CompletedUnverified)]
    [InlineData(MissionOutcome.Partial)]
    [InlineData(MissionOutcome.FailedPermanent)]
    public void AnUnverifiedMissionProducesNoFollowUps(string outcome) =>
        Assert.Empty(EvidenceFollowUps.From(new[] { VerifierRow(VerifierWithGaps) }, "m1", Parent(), outcome));

    [Fact]
    public void AVerifierThatDidNotComplete_ProducesNothing() =>
        Assert.Empty(EvidenceFollowUps.From(
            new[] { VerifierRow(VerifierWithGaps, status: "failed") }, "m1", Parent(), MissionOutcome.CompletedVerified));

    [Fact]
    public void NoTasksOrNoParent_ProducesNothing()
    {
        Assert.Empty(EvidenceFollowUps.From(null, "m1", Parent(), MissionOutcome.CompletedVerified));
        Assert.Empty(EvidenceFollowUps.From(Array.Empty<Dictionary<string, object?>>(), "m1", Parent(), MissionOutcome.CompletedVerified));
        Assert.Empty(EvidenceFollowUps.From(new[] { VerifierRow(VerifierWithGaps) }, "m1", null, MissionOutcome.CompletedVerified));
    }

    // ---- the three properties the ADR demands ---------------------------------------------------------

    /// <summary>
    /// A follow-up gets its OWN budget. Drawing on the parent's remaining runs would let an
    /// objective extend itself indefinitely by discovering more work each time.
    /// </summary>
    [Fact]
    public void AFollowUpCarriesItsOwnBudget_NotTheParents()
    {
        var parent = Parent();
        var followUps = EvidenceFollowUps.From(
            new[] { VerifierRow(VerifierWithGaps) }, "m1", parent, MissionOutcome.CompletedVerified);

        Assert.All(followUps, f =>
        {
            Assert.Equal(1, f.MaxRuns);
            Assert.NotEqual(parent.MaxRuns, f.MaxRuns);
            Assert.Equal(parent.Id, f.ParentObjectiveId);
        });
    }

    /// <summary>Findings must not be able to generate an unbounded objective tree.</summary>
    [Fact]
    public void TheChainTerminatesAtTheDepthLimit()
    {
        Assert.NotEmpty(EvidenceFollowUps.From(
            new[] { VerifierRow(VerifierWithGaps) }, "m1", Parent(depth: EvidenceFollowUps.MaxFollowUpDepth - 1),
            MissionOutcome.CompletedVerified));

        Assert.Empty(EvidenceFollowUps.From(
            new[] { VerifierRow(VerifierWithGaps) }, "m1", Parent(depth: EvidenceFollowUps.MaxFollowUpDepth),
            MissionOutcome.CompletedVerified));
    }

    [Fact]
    public void EachFollowUpRecordsTheEvidenceThatCausedIt()
    {
        var followUp = EvidenceFollowUps.From(
            new[] { VerifierRow(VerifierWithGaps) }, "m-source", Parent(), MissionOutcome.CompletedVerified).First();

        Assert.Equal("verification_evidence", followUp.Metadata["origin"]);
        Assert.Equal("m-source", followUp.Metadata["source_mission_id"]);
        Assert.Equal("task-v", followUp.Metadata["source_task_id"]);
        Assert.Equal(1, followUp.Metadata["follow_up_depth"]);
        // Traceable back to the sentence that caused it.
        Assert.Contains("regression test", followUp.Charter);
    }

    [Fact]
    public void DepthIsReadBackFromMetadata()
    {
        Assert.Equal(0, EvidenceFollowUps.DepthOf(Parent()));
        Assert.Equal(0, EvidenceFollowUps.DepthOf(null));
        Assert.Equal(2, EvidenceFollowUps.DepthOf(Parent(depth: 2)));
    }

    // ---- the call site ---------------------------------------------------------------------------------

    [Fact]
    public void TheDirectorEnqueuesEvidenceFollowUps()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ColonyDirector.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        Assert.Contains("EvidenceFollowUps.From(", code);
        Assert.Contains("evidence_follow_ups_created", code);
        // v2.26.0: the two sources are deliberately NOT merged any more. Evidence-derived
        // follow-ups stay auto-admitted; Strategist proposals are model opinions and land as
        // `suggested`, requiring operator approval before they can execute.
        Assert.Contains("SaveFollowUps(strategy.FollowUps, job.MissionId, run.Id, suggested: true)", code);
        Assert.Contains("SaveFollowUps(evidenceFollowUps, job.MissionId, run.Id, suggested: false)", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
