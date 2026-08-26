using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;       // Planner.IsConsequential, for deriving the expected verifier
using Anthill.Core.Security;
using Anthill.Core.Tools;          // CheckDefinition, for pinning the tester's check selection
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
/// v0.3.8.81 — SIX MORE CELLS DRIVEN LIVE, and the release exists because of what that found. The
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
    private readonly IReadOnlyList<CheckDefinition> _checksWere = AnthillRuntime.WorkspaceChecks;
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
        AnthillRuntime.WorkspaceChecks = _checksWere;
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

        // ---- before dispatch ------------------------------------------------------------------
        //
        // TEN, not twelve — v0.3.8.83, and it is the correction v0.3.8.82's own documents already
        // described. That release recorded the medic and the archivist as not-applicable here and
        // shipped a matrix that still drove all twelve: the edit was lost and nothing compared the
        // count in PLAN.md against the code that produces it. A declaration disagreeing with the
        // runtime, in the release whose subject was declarations disagreeing with runtimes.
        //
        // The fact itself is unchanged. This fixture drives a role by PLANNING a task for it, and
        // `AntRegistry.ValidateTask` refuses a planner-produced task for a `FailureTriggered` or
        // `PostFinalization` role — the medic diagnoses a failure that must already exist, the
        // archivist summarises a mission that must already be terminal.
        foreach (var role in Roles.Where(PlannerAssignable))
            cells.Add(new(role, "before_dispatch", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsEveryRoleBeforeItActs"));

        // v0.3.8.85 — THE ARCHIVIST'S before_dispatch POINT EXISTS AT A DIFFERENT DISPATCH SITE, and
        // once you look there it is drivable. `Queen.RunArchivistAfterFinalization` invokes the
        // handler directly after the canonical evaluation is persisted — no plan, no scheduler, no
        // task row — so "cancel before this role acts" means cancelling the MISSION and asking
        // whether finalization still reached it. It did, and nothing stopped it.
        cells.Add(new("archivist", "before_dispatch", How.Harness,
            "RoleCancellationTests.ACancelledMission_DoesNotRunTheArchivistAfterFinalization"));

        // v0.3.8.88 — THE LAST CELL, AND IT WAS DRIVEN FROM THE TRIGGER RATHER THAN EXCUSED.
        //
        // v0.3.8.83 recorded this not-applicable and said exactly what it would take: "the point
        // exists for its real trigger and needs a fixture that produces one — a critical task that
        // fails under adaptive mission control." That was right, and the fixture already existed one
        // file over. `CodePatchLifecycleTests` drives a patch mission whose policy-inserted tester
        // runs a check against the materialized revision, legitimately fails, and hands off to the
        // medic on the typed retryable failure.
        //
        // What makes the moment exact: both of the medic's admission paths — `IngestHandoffs` and
        // `ApplyAdaptiveDecision`'s repair arm — admit the task FIRST and log afterwards, naming the
        // destination role as the event's ant. So the admission event means "scheduled, persisted,
        // not yet dispatched", which is this cell's window, and the fixture stops the colony on it.
        cells.Add(new("medic", "before_dispatch", How.Harness,
            "CodePatchLifecycleTests.ACancelledMission_DoesNotRunTheMedicItHadJustScheduled"));

        // ---- during generation: only the five roles that can reach a model --------------------
        //
        // v0.3.8.76 established which roles hold a ModelRouter, and it is these five. The other
        // seven cannot be cancelled "during generation" because they never generate — a cell marked
        // not-applicable from a fact the contract asserts, not from an assumption.
        //
        // v0.3.8.81 — THREE OF THE FIVE ARE NOW DRIVEN LIVE, and the release exists because doing so
        // found what citing them could not. The cited tests prove the ambient scope aborts an
        // in-flight HTTP call, which was true and remains true. What no test asked was what the ROLE
        // then does with the aborted call, and the answer was: treats it as an unavailable provider
        // and carries on. See ACancelledMission_StopsARoleMidGeneration.
        foreach (var role in new[] { "researcher", "web", "coder", "builder" })
            cells.Add(new(role, "during_generation", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsARoleMidGeneration"));

        // v0.3.8.84 — THE VERIFIER JOINS THEM, and the reason it was cited was wrong.
        //
        // v0.3.8.81 recorded it as unreachable because `SchedulingMode.PolicyInserted` means "no
        // plan may assign it". `AntRegistry.ValidateTask` says otherwise, and says it deliberately:
        // only `FailureTriggered` and `PostFinalization` are refused from planner output, and
        // v0.3.8.51 narrowed the rule to those two on a field report — "a PLANNED tester/soldier
        // step is a plan asking for MORE safety, not less. PolicyInserted now means the runtime
        // GUARANTEES this role runs when its trigger fires, whatever the plan says — a floor, not a
        // ceiling."
        //
        // So the citation was a declaration disagreeing with the runtime, written into a matrix
        // whose purpose is catching exactly that. The soldier — also PolicyInserted — was being
        // driven by this same harness at both universal points the whole time, which is the
        // contradiction that should have been visible from inside the file.
        //
        // `builder`: attempted live and it did NOT reach a model call under a plan-assigned
        // `build_answer` task, so the gate never fired and the cell would have been decided by a
        // role that never generated. Recorded as an observation rather than a diagnosis: what the
        // builder does instead is not yet known, and guessing at it here is how a matrix acquires a
        // convenient belief. The behaviour this cell exists to prove is proved for the same code
        // path by `researcher` and `web`, whose non-Ok branch is the identical
        // SucceededWithWarnings degrade — so the finding is not resting on this cell.
        //
        // Both are named in PLAN.md §2 R3, and RoleQualificationRecordTests asserts they stay named.
        cells.Add(new("verifier", "during_generation", How.Harness,
            "RoleCancellationTests.ACancelledMission_StopsARoleMidGeneration"));

        foreach (var role in new[] { "tester", "file", "ui_cartographer", "scribe", "soldier", "medic", "archivist" })
            cells.Add(new(role, "during_generation", How.NotApplicable,
                "this role holds no ModelRouter and generates nothing — asserted by "
              + "ContractDeclarationTests.ARoleDeclaringModelCalls_HasAnAntThatCanBeGivenARouter, so "
              + "the point does not exist for it rather than being untested."));

        // ---- during a tool call: only the roles whose contract grants tools -------------------
        //
        // v0.3.8.81 — three of the six driven live. The cited tests below prove the process-launching
        // SITES kill their trees, which is the orphan-process half and is real; they say nothing
        // about what the ROLE records once its tool call has been stopped underneath it.
        foreach (var role in new[] { "file", "researcher", "web", "ui_cartographer", "scribe" })
            cells.Add(new(role, "during_tool_call", How.Harness,
                "RoleCancellationTests.ACancelledMission_StopsARoleMidToolCall"));

        // v0.3.8.84 — THE TESTER JOINS THEM TOO, on the same corrected reading of PolicyInserted,
        // and its cell is deliberately SPLIT rather than claimed whole.
        //
        // The harness drives what a harness can: the role is stopped inside a real dispatch of
        // `run_allowlisted_check` and must leave no completed task, no memory, no handoff and no
        // reputation behind. It does NOT prove the orphan-process property, because the tool it
        // dispatches is a gate rather than a process — so that half stays cited to the two tests
        // that do prove it, in the same Detail, where both are checked to resolve. A cell claiming
        // one test proved both halves would be the adjacent-question defect with extra steps.
        //
        // `scribe`: attempted live, and it dispatched NONE of the tools its contract grants under a
        // plan-assigned `release_notes` task. Consistent with the scribe refusing before it reads
        // anything — gate 8 says it cannot act positively on unverified work, and this fixture gives
        // it no verified work — but that is a hypothesis, and the cell records the observation.
        //
        // `ui_cartographer`: attempted live, and the gate tripped BEFORE any task for the role was
        // recorded. Something dispatches one of `list_directory`, `read_text_file`,
        // `search_workspace` or `repository_index` outside this role's own task, early enough in the
        // mission that shadowing the grant stops the mission before it starts. That is worth knowing
        // and is not yet explained; it is carried in PLAN.md §2 R3 as an open question rather than
        // asserted here.
        cells.Add(new("tester", "during_tool_call", How.Harness,
            "RoleCancellationTests.ACancelledMission_StopsARoleMidToolCall;"
          + "ProcessTreeCancellationTests.EverySiteThatWaitsWithATimeout_KillsTheWholeProcessTree;"
          + "SubprocessHangTests.AGitThatNeverExits_TimesOutAndReturns"));

        foreach (var role in new[] { "coder", "builder", "verifier", "soldier", "medic", "archivist" })
            cells.Add(new(role, "during_tool_call", How.NotApplicable,
                "this role's contract grants no tools (AllowedTools is empty), so there is no tool "
              + "call to cancel. The coder is the sharpest case and is deliberate: it PROPOSES "
              + "patches and never applies them, which is why it holds no apply_patch and no shell."));

        // ---- awaiting a dependency: the same ten, for the same contract reason ----------------
        foreach (var role in Roles.Where(PlannerAssignable))
            cells.Add(new(role, "awaiting_dependency", How.Harness,
                "RoleCancellationTests.ACancelledMission_SkipsWorkWaitingOnADependency"));

        // v0.3.8.87 — THE MEDIC'S REASON WAS TRUE OF THE PLANNER AND SILENT ABOUT THE RUNTIME, which
        // is the correction v0.3.8.83 made one cell over and this one did not receive.
        //
        // It read: "a role the planner may not assign has no planned task that can sit waiting on a
        // dependency." Both halves are true and the conclusion does not follow. The medic DOES get a
        // task — two paths create one at runtime — so "no PLANNED task" answers a question adjacent
        // to the one the cell asks, and passes.
        //
        // The runtime answer is stronger and is the one that belongs here. Every path that creates a
        // medic task sets ParentTaskIds and leaves DependsOn empty:
        //
        //   * `ExecutionService.ApplyAdaptiveDecision`'s repair arm — `ParentTaskIds = { broken.Id }`,
        //     no DependsOn. Compare the delta-plan arm four lines below, which sets BOTH for the
        //     verifier it inserts. The difference is deliberate rather than an oversight: the medic's
        //     parent is a task that has already FAILED, and a dependency edge onto a failed task is
        //     one the scheduler can never satisfy. The medic would not wait, it would deadlock.
        //   * `IngestHandoffs` — `created.ParentTaskIds = { sourceTask.Id }`, and `HandoffGate`
        //     constructs the task with no DependsOn at all. This is the tester's required
        //     "check failure needs diagnosis" handoff, the medic's other real trigger.
        //
        // So the point does not exist for this role because of how its work is scheduled, not
        // because of who may schedule it. `ANoFailureTriggeredRole_IsEverGivenADependency` holds the
        // creation sites to that.
        cells.Add(new("medic", "awaiting_dependency", How.NotApplicable,
            "no path that creates a medic task gives it a dependency, and none can: the medic's "
          + "input is a task that has already failed, so an edge onto it would never be satisfiable "
          + "and the role would deadlock rather than wait. Both creation sites — the adaptive "
          + "controller's repair arm and HandoffGate — set ParentTaskIds and leave DependsOn empty, "
          + "which is lineage without a wait. Pinned by "
          + "RoleCancellationTests.ANoFailureTriggeredRole_IsEverGivenADependency."));

        cells.Add(new("archivist", "awaiting_dependency", How.NotApplicable,
            "this role is never SCHEDULED at all — the Queen invokes it directly after finalization, "
          + "so there is no queue entry for a dependency to hold up, now or ever. The strongest "
          + "not-applicable in the matrix, and the only one that does not depend on how a task "
          + "happens to be constructed."));

        return cells.ToArray();
    }

    /// <summary>
    /// NO FAILURE-TRIGGERED ROLE IS EVER GIVEN A DEPENDENCY. v0.3.8.87.
    ///
    /// The medic's `awaiting_dependency` cell is not-applicable because of how its work is
    /// SCHEDULED, and a claim about scheduling has to be checked against the scheduling code — the
    /// old claim was about the planner, which is a different question that happens to have the same
    /// answer today. See the cell's Detail for why an edge onto the medic's parent could never be
    /// satisfied: the parent has already failed, so the role would deadlock rather than wait.
    ///
    /// A source guard, because the property is the ABSENCE of a line. Nothing can be run to observe
    /// a dependency that is never set; what can be observed is that no creation site sets one, and
    /// the day someone adds `DependsOn` beside `AssignedAnt = "medic"` — reasonably, to express
    /// lineage — this fails and points at the cell that has to be re-decided.
    ///
    /// Reads through <see cref="SourceText.CodeOnly"/> and skips this file, for the reason this
    /// repository has learned three times: the paragraph above quotes the construct it forbids.
    /// </summary>
    [Fact]
    public void ANoFailureTriggeredRole_IsEverGivenADependency()
    {
        var failureTriggered = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.Scheduling == SchedulingMode.FailureTriggered)
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(failureTriggered.Count > 0,
            "no role is FailureTriggered any more, so this guard ranges over nothing. The medic was "
          + "the case it was written for; if its scheduling changed, its awaiting_dependency cell "
          + "has to be re-decided rather than left inherited.");

        var offenders = new List<string>();
        var sitesSeen = 0;

        foreach (var file in SourceText.ProductionFiles(SourceText.RepoRoot()))
        {
            var code = SourceText.CodeOnly(File.ReadAllText(file));
            foreach (var body in ObjectInitializers(code, "new Task"))
            {
                sitesSeen++;
                foreach (var role in failureTriggered)
                {
                    if (!body.Contains($"AssignedAnt = \"{role}\"", StringComparison.Ordinal)) continue;
                    if (!body.Contains("DependsOn", StringComparison.Ordinal)) continue;
                    offenders.Add($"{Path.GetFileName(file)} constructs a '{role}' task with DependsOn");
                }
            }
        }

        Assert.True(sitesSeen >= 5,
            $"only {sitesSeen} `new Task` initializer(s) were found in src/. The runtime creates more "
          + "than that; the shape this guard scans for has moved and it is now checking nothing.");

        Assert.True(offenders.Count == 0,
            "a FailureTriggered role is being given a scheduling dependency:\n  "
          + string.Join("\n  ", offenders)
          + "\nIts parent is a task that has already failed, so the edge can never be satisfied and "
          + "the role deadlocks instead of waiting. If lineage is what was wanted, ParentTaskIds "
          + "carries it — that is why the adaptive controller's repair arm sets one and not the "
          + "other. If a dependency really is intended, this role's awaiting_dependency cell stops "
          + "being not-applicable and has to be driven.");
    }

    /// <summary>
    /// Every <c>{ ... }</c> body that follows <paramref name="construct"/>, brace-matched.
    ///
    /// Not a regex: these initializers nest (<c>ParentTaskIds = new List&lt;string&gt; { id }</c>),
    /// and a non-greedy match would stop at the inner close brace and read half a site as a whole
    /// one — which is how a source guard quietly starts covering less than it says.
    /// </summary>
    private static IEnumerable<string> ObjectInitializers(string code, string construct)
    {
        var at = 0;
        while ((at = code.IndexOf(construct, at, StringComparison.Ordinal)) >= 0)
        {
            at += construct.Length;

            var open = at;
            while (open < code.Length && char.IsWhiteSpace(code[open])) open++;
            if (open >= code.Length || code[open] != '{') continue;

            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) { yield return code[open..(i + 1)]; at = i; break; }
            }
        }
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

            // v0.3.8.82 — the third claim type, checked like the other two. A role whose scheduling
            // mode becomes planner-assignable stops being exempt from the universal points, and this
            // is what says so rather than the cell quietly staying wrong.
            if (cell.Point is "before_dispatch" or "awaiting_dependency" && PlannerAssignable(cell.Role))
                wrong.Add($"{cell.Role}/{cell.Point} is marked not-applicable on the grounds that the "
                        + $"planner may not assign it, and its contract is {contract.Scheduling} — "
                        + "which the planner may assign. Drive the cell.");
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
    [InlineData("verifier")] [InlineData("soldier")]
    public void ACancelledMission_StopsEveryRoleBeforeItActs(string role)
    {
        var (queen, missionId) = RunCancelled(role, alreadyCancelled: true);
        AssertTheMissionRanTheScriptedPlan(queen, missionId, role);

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
        var archived = queen.Memory.GetRecentEvents(200, "memory_candidate", missionId);
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
    [InlineData("verifier")] [InlineData("soldier")]
    public void ACancelledMission_SkipsWorkWaitingOnADependency(string role)
    {
        var (queen, missionId) = RunCancelled(role, alreadyCancelled: true);
        AssertTheMissionRanTheScriptedPlan(queen, missionId, role);

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

    /// <summary>
    /// Whether the PLANNER may assign this role at all, read from the same authority that decides
    /// it at runtime. v0.3.8.82.
    ///
    /// `AntRegistry.ValidateTask` refuses a planner-produced task for a `FailureTriggered` or
    /// `PostFinalization` role — the medic diagnoses a failure that must already exist, the
    /// archivist summarises a mission that must already be terminal, and both handlers open by
    /// refusing a planned invocation. `PolicyInserted` is deliberately NOT in that set (v0.3.8.51:
    /// a planned tester or soldier step is a plan asking for more safety, not less).
    ///
    /// Derived rather than listed, because a hardcoded list here would be a second copy of a rule
    /// the registry owns, and the two would disagree the first time a scheduling mode changed.
    /// </summary>
    private static bool PlannerAssignable(string role)
    {
        var contract = AntExecutionCatalog.ContractFor(role);
        return contract is null
            || contract.Scheduling is not (SchedulingMode.FailureTriggered or SchedulingMode.PostFinalization);
    }

    /// <summary>
    /// The roles this fixture scripts for <paramref name="role"/>, in plan order.
    ///
    /// THREE TASKS, AND THE NUMBER IS THE POINT. `Planner.TasksFromJson` rejects any plan with fewer
    /// than `AnthillRuntime.MinDynamicTasks` (3) usable tasks, and a rejected plan is replaced —
    /// silently, from the fixture's perspective — by `FallbackTasks`, a static researcher/file/coder/
    /// builder/verifier plan. **Every plan this harness scripted before v0.3.8.82 was below that
    /// minimum**: one task here, two in the pre-dispatch fixture. So every cell it claimed to drive
    /// was decided by a plan nobody wrote, and passed because each fallback branch happens to contain
    /// the role the assertion was looking for. `CodePatchLifecycleTests` scripts eight tasks and has
    /// therefore always worked, which is why the failure never surfaced there.
    ///
    /// The role under test is FIRST so the mission reaches it before anything else can end the run,
    /// and the two fillers depend on it so they are skipped rather than raced.
    /// </summary>
    private static IReadOnlyList<string> ScriptedRolesFor(string role)
    {
        // Fillers that any mission may carry, minus the role under test so a plan never names one
        // role twice — `TasksFromJson` rejects an AMBIGUOUS dependency title, and two tasks with the
        // same role are how a fixture drifts into one.
        var fillers = new[] { "researcher", "builder", "verifier" }
            .Where(r => !string.Equals(r, role, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return new[] { role }.Concat(fillers).ToList();
    }

    /// <summary>The scripted plan JSON: the role under test, then two dependent fillers.</summary>
    private static string ScriptedPlan(string role)
    {
        var roles = ScriptedRolesFor(role);
        var titles = roles.Select((r, i) => $"Step {i + 1} — {r}").ToList();

        var tasks = roles.Select((r, i) =>
        {
            var dependsOn = i == 0 ? "[]" : $"[\"{titles[i - 1]}\"]";
            return $$"""
                    { "title": "{{titles[i]}}", "description": "Scripted step for {{r}}.",
                      "assigned_ant": "{{r}}", "task_type": "{{TaskTypeFor[r]}}", "depends_on": {{dependsOn}} }
                """;
        });

        return "{\n  \"tasks\": [\n" + string.Join(",\n", tasks) + "\n  ]\n}";
    }

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
    [InlineData("verifier")]
    public void ACancelledMission_StopsARoleMidGeneration(string role)
    {
        var (queen, missionId) = RunStoppedDuring(role, duringToolCall: false);
        AssertTheMissionRanTheScriptedPlan(queen, missionId, role);
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
    [InlineData("ui_cartographer")] [InlineData("scribe")] [InlineData("tester")]
    public void ACancelledMission_StopsARoleMidToolCall(string role)
    {
        var (queen, missionId) = RunStoppedDuring(role, duringToolCall: true);
        AssertTheMissionRanTheScriptedPlan(queen, missionId, role);
        AssertTheRoleLeftNothingBehind(queen, missionId, role, "mid-tool-call");
    }

    /// <summary>
    /// WHAT THE MISSION ACTUALLY DID, for a failure message. v0.3.8.82.
    ///
    /// Written because three cells of this harness failed at v0.3.8.81 with messages that described
    /// the EXPECTATION and not the run — "the role never reached the stopping point", "no task was
    /// recorded for this role" — and each of the three then cost a release to guess at. A fixture
    /// that stops a mission from the inside has exactly one hard part, which is knowing what the
    /// mission did instead, and that is the thing these messages withheld.
    ///
    /// Reads the plan from `task_created` events rather than from the task ROWS: rows appear when a
    /// task starts running, so a plan whose tasks never ran is invisible in the table and fully
    /// visible here. That difference is the whole diagnosis in the cartographer's case — "planned but
    /// never dispatched" and "never planned" are different defects that produce the same empty table.
    /// </summary>
    private static string WhatHappened(Queen queen, string missionId)
    {
        string Field(Dictionary<string, object?> row, string key) =>
            row.GetValueOrDefault(key)?.ToString() ?? "";

        var planned = queen.Memory.GetRecentEvents(200, "task_created", missionId)
            .Select(e => Field(e, "ant_name"))
            .Where(a => a.Length > 0)
            .ToList();

        var ran = queen.Memory.GetTasksForMission(missionId)
            .Select(t => $"{Field(t, "assigned_ant")}"
                       + $"[{Field(t, "status")}"
                       + (Field(t, "failure_type").Length > 0 ? $"/{Field(t, "failure_type")}" : "")
                       + "]")
            .ToList();

        var dispatched = queen.Memory.GetRecentEvents(200, "tool_called", missionId)
            .Select(e => $"{Field(e, "ant_name")}:{Field(e, "message")}")
            .ToList();

        return "\n    planned (task_created): "
             + (planned.Count > 0 ? string.Join(", ", planned) : "<nothing — the planner produced no tasks>")
             + "\n    ran (task rows): "
             + (ran.Count > 0 ? string.Join(", ", ran) : "<no task ever started>")
             + "\n    tools dispatched: "
             + (dispatched.Count > 0 ? string.Join(", ", dispatched) : "<none>");
    }

    /// <summary>
    /// The mission ran the plan this fixture WROTE. v0.3.8.82.
    ///
    /// A scripted-plan scenario that does not check this is not testing what it wrote: `Planner`
    /// falls back to a static plan whenever the dynamic one is rejected or fails to parse, and
    /// `AssignDefaultWorkers` drops individual tasks the registry refuses — both loudly on stderr and
    /// both invisible to an assertion that only looks for the role it hoped for. The fallback plan
    /// contains researcher, file and verifier steps, so a fixture aiming at one of those roles would
    /// PASS on a plan it never wrote, which is this repository's oldest defect shape pointed at its
    /// own test fixtures.
    /// </summary>
    private static void AssertTheMissionRanTheScriptedPlan(Queen queen, string missionId, string role)
    {
        // The roles the fixture wrote, minus any the REGISTRY refuses from a planner-produced plan.
        // The medic and the archivist are dropped by `AntRegistry.ValidateTask` before dispatch, so
        // expecting them here would be asserting against a rule the runtime owns and enforces.
        // The roles the fixture wrote, minus any the REGISTRY refuses from a planner-produced plan,
        // plus the verifier the PARSER appends. Three rules, three owners, and the expected set is
        // only right when it reads all three:
        //
        //   * `AntRegistry.ValidateTask` drops a `FailureTriggered` / `PostFinalization` role — the
        //     medic and the archivist — before dispatch;
        //   * `Planner.TasksFromJson` appends a verifier task when a CONSEQUENTIAL plan names none —
        //     v0.3.8.93 split the rule: a plan with patch-producing work always gets its verifier,
        //     an informational plan keeps the shape the fixture wrote. This derivation reads
        //     `Planner.IsConsequential` itself, so the expected set moves with the runtime's rule
        //     instead of restating it;
        //   * everything else survives as written.
        //
        // Union rather than append, because a plan that already names the verifier gets no second
        // one — `ScriptedRolesFor` includes it whenever the role under test is not itself one of the
        // two fillers it displaces.
        var scripted = ScriptedRolesFor(role).Where(PlannerAssignable).ToList();
        var consequential = scripted.Any(r =>
            Planner.IsConsequential(new Anthill.Core.Domain.Task { AssignedAnt = r, TaskType = "" }));
        var expected = (consequential ? scripted.Concat(new[] { "verifier" }) : scripted)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var planned = queen.Memory.GetRecentEvents(200, "task_created", missionId)
            .Select(e => e.GetValueOrDefault("ant_name")?.ToString() ?? "")
            .Where(a => a.Length > 0)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.True(planned.SequenceEqual(expected, StringComparer.Ordinal),
            $"this fixture scripted [{string.Join(", ", expected)}] and the mission planned "
          + $"[{string.Join(", ", planned)}]. A plan `Planner.TasksFromJson` rejects is replaced by "
          + "`FallbackTasks` — a static researcher/file/coder/builder/verifier graph — and the "
          + "substitution is invisible from here unless something asserts it. Below "
          + $"{AnthillRuntime.MinDynamicTasks} usable tasks is the rejection this harness kept "
          + "hitting." + WhatHappened(queen, missionId));

        if (PlannerAssignable(role)) return;

        // A role the planner may not assign cannot be driven by planning a task for it, and saying
        // so is the cell's content rather than a gap in it. Asserted rather than commented, because
        // a scheduling-mode change that made one of these plannable should show up as this test
        // failing and the matrix being updated with the evidence.
        Assert.DoesNotContain(role, planned);
    }

    /// <summary>
    /// A CANCELLED MISSION DOES NOT RUN THE ARCHIVIST. v0.3.8.85 (PLAN.md §2 R3).
    ///
    /// THE CELL THIS CLOSES, and why it was not reachable from the theories above. Every other role
    /// is driven by planning a task for it; the archivist is `PostFinalization` and
    /// `AntRegistry.ValidateTask` refuses a planned one, so v0.3.8.82 recorded its `before_dispatch`
    /// cell as not-applicable. That was true of the PLANNER and false of the role: the archivist has
    /// a dispatch site of its own — `Queen.RunArchivistAfterFinalization`, invoked directly once the
    /// canonical evaluation is persisted — and "cancel before this role acts" is a perfectly
    /// meaningful question to ask there.
    ///
    /// WHAT THE ANSWER WAS. Nothing stopped it. That path does not go through
    /// `ExecutionService.RunSingleTask`, so it could not inherit v0.3.8.81's stop check, and it had
    /// no check of its own: a mission the operator cancelled still ran the archivist over its
    /// partial work and still ingested the candidates it proposed.
    ///
    /// AND WHY THIS HARNESS MISSED IT for five releases while asserting "no positive memory". The
    /// existing property watches `memory_candidate` events, and a stopped mission usually
    /// gives the archivist nothing worth proposing — so the assertion passed because the archivist
    /// found nothing, not because it was prevented from looking. A property that holds by luck is
    /// indistinguishable from one that holds by design until the day it stops.
    /// </summary>
    [Fact]
    public void ACancelledMission_DoesNotRunTheArchivistAfterFinalization()
    {
        var (queen, missionId) = RunCancelled("researcher", alreadyCancelled: true);

        var ran = queen.Memory.GetRecentEvents(200, "archivist_ran", missionId);
        Assert.True(ran.Count == 0,
            "the archivist ran after a cancelled mission finalized. A stopped mission's partial work "
          + "is not a lesson, and the memory it writes is the thing that outlives the mission."
          + WhatHappened(queen, missionId));

        // Skipped for the RIGHT reason. The same event already fires when the role is switched off,
        // and "no lessons were extracted" and "the archivist is unavailable" must not look the same
        // — which is the distinction that event was created to preserve in the first place.
        var skipped = queen.Memory.GetRecentEvents(200, "archivist_skipped", missionId);
        Assert.True(skipped.Count > 0,
            "the archivist neither ran nor recorded why it did not. Silence here is the state this "
          + "event exists to prevent." + WhatHappened(queen, missionId));

        Assert.Contains(skipped, e =>
            (e.GetValueOrDefault("message")?.ToString() ?? "").Contains("cancelled", StringComparison.OrdinalIgnoreCase)
         || (e.GetValueOrDefault("metadata_json")?.ToString() ?? "").Contains("mission_stopped", StringComparison.Ordinal));

        // And the property that was holding by luck now holds because nothing was asked.
        Assert.Empty(queen.Memory.GetRecentEvents(200, "memory_candidate", missionId));
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

        Assert.True(mine.Count > 0,
            $"no task was recorded for '{role}' at all, so the {point} cancellation was never "
          + "exercised. A task ROW appears when the task starts running, so an empty one here with a "
          + "populated plan below means the task was planned and never dispatched — a different "
          + "defect from never being planned, and the two used to be indistinguishable from this "
          + "message." + WhatHappened(queen, missionId));

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
        var archived = queen.Memory.GetRecentEvents(200, "memory_candidate", missionId);
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

        // THREE tasks, role under test first — see ScriptedPlan. A one-task plan is below
        // MinDynamicTasks and is discarded for the static fallback before the mission ever starts.
        var plan = ScriptedPlan(role);

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
          + " A passing assertion after this point would be about a role that never acted."
          + WhatHappened(queen, missionId!));
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
        // No DECLARED checks, so `CheckSource.DefaultSelection` falls back to the historical .NET
        // pair and the tester always has something to dispatch. Pinned rather than inherited: this
        // collection shares static state, and a leaked configuration that declared zero checks would
        // make TesterAnt return Blocked — a role that never acted, passing the cell.
        AnthillRuntime.WorkspaceChecks = Array.Empty<CheckDefinition>();
        AnthillRuntime.AllowedWorkspaceRoot = Path.Combine(_dir, "workspace");
    }

    /// <summary>
    /// Drives one mission whose plan assigns a task to <paramref name="role"/>, with the mission
    /// token already cancelled. Shared by both theories so the twenty-four cells are one fixture.
    /// </summary>
    private (Queen Queen, string MissionId) RunCancelled(string role, bool alreadyCancelled)
    {
        ApplyFullRoster();

        // v0.3.8.82: three tasks, and the role's OWN task type rather than "research" for every
        // role. The previous two-task plan was below MinDynamicTasks and was discarded, so these
        // twenty-four cells have been asserting mission-wide properties on a plan the fixture did
        // not write since v0.3.8.80 — true statements about the wrong mission.
        var plan = ScriptedPlan(role);

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
