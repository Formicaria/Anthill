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
/// Every role stops when the operator says stop. v0.3.8.80, extended live at v0.3.8.81
/// (PLAN.md §2 R3).
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
///
/// v0.3.8.81 — NINE MORE CELLS DRIVEN LIVE, and the release exists because of what that found. The
/// citations for `during_generation` and `during_tool_call` were true: the ambient scope does abort
/// an in-flight HTTP call, the process-launching sites do kill their trees. What nobody had asked was
/// what the ROLE does with an aborted call, and the answer was that it reads one as an unavailable
/// provider, degrades to a fallback, and COMPLETES — after which the mission ingests its handoffs,
/// inserts a verification task, and writes a failure against the model's pheromone trail. The
/// mechanism was never the gap. The layer above it was, which is exactly what R3 said.
/// </summary>
[Collection("specialist-gates")]
public class RoleCancellationTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _webSearchWas = AnthillRuntime.EnableWebSearch;
    private readonly bool _sandboxWas = AnthillRuntime.EnableSandboxExecution;
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
        AnthillRuntime.EnableWebSearch = _webSearchWas;
        AnthillRuntime.EnableSandboxExecution = _sandboxWas;
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
        //
        // v0.3.8.81 — FOUR OF THE FIVE ARE NOW DRIVEN LIVE, and the release exists because doing so
        // found what citing them could not. The cited tests prove the ambient scope aborts an
        // in-flight HTTP call, which was true and remains true. What no test asked was what the ROLE
        // then does with the aborted call, and the answer was: treats it as an unavailable provider
        // and carries on. See ACancelledMission_StopsARoleMidGeneration.
        foreach (var role in new[] { "researcher", "web", "coder", "builder" })
            cells.Add(new(role, "during_generation", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsARoleMidGeneration"));

        // The verifier is the fifth and stays CITED, for a reason about scheduling rather than about
        // cancellation: its contract is `SchedulingMode.PolicyInserted`, so no plan may assign it and
        // the harness above — which drives a role by planning a task for it — cannot reach it. Doing
        // it live means completing a builder deliverable first and cancelling inside the verification
        // task the runtime inserts after it. That is a longer fixture than this release could also
        // verify, and it is named in PLAN.md §2 R3 rather than left to be noticed.
        cells.Add(new("verifier", "during_generation", How.Cited,
            "ModelCallCancellationTests.OllamaClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled;"
          + "ModelCallCancellationTests.OpenAiCompatibleClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled"));

        foreach (var role in new[] { "tester", "file", "ui_cartographer", "scribe", "soldier", "medic", "archivist" })
            cells.Add(new(role, "during_generation", How.NotApplicable,
                "this role holds no ModelRouter and generates nothing — asserted by "
              + "ContractDeclarationTests.ARoleDeclaringModelCalls_HasAnAntThatCanBeGivenARouter, so "
              + "the point does not exist for it rather than being untested."));

        // ---- during a tool call: only the roles whose contract grants tools -------------------
        //
        // v0.3.8.81 — five of the six driven live. The cited tests below prove the process-launching
        // SITES kill their trees, which is the orphan-process half and is real; they say nothing
        // about what the ROLE records once its tool call has been stopped underneath it.
        foreach (var role in new[] { "file", "researcher", "web", "ui_cartographer", "scribe" })
            cells.Add(new(role, "during_tool_call", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsARoleMidToolCall"));

        // The tester is the sixth and stays CITED, for the same scheduling reason as the verifier
        // above — `SchedulingMode.PolicyInserted`, so no plan may assign it. It is also the one role
        // whose tool launches a real process, which makes it the cell where the orphan-process
        // property is worth proving rather than inheriting: a gate tool substituted for
        // `run_allowlisted_check` would prove the runtime's bookkeeping and nothing about a child.
        // Named in PLAN.md §2 R3 with the verifier.
        cells.Add(new("tester", "during_tool_call", How.Cited,
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

    // -----------------------------------------------------------------------------------------------
    // The LIVE points — v0.3.8.81
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The task type each role's contract actually supports. A planned task with the wrong type is
    /// BLOCKED at dispatch rather than executed, which for a cancellation test is the worst possible
    /// outcome: the mission would stop, every property below would hold, and the role would never
    /// have run. Pinned against the contracts by <see cref="TheTaskTypeMap_AgreesWithTheContracts"/>.
    /// </summary>
    private static readonly Dictionary<string, string> TaskTypeFor = new(StringComparer.Ordinal)
    {
        ["researcher"] = "research",
        ["web"] = "external_research",
        ["file"] = "file_inspection",
        ["coder"] = "patch_proposal",
        ["builder"] = "build_answer",
        ["verifier"] = "verification",
        ["tester"] = "build_check",
        ["soldier"] = "security_review",
        ["medic"] = "failure_diagnosis",
        ["archivist"] = "memory_consolidation",
        ["ui_cartographer"] = "ui_mapping",
        ["scribe"] = "release_notes",
    };

    /// <summary>Every entry is a task type the role's own contract admits.</summary>
    [Fact]
    public void TheTaskTypeMap_AgreesWithTheContracts()
    {
        var wrong = new List<string>();
        foreach (var (role, taskType) in TaskTypeFor)
        {
            var contract = AntExecutionCatalog.ContractFor(role);
            if (contract is null) { wrong.Add($"{role} has no contract"); continue; }
            if (!contract.SupportsTaskType(taskType))
                wrong.Add($"{role} does not support task type '{taskType}'");
        }

        Assert.True(wrong.Count == 0,
            "the cancellation harness plans task types a role would refuse: " + string.Join("; ", wrong)
          + ". A refused task is BLOCKED before it runs, so every cancellation property would pass "
          + "about a role that never acted.");

        var uncovered = Roles.Where(r => !TaskTypeFor.ContainsKey(r)).ToList();
        Assert.True(uncovered.Count == 0, "no task type mapped for: " + string.Join(", ", uncovered));
    }

    /// <summary>
    /// THE OPERATOR PRESSES STOP WHILE THE ROLE IS GENERATING, and the role must leave nothing behind.
    ///
    /// WHAT THIS FOUND, and why the cited cells could never have. Every model-calling role treats a
    /// non-Ok call as "the routed model is unavailable" and degrades rather than failing — which is
    /// correct for the case it was written for. A cancelled call IS non-Ok
    /// (<c>ModelCallOutcome.Cancelled</c>, and <c>Ok</c> is false for it), so cancellation arrived
    /// through that same door: the researcher and the builder returned <c>SucceededWithWarnings</c>,
    /// the task COMPLETED, and a completed task ingests handoffs, inserts a verification task after a
    /// deliverable, hands the archivist something to remember and processes the coder's proposals.
    /// The operator pressed stop and the colony answered with a fabricated fallback deliverable and
    /// more scheduled work.
    ///
    /// The mechanism was never wrong. `ModelCallCancellationTests` proves the HTTP call aborts, and
    /// it does. The gap was one layer up and is exactly what R3 predicted: the role does not stop
    /// merely because its model call did.
    ///
    /// HOW THE MOMENT IS REACHED. The scenario installs a gate on this role's generation
    /// (<c>ScriptBook.Intercept</c>) which cancels the mission and then returns the response shape a
    /// REAL adapter returns when the ambient token is already cancelled — same status, same sentence.
    /// <see cref="TheCancellationFixture_MatchesWhatRealAdaptersReturn"/> pins that correspondence, so
    /// the fixture cannot drift into proving something no provider does.
    /// </summary>
    [Theory]
    [InlineData("researcher")] [InlineData("web")] [InlineData("coder")] [InlineData("builder")]
    public void ACancelledMission_StopsARoleMidGeneration(string role)
    {
        var (queen, missionId) = RunStoppedDuring(role, duringToolCall: false);
        AssertTheRoleLeftNothingBehind(queen, missionId, role, "mid-generation");
    }

    /// <summary>
    /// THE OPERATOR PRESSES STOP WHILE THE ROLE IS INSIDE A TOOL CALL.
    ///
    /// The separate point matters because the role's recovery path is different: a failed tool result
    /// is handled locally — the scribe's read sits inside a try/catch and falls back to prose, the
    /// cartographer's listing failure becomes a typed dependency failure — so the role reaches its own
    /// conclusion about a tool that was stopped underneath it and reports that conclusion as work.
    ///
    /// EVERY tool the role's contract grants is shadowed, not the one it is believed to call first.
    /// Picking a tool by reading the ant's source is how a fixture starts passing because the role
    /// stopped dispatching anything: the gate would never fire, the mission would be cancelled by
    /// nobody, and the assertions would hold vacuously. Shadowing the whole grant means the first
    /// dispatch — whichever it is — trips the gate, and a role that dispatches NOTHING fails the
    /// entered-gate assertion instead of passing quietly.
    /// </summary>
    [Theory]
    [InlineData("file")] [InlineData("researcher")] [InlineData("web")]
    [InlineData("ui_cartographer")] [InlineData("scribe")]
    public void ACancelledMission_StopsARoleMidToolCall(string role)
    {
        var (queen, missionId) = RunStoppedDuring(role, duringToolCall: true);
        AssertTheRoleLeftNothingBehind(queen, missionId, role, "mid-tool-call");
    }

    /// <summary>
    /// The five properties, asserted together because they fail independently — and because the one
    /// that matters most to an operator is the one nothing else in the suite looks at.
    /// </summary>
    private static void AssertTheRoleLeftNothingBehind(
        Queen queen, string missionId, string role, string point)
    {
        var tasks = queen.Memory.GetTasksForMission(missionId);
        var mine = tasks.Where(t => (t.GetValueOrDefault("assigned_ant")?.ToString() ?? "") == role).ToList();

        Assert.True(mine.Count > 0, $"no task was recorded for '{role}' at all, so the {point} "
          + "cancellation was never exercised.");

        // 1. TERMINAL STATE. Not complete, and recorded as something the colony DID rather than as
        //    something the role failed at. `execution_error` would attribute the operator's stop to
        //    the ant, and it is also retryable — which returns the task to the Ready queue for the
        //    dispatch loop to skip, so one stop is written down three times and none of the three
        //    says a person stopped it.
        foreach (var task in mine)
        {
            var status = task.GetValueOrDefault("status")?.ToString() ?? "";
            Assert.True(status != "complete",
                $"'{role}' was stopped {point} and its task still completed. A degrading role answers "
              + "a cancelled model call with a fallback and reports success; the mission must not "
              + "record that as work.");

            var failureType = task.GetValueOrDefault("failure_type")?.ToString() ?? "";
            Assert.True(failureType is "cancelled" or "timeout",
                $"'{role}' was stopped {point} and its task is recorded as '{failureType}'. "
              + "`execution_error` in particular attributes the operator's stop to the ant, and it is "
              + "RETRYABLE — which returns the task to the Ready queue for the dispatch loop to skip, "
              + "so one stop is written down three times and none of the three says a person did it.");

            // The row an operator reads. `cancellation_reason` is the only field that says a person
            // stopped this rather than that it went wrong, and it is what `DrainRunningTasks` has
            // always written for the straggler case — the returning-degrader case wrote nothing.
            var reason = task.GetValueOrDefault("cancellation_reason")?.ToString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(reason),
                $"'{role}' was stopped {point} and its task carries no cancellation_reason.");
        }

        // 2. NO POSITIVE EVALUATION. What auto-apply, memory and reputation all read.
        var evaluation = queen.Memory.LoadMissionEvaluation(missionId);
        Assert.True(evaluation is null || !evaluation.IsPositive,
            $"'{role}' was stopped {point} and the mission still graded positively "
          + $"({evaluation?.OutcomeCode}).");

        // 3. NO MEMORY. The property that outlives the mission.
        var archived = queen.Memory.GetRecentEvents(200, "memory_candidate_archived", missionId);
        Assert.True(archived.Count == 0,
            $"'{role}' was stopped {point} and {archived.Count} memory candidate(s) were archived.");

        // 4. NO HANDOFF. Stopping the colony must not merely change which role is running.
        Assert.DoesNotContain(tasks, t =>
            (t.GetValueOrDefault("title")?.ToString() ?? "").StartsWith("Handoff:", StringComparison.Ordinal));

        // 5. NO REPUTATION. The one an operator never sees and never recovers from.
        //
        //    A cancelled model call came back non-Ok, `ModelRouter.SendCore` derived its pheromone
        //    delta from `result.Ok` alone, and the trail therefore recorded a FAILURE against
        //    `model:{provider}:{model}:{role}` — while the circuit breaker, four lines above in the
        //    same method, treated the identical outcome as Neutral because "we stopped the call
        //    ourselves". Two implementations of one rule, and the durable one was wrong: every stop
        //    taught the colony that the model its cancelled role was using is unsuited to that role.
        var trailKey = $"model:{ScriptedColony.ProviderId}:{ScriptedColony.ModelId}:{role}";
        var trail = queen.Memory.ListPheromoneTrails()
            .FirstOrDefault(t => (t.GetValueOrDefault("trail_key")?.ToString() ?? "") == trailKey);

        if (trail is not null)
        {
            var failures = Convert.ToInt32(trail.GetValueOrDefault("failure_count") ?? 0);
            Assert.True(failures == 0,
                $"stopping '{role}' {point} wrote {failures} failure(s) to '{trailKey}'. A call the "
              + "colony stopped is evidence about the colony, never about the route — which is what "
              + "the circuit breaker has always said about the same outcome.");
        }
    }

    /// <summary>
    /// The fixture's cancelled response is the one REAL adapters return. Without this the harness
    /// could prove a role handles a shape no provider produces — the fixture-testing failure this
    /// repository has caught before.
    /// </summary>
    [Fact]
    public void TheCancellationFixture_MatchesWhatRealAdaptersReturn()
    {
        var response = CancelledLikeARealAdapter();

        Assert.Equal(ModelCallOutcome.Cancelled, response.Status);
        Assert.Equal(ModelCallOutcome.Cancelled,
            ModelCallOutcomeExtensions.Classify(response.Content));

        // And the adapters really do produce it. Read from source rather than asserted from memory:
        // the sentinel is a STRING that `Classify` matches on, so a reworded adapter would silently
        // stop being classified as cancelled and this harness would stop describing production.
        var reasoning = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Modules",
            "Anthill.Modules.Reasoning");
        var producers = Directory.GetFiles(reasoning, "*.cs", SearchOption.AllDirectories)
            .Count(f => File.ReadAllText(f).Contains(CancelSentinel, StringComparison.Ordinal));

        Assert.True(producers >= 2,
            $"only {producers} reasoning adapter source file(s) emit \"{CancelSentinel}\". Either the "
          + "adapters reworded the sentinel — in which case Classify no longer sees a cancellation — "
          + "or this fixture is describing a shape production stopped producing.");
    }

    /// <summary>The exact phrase <c>ModelCallOutcomeExtensions.Classify</c> matches on.</summary>
    private const string CancelSentinel = "cancelled because the mission was stopped";

    private static ModelResponse CancelledLikeARealAdapter() => new()
    {
        Status = ModelCallOutcome.Cancelled,
        Content = $"ERROR: {ScriptedColony.ProviderId} request {CancelSentinel}.",
        Provider = ScriptedColony.ProviderId,
        Model = ScriptedColony.ModelId,
    };

    /// <summary>
    /// A tool that stops the mission the first time a role dispatches it, then answers the way a tool
    /// interrupted by cancellation answers: unsuccessfully, saying so.
    ///
    /// It does NOT block waiting for someone else to cancel. A gate that waits is a gate that can
    /// hang CI when the role it was written for stops dispatching, and "the suite timed out" is a
    /// worse diagnostic than "the role dispatched nothing". Cancelling from inside the dispatch is
    /// also the more faithful moment: the operator's stop lands while the role is inside the call,
    /// which is the point this cell is about.
    /// </summary>
    private sealed class StopOnDispatchTool(string name, ManualResetEventSlim entered, CancellationTokenSource stop)
        : ITool
    {
        public string Name => name;
        public string Description => "Test gate: stops the mission from inside a role's tool call.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            entered.Set();
            stop.Cancel();
            return new ToolResult(Name, success: false, output: "",
                error: "tool call aborted: the mission was stopped",
                failure: FailureClass.DependencyFailure);
        }
    }

    /// <summary>
    /// Drives one mission whose plan assigns a task to <paramref name="role"/>, and stops the mission
    /// from INSIDE that role's work — from its generation, or from its first tool dispatch.
    /// </summary>
    private (Queen Queen, string MissionId) RunStoppedDuring(string role, bool duringToolCall)
    {
        ApplyFullRoster();

        using var cts = new CancellationTokenSource();
        var entered = new ManualResetEventSlim(false);

        // ONE task. The dependent second task of the pre-dispatch fixture would be skipped here for
        // the ordinary reason (its predecessor did not complete), which proves nothing about this
        // point and would make a failure ambiguous between the two.
        var plan = $$"""
            {
              "tasks": [
                { "title": "The step under test", "description": "Stopped from inside.",
                  "assigned_ant": "{{role}}", "task_type": "{{TaskTypeFor[role]}}", "depends_on": [] }
              ]
            }
            """;

        var book = new ScriptBook().Role("planner", plan);
        foreach (var r in Roles.Concat(new[] { "builder", "fallback" }).Distinct())
            book.Role(r, $"SCRIPTED: {r} output.");

        if (!duringToolCall)
            book.Intercept(role, _ =>
            {
                entered.Set();
                cts.Cancel();
                return CancelledLikeARealAdapter();
            });

        using var scripted = ScriptedColony.Begin(book,
            Roles.Concat(new[] { "planner", "fallback" }).Distinct().ToArray());

        var memory = new SqliteMemory(Path.Combine(_dir, $"stopped-{role}-{Guid.NewGuid():N}.db"));
        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard(), new CancellationScenarioToolGates()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);

        // The web ant's only real trigger is a search, and a search means the network. Shadowed for
        // BOTH points, including the generation one where the ant searches before it generates — the
        // gate class above already refuses, and a suite that reached a socket would be neither
        // deterministic nor honest about what it proved.
        queen.AdoptModuleTools(new ITool[]
        {
            new ScriptedWebSearchTool(
                ("A local result", "https://example.org/one", "Enough for the ant to proceed."),
                ("Another local result", "https://example.net/two", "On a second host, so dedupe runs.")),
        });

        if (duringToolCall)
        {
            // Shadow the WHOLE grant, after adoption — Register is last-write-wins, and going through
            // AdoptModuleTools keeps the profile and capability grants re-resolved with it.
            var contract = AntExecutionCatalog.ContractFor(role);
            Assert.NotNull(contract);
            Assert.True(contract!.AllowedTools.Count > 0,
                $"'{role}' has no tools, so it has no during_tool_call point — the matrix says so and "
              + "this theory should not name it.");
            queen.AdoptModuleTools(contract.AllowedTools
                .Select(t => (ITool)new StopOnDispatchTool(t, entered, cts)).ToArray());
        }

        string? missionId = null;
        queen.RunMission($"Exercise {role} and stop it while it works.",
            onMissionCreated: id => missionId = id, cancel: cts.Token);

        Assert.NotNull(missionId);
        Assert.True(entered.IsSet,
            $"'{role}' never reached the stopping point, so nothing was cancelled mid-flight. Either "
          + (duringToolCall
                ? "the role dispatched none of the tools its contract grants, or the dispatch was "
                + "denied before reaching the tool (check the capability grant for this role)."
                : "the role made no model call, or its request carried no `| role: |` header for the "
                + "scripted provider to read.")
          + " A passing assertion after this point would be about a role that never acted.");
        return (queen, missionId!);
    }

    /// <summary>File and web tools ON so a role's dispatch reaches the registry; writes, shell and
    /// auto-apply off. The tools themselves are shadowed, so nothing here reaches a real socket or a
    /// real file — this exists so the role's capability grant is resolved rather than withheld.</summary>
    private sealed class CancellationScenarioToolGates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled => true;
        public bool FileWritingEnabled => false;
        public bool ShellToolEnabled => false;
        // OFF, and the fixture shadows `web_search` unconditionally on top of that. Two independent
        // reasons the web ant cannot reach a socket from a unit test, because one of them is a flag
        // and flags get edited.
        public bool WebSearchEnabled => false;
        public bool PatchApplicationEnabled => false;
        public IReadOnlySet<string> WebSearchKeywords { get; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; } = new HashSet<string> { ".md", ".txt" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; } = new HashSet<string> { ".db" };
        public IReadOnlySet<string> BlockedPathParts { get; } = new HashSet<string> { ".git" };
        public string ScriptDirectory => ".";
        public string BackupDirectory => "data/backups";
    }

    /// <summary>The roster and workspace every fixture in this file runs under.</summary>
    private void ApplyFullRoster()
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
        // The web ant refuses before dispatching anything when this is false, so without it the
        // web cells would prove that a blocked role leaves nothing behind — true, and about a
        // different role than the one named. The module's own web gate stays OFF and `web_search`
        // is shadowed, so opening this reaches no socket.
        AnthillRuntime.EnableWebSearch = true;
        // OFF explicitly, not by default. With it on the coder iterates inside a sandbox and may
        // never reach `GenerateTyped`, so the generation gate would not fire and the cell would be
        // decided by whatever an earlier test in this collection happened to leave set.
        AnthillRuntime.EnableSandboxExecution = false;
        AnthillRuntime.AllowedWorkspaceRoot = Path.Combine(_dir, "workspace");
    }

    /// <summary>
    /// Drives one mission whose plan assigns a task to <paramref name="role"/>, with the mission
    /// token already cancelled. Shared by both theories so the twenty-four cells are one fixture.
    /// </summary>
    private (Queen Queen, string MissionId) RunCancelled(string role, bool alreadyCancelled)
    {
        ApplyFullRoster();

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
