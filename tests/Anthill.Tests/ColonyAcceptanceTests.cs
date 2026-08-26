using System.Text.Json;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Structural repair §12/§13 — the Queen-driven composed acceptance suite. Missions are submitted
/// through <see cref="Queen.RunMission(string)"/>, the same public path an operator uses; the real
/// graph runs (planner, scheduler, ExecutionService, registry, contracts, router, evaluator,
/// Sqlite persistence). No ant is invoked by hand. Assertions read STRUCTURED persisted state —
/// task rows, the persisted evaluation, the event stream — never model prose.
///
/// Scenarios that need injected failures live as focused behavioral tests beside the components
/// they guard (MedicAntTests: parallel failures E, the ui-word guard I, semantic dedupe D and its
/// counter-case; MissionRevisionTests: stale evidence J and unpatched-tree evidence; the
/// PlanVerificationPolicyTests: planner-omitted verification). What THIS file proves is the
/// composed flow: research end-to-end (A), cancellation leaving nothing running (K), and durable
/// reload keeping the graph (L).
/// </summary>
[Collection("specialist-gates")]   // gate toggles are static; serialize with the other togglers
public class ColonyAcceptanceTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas;

    /// <summary>
    /// THE PLANNER IS PINNED OFFLINE, and saying so is the point. v0.3.8.69.
    ///
    /// ScenarioA asserts "the default plan is research → build → verify" — a claim about the
    /// DETERMINISTIC FALLBACK planner, which is the only planner that has a default. With
    /// <see cref="Anthill.Core.Configuration.AnthillRuntime.UseOllama"/> true and a local model
    /// running, the dynamic planner answers instead and writes whatever plan it likes; the assertion
    /// then passes or fails on a model's prose, which is exactly what this file's header says it
    /// never does ("Assertions read STRUCTURED persisted state … never model prose").
    ///
    /// It reached this state without anyone choosing it. The flag was leaking in from
    /// `ModelReliabilityTests`, which set it and never restored it (fixed in the same release), so
    /// whether this suite planned online depended on collection ordering. But the leak only exposed
    /// the gap — an acceptance test whose outcome changes when a model happens to be installed was
    /// never deterministic, and closing the leak alone would leave it one config change from
    /// flaking again.
    ///
    /// Everything else in these scenarios stays real: the Queen, the scheduler, ExecutionService,
    /// the registry, contracts, the evaluator and Sqlite. Only the planner's source of a plan is
    /// pinned, and it is pinned to the one this file makes assertions about.
    /// </summary>
    public ColonyAcceptanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-accept-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);

        _useOllamaWas = Anthill.Core.Configuration.AnthillRuntime.UseOllama;
        Anthill.Core.Configuration.AnthillRuntime.UseOllama = false;
    }

    public void Dispose()
    {
        Anthill.Core.Configuration.AnthillRuntime.UseOllama = _useOllamaWas;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Queen NewQueen(string db) => new(new SqliteMemory(Path.Combine(_dir, db)));

    private static string Field(Dictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key)?.ToString() ?? "";

    private static List<string> JsonIds(Dictionary<string, object?> row, string key)
    {
        var raw = Field(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? new(); }
        catch { return new(); }
    }

    // ---- Scenario A: simple research, end to end ------------------------------------------------

    [Fact]
    public void ScenarioA_AResearchMission_RunsTheRealPath_AndLeavesAReconstructableRecord()
    {
        string? missionId = null;
        // The archivist is gate-controlled; the scenario asserts its candidates, so its gate is
        // open for the run — the same pattern every specialist-gate test uses.
        //
        // v0.3.8.87 — AND THE GATE IS OPENED BEFORE THE QUEEN IS BUILT, which is the half that was
        // missing. `Queen`'s constructor snapshots role availability
        // (`AntExecutorCatalog.Initialize`) from the flags as they stand at that moment, so a gate
        // opened afterwards opens nothing for that colony. `NewQueen` used to be the first line of
        // this test: the archivist was unavailable for the whole run, and the candidates asserted
        // below existed only when an earlier test had left the flag on. `RosterGates.Capture` in the
        // fixture also forces `AnthillRuntime.Initialize` first, so the constructor's own one-shot
        // config load can no longer overwrite what is set here.
        var gatesWere = RosterGates.Capture();
        Queen queen;
        try
        {
            Anthill.Core.Configuration.AnthillRuntime.EnableSpecialistAntExecution = true;
            Anthill.Core.Configuration.AnthillRuntime.ActivationTier = Anthill.Core.Agents.ActivationTier.Full;
            Anthill.Core.Configuration.AnthillRuntime.EnableArchivistAnt = true;

            queen = NewQueen("a.db");
            // v0.3.8.93 — the goal is deliberately BRIEF-SIZED (above Planner.SimpleAnswerGoalChars).
            // Proportional planning made the old one-line goal a legitimate single-builder mission,
            // which is the right plan for that question and the wrong fixture for this scenario:
            // this scenario exists to prove the multi-role path, graph integrity across real edges,
            // and the archivist's post-finalization run. The single-task path has its own proof in
            // PlanProportionalityTests; this goal is one a research→build→verify plan actually fits.
            queen.RunMission(
                "Summarize in one sentence what the ANTHILL framework does. Ground the sentence in "
              + "the colony's own context: how a mission travels from an operator's request through "
              + "planning, execution and verification to a final answer, which roles take part at "
              + "each stage and what each contributes, and whatever the colony's stored memory can "
              + "supply about its own history. Close with the single-sentence version an operator "
              + "would read first.",
                onMissionCreated: id => missionId = id);
        }
        finally
        {
            RosterGates.Restore(gatesWere);
        }
        Assert.NotNull(missionId);

        // The mission row is terminal and complete.
        var mission = queen.Memory.GetMission(missionId!);
        Assert.NotNull(mission);
        Assert.Equal("complete", Field(mission!, "status"));

        // The plan ran through real roles; every dependency and parent edge points at a real task
        // row — graph integrity (§7) proven from PERSISTED state, not process memory.
        var tasks = queen.Memory.GetTasksForMission(missionId!);
        Assert.True(tasks.Count >= 3, "the default plan is research → build → verify");
        Assert.Contains(tasks, t => Field(t, "assigned_ant") == "verifier");
        var ids = tasks.Select(t => Field(t, "id")).ToHashSet(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            Assert.All(JsonIds(t, "depends_on_json"), dep => Assert.Contains(dep, ids));
            Assert.All(JsonIds(t, "parent_task_ids_json"), p => Assert.Contains(p, ids));
            Assert.NotEqual("running", Field(t, "status"));
        }

        // The canonical evaluation was persisted, and the archivist ran post-finalization —
        // memory candidates exist as durable events with provenance.
        Assert.NotNull(queen.Memory.LoadMissionEvaluation(missionId!));
        var candidates = queen.Memory.GetRecentEvents(50, "memory_candidate", missionId);
        Assert.NotEmpty(candidates);
    }

    // ---- Scenario K: cancellation leaves nothing running ----------------------------------------

    [Fact]
    public void ScenarioK_ACancelledMission_LeavesNoRunningTasks_AndNeverClaimsVerifiedSuccess()
    {
        var queen = NewQueen("k.db");
        using var cts = new CancellationTokenSource();
        cts.Cancel();   // cancelled before the first task can dispatch

        string? missionId = null;
        queen.RunMission("Summarize the colony, slowly.", id => missionId = id, cts.Token);
        Assert.NotNull(missionId);

        var mission = queen.Memory.GetMission(missionId!);
        Assert.NotNull(mission);
        Assert.NotEqual("running", Field(mission!, "status"));

        var tasks = queen.Memory.GetTasksForMission(missionId!);
        Assert.DoesNotContain(tasks, t => Field(t, "status") == "running");

        var evaluation = queen.Memory.LoadMissionEvaluation(missionId!);
        Assert.NotNull(evaluation);
        Assert.NotEqual("completed_verified", evaluation!.OutcomeCode);
    }

    // ---- Scenario L: restart keeps the graph and does not re-evaluate ---------------------------

    [Fact]
    public void ScenarioL_ARestartedProcess_ReadsTheSameGraphAndTheSameEvaluation()
    {
        string? missionId = null;
        int taskCount;
        string firstOutcome;

        // First process lifetime.
        {
            var queen = NewQueen("l.db");
            queen.RunMission("List the ant roles in the colony.", id => missionId = id);
            taskCount = queen.Memory.GetTasksForMission(missionId!).Count;
            firstOutcome = queen.Memory.LoadMissionEvaluation(missionId!)!.OutcomeCode;
        }

        // Second process lifetime: a NEW Queen (with its startup recovery) over the SAME database.
        var restarted = NewQueen("l.db");
        var tasks = restarted.Memory.GetTasksForMission(missionId!);

        Assert.Equal(taskCount, tasks.Count);
        Assert.All(tasks, t => Assert.NotEqual("running", Field(t, "status")));

        // Lineage survived persistence — the graph is reconstructable from rows alone.
        var ids = tasks.Select(t => Field(t, "id")).ToHashSet(StringComparer.Ordinal);
        foreach (var t in tasks)
            Assert.All(JsonIds(t, "depends_on_json"), dep => Assert.Contains(dep, ids));

        // The evaluation is the SAME one — restart did not re-evaluate, duplicate, or overwrite.
        Assert.Equal(firstOutcome, restarted.Memory.LoadMissionEvaluation(missionId!)!.OutcomeCode);
    }
}
