using Anthill.Core.Common;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Projects;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.48 — the schedule subsystem, tested at the promises the UI makes: local rules fire at
/// local times across DST, claims are atomic, overlaps skip visibly, missed runs fire once,
/// one-time schedules retire, Ask-mode runs wait, and a restart tells the truth about what died.
/// </summary>
public class ProjectScheduleTests : IDisposable
{
    private readonly string _dir;

    public ProjectScheduleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-sched-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SqliteMemory Memory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static ProjectSchedule Daily(string tz = "UTC", string at = "07:00") => new()
    {
        Id = "s1", ProjectId = "p1", Name = "morning", Prompt = "do the thing",
        TriggerType = "daily", LocalTime = at, Timezone = tz,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    // ---- next-run arithmetic ---------------------------------------------------------------------

    [Fact]
    public void Daily_FiresAtTheLocalHour_TodayIfStillAhead_ElseTomorrow()
    {
        var s = Daily();
        Assert.Equal(new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc),
            s.ComputeNextRun(new DateTime(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 3, 11, 7, 0, 0, DateTimeKind.Utc),
            s.ComputeNextRun(new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>"07:00 in Chicago" stays 07:00 in Chicago across the spring-forward: the UTC
    /// instant SHIFTS from 13:00Z (CST) to 12:00Z (CDT), which is exactly the point.</summary>
    [Fact]
    public void Daily_TracksItsZoneAcrossDst()
    {
        var s = Daily(tz: "America/Chicago");
        // 2026-03-07 is CST (UTC-6); 2026-03-09 (after the 08 Mar transition) is CDT (UTC-5).
        Assert.Equal(new DateTime(2026, 3, 7, 13, 0, 0, DateTimeKind.Utc),
            s.ComputeNextRun(new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
            s.ComputeNextRun(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>A local time inside the spring-forward gap nudges past it instead of throwing.</summary>
    [Fact]
    public void ALocalTimeSkippedByDst_NudgesPastTheGap()
    {
        var s = Daily(tz: "America/Chicago", at: "02:30");   // 02:30 does not exist on 08 Mar 2026
        var next = s.ComputeNextRun(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(next);
    }

    [Fact]
    public void Weekdays_SkipTheWeekend()
    {
        var s = Daily() with { TriggerType = "weekdays" };
        // Friday 2026-03-13 after 07:00 → Monday 2026-03-16.
        Assert.Equal(DayOfWeek.Monday,
            s.ComputeNextRun(new DateTime(2026, 3, 13, 8, 0, 0, DateTimeKind.Utc))!.Value.DayOfWeek);
    }

    [Fact]
    public void Weekly_KeepsTheWeekdayOfItsCreation()
    {
        var s = Daily() with { TriggerType = "weekly" };     // created on a Thursday (2026-01-01)
        var next = s.ComputeNextRun(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DayOfWeek.Thursday, next!.Value.DayOfWeek);
    }

    [Fact]
    public void ManualAndSpentOneTime_NeverFire()
    {
        Assert.Null((Daily() with { TriggerType = "manual" }).ComputeNextRun(DateTime.UtcNow));
        Assert.Null((Daily() with { TriggerType = "once", OneTimeAt = DateTime.UtcNow.AddDays(-1) })
            .ComputeNextRun(DateTime.UtcNow));
    }

    [Theory]
    [InlineData("30 6 * * 1", true)]
    [InlineData("0,30 * * * *", true)]
    [InlineData("* * * *", false)]        // four fields
    [InlineData("70 * * * *", false)]     // minute out of range
    [InlineData("a b c d e", false)]
    public void Cron_IsValidatedNotTrusted(string cron, bool valid) =>
        Assert.Equal(valid, ProjectSchedule.CronIsValid(cron));

    // ---- storage and the claim -------------------------------------------------------------------

    [Fact]
    public void AClaim_IsAtomic_TheSecondTakerLoses()
    {
        using var memory = Memory();
        var due = Daily() with { NextRunAt = DateTime.UtcNow.AddMinutes(-1) };
        memory.SaveSchedule(due);

        Assert.True(memory.TryClaimSchedule("s1", "host-a", DateTime.UtcNow));
        Assert.False(memory.TryClaimSchedule("s1", "host-b", DateTime.UtcNow));
    }

    [Fact]
    public void AStaleClaim_IsReclaimable()
    {
        using var memory = Memory();
        memory.SaveSchedule(Daily() with
        {
            NextRunAt = DateTime.UtcNow.AddMinutes(-30),
            ClaimedBy = "host-dead", ClaimedAt = DateTime.UtcNow.AddMinutes(-30),
        });

        Assert.True(memory.TryClaimSchedule("s1", "host-alive", DateTime.UtcNow));
    }

    // ---- execution through the real runner -------------------------------------------------------

    private static (ProjectScheduler Scheduler, SqliteMemory Memory) Rig(SqliteMemory memory)
    {
        var runner = new ConversationRunner(memory,
            (_, onCreated, _) => { var id = Guid.NewGuid().ToString("N")[..12]; onCreated(id); return id; });
        return (new ProjectScheduler(memory, runner), memory);
    }

    [Fact]
    public void ARun_IsARealConversation_InTheProject()
    {
        using var memory = Memory();
        memory.SaveProject(new Project { Id = "p1", Name = "the project" });
        var (scheduler, _) = Rig(memory);
        var s = Daily() with { ApprovalMode = EscalationPolicy.AutoApprove, CreatedBy = "zwright" };
        memory.SaveSchedule(s);

        var run = scheduler.RunNow(s, "zwright");

        Assert.Equal("complete", run.Status);
        var conversation = memory.LoadConversation(run.ConversationId!)!;
        Assert.Equal("p1", conversation.ProjectId);
        Assert.Equal(EscalationPolicy.AutoApprove, conversation.EffectivePolicy);   // attributed
        Assert.Equal("zwright", conversation.PolicySetBy);
    }

    /// <summary>Ask-mode scheduled work WAITS, visibly — it never self-promotes to automatic.</summary>
    [Fact]
    public void AnAskModeRun_WaitsForTheOperator()
    {
        using var memory = Memory();
        var (scheduler, _) = Rig(memory);
        var s = Daily() with { ApprovalMode = EscalationPolicy.Ask };
        memory.SaveSchedule(s);

        var run = scheduler.RunNow(s, "zwright");

        Assert.Equal("waiting_approval", run.Status);
        Assert.True(ConversationStateReader.Read(memory, run.ConversationId!).NeedsOperator);
    }

    [Fact]
    public void AnOverlappingOccurrence_SkipsAndSaysSo()
    {
        using var memory = Memory();
        var (scheduler, _) = Rig(memory);
        var s = Daily();
        memory.SaveSchedule(s);
        memory.SaveScheduleRun(new ScheduleRun("r0", "s1", "p1", null, "running", "schedule", null,
            DateTime.UtcNow.AddMinutes(-2), null));

        var run = scheduler.RunNow(s, "zwright");

        Assert.Equal("skipped_overlap", run.Status);
    }

    [Fact]
    public void AOneTimeSchedule_RetiresAfterItsRun_AndAMissedOccurrenceFiresOnce()
    {
        using var memory = Memory();
        var (scheduler, _) = Rig(memory);
        // Due 3 days ago: three "missed" dailies would exist; the tick must produce ONE run.
        var s = Daily() with
        {
            TriggerType = "once", ApprovalMode = EscalationPolicy.AutoApprove, CreatedBy = "z",
            OneTimeAt = DateTime.UtcNow.AddDays(-3), NextRunAt = DateTime.UtcNow.AddDays(-3),
        };
        memory.SaveSchedule(s);

        scheduler.Tick();

        var runs = memory.LoadScheduleRuns("s1");
        Assert.Single(runs);
        Assert.Equal("missed_catchup", runs[0].Trigger);
        var after = memory.LoadSchedule("s1")!;
        Assert.False(after.Enabled);          // one-time retired itself
        Assert.Null(after.NextRunAt);
        Assert.Null(after.ClaimedBy);         // claim released
    }

    /// <summary>Restart: a run the dead host left "running" reads failed with honest words.</summary>
    [Fact]
    public void RestartRecovery_FailsOrphanedRuns_AndClearsDeadClaims()
    {
        using var memory = Memory();
        memory.SaveSchedule(Daily() with { ClaimedBy = "host-dead", ClaimedAt = DateTime.UtcNow });
        memory.SaveScheduleRun(new ScheduleRun("r1", "s1", "p1", null, "running", "schedule", null,
            DateTime.UtcNow.AddMinutes(-9), null));

        var (scheduler, _) = Rig(memory);
        scheduler.Start();   // recovery runs before the timer's first tick
        scheduler.Dispose();

        var run = memory.LoadScheduleRuns("s1")[0];
        Assert.Equal("failed", run.Status);
        Assert.Contains("host stopped", run.Summary);
        Assert.Null(memory.LoadSchedule("s1")!.ClaimedBy);
    }
}
