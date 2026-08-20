using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The per-role GRADUATION RECORD: nine proofs per role, and an honest account of the ones missing.
/// v0.3.8.57.
///
/// PLAN.md asks that each ant carry a unit proof, an integration proof, a production-call-site proof,
/// a fault proof, an end-to-end proof, a cancellation-and-timeout proof, an activation record, a
/// rollback/kill switch, and an exact readiness blocker. The readiness SURFACE exists —
/// `RoleReadiness` reports per role — but readiness answers "can this run now", which is a different
/// question from "has this been proved". A role can be Ready and have no fault proof at all.
///
/// WHY A LEDGER RATHER THAN A SWEEP. A sweep would have to guess which test proves what, and the
/// guess would be a substring match on a role name — which every one of these roles satisfies in
/// twenty files, most of them incidentally. A role named in `AntExecutorCatalogTests` is not thereby
/// fault-proved. So each cell is CLAIMED explicitly and checked shallowly (the file exists, and it
/// mentions the role), and the value is that a missing cell is visible rather than inferred.
///
/// THE POINT WAS THE GAPS, and at v0.3.8.57 that read: "NO role has a cancellation-and-timeout
/// proof — twelve of twelve cells empty — and saying so is the deliverable." It was true for
/// twenty-three releases.
///
/// v0.3.8.81 CLOSES THE COLUMN, and the two tests at the bottom of this file are the ones that used
/// to assert it was open. Both were rewritten rather than relaxed, because both had the shape this
/// repository has now corrected three times: `PartialCoverage_IsDeclaredRatherThanImplied` at
/// v0.3.8.79, its sibling at v0.3.8.74, and now these — a guard that asserts a gap EXISTS cannot
/// express the outcome the work was for, so it stops being a guard and becomes a deadline. The
/// replacements assert the opposite thing for the same reason: the column is full, one matrix decides
/// every cell, and PLAN.md still names the cells that are cited rather than driven — so a record that
/// LOOKS complete cannot quietly stop saying how complete it is.
///
/// The remaining honest gaps are recorded in the matrix rather than here. Two of the forty-eight
/// cancellation cells are CITED — `verifier/during_generation` and `tester/during_tool_call`, both
/// `SchedulingMode.PolicyInserted`. Four more are NOT-APPLICABLE for a contract reason found at
/// v0.3.8.82: the medic and the archivist are `FailureTriggered` / `PostFinalization`, so a planner
/// may not assign them and a fixture that drives a role by planning a task cannot reach their
/// `before_dispatch` or `awaiting_dependency` points at all. Those four LOOKED driven for two
/// releases, because the scripted plan was being discarded for a fallback that contained neither
/// role — the assertions passed about a mission in which the named role never appeared.
/// </summary>
public class RoleQualificationRecordTests
{
    /// <summary>
    /// The nine proofs PLAN.md asks for. Named as an enum-like list so a missing KIND is as visible
    /// as a missing role — the first draft of this ledger simply had no column for cancellation, and
    /// a gap with no column is one nobody can see.
    /// </summary>
    private static readonly string[] ProofKinds =
    {
        "unit", "integration", "production-call-site", "fault", "end-to-end",
        "cancellation-and-timeout", "activation", "kill-switch", "readiness-blocker",
    };

    /// <param name="Proofs">
    /// Proof kind → the test file that establishes it, or null when nothing does. Null is the
    /// load-bearing value in this record.
    /// </param>
    private sealed record RoleRecord(string Role, Dictionary<string, string?> Proofs);

    /// <summary>
    /// Shared cells. Several proofs are established once for EVERY role by a catalog-wide test, and
    /// duplicating them per role would be twelve copies of one fact — which is how a ledger comes to
    /// disagree with itself.
    /// </summary>
    private const string Activation = "ActivationTierTests.cs";
    private const string KillSwitch = "RuntimeRosterTests.cs";
    private const string Readiness = "RoleReadinessTests.cs";
    private const string CallSite = "AntExecutorCatalogTests.cs";
    private const string Contract = "AntExecutionFrameworkTests.cs";
    private const string Roster = "FullRosterQualificationTests.cs";
    private const string Structured = "AntStructuredResultTests.cs";
    private const string Lifecycle = "CodePatchLifecycleTests.cs";

    /// <summary>
    /// v0.3.8.81 — one file for every row, because ONE matrix decides all forty-eight cells. Twelve
    /// separate citations would be twelve copies of one fact, which is how a ledger comes to disagree
    /// with itself — the same argument the shared cells above already make.
    /// </summary>
    private const string Cancellation = "RoleCancellationTests.cs";

    private static Dictionary<string, string?> Row(
        string? unit, string? integration, string? fault, string? endToEnd, string? cancellation) =>
        new()
        {
            ["unit"] = unit,
            ["integration"] = integration,
            ["production-call-site"] = CallSite,
            ["fault"] = fault,
            ["end-to-end"] = endToEnd,
            ["cancellation-and-timeout"] = cancellation,
            ["activation"] = Activation,
            ["kill-switch"] = KillSwitch,
            ["readiness-blocker"] = Readiness,
        };

    private static readonly RoleRecord[] Records =
    {
        new("researcher", Row("StructuredCoreOutputTests.cs", Contract, Roster, Lifecycle, Cancellation)),
        new("web",        Row(Structured, Contract, Roster, Lifecycle, Cancellation)),
        new("file",       Row("WorkspaceToolsTests.cs", Contract, Roster, Lifecycle, Cancellation)),
        new("coder",      Row("SandboxedCoderRunnerTests.cs", "CodePatchLifecycleTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, Cancellation)),
        new("builder",    Row(Structured, Contract, Roster, Lifecycle, Cancellation)),
        new("verifier",   Row("VerificationVerdictTests.cs", "VerificationFrameworkTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, Cancellation)),

        // v0.3.8.81 — the fault cell, and the last non-cancellation null in the record. Filled with a
        // NEW file rather than with the unit cell's `UiCartographerAntTests.cs`, which contains a
        // fault about the INPUT (an empty workspace) and none about the TOOL. Citing it here would
        // have been true about the file and false about the column.
        new("ui_cartographer", Row("UiCartographerAntTests.cs", "UiChangeGateTests.cs",
                                   "UiCartographerFaultTests.cs", Lifecycle, Cancellation)),
        new("tester",     Row("TesterAntTests.cs", "TesterAntStructuredTests.cs",
                              "FailureHandoffGateTests.cs", "MissionRevisionTests.cs", Cancellation)),
        new("soldier",    Row("SoldierAntTests.cs", "StageBConsequentialTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, Cancellation)),
        new("scribe",     Row("ScribeAntTests.cs", "ScribeArchivistOrderingTests.cs",
                              "RoleGraduationTests.cs", Lifecycle, Cancellation)),
        new("medic",      Row("MedicAntTests.cs", "BoundedRepairTests.cs",
                              "FailureHandoffGateTests.cs", Lifecycle, Cancellation)),
        new("archivist",  Row("ArchivistAntTests.cs", "ScribeArchivistOrderingTests.cs",
                              "MemoryCandidateIngestTests.cs", "FinalizationOrderTests.cs", Cancellation)),
    };

    private static string TestPath(string file) =>
        Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", file);

    // -------------------------------------------------------------------------------------------
    // The record covers the colony
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every executable role has a row. A role that graduates without a record is one whose readiness
    /// nobody wrote down — and readiness is reported per role, so its absence here would be invisible.
    /// </summary>
    [Fact]
    public void EveryExecutableRole_HasAGraduationRecord()
    {
        var executable = AntRegistry.ExecutableRoleIds.OrderBy(r => r, StringComparer.Ordinal).ToList();
        var recorded = Records.Select(r => r.Role).ToHashSet(StringComparer.Ordinal);

        var unrecorded = executable.Where(r => !recorded.Contains(r)).ToList();

        Assert.True(unrecorded.Count == 0,
            $"these executable roles have no graduation record: {string.Join(", ", unrecorded)}. "
          + "Add a row — including one that is mostly nulls, which is a truthful record and a useful one.");
    }

    /// <summary>
    /// Every row has a cell for every proof kind — present or explicitly null. A row missing a KEY is
    /// different from one whose value is null: the first is a question nobody asked.
    /// </summary>
    [Fact]
    public void EveryRecord_AddressesEveryProofKind()
    {
        foreach (var record in Records)
            foreach (var kind in ProofKinds)
                Assert.True(record.Proofs.ContainsKey(kind),
                    $"{record.Role} has no '{kind}' cell. A missing cell is an unasked question; a null "
                  + "cell is a recorded gap, and only the second one can be scheduled.");
    }

    /// <summary>
    /// Every claimed proof is a real file that mentions the role.
    ///
    /// The role check is shallow ON PURPOSE and is not pretending otherwise: it catches a citation
    /// pasted from the wrong row, which is the realistic error in a hand-built table this size. It
    /// does not and cannot establish that the file proves what the cell claims.
    /// </summary>
    [Fact]
    public void EveryClaimedProof_ExistsAndConcernsThatRole()
    {
        var problems = new List<string>();

        foreach (var record in Records)
            foreach (var (kind, file) in record.Proofs)
            {
                if (file is null) continue;

                var path = TestPath(file);
                if (!File.Exists(path)) { problems.Add($"{record.Role}/{kind}: {file} does not exist"); continue; }

                // Catalog-wide proofs cover every role by construction and need not name one.
                if (file is Activation or KillSwitch or Readiness or CallSite) continue;

                if (!File.ReadAllText(path).Contains(record.Role, StringComparison.Ordinal))
                    problems.Add($"{record.Role}/{kind}: {file} never mentions '{record.Role}'");
            }

        Assert.True(problems.Count == 0,
            "graduation record citations that do not hold up:\n  " + string.Join("\n  ", problems));
    }

    // -------------------------------------------------------------------------------------------
    // The record is complete — and says how complete
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY CELL IS FILLED, and the plan still says which of them are cited rather than driven.
    ///
    /// This test replaces `TheRecordDeclaresItsGaps_AndThePlanNamesThem`, which asserted
    /// `gaps.Count > 0` — an assertion that would have failed for the single outcome the ledger
    /// exists to reach. That guard was right for twenty-three releases and could not survive being
    /// satisfied, which is the third time this repository has had to correct the same shape
    /// (v0.3.8.74, v0.3.8.79, here): a guard that cannot express success is not a guard, it is a
    /// deadline.
    ///
    /// What replaces it has to do the job the old one did, which was NOT "count nulls" — it was
    /// "stop a cell being filled to quiet the suite". So the completeness assertion is paired with
    /// the one that actually costs something to satisfy dishonestly: PLAN.md must still name the
    /// cancellation cells that are CITED rather than harness-driven. A future release that quietly
    /// converted a driven cell back to a citation, or that drove the last two and forgot to say so,
    /// fails here — which is the only way a full record keeps meaning anything.
    /// </summary>
    [Fact]
    public void TheRecordIsComplete_AndThePlanNamesWhatIsStillOnlyCited()
    {
        var gaps = Records
            .SelectMany(r => r.Proofs.Where(p => p.Value is null).Select(p => $"{r.Role}/{p.Key}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(gaps.Count == 0,
            "the graduation record has gaps again: " + string.Join(", ", gaps)
          + ". A cell that goes back to null is a proof that was deleted or a role that was added; "
          + "either way say which in PLAN.md rather than leaving the record to imply it.");

        var plan = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "PLAN.md"));
        Assert.Contains("graduation record", plan, StringComparison.OrdinalIgnoreCase);

        // The two cells the matrix still decides by CITATION rather than by driving them. Named here
        // by role and point so that closing them means editing this list with the evidence in hand.
        foreach (var cited in new[] { "verifier/during_generation", "tester/during_tool_call" })
            Assert.True(plan.Contains(cited, StringComparison.Ordinal),
                $"PLAN.md no longer names '{cited}' as a cited cancellation cell. If it was driven "
              + "live, remove it from this list in the release that did so; if it was simply dropped "
              + "from the plan, the record above now reads as more complete than the colony is.");
    }

    /// <summary>
    /// THE CANCELLATION COLUMN, and the test that used to assert it was empty.
    ///
    /// `NoRoleHasACancellationProof_AndThatIsRecordedRatherThanHidden` was the sharpest thing in this
    /// file: the first draft of the ledger filled five cells with `ModelCallCancellationTests` and the
    /// tester's with `ProcessTreeCancellationTests`, and the weakest check in the file — does the
    /// cited file so much as mention this role — caught it. Both files are real and prove something
    /// true about a MECHANISM; neither said anything about a ROLE.
    ///
    /// v0.3.8.80 built the matrix that decides all forty-eight role×point cells and v0.3.8.81 drove
    /// nine of them live, so the column is now citable for the reason it was never citable before.
    /// The check kept from the old test is the one that mattered: the citation must be a file that
    /// DECIDES this role rather than one that mentions it, which is asserted by requiring the matrix
    /// to carry its own completeness guard.
    /// </summary>
    [Fact]
    public void TheCancellationColumn_IsOneMatrixThatDecidesEveryRole()
    {
        var without = Records
            .Where(r => r.Proofs["cancellation-and-timeout"] is null)
            .Select(r => r.Role)
            .ToList();

        Assert.True(without.Count == 0,
            "these roles have no cancellation-and-timeout proof: " + string.Join(", ", without));

        // One file for twelve rows. Twelve different citations would mean twelve places for the
        // column to start disagreeing with itself about what proved what.
        var cited = Records.Select(r => r.Proofs["cancellation-and-timeout"]).Distinct().ToList();
        Assert.True(cited.Count == 1 && cited[0] == Cancellation,
            "the cancellation column cites more than one file: " + string.Join(", ", cited));

        // And that file carries the guard that makes the citation mean something. Without this the
        // column would rest on a filename, which is exactly the failure the old test caught.
        var matrix = File.ReadAllText(TestPath(Cancellation));
        Assert.Matches(@"\bEveryRoleAndPoint_IsDecidedExactlyOnce\s*\(", matrix);
        Assert.Matches(@"\bTheMatrix_CoversEveryExecutableRole\s*\(", matrix);

        foreach (var record in Records)
            Assert.True(matrix.Contains($"\"{record.Role}\"", StringComparison.Ordinal),
                $"the cancellation matrix never names '{record.Role}', so the guards above cannot be "
              + "deciding a cell for it.");
    }
}
