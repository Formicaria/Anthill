using Anthill.Core.Common;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;

namespace Anthill.Core.Projects;

/// <summary>
/// v0.3.8.48 — the schedule executor. A 30-second tick scans for due, enabled schedules,
/// claims each atomically (see <see cref="SqliteMemory.TryClaimSchedule"/>), and turns the
/// occurrence into a REAL conversation inside the schedule's project — the operator can open
/// it, read what happened, review changes, and keep talking to it. There is no cloud: this
/// runs while the Anthill host runs, and the UI says so in those words.
///
/// Missed occurrences resolve conservatively at startup and on every tick: a schedule whose
/// next_run_at fell during downtime fires ONCE (trigger "missed_catchup"), then recomputes
/// forward. A backlog is never replayed — ten missed dailies are one run, not ten.
///
/// Overlap: "skip" (default) records a skipped_overlap run when the previous one is still
/// going; "queue" lets the new occurrence start when claimed after the running one finishes.
/// One-time schedules disable themselves after a completed run.
/// </summary>
public sealed class ProjectScheduler : IDisposable
{
    private readonly SqliteMemory _memory;
    private readonly ConversationRunner _conversations;
    private readonly string _instanceId = "host-" + Guid.NewGuid().ToString("N")[..8];
    private Timer? _timer;

    public ProjectScheduler(SqliteMemory memory, ConversationRunner conversations)
    {
        _memory = memory;
        _conversations = conversations;
    }

    public void Start()
    {
        RecoverAfterRestart();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Restart recovery: clear this-process claims that can no longer be running, and mark runs
    /// the previous process left "running" as failed with an honest reason — a run that died with
    /// the host must not read as in-flight forever.
    /// </summary>
    private void RecoverAfterRestart()
    {
        foreach (var s in _memory.LoadAllSchedules())
        {
            foreach (var run in _memory.LoadScheduleRuns(s.Id, 5).Where(r => r.Status == "running"))
                _memory.SaveScheduleRun(run with
                {
                    Status = "failed",
                    Summary = "the Anthill host stopped while this run was in flight",
                    FinishedAt = AnthillTime.NowUtc(),
                });
            if (s.ClaimedBy is not null)
                _memory.SaveSchedule(s with { ClaimedBy = null, ClaimedAt = null });
        }
    }

    internal void Tick()
    {
        var now = AnthillTime.NowUtc();
        foreach (var s in _memory.LoadAllSchedules())
        {
            if (!s.Enabled || s.NextRunAt is null || s.NextRunAt > now) continue;
            if (!_memory.TryClaimSchedule(s.Id, _instanceId, now)) continue;
            var missed = now - s.NextRunAt.Value > TimeSpan.FromMinutes(5);
            try { Execute(s, missed ? "missed_catchup" : "schedule"); }
            catch (Exception error)
            {
                _memory.SaveScheduleRun(new ScheduleRun(
                    Guid.NewGuid().ToString("N")[..12], s.Id, s.ProjectId, null,
                    "failed", "schedule", error.Message, now, AnthillTime.NowUtc()));
            }
            finally { Advance(s, now); }
        }
    }

    /// <summary>Run now, from the UI. Overlap policy still applies; the claim does not.</summary>
    public ScheduleRun RunNow(ProjectSchedule schedule, string requestedBy) =>
        Execute(schedule, "manual", requestedBy);

    private ScheduleRun Execute(ProjectSchedule s, string trigger, string? requestedBy = null)
    {
        var now = AnthillTime.NowUtc();
        var runId = Guid.NewGuid().ToString("N")[..12];

        // Overlap: a previous run of THIS schedule still marked running means skip (recorded, not
        // silent) under the default policy.
        if (s.OverlapPolicy == "skip"
            && _memory.LoadScheduleRuns(s.Id, 3).Any(r => r.Status == "running"))
        {
            var skipped = new ScheduleRun(runId, s.Id, s.ProjectId, null,
                "skipped_overlap", trigger, "previous run still in progress", now, now);
            _memory.SaveScheduleRun(skipped);
            return skipped;
        }

        // The run IS a conversation, in the schedule's project, under the schedule's approval
        // mode — attributed to whoever created the schedule (or pressed Run now), because that
        // person made the standing decision. Fail-closed rule intact: no author, no permission.
        var by = string.IsNullOrWhiteSpace(requestedBy) ? s.CreatedBy : requestedBy;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Title = $"{s.Name} — {TimeZoneInfo.ConvertTimeFromUtc(now, Tz(s.Timezone)):MMM d HH:mm}",
            ProjectId = s.ProjectId,
            Policy = s.ApprovalMode,
            PolicySetBy = s.ApprovalMode == EscalationPolicy.Ask ? null : by,
            PolicySetAt = s.ApprovalMode == EscalationPolicy.Ask ? null : now,
        };
        _memory.SaveConversation(conversation);

        var run = new ScheduleRun(runId, s.Id, s.ProjectId, conversation.Id,
            "running", trigger, null, now, null);
        _memory.SaveScheduleRun(run);

        var answers = s.ApprovalMode == EscalationPolicy.Ask
            ? null
            : new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" };
        var outcome = _conversations.Run(conversation, s.Prompt, ConversationMode.Mission, answers);

        // Ask-mode runs that stopped at the gate WAIT, visibly — the pending decision surfaces in
        // the project conversation, and the run says so instead of converting itself to auto.
        var status = outcome.Started ? "complete"
                   : outcome.Decision is { Allowed: false } ? "waiting_approval"
                   : "failed";
        var finished = run with { Status = status, Summary = outcome.Summary, FinishedAt = AnthillTime.NowUtc() };
        _memory.SaveScheduleRun(finished);
        return finished;
    }

    private void Advance(ProjectSchedule s, DateTime now)
    {
        var current = _memory.LoadSchedule(s.Id) ?? s;
        var next = current.ComputeNextRun(now);
        _memory.SaveSchedule(current with
        {
            ClaimedBy = null,
            ClaimedAt = null,
            LastRunAt = now,
            NextRunAt = next,
            // One-time schedules retire themselves after their run; the record stays.
            Enabled = current.TriggerType == "once" ? false : current.Enabled,
            UpdatedAt = now,
        });
    }

    private static TimeZoneInfo Tz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public void Dispose() => _timer?.Dispose();
}
