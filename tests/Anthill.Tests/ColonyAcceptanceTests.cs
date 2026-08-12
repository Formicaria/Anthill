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

    public ColonyAcceptanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-accept-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
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
        var queen = NewQueen("a.db");
        string? missionId = null;
        // The archivist is gate-controlled; the scenario asserts its candidates, so its gate is
        // open for the run — the same pattern every specialist-gate test uses.
        var specialistsWere = Anthill.Core.Configuration.AnthillRuntime.EnableSpecialistAntExecution;
        var archivistWas = Anthill.Core.Configuration.AnthillRuntime.EnableArchivistAnt;
        try
        {
            Anthill.Core.Configuration.AnthillRuntime.EnableSpecialistAntExecution = true;
            Anthill.Core.Configuration.AnthillRuntime.EnableArchivistAnt = true;
            queen.RunMission("Summarize in one sentence what the ANTHILL framework does.",
                onMissionCreated: id => missionId = id);
        }
        finally
        {
            Anthill.Core.Configuration.AnthillRuntime.EnableSpecialistAntExecution = specialistsWere;
            Anthill.Core.Configuration.AnthillRuntime.EnableArchivistAnt = archivistWas;
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
