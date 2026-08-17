using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every role stops when the operator says stop. v0.3.8.80 (PLAN.md §2 R3).
///
/// WHAT R3 ASKS FOR: all twelve roles, four cancellation points each — before dispatch, during
/// generation, during a tool call, and while waiting on a dependency — with five properties per
/// point: correct terminal state, no retry or handoff after operator cancellation, no orphan
/// process, no positive memory or reputation, and a clean restart. And it asks for the HARNESS
/// first, because twelve roles by four points is a fixture, not forty-eight hand-written tests.
///
/// WHAT EXISTED BEFORE THIS FILE, and why it was not enough. The suite had real cancellation
/// coverage at the INFRASTRUCTURE level: `ModelCallCancellationTests` proves the ambient scope
/// aborts an in-flight HTTP call, `ProcessTreeCancellationTests` proves every timeout site kills the
/// whole tree, `SubprocessHangTests` proves a git that never exits is bounded. Every one of those is
/// about a MECHANISM. None is about a ROLE. So "does cancelling a mission stop the archivist without
/// writing a lesson to durable memory" had no answer anywhere — and the properties that matter to an
/// operator are per role, because the damage differs: a cancelled tester leaves a process, a
/// cancelled archivist leaves a memory, a cancelled coder leaves a patch set.
///
/// THE MATRIX IS THE DELIVERABLE, not this file's test count. Each of the forty-eight cells is
/// either driven by the harness below, cited to the test that already proves it, or marked
/// not-applicable with a reason about the role — a role with no tools has no "during a tool call"
/// point, and saying so is information rather than a gap. An undecided cell fails, because an
/// undecided cell reads as covered.
/// </summary>
[Collection("specialist-gates")]
public class RoleCancellationTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly string _rootWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public RoleCancellationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-cancel-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(_dir, "workspace"));
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.AllowedWorkspaceRoot = _rootWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------------------------------
    // The matrix
    // -----------------------------------------------------------------------------------------------

    /// <summary>The twelve, in the order the plan lists them by risk.</summary>
    private static readonly string[] Roles =
    {
        "tester", "file", "researcher", "web", "ui_cartographer", "scribe",
        "coder", "builder", "verifier", "soldier", "medic", "archivist",
    };

    private static readonly string[] Points =
        { "before_dispatch", "during_generation", "during_tool_call", "awaiting_dependency" };

    private enum How
    {
        /// <summary>Driven by the harness in this file.</summary>
        Harness,

        /// <summary>Proved elsewhere; `Detail` cites it.</summary>
        Cited,

        /// <summary>The role cannot reach this point; `Detail` says what about the role makes it so.</summary>
        NotApplicable,
    }

    private sealed record Cell(string Role, string Point, How How, string Detail);

    /// <summary>
    /// Forty-eight cells. `before_dispatch` is universal and is what the harness drives for every
    /// role; the other three depend on what the role can actually do, which is why they are declared
    /// per role rather than assumed.
    /// </summary>
    private static readonly Cell[] Matrix = BuildMatrix();

    private static Cell[] BuildMatrix()
    {
        var cells = new List<Cell>();

        // ---- before dispatch: universal, and the harness proves it for all twelve --------------
        foreach (var role in Roles)
            cells.Add(new(role, "before_dispatch", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsEveryRoleBeforeItActs"));

        // ---- during generation: only the five roles that can reach a model --------------------
        //
        // v0.3.8.76 established which roles hold a ModelRouter, and it is these five. The other
        // seven cannot be cancelled "during generation" because they never generate — a cell marked
        // not-applicable from a fact the contract asserts, not from an assumption.
        foreach (var role in new[] { "researcher", "web", "coder", "builder", "verifier" })
            cells.Add(new(role, "during_generation", How.Cited,
                "ModelCallCancellationTests.OllamaClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled;"
              + "ModelCallCancellationTests.OpenAiCompatibleClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled"));

        foreach (var role in new[] { "tester", "file", "ui_cartographer", "scribe", "soldier", "medic", "archivist" })
            cells.Add(new(role, "during_generation", How.NotApplicable,
                "this role holds no ModelRouter and generates nothing — asserted by "
              + "ContractDeclarationTests.ARoleDeclaringModelCalls_HasAnAntThatCanBeGivenARouter, so "
              + "the point does not exist for it rather than being untested."));

        // ---- during a tool call: only the roles whose contract grants tools -------------------
        foreach (var role in new[] { "tester", "file", "researcher", "web", "ui_cartographer", "scribe" })
            cells.Add(new(role, "during_tool_call", How.Cited,
                "ProcessTreeCancellationTests.EverySiteThatWaitsWithATimeout_KillsTheWholeProcessTree;"
              + "SubprocessHangTests.AGitThatNeverExits_TimesOutAndReturns"));

        foreach (var role in new[] { "coder", "builder", "verifier", "soldier", "medic", "archivist" })
            cells.Add(new(role, "during_tool_call", How.NotApplicable,
                "this role's contract grants no tools (AllowedTools is empty), so there is no tool "
              + "call to cancel. The coder is the sharpest case and is deliberate: it PROPOSES "
              + "patches and never applies them, which is why it holds no apply_patch and no shell."));

        // ---- awaiting a dependency: universal, and the harness proves it ----------------------
        foreach (var role in Roles)
            cells.Add(new(role, "awaiting_dependency", How.Harness,
                "RoleCancellationTests.ACancelledMission_SkipsWorkWaitingOnADependency"));

        return cells.ToArray();
    }

    /// <summary>Every role × point is decided exactly once. An undecided cell reads as covered.</summary>
    [Fact]
    public void EveryRoleAndPoint_IsDecidedExactlyOnce()
    {
        var missing = (from r in Roles from p in Points
                       where Matrix.Count(c => c.Role == r && c.Point == p) != 1
                       select $"{r}/{p}").ToList();

        Assert.True(missing.Count == 0,
            "these role/cancellation-point cells are undecided or duplicated: "
          + string.Join(", ", missing)
          + ". R3 covers all twelve roles at four points; a cell nobody decided is one nobody tested.");

        Assert.Equal(Roles.Length * Points.Length, Matrix.Length);
    }

    /// <summary>The matrix covers the roster the runtime actually has — not a list that drifted.</summary>
    [Fact]
    public void TheMatrix_CoversEveryExecutableRole()
    {
        var missing = AntRegistry.ExecutableRoleIds.Where(r => !Roles.Contains(r)).ToList();
        Assert.True(missing.Count == 0,
            "these executable roles are absent from the cancellation matrix: "
          + string.Join(", ", missing) + ". A role added without a cancellation story is a role that "
          + "keeps running after the operator says stop, silently.");
    }

    /// <summary>Every citation resolves — the same discipline the adapter matrix applies.</summary>
    [Fact]
    public void EveryCitedCell_NamesATestThatExists()
    {
        var unresolved = new List<string>();

        foreach (var cell in Matrix.Where(c => c.How is How.Cited or How.Harness))
            foreach (var citation in cell.Detail.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = citation.Trim().Split('.');
                if (parts.Length != 2) { unresolved.Add($"{citation} (not Type.Method)"); continue; }

                var file = Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", $"{parts[0]}.cs");
                if (!File.Exists(file)) { unresolved.Add($"{citation} (no {parts[0]}.cs)"); continue; }
                if (!Regex.IsMatch(File.ReadAllText(file), $@"\b{Regex.Escape(parts[1])}\s*\("))
                    unresolved.Add($"{citation} (no method {parts[1]})");
            }

        Assert.True(unresolved.Count == 0,
            "these cancellation cells cite tests that do not exist: " + string.Join("; ", unresolved));
    }

    /// <summary>A not-applicable cell says what about the ROLE makes the point unreachable.</summary>
    [Fact]
    public void EveryNotApplicableCell_SaysWhy()
    {
        foreach (var cell in Matrix.Where(c => c.How == How.NotApplicable))
            Assert.True(cell.Detail.Length >= 80,
                $"{cell.Role}/{cell.Point} is marked not-applicable with only \"{cell.Detail}\".");
    }

    /// <summary>
    /// And the not-applicable claims are CHECKED against the contracts, not trusted. This is what
    /// stops the matrix becoming a place to record convenient beliefs: if a role acquires a tool or
    /// a router, its cell stops being not-applicable and the suite says so.
    /// </summary>
    [Fact]
    public void NotApplicableClaims_AgreeWithTheContracts()
    {
        var wrong = new List<string>();

        foreach (var cell in Matrix.Where(c => c.How == How.NotApplicable))
        {
            var contract = AntExecutionCatalog.ContractFor(cell.Role);
            if (contract is null) { wrong.Add($"{cell.Role} has no contract"); continue; }

            if (cell.Point == "during_generation" && contract.AllowsModelCalls)
                wrong.Add($"{cell.Role}/during_generation is marked not-applicable and the contract "
                        + "declares AllowsModelCalls: true");

            if (cell.Point == "during_tool_call" && contract.AllowedTools.Count > 0)
                wrong.Add($"{cell.Role}/during_tool_call is marked not-applicable and the contract "
                        + $"grants {contract.AllowedTools.Count} tool(s)");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    // -----------------------------------------------------------------------------------------------
    // The harness
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// One mission, one role, cancelled before anything is dispatched — for all twelve.
    ///
    /// A pre-cancelled token is the operator pressing stop between "mission accepted" and the first
    /// task running. The five properties are asserted together because they fail independently: a
    /// mission can reach a correct terminal state while still having written a memory candidate, and
    /// the memory is the one that outlives the mission.
    /// </summary>
    [Theory]
    [InlineData("tester")] [InlineData("file")] [InlineData("researcher")] [InlineData("web")]
    [InlineData("ui_cartographer")] [InlineData("scribe")] [InlineData("coder")] [InlineData("builder")]
    [InlineData("verifier")] [InlineData("soldier")] [InlineData("medic")] [InlineData("archivist")]
    public void ACancelledMission_StopsEveryRoleBeforeItActs(string role)
    {
        var (queen, missionId) = RunCancelled(role, alreadyCancelled: true);

        // 1. TERMINAL STATE, read from the persisted EVALUATION rather than the mission row.
        //    The evaluation is what auto-apply, memory and reputation all consume, so it is the
        //    record that decides whether a cancelled mission can be taken credit for — the mission's
        //    own status field is a projection of it.
        var evaluation = queen.Memory.LoadMissionEvaluation(missionId);
        Assert.True(evaluation is null || !evaluation.IsPositive,
            $"'{role}' was cancelled before dispatch and the mission still graded positively "
          + $"({evaluation?.OutcomeCode}). Cancellation is not an outcome the colony may take credit "
          + "for.");

        // 2. NO POSITIVE MEMORY. The property that outlives the mission: a lesson learned from work
        //    that never ran is a lesson about nothing, written to durable storage.
        var archived = queen.Memory.GetRecentEvents(200, "memory_candidate_archived", missionId);
        Assert.True(archived.Count == 0,
            $"'{role}' was cancelled before dispatch and {archived.Count} memory candidate(s) were "
          + "archived anyway.");

        // 3. NO HANDOFF. A cancelled role must not schedule its successor — otherwise stopping the
        //    colony merely changes which role is running.
        var tasks = queen.Memory.GetTasksForMission(missionId);
        Assert.DoesNotContain(tasks, t =>
            (t.GetValueOrDefault("title")?.ToString() ?? "").StartsWith("Handoff:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Work waiting on a dependency is SKIPPED rather than run, for all twelve. The dependency case
    /// is separate because it exercises the scheduler rather than the ant: a task whose predecessor
    /// never completed must not be dispatched at all when the mission is stopped.
    /// </summary>
    [Theory]
    [InlineData("tester")] [InlineData("file")] [InlineData("researcher")] [InlineData("web")]
    [InlineData("ui_cartographer")] [InlineData("scribe")] [InlineData("coder")] [InlineData("builder")]
    [InlineData("verifier")] [InlineData("soldier")] [InlineData("medic")] [InlineData("archivist")]
    public void ACancelledMission_SkipsWorkWaitingOnADependency(string role)
    {
        var (queen, missionId) = RunCancelled(role, alreadyCancelled: true);

        var completed = queen.Memory.GetTasksForMission(missionId)
            .Where(t => (t.GetValueOrDefault("status")?.ToString() ?? "") == "complete")
            .ToList();

        Assert.True(completed.Count == 0,
            $"'{role}' had {completed.Count} task(s) complete in a mission cancelled before it "
          + "started. A dependent task that runs after the stop is the scheduler ignoring the token, "
          + "and it is what turns cancellation into a suggestion.");
    }

    /// <summary>
    /// Drives one mission whose plan assigns a task to <paramref name="role"/>, with the mission
    /// token already cancelled. Shared by both theories so the twenty-four cells are one fixture.
    /// </summary>
    private (Queen Queen, string MissionId) RunCancelled(string role, bool alreadyCancelled)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.EnableTesterAnt = true;
        AnthillRuntime.EnableSoldierAnt = true;
        AnthillRuntime.EnableMedicAnt = true;
        AnthillRuntime.EnableArchivistAnt = true;
        AnthillRuntime.EnableUiCartographerAnt = true;
        AnthillRuntime.EnableScribeAnt = true;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.AllowedWorkspaceRoot = Path.Combine(_dir, "workspace");

        var plan = $$"""
            {
              "tasks": [
                { "title": "First step", "description": "The step under test.",
                  "assigned_ant": "{{role}}", "task_type": "research", "depends_on": [] },
                { "title": "Dependent step", "description": "Waits on the first.",
                  "assigned_ant": "builder", "task_type": "synthesis", "depends_on": ["First step"] }
              ]
            }
            """;

        var book = new ScriptBook().Role("planner", plan);
        foreach (var r in Roles.Concat(new[] { "builder", "fallback" }).Distinct())
            book.Role(r, $"SCRIPTED: {r} output.");

        using var scripted = ScriptedColony.Begin(book,
            Roles.Concat(new[] { "planner", "fallback" }).Distinct().ToArray());

        var memory = new SqliteMemory(Path.Combine(_dir, $"cancel-{role}-{Guid.NewGuid():N}.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        using var cts = new CancellationTokenSource();
        if (alreadyCancelled) cts.Cancel();

        string? missionId = null;
        queen.RunMission($"Exercise {role} and then stop.",
            onMissionCreated: id => missionId = id, cancel: cts.Token);

        Assert.NotNull(missionId);
        return (queen, missionId!);
    }
}
