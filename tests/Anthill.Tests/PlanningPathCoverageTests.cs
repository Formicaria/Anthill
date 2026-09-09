using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Missions;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// EVERY PLANNING PATH LEAVES A PLAN THE MISSION'S CLASS CAN BE GRADED AGAINST. v0.3.8.146.
///
/// FOUND IN THE OPERATOR'S LIVE COLONY, not in the plan. "explain to me what science is" ended
/// `failed_permanent` in 13.7 seconds with one pending task, and the record said exactly why:
///
///     preflight refused this plan — unverified_criterion [simple_answer]: 'simple_answer' is
///     objectively verified and this plan has no verifier, so its integrity gate could never be
///     satisfied however well the work went.
///
/// `Planner.CreateTasks` has five return paths. TWO of them — a failed planner model call, and a plan the
/// parser rejected — returned `EnforceConstraints(FallbackTasks(goal), …)` and nothing else: no
/// `EnsureClassCoverage`, no `AssignDefaultWorkers`. The mission logged
/// `mission_plan_substituted (plan_rejected)`, took one of those two, and reached preflight with a
/// plan its own class could never satisfy.
///
/// AND THE FILE SAID OTHERWISE IN WRITING. The comment above those paths reads: "the step itself is
/// guaranteed by `EnsureClassCoverage`, which every one of the five return paths below funnels
/// through — that is where it belongs, because a guarantee written on one path is a guarantee the
/// other four do not have." It was right about the principle and wrong about the code, which is the
/// most expensive combination: nobody re-checks a claim that is already written down.
///
/// `.145` DID NOT CLOSE THIS, and its fix made the reach narrower rather than smaller.
/// `ClassNeedsNoPlan` short-circuits `simple_answer` before the planner is called, so that class no
/// longer arrives here — but audit, troubleshooting, system action, external action and research all
/// still do. A failed model call now hands a class-less plan to the classes that propose real
/// operations instead of to the one that answers questions.
///
/// So this is a source guard rather than four more behavioural cases: the property is about every
/// `return` in one method, and a test that exercised the paths it could reach would pass while the
/// sixth one, added later, walked out bare.
/// </summary>
public class PlanningPathCoverageTests
{
    private static string PlannerSource() =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Planning", "Planner.cs")));

    /// <summary>
    /// THE NAMED GUARD. Every `return` that produces a plan inside `CreateTasks` passes through
    /// `EnsureClassCoverage`, so a plan the class gate will grade is a plan the class gate could
    /// satisfy.
    ///
    /// Read from the member's own body by brace matching rather than by a character budget —
    /// `SourceText` carries two paragraphs on why, and this method is long enough that a budget
    /// would silently stop covering its own tail.
    /// </summary>
    [Fact]
    public void EveryReturnInPlan_GoesThroughClassCoverage()
    {
        var source = PlannerSource();
        var at = source.IndexOf("public List<Task> CreateTasks(", StringComparison.Ordinal);

        Assert.True(at >= 0,
            "Planner.cs no longer declares `CreateTasks(`, so this guard reads nothing.");

        var body = SourceText.MemberBody(source, at);

        // Non-vacuity: this method is the one with five exits, and a body that lost them is a body
        // this guard has stopped measuring.
        var returns = Regex.Matches(body, @"\breturn\s+\S").Count;
        Assert.True(returns >= 4,
            $"`CreateTasks` has {returns} returns; this guard was written for a method with five "
          + "and is probably reading the wrong member.");

        // The bare shape, named exactly: a return that hands back a constrained plan without ever
        // asking the class what it needs. Every legitimate exit wraps `EnforceConstraints` in
        // `EnsureClassCoverage`, so the pattern below matches only the defect.
        var bare = Regex.Matches(body, @"return\s+EnforceConstraints\s*\(").Count;

        Assert.True(bare == 0,
            "a return in `CreateTasks` produces a plan without `EnsureClassCoverage`. A recognized "
          + "class is "
          + "objectively verified, so a plan that skipped coverage reaches preflight unable to "
          + "satisfy its own gate — 'explain to me what science is' died that way in 13.7 seconds.");
    }

    /// <summary>
    /// AND THE ROUTING DECISION READS THE OPERATOR'S ASK. `mission.Goal` is composed — the request,
    /// the project's standing context, then the conversation below a `--- ` marker — and `.110`
    /// moved the EVALUATOR off it for exactly this reason without propagating the fix here.
    ///
    /// The loop it left open: a failed mission's own MISSION RECORD enters the next request's
    /// transcript, naming `builder`, `coder`, `file`, `patch_sets`, `tester`, `verifier` because that
    /// is what a mission record says — and those words trip the code lane. The operator's retry of
    /// "explain to me what science is" was planned as fourteen tasks with a coder, patch proposals,
    /// testers, soldiers and medics. `.96` paid for this shape once already.
    /// </summary>
    [Fact]
    public void TheFallbackPlan_RoutesOnTheOperatorsAsk_NotTheTranscript()
    {
        var before = AnthillRuntime.EnableWebSearch;
        try
        {
            AnthillRuntime.EnableWebSearch = false;

            var ask = "explain to me what science is";
            // A composed goal exactly like the one that ran: the question, then the previous
            // mission's record. Every code-lane word below is quoted FROM that record.
            var composed = ask
                + "\n\n--- conversation context (what the request above refers to) ---\n"
                + "=== MISSION RECORD ===\nstatus: failed\n"
                + "tasks (1): builder/builder.result_compiler — Answer the request\n"
                + "roles registered: builder, coder, file, tester, verifier\n"
                + "patch_sets: 0   artifacts: 1   evidence_rows: 0\n";

            var specification = MissionIntake.Resolve(ask);
            var planner = new Planner(useOllama: false, router: null);

            var tasks = planner.CreateTasks(composed, Anthill.Core.Common.MissionConstraints.None,
                specification: specification);

            Assert.DoesNotContain(tasks, t =>
                string.Equals(t.AssignedAnt, "coder", StringComparison.OrdinalIgnoreCase));
        }
        finally { AnthillRuntime.EnableWebSearch = before; }
    }
}
