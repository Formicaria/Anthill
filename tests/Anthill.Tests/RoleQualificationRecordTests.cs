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
/// THE POINT IS THE GAPS. This ledger is not an achievement record. NO role has a
/// cancellation-and-timeout proof — twelve of twelve cells empty — and saying so is the deliverable.
/// A graduation record showing twelve complete rows would be describing a colony this is not.
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
        new("researcher", Row("StructuredCoreOutputTests.cs", Contract, Roster, Lifecycle, null)),
        new("web",        Row(Structured, Contract, Roster, Lifecycle, null)),
        new("file",       Row("WorkspaceToolsTests.cs", Contract, Roster, Lifecycle, null)),
        new("coder",      Row("SandboxedCoderRunnerTests.cs", "CodePatchLifecycleTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, null)),
        new("builder",    Row(Structured, Contract, Roster, Lifecycle, null)),
        new("verifier",   Row("VerificationVerdictTests.cs", "VerificationFrameworkTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, null)),

        new("ui_cartographer", Row("UiCartographerAntTests.cs", "UiChangeGateTests.cs",
                                   null, Lifecycle, null)),
        new("tester",     Row("TesterAntTests.cs", "TesterAntStructuredTests.cs",
                              "FailureHandoffGateTests.cs", "MissionRevisionTests.cs", null)),
        new("soldier",    Row("SoldierAntTests.cs", "StageBConsequentialTests.cs",
                              "DeterministicBlockTests.cs", Lifecycle, null)),
        new("scribe",     Row("ScribeAntTests.cs", "ScribeArchivistOrderingTests.cs",
                              "RoleGraduationTests.cs", Lifecycle, null)),
        new("medic",      Row("MedicAntTests.cs", "BoundedRepairTests.cs",
                              "FailureHandoffGateTests.cs", Lifecycle, null)),
        new("archivist",  Row("ArchivistAntTests.cs", "ScribeArchivistOrderingTests.cs",
                              "MemoryCandidateIngestTests.cs", "FinalizationOrderTests.cs", null)),
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
    // The gaps
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The record is HONEST about being incomplete, and the plan says which rows are short.
    ///
    /// This assertion is deliberately the opposite of the usual one: it fails if the ledger ever
    /// claims a complete colony, because at that point somebody has either done the work — and should
    /// delete this test with the evidence in hand — or filled the cells in to make the suite quiet.
    /// The second is far more likely, and this is the only place it can be caught.
    /// </summary>
    [Fact]
    public void TheRecordDeclaresItsGaps_AndThePlanNamesThem()
    {
        var gaps = Records
            .SelectMany(r => r.Proofs.Where(p => p.Value is null).Select(p => $"{r.Role}/{p.Key}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(gaps.Count > 0,
            "every graduation cell is now filled. If that is real, this test should be deleted along "
          + "with the evidence that closed the last gap. If it is not real, a cell was filled to quiet "
          + "the suite — check the cancellation column first, which is where the genuine gaps were.");

        var plan = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "PLAN.md"));
        Assert.Contains("graduation record", plan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NO ROLE HAS A CANCELLATION-AND-TIMEOUT PROOF. Twelve of twelve cells are empty.
    ///
    /// This is the finding, and it arrived by the ledger catching me rather than by inspection. The
    /// first draft filled five of these cells with `ModelCallCancellationTests` and the tester's with
    /// `ProcessTreeCancellationTests`. Both files are real and both prove something true — the model
    /// call observes cancellation; the process-launching SITES kill their trees. Neither says anything
    /// about a ROLE. A citation that is true about the system and false about the row is precisely how
    /// a graduation record fills up while nothing gets proved, and the check that caught it was the
    /// weakest one in the file: does the cited file so much as mention this role.
    ///
    /// It matters more than the count suggests. v0.3.8.57 found FIVE separate sites that abandoned a
    /// running process on timeout, all of them in the area this column is emptiest about. Under-tested
    /// and under-implemented turned out to be the same region, which is the argument for writing the
    /// gap down instead of leaving it to be discovered again.
    /// </summary>
    [Fact]
    public void NoRoleHasACancellationProof_AndThatIsRecordedRatherThanHidden()
    {
        var without = Records
            .Where(r => r.Proofs["cancellation-and-timeout"] is null)
            .Select(r => r.Role)
            .ToList();

        Assert.Equal(Records.Length, without.Count);

        // Every other column is better covered. When that stops being true — because someone writes a
        // real per-role cancellation proof — this test's premise is stale and should be rewritten
        // with the evidence, not relaxed.
        foreach (var kind in ProofKinds.Where(k => k != "cancellation-and-timeout"))
        {
            var missing = Records.Count(r => r.Proofs[kind] is null);
            Assert.True(missing < without.Count,
                $"the '{kind}' column now has {missing} gaps against cancellation's {without.Count}. "
              + "Either cancellation gained a proof or another column lost one; either way the claim "
              + "above no longer describes the colony.");
        }
    }
}
