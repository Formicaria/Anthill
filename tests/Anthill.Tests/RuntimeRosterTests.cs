using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.21.0 Phase B3: the planner plans against the roster it can actually run.
///
/// The prompt used to list six roles as a literal string, which was wrong in both directions: a
/// disabled role was still offered (producing tasks that ValidateTask then dropped, with the only
/// trace a line on stderr), and a specialist whose rollout gates were OPEN could never be planned
/// with, because the prompt never mentioned it. Enabling the tester bought nothing.
/// </summary>
[Collection("specialist-gates")]
public class RuntimeRosterTests
{
    /// <summary>
    /// Run <paramref name="body"/> with EXACTLY the named specialists open, and the ambient gates
    /// put back afterwards.
    ///
    /// v0.3.8.60 — this now DELEGATES to <see cref="RosterGates"/> rather than hand-rolling the
    /// save/restore, because that helper has existed since v0.3.8.41 for precisely this and its own
    /// doc names the defect: "the older helpers restored to false, which was indistinguishable from
    /// correct while false was also the default and is now a way for one test to silently disable
    /// the roster for every test that runs after it."
    ///
    /// This was one of the older helpers. It did not baseline, so a block built "with tester and
    /// medic" also contained an ambiently-enabled cartographer; and it restored to false, so calling
    /// it twice gave two different answers — which `TheRosterIsDeterministic` does, and compares.
    /// The test that exists to prove the roster is deterministic was made non-deterministic by its
    /// own fixture.
    ///
    /// RosterGates also pins the ACTIVATION TIER, which this never did. The tier is a ceiling over
    /// the flags, so "gate open" with an ambient tier of Core still leaves a role shut — one more
    /// ambient value this helper was silently inheriting.
    /// </summary>
    private static T WithGates<T>(Func<T> body, params string[] specialists) =>
        RosterGates.With(body,
            specialists: specialists.Length > 0,
            tier: ActivationTier.Full,
            tester: specialists.Contains("tester"),
            medic: specialists.Contains("medic"),
            uiCartographer: specialists.Contains("ui_cartographer"),
            soldier: false, archivist: false, scribe: false);

    [Fact]
    public void TheCoreAntsAreAlwaysPlannable()
    {
        var ids = RuntimeRoster.PlannableRoles().Select(r => r.RoleId).ToList();
        foreach (var core in new[] { "researcher", "web", "file", "coder", "builder", "verifier" })
            Assert.Contains(core, ids);
    }

    /// <summary>
    /// The gap this phase closes: with its gates open, a specialist becomes plannable. Before, the
    /// hardcoded prompt meant enabling one changed nothing about what could be planned.
    /// </summary>
    [Fact]
    public void AGatedSpecialist_BecomesPlannableOnlyWhenItsGatesOpen()
    {
        // Each assertion NAMES the gate state it depends on. Reading the ambient value made these
        // pass only while the neighbouring classes happened to leave the gates closed.
        Assert.False(WithGates(() => RuntimeRoster.CanPlanFor("tester")));
        Assert.True(WithGates(() => RuntimeRoster.CanPlanFor("tester"), "tester"));
        Assert.False(WithGates(() => RuntimeRoster.CanPlanFor("tester")));   // closed again after
    }

    [Fact]
    public void AGatedSpecialist_AppearsInThePromptBlockWhenOpen()
    {
        Assert.DoesNotContain("- tester:", WithGates(() => RuntimeRoster.PromptBlock()));
        Assert.Contains("- tester:", WithGates(() => RuntimeRoster.PromptBlock(), "tester"));
    }

    /// <summary>
    /// Control-plane roles are excluded structurally. A planner able to plan a task for the planner
    /// is a loop generator, and the doctrine keeps control-plane roles non-executable regardless.
    /// </summary>
    [Fact]
    public void ControlPlaneRoles_AreNeverPlannable()
    {
        foreach (var role in new[] { "queen", "director", "planner", "constraint" })
            Assert.False(RuntimeRoster.CanPlanFor(role), $"{role} must never be plannable");
    }

    [Fact]
    public void NothingDisabledOrNonExecutable_IsOffered()
    {
        var executable = AntRegistry.ExecutableRoleIds;
        Assert.All(RuntimeRoster.PlannableRoles(), r =>
        {
            Assert.True(r.Enabled, $"{r.RoleId} is disabled but was offered");
            Assert.Contains(r.RoleId, executable);
        });
    }

    /// <summary>Same gates in, same roster out — routing decisions have to be reproducible.</summary>
    [Fact]
    public void TheRosterIsDeterministic()
    {
        Assert.Equal(WithGates(() => RuntimeRoster.PromptBlock()),
                     WithGates(() => RuntimeRoster.PromptBlock()));
        Assert.Equal(
            WithGates(() => RuntimeRoster.PlannableRoles().Select(r => r.RoleId).ToList()),
            WithGates(() => RuntimeRoster.PlannableRoles().Select(r => r.RoleId).ToList()));

        var open = WithGates(() => RuntimeRoster.PromptBlock(), "tester", "medic");
        var openAgain = WithGates(() => RuntimeRoster.PromptBlock(), "tester", "medic");
        Assert.Equal(open, openAgain);
    }

    /// <summary>
    /// Operational emphasis the registry's one-line Purpose cannot carry must survive — losing
    /// "the ONLY ant that changes files" would quietly degrade every plan involving a file change.
    /// </summary>
    [Fact]
    public void HandWrittenEmphasisSurvives_AndUnlistedRolesFallBackToTheirPurpose()
    {
        var block = RuntimeRoster.PromptBlock(Planner.PlannerEmphasis);
        Assert.Contains("ONLY ant that changes files", block);

        // A specialist has no emphasis entry, so it is described by its registry purpose.
        var withTester = WithGates(() => RuntimeRoster.PromptBlock(Planner.PlannerEmphasis), "tester");
        var testerPurpose = AntRegistry.ByRole["tester"].Purpose;
        Assert.Contains(testerPurpose, withTester);
    }

    /// <summary>
    /// The planner prompt must be built from the roster, not from a literal list — otherwise the
    /// two drift apart again and nothing catches it.
    /// </summary>
    [Fact]
    public void ThePlannerPrompt_IsBuiltFromTheRoster()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Planning", "Planner.cs"));

        // "Available ants:" is followed by the interpolation, not by a literal list. Checked as
        // adjacent non-blank lines so a line-ending difference cannot make this pass vacuously.
        var lines = source.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var header = lines.FindIndex(l => l.Trim() == "Available ants:");
        Assert.True(header >= 0, "the planner prompt no longer has an 'Available ants:' header");
        Assert.Equal("{RuntimeRoster.PromptBlock(PlannerEmphasis)}", lines[header + 1].Trim());

        // And the roles are declared once, in the emphasis table — not duplicated in the prompt.
        // Comment lines are stripped first: the doc comment explaining WHY the table exists quotes
        // the phrase, and a comment quoting a string is not a second declaration of it. (Same trap
        // as the Compat guard in TesterAntStructuredTests, which matched its own explanation.)
        var codeLines = lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        Assert.Equal(1, codeLines.Count(l => l.Contains("ONLY ant that changes files", StringComparison.Ordinal)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
