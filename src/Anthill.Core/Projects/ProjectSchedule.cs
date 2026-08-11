using Anthill.Core.Common;
using Anthill.Core.Conversations;

namespace Anthill.Core.Projects;

/// <summary>
/// v0.3.8.48 — a scheduled task belonging to exactly one project.
///
/// Times are two facts kept honest together: the next-run INSTANT is stored UTC, and the
/// operator's IANA timezone is stored beside it, because "daily at 07:00" means 07:00 where the
/// operator lives — across DST transitions, forever. The instant is recomputed from the local
/// rule after every run rather than by adding a fixed interval, which is the only arithmetic
/// that survives a clock change.
///
/// Schedules execute only while the Anthill host is running. That is stated to the operator,
/// not hidden: there is no cloud, and a schedule that fires "in the cloud" would be a lie.
/// Missed occurrences after downtime resolve conservatively — the LATEST missed occurrence
/// runs once; a backlog is never replayed.
/// </summary>
public sealed record ProjectSchedule
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string Name { get; init; } = "";

    /// <summary>What each run asks the colony to do — becomes the first message of a real conversation.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>manual | once | hourly | daily | weekdays | weekly | cron.</summary>
    public string TriggerType { get; init; } = "manual";

    /// <summary>Validated cron expression, only when TriggerType is cron.</summary>
    public string? Cron { get; init; }

    /// <summary>The one-time instant (UTC), only when TriggerType is once.</summary>
    public DateTime? OneTimeAt { get; init; }

    /// <summary>Local wall-clock time "HH:mm" for daily/weekdays/weekly; minute "mm" for hourly.</summary>
    public string? LocalTime { get; init; }

    /// <summary>IANA timezone id the local rule is interpreted in.</summary>
    public string Timezone { get; init; } = "UTC";

    public EscalationPolicy ApprovalMode { get; init; } = EscalationPolicy.Ask;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>skip (default: a still-running previous run means this occurrence is skipped) | queue.</summary>
    public string OverlapPolicy { get; init; } = "skip";

    public DateTime? NextRunAt { get; init; }
    public DateTime? LastRunAt { get; init; }

    /// <summary>Atomic-claim bookkeeping: which host instance took the due run, and when.</summary>
    public string? ClaimedBy { get; init; }
    public DateTime? ClaimedAt { get; init; }

    public string CreatedBy { get; init; } = "";
    public string UpdatedBy { get; init; } = "";
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
    public DateTime UpdatedAt { get; init; } = AnthillTime.NowUtc();

    public static readonly string[] TriggerTypes = { "manual", "once", "hourly", "daily", "weekdays", "weekly", "cron" };

    /// <summary>
    /// The next UTC instant this schedule should fire after <paramref name="afterUtc"/>, or null
    /// for manual/disabled/finished schedules. Local rules are evaluated in the schedule's own
    /// timezone; the returned instant is UTC.
    /// </summary>
    public DateTime? ComputeNextRun(DateTime afterUtc)
    {
        if (!Enabled) return null;
        switch (TriggerType)
        {
            case "manual": return null;
            case "once":
                return OneTimeAt is { } once && once > afterUtc ? once : null;
        }

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(Timezone); }
        catch { tz = TimeZoneInfo.Utc; }   // an unknown zone degrades to UTC rather than never firing
        var local = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, tz);

        if (TriggerType == "hourly")
        {
            var minute = int.TryParse(LocalTime, out var m) ? Math.Clamp(m, 0, 59) : 0;
            var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, minute, 0);
            if (candidate <= local) candidate = candidate.AddHours(1);
            return ToUtc(candidate, tz);
        }

        var parts = (LocalTime ?? "09:00").Split(':');
        var hh = int.TryParse(parts.ElementAtOrDefault(0), out var h) ? Math.Clamp(h, 0, 23) : 9;
        var mm = int.TryParse(parts.ElementAtOrDefault(1), out var mi) ? Math.Clamp(mi, 0, 59) : 0;
        var day = new DateTime(local.Year, local.Month, local.Day, hh, mm, 0);

        switch (TriggerType)
        {
            case "daily":
                if (day <= local) day = day.AddDays(1);
                return ToUtc(day, tz);
            case "weekdays":
                if (day <= local) day = day.AddDays(1);
                while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
                return ToUtc(day, tz);
            case "weekly":
                // The schedule keeps firing on the weekday of its creation instant, in its zone.
                var anchor = TimeZoneInfo.ConvertTimeFromUtc(CreatedAt, tz).DayOfWeek;
                while (day.DayOfWeek != anchor || day <= local) day = day.AddDays(1);
                return ToUtc(day, tz);
            case "cron":
                return CronNext(Cron, local, tz);
        }
        return null;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        // A local time skipped by a DST spring-forward is nudged past the gap rather than thrown.
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// Minimal five-field cron (minute hour day-of-month month day-of-week; numbers, *, and
    /// comma lists). Deliberately small: a validated subset that cannot surprise, not a clone of
    /// every cron dialect. <see cref="CronIsValid"/> is the gate the API uses.
    /// </summary>
    internal static DateTime? CronNext(string? cron, DateTime local, TimeZoneInfo tz)
    {
        if (!CronIsValid(cron)) return null;
        var f = cron!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Hit(string field, int value) =>
            field == "*" || field.Split(',').Any(p => int.TryParse(p, out var n) && n == value);

        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0).AddMinutes(1);
        for (var i = 0; i < 366 * 24 * 60; i++, t = t.AddMinutes(1))
            if (Hit(f[0], t.Minute) && Hit(f[1], t.Hour) && Hit(f[2], t.Day)
                && Hit(f[3], t.Month) && Hit(f[4], (int)t.DayOfWeek))
                return ToUtc(t, tz);
        return null;   // nothing within a year: treat as never rather than spinning
    }

    public static bool CronIsValid(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var f = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return false;
        var limits = new[] { 59, 23, 31, 12, 6 };
        for (var i = 0; i < 5; i++)
        {
            if (f[i] == "*") continue;
            foreach (var part in f[i].Split(','))
                if (!int.TryParse(part, out var n) || n < 0 || n > limits[i]) return false;
        }
        return true;
    }
}

/// <summary>One execution of a schedule: when, how triggered, and the conversation it became.</summary>
public sealed record ScheduleRun(
    string Id,
    string ScheduleId,
    string ProjectId,
    string? ConversationId,
    string Status,          // running | complete | failed | skipped_overlap | waiting_approval
    string Trigger,         // schedule | manual | missed_catchup
    string? Summary,
    DateTime StartedAt,
    DateTime? FinishedAt);
