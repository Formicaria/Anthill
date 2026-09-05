using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.122 — the two facts a finished mission used to lose.
///
/// `docs/ORCHESTRATION-FINDINGS.md` mapped the eight places mission orchestration decides something,
/// and two of its three ▲ findings are about a decision that was MADE and then not recorded anywhere
/// a later stage could read:
///
///   • the planner substituted a static plan on five conditions and reported every one of them to
///     `Console.Error` and nothing else;
///   • the structural status never consulted the verification verdict.
///
/// The FIRST is fixed and tested here, as a pure function: no database, no scheduler, no mission.
///
/// The SECOND is not, and the attempt is worth more than the fix would have been.
/// `Verification.Failed` does not mean "a check said no" — `MissionVerification.IsSatisfied` needs
/// the verifier's verdict to be a PASS, and `VerifierAnt` downgrades a model-authored pass to
/// `Unknown` when no deterministic evidence backs it. So `failed` spans "the check said no" and
/// "nothing could satisfy the check", and demoting on it reclassified a legitimately complete
/// mission. `ScriptedProviderTests` caught that in one run. Closure enforcement needs those two
/// separated first, which needs the per-task execution record — `docs/PLAN.md` §2e.
/// </summary>
public class OrchestrationRecordTests
{
    // ---- the planner's substitutions ----------------------------------------------------------

    /// <summary>No router: `CreateTasks` goes straight to the static fallback, which is one of the
    /// five substitutions and the easiest to reach without a model.</summary>
    private static Planner Offline() => new(useOllama: false, router: null);

    /// <summary>
    /// THE SUBSTITUTION IS REPORTED, NOT MERELY DONE. Before this, a mission planned by the static
    /// fallback was indistinguishable in the record from one planned as requested — same tasks
    /// table, same events, same green run. The reason code is what makes "the colony ignored my
    /// goal" and "the colony did what I asked and the answer is poor" different findings.
    /// </summary>
    [Fact]
    public void AbsentAModel_ThePlannerReportsThatItSubstitutedAPlan()
    {
        var reported = new List<string>();
        var before = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableWebSearch = false;
            var tasks = Offline().CreateTasks("What is the queen's role in the colony?",
                MissionConstraints.None, onSubstituted: (reason, _) => reported.Add(reason));

            Assert.NotEmpty(tasks);   // the mission still runs; a substitution is not a failure
            Assert.Equal(PlanSubstitutions.NoModelRouter, Assert.Single(reported));
        }
        finally { AnthillRuntime.EnableWebSearch = before; }
    }

    /// <summary>
    /// THE GATE THAT PUNISHES PRECISION, NOW VISIBLE. Spec ingestion fires on `goal.Length` and
    /// nothing else, so a detailed workflow request — long by construction — is the input MOST
    /// likely to be chunked into section analyses instead of followed. `.122` does not move that
    /// gate; moving it needs the execution records. It makes a mission it fired on stop looking
    /// identical to one that was planned as asked.
    /// </summary>
    [Fact]
    public void ALongGoal_ReportsThatItWasPlannedAsSpecIngestion()
    {
        var reported = new List<string>();
        var wasEnabled = AnthillRuntime.EnableSpecIngestion;
        var wasThreshold = AnthillRuntime.LongInputThreshold;
        try
        {
            AnthillRuntime.EnableSpecIngestion = true;
            AnthillRuntime.LongInputThreshold = 6000;
            var goal = new string('x', AnthillRuntime.LongInputThreshold + 1);

            Offline().CreateTasks(goal, MissionConstraints.None,
                onSubstituted: (reason, _) => reported.Add(reason));

            Assert.Contains(PlanSubstitutions.LongInputSpecIngestion, reported);
        }
        finally
        {
            AnthillRuntime.EnableSpecIngestion = wasEnabled;
            AnthillRuntime.LongInputThreshold = wasThreshold;
        }
    }

    /// <summary>
    /// A CALLER THAT ASKS FOR NOTHING GETS EXACTLY WHAT IT ALWAYS GOT. The reporting is an optional
    /// trailing callback for one reason: every existing call site, in production and in the two
    /// planning test classes beside this one, passes nothing and must keep planning identically.
    /// `.118` shipped a CS0535 to CI by widening a signature that an interface member had to match;
    /// this asserts the widening is additive at the only level that matters — the behaviour.
    /// </summary>
    [Fact]
    public void WithNoCallback_PlanningIsUnchanged()
    {
        var before = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableWebSearch = false;
            const string goal = "What is the queen's role in the colony?";

            var withReporting = Offline().CreateTasks(goal, MissionConstraints.None,
                onSubstituted: (_, _) => { });
            var without = Offline().CreateTasks(goal, MissionConstraints.None);

            Assert.Equal(without.Count, withReporting.Count);
            Assert.Equal(without.Select(t => t.TaskType), withReporting.Select(t => t.TaskType));
            Assert.Equal(without.Select(t => t.AssignedAnt), withReporting.Select(t => t.AssignedAnt));
        }
        finally { AnthillRuntime.EnableWebSearch = before; }
    }

    /// <summary>
    /// VACUITY FLOOR. Every declared reason code is distinct and non-empty, and the two the tests
    /// above actually reach are among them — so a future edit that renames a code cannot leave these
    /// facts passing against a vocabulary nothing uses.
    /// </summary>
    [Fact]
    public void TheSubstitutionVocabulary_IsCompleteAndDistinct()
    {
        Assert.Equal(6, PlanSubstitutions.All.Count);
        Assert.Equal(PlanSubstitutions.All.Count, PlanSubstitutions.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PlanSubstitutions.All, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        Assert.Contains(PlanSubstitutions.NoModelRouter, PlanSubstitutions.All);
        Assert.Contains(PlanSubstitutions.LongInputSpecIngestion, PlanSubstitutions.All);
        // v0.3.8.123's sixth. Reachable, and reached by `EvidenceGroundedPlanningTests` — a code in
        // this list that nothing emits is the declaration-reaching-nobody defect in vocabulary form.
        Assert.Contains(PlanSubstitutions.GroundedInspectionRequired, PlanSubstitutions.All);
    }
}
