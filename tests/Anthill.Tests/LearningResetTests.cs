using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Microsoft.Data.Sqlite;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.20.0 Stage 7: the one-time reset of learning state derived under the pre-v2.19 completion
/// rule. The contract under test, verbatim from the migration constraints: reset only derived
/// state (objective EMA, trail strengths, success counters that accepted unverified outcomes);
/// never touch raw history or failure history; back up before mutating; run exactly once; mark
/// unreconstructable data legacy_unverified, retain it for reporting, and exclude it from
/// planning until it earns post-reset evidence.
/// </summary>
public class LearningResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_reset_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Fresh(out string dbPath)
    {
        Directory.CreateDirectory(_dir);
        dbPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        return new SqliteMemory(dbPath);
    }

    /// <summary>
    /// Simulate a database created BEFORE the reset shipped: seed learning state, then strip the
    /// marker the constructor stamped (a genuinely old DB has data but no marker).
    /// </summary>
    private static void StripMarker(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM anthill_meta WHERE key LIKE 'learning_reset%'";
        cmd.ExecuteNonQuery();
    }

    private static SqliteMemory SeedPreBoundary(SqliteMemory mem, string dbPath)
    {
        var o = new Objective { Id = "obj-legacy", Title = "legacy", Charter = "do things", Status = ObjectiveStatus.Active };
        o.SuccessEma = 0.83;                    // accumulated under the defective rule
        o.ConsecutiveFailures = 2;              // failure history — must survive
        mem.SaveObjective(o);

        // A strong trail (12 successes under the old rule, 3 failures) and a weak one.
        for (var i = 0; i < 12; i++) mem.UpdatePheromoneTrail("ant:builder", "ant", true, 0.03);
        for (var i = 0; i < 3; i++) mem.UpdatePheromoneTrail("ant:builder", "ant", false, -0.08);
        mem.UpdatePheromoneTrail("task_type:research", "task_type", true, 0.02);

        // Raw history that must never be touched.
        var m = new Mission { Id = "m-history", Goal = "old mission", Status = MissionStatus.Complete };
        m.Tasks.Add(new DomainTask { Title = "t", AssignedAnt = "builder", Status = TaskStatus.Complete, Result = "done" });
        mem.SaveMission(m);
        mem.LogEvent("m-history", "task_completed", "historic event");

        StripMarker(dbPath);
        return mem;
    }

    private static Dictionary<string, object?> Trail(SqliteMemory mem, string key) =>
        mem.ListPheromoneTrails(100).First(t => t["trail_key"]?.ToString() == key);

    private static long L(object? v) => Convert.ToInt64(v);
    private static double D(object? v) => Convert.ToDouble(v);

    // ---- fresh databases -----------------------------------------------------------------------

    [Fact]
    public void AFreshDatabase_GetsTheMarkerAtCreation_WithNothingToBackUp()
    {
        var mem = Fresh(out var dbPath);
        // The constructor already ran the reset; a second call must be a no-op.
        var again = mem.ApplyLearningReset();
        Assert.True(again.AlreadyApplied);
        Assert.NotNull(mem.LearningResetDate());
        // Nothing was mutated on a fresh DB, so nothing was backed up.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dbPath)!, "*.bak"));
    }

    /// <summary>
    /// The marker on fresh DBs is what makes the reset a BOUNDARY, not a recurring purge: state
    /// accumulated after v2.19 is earned under the corrected rule and must never be reset.
    /// </summary>
    [Fact]
    public void PostBoundaryLearning_IsNeverReset()
    {
        var mem = Fresh(out _);
        mem.UpdatePheromoneTrail("ant:verifier", "ant", true, 0.05);
        var report = mem.ApplyLearningReset();
        Assert.True(report.AlreadyApplied);
        Assert.Equal(0.55, D(Trail(mem, "ant:verifier")["strength"]), 3);
        Assert.Equal(0, L(Trail(mem, "ant:verifier")["legacy"]));
    }

    // ---- pre-boundary databases ----------------------------------------------------------------

    [Fact]
    public void PreBoundaryState_IsReset_AndSnapshottedForReporting()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        var report = mem.ApplyLearningReset();

        Assert.False(report.AlreadyApplied);
        Assert.Equal(1, report.ObjectivesReset);
        Assert.Equal(2, report.TrailsMarkedLegacy);

        // EMA neutral/unset; the old value survives in metadata for reporting.
        var o = mem.GetObjective("obj-legacy")!;
        Assert.Null(o.SuccessEma);
        Assert.Equal("0.83", o.Metadata["legacy_success_ema"]?.ToString());

        // Trail: neutral strength, success counter restarted, legacy-marked, snapshot retained.
        var t = Trail(mem, "ant:builder");
        Assert.Equal(0.5, D(t["strength"]), 3);
        Assert.Equal(0, L(t["success_count"]));
        Assert.Equal(1, L(t["legacy"]));
        Assert.Contains("\"legacy_success_count\":12", (string)t["metadata_json"]!);
    }

    [Fact]
    public void FailureHistory_IsPreserved_InPlace()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        mem.ApplyLearningReset();
        Assert.Equal(3, L(Trail(mem, "ant:builder")["failure_count"]));
        Assert.Equal(2, mem.GetObjective("obj-legacy")!.ConsecutiveFailures);
    }

    [Fact]
    public void RawHistory_IsUntouched()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        var missionBefore = mem.GetMission("m-history")!;
        var tasksBefore = mem.GetTasksForMission("m-history");
        mem.ApplyLearningReset();

        var missionAfter = mem.GetMission("m-history")!;
        Assert.Equal(missionBefore["goal"], missionAfter["goal"]);
        Assert.Equal(missionBefore["status"], missionAfter["status"]);
        Assert.Equal(tasksBefore.Count, mem.GetTasksForMission("m-history").Count);
        Assert.Contains(mem.GetRecentEvents(50, missionId: "m-history"),
            e => e["event_type"]?.ToString() == "task_completed");
    }

    [Fact]
    public void TheResetRunsExactlyOnce()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        var first = mem.ApplyLearningReset();
        Assert.False(first.AlreadyApplied);

        // Earn post-reset evidence, then prove a second run does not destroy it.
        mem.UpdatePheromoneTrail("ant:builder", "ant", true, 0.04);
        var second = mem.ApplyLearningReset();
        Assert.True(second.AlreadyApplied);
        Assert.Equal(1, L(Trail(mem, "ant:builder")["success_count"]));
        Assert.Equal(0.54, D(Trail(mem, "ant:builder")["strength"]), 3);
    }

    [Fact]
    public void ABackupIsTaken_BeforeAnyMutation_AndContainsThePreResetValues()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        var report = mem.ApplyLearningReset();
        Assert.NotNull(report.BackupPath);
        Assert.True(File.Exists(report.BackupPath));

        // The backup holds the world as it was BEFORE the reset.
        using var conn = new SqliteConnection($"Data Source={report.BackupPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT success_ema FROM objectives WHERE id = 'obj-legacy'";
        Assert.Equal(0.83, Convert.ToDouble(cmd.ExecuteScalar()), 3);
        cmd.CommandText = "SELECT success_count FROM pheromone_trails WHERE trail_key = 'ant:builder'";
        Assert.Equal(12L, cmd.ExecuteScalar());
    }

    [Fact]
    public void AnAuditEvent_RecordsWhatTheResetDid()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        mem.ApplyLearningReset();
        var audit = mem.GetRecentEvents(50, eventType: "learning_reset");
        var ev = Assert.Single(audit);
        Assert.Contains("1 objective EMA", ev["message"]?.ToString());
        Assert.Contains("2 pheromone trail", ev["message"]?.ToString());
    }

    // ---- legacy semantics: retained for reporting, excluded from planning ----------------------

    [Fact]
    public void LegacyTrails_AreExcludedFromPlanning_ButRetainedForReporting()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        mem.ApplyLearningReset();

        // Planning (Strategist context) sees nothing until evidence is re-earned...
        Assert.DoesNotContain(mem.GetTopPheromoneTrails(20),
            t => t["trail_key"]?.ToString() == "ant:builder");
        // ...reporting still sees everything, with the flag visible.
        Assert.Equal(1, L(Trail(mem, "ant:builder")["legacy"]));
    }

    [Fact]
    public void APostResetSuccess_ReadmitsTheTrailToPlanning()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        mem.ApplyLearningReset();
        mem.UpdatePheromoneTrail("ant:builder", "ant", true, 0.05);
        Assert.Contains(mem.GetTopPheromoneTrails(20),
            t => t["trail_key"]?.ToString() == "ant:builder");
    }

    /// <summary>Retention means retention: no prune threshold may delete a legacy trail.</summary>
    [Fact]
    public void Prune_CanNeverDeleteLegacyTrails()
    {
        var mem = SeedPreBoundary(Fresh(out var dbPath), dbPath);
        mem.ApplyLearningReset();
        mem.PrunePheromones(minStrength: 0.99, dropFailureDominant: true);
        Assert.Equal(1, L(Trail(mem, "ant:builder")["legacy"])); // still there
    }

    [Fact]
    public void PruneStillWorks_OnNonLegacyTrails()
    {
        var mem = Fresh(out _);
        mem.UpdatePheromoneTrail("ant:weak", "ant", false, -0.45); // 0.05 — below default threshold
        var removed = mem.PrunePheromones();
        Assert.True(removed >= 1);
        Assert.DoesNotContain(mem.ListPheromoneTrails(100), t => t["trail_key"]?.ToString() == "ant:weak");
    }

    // ---- surfacing -----------------------------------------------------------------------------

    [Fact]
    public void TheResetDate_IsVisibleInThePheromoneContext()
    {
        var mem = Fresh(out _);
        Assert.Contains("learning reset", mem.FormatPheromoneContext());
    }
}
