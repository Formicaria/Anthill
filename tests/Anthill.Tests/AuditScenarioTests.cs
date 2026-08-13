using Anthill.Core.Domain;
using Anthill.Core.Verification;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.53 — audit Phase 10: the qualification scenario matrix, as a LEDGER with one previously
/// unpinned scenario proved below. The audit names twenty scenarios; most already have production
/// tests, and re-proving them here would be the parallel-implementation the audit forbids. Where
/// each one lives:
///
///   1  research mission, composed        → ColonyAcceptanceTests.ScenarioA
///   5  passes-on-base/fails-on-patch     → MissionRevisionTests (unpatched-tree evidence refused;
///                                          the tester runs IN the materialized revision)
///   6  medic repair + mandatory fresh    → MedicAntTests + MissionRevisionTests (a repair's new
///                                          patch set REPLACES the revision; old green cannot ride)
///   8  planner omits tester              → SchedulingMode.PolicyInserted covers tester/soldier in
///                                          ExecutionService; verification fails closed without a
///                                          tester run on the LATEST revision (MissionRevisionTests)
///   9  planner omits verifier            → PlanVerificationPolicyTests (appended with lineage)
///   10 stale evidence rejected           → MissionRevisionTests.PatchB_CannotBeVerified…
///   11 provider outage ≠ ant blame       → FullRosterQualificationTests (neutral attribution)
///   13 cancellation leaves nothing       → ColonyAcceptanceTests.ScenarioK
///   14 restart keeps the graph           → ColonyAcceptanceTests.ScenarioL
///   15 base-hash conflict refused        → PatchBaseHashTests
///   16 partial PatchSet is atomic        → THIS FILE (was unpinned)
///   17 direct-agent edit → unverified    → DirectAgentLaneTests (v0.3.8.53)
///   18 archivist after evaluation        → FinalizationOrderTests.TheArchivistRunsBeforeLearning
///   19 finalization replay idempotent    → FinalizationOrderTests + the --qualification probe
///
/// REMAINING OPEN, stated plainly (the audit's own rule): scenarios 3/4/7 (a full doc/code patch
/// lifecycle driven composed through the Queen with a scripted reasoning provider, and a Soldier
/// block therein) and 20 (all twelve roles through their production triggers in one deterministic
/// scenario set). docs/PLAN.md §6 names 20 as the next release's whole job; TwelveRoleEndToEndTests
/// states in its own header why it is not a substitute.
/// </summary>
public class AuditScenarioTests : IDisposable
{
    private readonly string _src;

    public AuditScenarioTests()
    {
        _src = Path.Combine(Path.GetTempPath(), "anthill-audit16-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_src);
        File.WriteAllText(Path.Combine(_src, "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(_src, "b.txt"), "beta\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_src, recursive: true); } catch { }
    }

    /// <summary>
    /// Scenario 16: a PatchSet whose second proposal cannot apply must materialize NOTHING —
    /// no half-applied tree offered for evaluation — and the source tree must be byte-identical
    /// afterwards, even though the FIRST proposal was individually applicable.
    /// </summary>
    [Fact]
    public void PartialPatchSetFailure_MaterializesNothing_AndLeavesTheSourceUntouched()
    {
        var set = new PatchSet
        {
            MissionId = "m-audit16", TaskId = "t-audit16", Summary = "atomicity probe",
            Proposals =
            {
                new PatchProposal
                {
                    FilePath = "a.txt", ChangeType = PatchChangeType.Modify,
                    OldContent = "alpha", NewContent = "ALPHA",
                    BaseHash = PatchApply.HashOf("alpha\n"),
                },
                new PatchProposal
                {
                    // Guaranteed refusal: this old content exists nowhere in b.txt.
                    FilePath = "b.txt", ChangeType = PatchChangeType.Modify,
                    OldContent = "this text is not in the file", NewContent = "BETA",
                },
            },
        };

        var result = PatchSetMaterializer.Materialize(set, _src);

        Assert.Null(result.Materialized);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));

        // The source tree is untouched — including the file whose proposal WAS applicable.
        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(_src, "a.txt")));
        Assert.Equal("beta\n", File.ReadAllText(Path.Combine(_src, "b.txt")));
    }
}
