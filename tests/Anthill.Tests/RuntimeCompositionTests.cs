using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.1.0 — ADR-001 (runtime composition) and ADR-002 (MissionContext).
///
/// These are not tests of new features; v3.1.0 adds none. They test the two properties the refactor
/// exists to create, both of which were previously untestable because there was nothing to test
/// them against:
///
///   1. Configuration read by a run is a SNAPSHOT. Flipping a static mid-run cannot change what the
///      run is permitted to do. This is what lets two runtimes coexist in one process.
///   2. A mission's governing facts are resolved ONCE, at intake. Constraints, deadline, and grants
///      are identical at intake and at finalization because they are the same object.
///
/// The characterization tests from v3.0.0 prove the refactor preserved behaviour. These prove it
/// achieved something.
/// </summary>
[Collection("specialist-gates")]
public class RuntimeCompositionTests
{
    // ---- ADR-001: options are an immutable snapshot ------------------------------------------------

    /// <summary>
    /// The defect this removes. Before v3.1.0 every consumer read the live static at the moment it
    /// happened to look, so a gate flipped mid-mission changed what an in-flight mission could do —
    /// and two consumers looking at different instants could disagree about the same question.
    /// </summary>
    [Fact]
    public void CapturedOptions_DoNotChangeWhenTheStaticIsMutatedAfterwards()
    {
        var original = AnthillRuntime.EnableHandoffIngestion;
        try
        {
            AnthillRuntime.EnableHandoffIngestion = false;
            var captured = RuntimeOptions.Capture();
            Assert.False(captured.HandoffIngestion);

            AnthillRuntime.EnableHandoffIngestion = true;

            // The static moved. The snapshot did not.
            Assert.True(AnthillRuntime.EnableHandoffIngestion);
            Assert.False(captured.HandoffIngestion);
        }
        finally { AnthillRuntime.EnableHandoffIngestion = original; }
    }

    /// <summary>
    /// ADR-001's exit gate, at the layer v3.1.0 delivers: two runs resolved at two different
    /// configuration instants coexist in one process, each holding its own answer. No save/restore
    /// dance, no ordering dependency between them.
    /// </summary>
    [Fact]
    public void TwoRunsResolvedAtDifferentInstants_DoNotLeakIntoEachOther()
    {
        var original = AnthillRuntime.EnableAdaptiveMissionControl;
        try
        {
            AnthillRuntime.EnableAdaptiveMissionControl = true;
            var runA = RuntimeProfile.Resolve(RuntimeOptions.Capture(), new[] { "read_file" });

            AnthillRuntime.EnableAdaptiveMissionControl = false;
            var runB = RuntimeProfile.Resolve(RuntimeOptions.Capture(), new[] { "read_file", "write_file" });

            Assert.True(runA.Options.AdaptiveMissionControl);
            Assert.False(runB.Options.AdaptiveMissionControl);
            Assert.True(runA.HasTool("read_file"));
            Assert.False(runA.HasTool("write_file"));
            Assert.True(runB.HasTool("write_file"));
        }
        finally { AnthillRuntime.EnableAdaptiveMissionControl = original; }
    }

    /// <summary>
    /// The profile reports the tools the run ACTUALLY registered, rather than re-deriving them from
    /// the capability gates. A gate that is on while its tool failed to register is a discrepancy
    /// the operator should be able to see, not one the profile should paper over.
    /// </summary>
    [Fact]
    public void ToolGrants_ComeFromTheRegistry_NotFromTheGates()
    {
        var profile = RuntimeProfile.Resolve(RuntimeOptions.Capture(), new[] { "system_info" });
        Assert.True(profile.HasTool("system_info"));
        Assert.False(profile.HasTool("shell_command"));
        Assert.Single(profile.ToolGrants);
    }

    /// <summary>
    /// The validator's contract is to degrade loudly, never to refuse boot. Resolving a profile
    /// under a bad combination must therefore SURFACE the finding rather than throw — an operator
    /// with a half-configured colony needs a running console that explains the problem.
    /// </summary>
    [Fact]
    public void ABreakGlassConfiguration_IsCarriedAsACriticalFinding_NotAnException()
    {
        var original = AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify;
        try
        {
            AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = true;
            var profile = RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>());

            Assert.True(profile.HasCriticalFinding);
            Assert.Contains(profile.Findings, f => f.Combination == "break_glass_keep_without_verify");
            // And the run knows it cannot claim qualifying verified success while it is on.
            Assert.False(profile.Verification.CanRecordVerifiedSuccess);
        }
        finally { AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify = original; }
    }

    [Fact]
    public void WritePermissions_ReportReadOnlyWhenNothingMayBeWritten()
    {
        var readOnly = new WritePermissions(Files: false, Patches: false, Shell: false, Root: ".");
        Assert.True(readOnly.IsReadOnly);
        Assert.False((readOnly with { Files = true }).IsReadOnly);
        Assert.False((readOnly with { Patches = true }).IsReadOnly);
        Assert.False((readOnly with { Shell = true }).IsReadOnly);
    }

    // ---- ADR-002: the mission context ---------------------------------------------------------------

    [Fact]
    public void TheContext_ResolvesConstraintsOnce_AndAgreesWithADirectParse()
    {
        const string goal = "verification only, do not modify files";
        var context = MissionContext.ForMission(new Mission { Goal = goal });

        Assert.Equal(MissionConstraints.Parse(goal), context.Constraints);
        Assert.True(context.Constraints.BlocksPatches);
        Assert.True(context.Constraints.VerificationOnly);
    }

    /// <summary>
    /// The deadline is an ABSOLUTE instant anchored to the mission's own start, not a duration
    /// measured from whenever a loop happens to look. Two loops comparing the same instant cannot
    /// disagree about when the mission expired, and a resumed run inherits the original boundary
    /// instead of restarting its clock.
    /// </summary>
    [Fact]
    public void TheDeadlineIsAbsolute_AndAnchoredToTheMissionStart()
    {
        var startedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var profile = RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>());
        var context = MissionContext.Create(new Mission { Goal = "g" }, profile, startedAt);

        Assert.Equal(startedAt.AddSeconds(profile.Options.MaxMissionSeconds), context.Deadline);
        Assert.Equal(startedAt, context.CreatedAt);

        Assert.False(context.IsPastDeadline(startedAt));
        Assert.True(context.IsPastDeadline(context.Deadline));                   // the boundary is inclusive
        Assert.True(context.IsPastDeadline(context.Deadline.AddSeconds(1)));

        // A resumed run reconstructing the same context gets the same boundary, not a fresh budget.
        var resumed = MissionContext.Create(new Mission { Goal = "g" }, profile, startedAt);
        Assert.Equal(context.Deadline, resumed.Deadline);
    }

    [Fact]
    public void RemainingTime_IsNeverNegative()
    {
        var startedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var context = MissionContext.Create(new Mission { Goal = "g" },
            RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>()), startedAt);

        Assert.True(context.Remaining(startedAt) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, context.Remaining(context.Deadline.AddHours(1)));
    }

    /// <summary>
    /// ADR-002 §3 rejected a mutable context: "just update the budget on the context" is how a bound
    /// stops bounding. The context holds ceilings; consumption lives in execution state. A record's
    /// <c>with</c> produces a NEW context rather than mutating the one a running mission holds.
    /// </summary>
    [Fact]
    public void TheContextIsImmutable_SoAMissionsBoundsCannotWidenMidFlight()
    {
        var context = MissionContext.ForMission(new Mission { Goal = "g" });
        var widened = context with { Deadline = context.Deadline.AddHours(1) };

        Assert.NotSame(context, widened);
        Assert.NotEqual(context.Deadline, widened.Deadline);
        Assert.True(widened.Deadline > context.Deadline);
    }

    [Fact]
    public void BudgetsCarryTheAdaptiveCeilings_SoTheyAreVisibleToTheOperator()
    {
        var context = MissionContext.ForMission(new Mission { Goal = "g" });
        Assert.Equal(AdaptiveBudget.MaxReplans, context.Budgets.MaxDeltaPlans);
        Assert.Equal(AdaptiveBudget.MaxRepairCycles, context.Budgets.MaxRepairCycles);
        Assert.Equal(context.Options.MaxMissionSeconds, context.Budgets.MaxElapsedSeconds);
        Assert.Equal(context.Options.MaxTaskSeconds, context.Budgets.MaxTaskSeconds);
    }

    /// <summary>The operator-facing projection must be secret-free and complete enough to explain a
    /// mission's boundaries without reading the database.</summary>
    [Fact]
    public void TheSnapshot_ExposesBoundsAndGrants_AndNoSecrets()
    {
        var snapshot = MissionContext.ForMission(new Mission { Goal = "read-only inspection" }).Snapshot();

        Assert.True(snapshot.ContainsKey("constraints"));
        Assert.True(snapshot.ContainsKey("budgets"));
        Assert.True(snapshot.ContainsKey("deadline"));
        Assert.True(snapshot.ContainsKey("profile"));

        var rendered = Json.Dumps(snapshot);
        Assert.DoesNotContain("token", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the guard ADR-002 asks for ----------------------------------------------------------------

    /// <summary>
    /// ADR-002 §4: "MissionConstraints.Parse appears exactly once on the mission path."
    ///
    /// v3.1.0 delivers that for the mission ENGINE — the Queen and the context type. The parse now
    /// happens in exactly one place, <c>MissionContext.Create</c>, and the Queen consumes
    /// <c>context.Constraints</c> at all four sites that previously re-parsed (planning admission,
    /// per-task runtime resolution, handoff admission, adaptive admission).
    ///
    /// Sites outside the engine still parse, and are listed in the phase-2 checklist below rather
    /// than left to be rediscovered. Each needs a context threaded to it, which means a signature
    /// change on a type the engine does not own:
    ///
    ///   - Planning/Planner.cs            — planner takes the goal, not the mission
    ///   - Agents/Ants.cs (CoderAnt)      — ant contract takes (Task, Mission)
    ///   - Outcomes/MissionEvaluation.cs  — evaluator takes (Mission, stopReason, patchCount)
    ///   - Outcomes/ObjectiveVerification.cs
    ///   - Autonomy/ObjectiveLifecycle.cs — objective charters, not missions
    ///   - Anthill.Api/ApiHost.cs         — plan preview
    ///
    /// This test fails if the engine regresses, and its list is the definition of done for the rest.
    /// </summary>
    [Fact]
    public void TheMissionEngine_ParsesConstraintsExactlyOnce()
    {
        var root = RepoRoot();

        var queen = CodeOnly(File.ReadAllText(
            Path.Combine(root, "src", "Anthill.Core", "Orchestration", "Queen.cs")));
        var queenViews = CodeOnly(File.ReadAllText(
            Path.Combine(root, "src", "Anthill.Core", "Orchestration", "Queen.Views.cs")));
        var context = CodeOnly(File.ReadAllText(
            Path.Combine(root, "src", "Anthill.Core", "Orchestration", "MissionContext.cs")));

        Assert.Equal(0, Occurrences(queen, "MissionConstraints.Parse"));
        Assert.Equal(0, Occurrences(queenViews, "MissionConstraints.Parse"));
        Assert.Equal(1, Occurrences(context, "MissionConstraints.Parse"));

        // And the Queen consumes the resolved value instead.
        Assert.Contains("context.Constraints", queen);
    }

    /// <summary>
    /// The mission engine must not read a mutable capability gate directly — that is the coupling
    /// ADR-001 exists to remove, and an extraction that leaves the static read behind has moved
    /// lines rather than coupling. Construction-time reads are exempt: the Queen still builds its
    /// ants and tool registry from the live runtime, which v3.1.0 does not change.
    /// </summary>
    [Fact]
    public void TheMissionExecutionPath_ReadsNoMutableFeatureGate()
    {
        var queen = CodeOnly(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        // Two spans: mission intake, and the execution engine. PlanPreview sits between them and is
        // deliberately excluded — it is a read-only preview that creates no mission and therefore
        // has no context to consume. It moves behind the composition root in a later phase.
        var intake = Between(queen, "public string RunMission(string goal, Action<string>? onMissionCreated",
                                    "public List<Task> PlanPreview");
        var engine = Between(queen, "private string? ExecuteTasksSequential", "private void RecordAgentMessage");

        foreach (var gate in new[]
                 {
                     "AnthillRuntime.EnableParallelExecution",
                     "AnthillRuntime.MaxParallelWorkers",
                     "AnthillRuntime.EnableHandoffIngestion",
                     "AnthillRuntime.EnableAdaptiveMissionControl",
                     "AnthillRuntime.EnableAutoDependencyWiring",
                     "AnthillRuntime.MissionDrainGraceSeconds",
                     "AnthillRuntime.EnvironmentFingerprint",
                 })
        {
            Assert.DoesNotContain(gate, intake);
            Assert.DoesNotContain(gate, engine);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static int Occurrences(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"anchor not found: {start}");
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b > a ? text[a..b] : text[a..];
    }

    private static string CodeOnly(string src) => string.Join("\n", src.Split('\n')
        .Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
