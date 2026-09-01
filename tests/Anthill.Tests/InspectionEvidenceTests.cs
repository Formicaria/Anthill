using Anthill.Core.Tools;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — AN INSPECTION IS RECORDED, AND STILL PROMOTES NOTHING.
///
/// WHY THE LANE WAS ADDED. An assessment mission's authority is `observe`: it runs no checks, so
/// the deterministic lane is empty by design and the evidence store stayed empty however much the
/// colony actually read. "This audit inspected nothing and asserted its findings" and "this audit
/// read the repository" were the same record — which is mission `7afd85b2`'s exact shape, and the
/// reason its emptiness could not be detected by the runtime that produced it.
///
/// WHY THAT IS NOT A WEAKENING. The rule being widened is WHERE evidence comes from. The rule left
/// strictly alone is WHAT a verdict is: `run_allowlisted_check` remains the only tool in the
/// deterministic table, an inspection is recorded non-deterministic, and every promotion path
/// filters on `deterministic = 1`. These tests hold that boundary from both sides — the lane
/// produces rows, and those rows cannot carry a mission to a verified outcome.
/// </summary>
public class InspectionEvidenceTests
{
    private const string Mission = "m_inspection";

    [Fact]
    public void TheVerdictLane_StillHasExactlyOneTool()
    {
        Assert.True(ToolEvidence.IsDeterministic("run_allowlisted_check"));

        foreach (var readOnly in new[] { "list_directory", "read_text_file", "search_workspace", "repository_index" })
        {
            Assert.False(ToolEvidence.IsDeterministic(readOnly));
            Assert.True(ToolEvidence.IsObservation(readOnly));
            Assert.True(ToolEvidence.Records(readOnly));
        }

        // v0.3.8.109 — THE RETRIEVAL LANE, and `web_search` moved into it from the line below.
        //
        // This assertion said web_search records NOTHING, and at `.98` that was right: the only
        // reason to record a read was so an audit could show it had inspected the operator's own
        // repository, and a search of the internet is no evidence of that. `.109` gives the outward
        // read its own kind rather than folding it into `inspection`, which keeps that reasoning
        // intact — an audit still cannot satisfy its inspection requirement by searching the web —
        // while making "did this research mission retrieve anything" answerable from the store.
        foreach (var retrieval in new[] { "web_search", "open_public_source", "read_public_source" })
        {
            Assert.False(ToolEvidence.IsDeterministic(retrieval));
            Assert.False(ToolEvidence.IsObservation(retrieval));
            Assert.True(ToolEvidence.IsRetrieval(retrieval));
            Assert.True(ToolEvidence.Records(retrieval));
        }

        // Everything else still records nothing at all. The store is not an audit log — the event
        // stream is — and widening it further costs the property that makes it useful.
        foreach (var unrecorded in new[] { "shell_command", "write_text_file", "apply_patch", "system_info" })
            Assert.False(ToolEvidence.Records(unrecorded));
    }

    [Fact]
    public void AnObservation_IsRecordedNonDeterministic_AndClaimsNoRevision()
    {
        var evidence = ToolEvidence.For("read_text_file", success: true, Mission, taskId: "t1",
            detail: "src/Anthill.Core/Orchestration/Queen.cs");

        Assert.NotNull(evidence);
        Assert.Equal(EvidenceKinds.Inspection, evidence!.Kind);
        Assert.False(evidence.Deterministic);
        Assert.True(evidence.Passed);
        // The tool is named in the detail: "an inspection happened" is only useful if a reader can
        // tell a directory listing from a file read.
        Assert.StartsWith("read_text_file:", evidence.Detail, StringComparison.Ordinal);
        // No identity stamped. An unpatched workspace is not a revision, and labelling one would
        // let an observation of the base tree look like evidence about a promotion candidate.
        Assert.False(evidence.IdentifiesARevision);
    }

    /// <summary>
    /// The kind's reproducibility and the row's claim must agree — the check
    /// <see cref="EvidenceKinds.AgreesWithKind"/> exists to make. An `inspection` that claimed to be
    /// deterministic would be exactly the mislabel this lane is designed not to be.
    /// </summary>
    [Fact]
    public void TheInspectionKind_IsNotReproducible()
    {
        Assert.DoesNotContain(EvidenceKinds.Inspection, EvidenceKinds.Reproducible);
        Assert.True(EvidenceKinds.AgreesWithKind(EvidenceKinds.Inspection, deterministic: false));
        Assert.False(EvidenceKinds.AgreesWithKind(EvidenceKinds.Inspection, deterministic: true));
    }

    /// <summary>
    /// THE LOAD-BEARING ONE. A mission whose every evidence row is an inspection has verified
    /// nothing, and the verdict must say so in those words — the old "no evidence was recorded"
    /// sentence becomes "reviews recorded and NO reproducible check", which is a different and more
    /// accurate statement, and neither of them is a pass.
    /// </summary>
    [Fact]
    public void AMissionOfPureInspection_IsNotVerified()
    {
        var rows = new[]
        {
            ToolEvidence.For("list_directory", true, Mission, "t1", ".")!,
            ToolEvidence.For("read_text_file", true, Mission, "t1", "README.md")!,
        };

        var verdict = Anthill.Core.Outcomes.EvidenceVerdict.For(rows);

        Assert.Equal(Anthill.Core.Outcomes.VerificationVerdict.Unknown, verdict.Verdict);
        Assert.False(verdict.IsPass);
        Assert.False(verdict.HasDeterministicEvidence);
        Assert.Equal(0, verdict.DeterministicPassed);
        Assert.Equal(rows.Length, verdict.NonDeterministicRecorded);
    }

    /// <summary>And a failed read is recorded as a failed inspection rather than dropped: "I looked
    /// and could not read it" is a finding, and an audit that loses it reports a gap as a fact.</summary>
    [Fact]
    public void AFailedRead_IsStillRecorded_AsAFailedObservation()
    {
        var evidence = ToolEvidence.For("read_text_file", success: false, Mission, taskId: "t1",
            detail: "path outside the allowed workspace root");

        Assert.NotNull(evidence);
        Assert.False(evidence!.Passed);
        Assert.False(evidence.Deterministic);
    }
}
